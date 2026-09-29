using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

[CollectionDefinition("PreorderMutationLock")]
public sealed class PreorderMutationLockCollection { }

[Collection("PreorderMutationLock")]
public sealed class PreorderPersistenceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public PreorderPersistenceTests()
    {
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(
            typeof(Product), typeof(WarehouseProduct), typeof(Store), typeof(UserStore),
            typeof(WareHouseOrder), typeof(WareHouseOrderDetails),
            typeof(PreorderTemplate), typeof(PreorderTemplateItem), typeof(PreorderTemplateStore),
            typeof(PreorderActivation), typeof(PreorderActivationItem), typeof(PreorderActivationStore),
            typeof(PreorderWarehouseOrder), typeof(PreorderWarehouseOrderItem)
        );
        PreorderSchemaBootstrap.EnsureIndexesAsync(_db).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task SQLite_激活期号和分店订单明细均受业务唯一索引保护()
    {
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "activation-1",
            TemplateGuid = "template-1",
            PeriodNumber = 1,
            ActivationCode = "PRE-1",
            TemplateNameSnapshot = "测试",
            SourceTemplateRevision = 1,
            StartAtUtc = Now,
            EndAtUtc = Now.AddDays(1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "activation-2",
            TemplateGuid = "template-1",
            PeriodNumber = 1,
            ActivationCode = "PRE-2",
            TemplateNameSnapshot = "测试",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddDays(2),
            EndAtUtc = Now.AddDays(3),
            Status = PreorderActivationStatuses.Scheduled,
        }).ExecuteCommandAsync());

        await _db.Insertable(new PreorderWarehouseOrder
        {
            OrderGuid = "order-1",
            ActivationGuid = "activation-1",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
            OrderNo = "PRE-1-S01",
        }).ExecuteCommandAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => _db.Insertable(new PreorderWarehouseOrder
        {
            OrderGuid = "order-2",
            ActivationGuid = "activation-1",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
            OrderNo = "PRE-1-S01-2",
        }).ExecuteCommandAsync());

        await _db.Insertable(new PreorderWarehouseOrderItem
        {
            OrderItemGuid = "detail-1",
            OrderGuid = "order-1",
            ActivationItemGuid = "item-1",
            ProductCode = "P1",
            ItemNumber = "I1",
            ProductName = "商品",
            MinimumOrderQuantity = 1,
        }).ExecuteCommandAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => _db.Insertable(new PreorderWarehouseOrderItem
        {
            OrderItemGuid = "detail-2",
            OrderGuid = "order-1",
            ActivationItemGuid = "item-1",
            ProductCode = "P1",
            ItemNumber = "I1",
            ProductName = "商品",
            MinimumOrderQuantity = 1,
        }).ExecuteCommandAsync());
    }

    [Fact]
    public async Task SQLite_订单明细按OrderGuid查询具备非过滤索引()
    {
        var indexes = await _db.Ado.SqlQueryAsync<SqliteIndexInfo>(
            "PRAGMA index_list('PreorderWarehouseOrderItem')"
        );
        var orderGuidIndex = Assert.Single(indexes, item =>
            item.name == "IX_PreorderWarehouseOrderItem_OrderGuid"
        );

        Assert.Equal(0, orderGuidIndex.partial);
        var columns = await _db.Ado.SqlQueryAsync<SqliteIndexColumnInfo>(
            "PRAGMA index_info('IX_PreorderWarehouseOrderItem_OrderGuid')"
        );
        Assert.Equal("OrderGuid", Assert.Single(columns).name);
    }

    [Fact]
    public async Task SQLite_预计到货日期使用可空date列()
    {
        var columns = await _db.Ado.SqlQueryAsync<SqliteTableColumnInfo>(
            "PRAGMA table_info('PreorderActivation')"
        );
        var column = Assert.Single(columns, item => item.name == "EstimatedArrivalDate");

        Assert.Equal("DATE", column.type, ignoreCase: true);
        Assert.Equal(0, column.notnull);
    }

    [Fact]
    public async Task SQLite_批次和订单状态受数据库约束保护()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "invalid-status-activation",
            TemplateGuid = "template-1",
            PeriodNumber = 1,
            ActivationCode = "PRE-INVALID-ACTIVATION",
            TemplateNameSnapshot = "非法状态",
            SourceTemplateRevision = 1,
            StartAtUtc = Now,
            EndAtUtc = Now.AddHours(1),
            Status = "Unexpected",
        }).ExecuteCommandAsync());

        await Assert.ThrowsAnyAsync<Exception>(() => _db.Insertable(new PreorderWarehouseOrder
        {
            OrderGuid = "invalid-status-order",
            ActivationGuid = "activation-1",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
            OrderNo = "PRE-INVALID-ORDER",
            Status = "Unexpected",
        }).ExecuteCommandAsync());
    }

    [Fact]
    public async Task 激活后商品与价格使用快照且不随模板源数据变化()
    {
        await SeedProductAndStoreAsync();
        var service = CreateService("admin-user", manageOrders: true);
        var template = await service.CreateTemplateAsync(new SavePreorderTemplateDto
        {
            Name = "进口新品",
            Items = new() { new() { ProductCode = "P1", MinimumOrderQuantity = 6 } },
            StoreGuids = new() { "store-1" },
        });
        var activation = await service.ActivateAsync(template.TemplateGuid, new ActivatePreorderTemplateDto
        {
            ExpectedRevision = template.Revision,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddDays(1),
        });

        await _db.Updateable<Product>()
            .SetColumns(item => item.ProductName == "新名称")
            .Where(item => item.ProductCode == "P1")
            .ExecuteCommandAsync();
        await _db.Updateable<WarehouseProduct>()
            .SetColumns(item => item.ImportPrice == 99m)
            .Where(item => item.ProductCode == "P1")
            .ExecuteCommandAsync();

        var detail = await service.GetActivationAsync(activation.ActivationGuid);
        var item = Assert.Single(detail.Items);
        Assert.Equal("旧名称", item.ProductName);
        Assert.Equal(3.25m, item.ImportPrice);
        Assert.Equal(6, item.MinimumOrderQuantity);
    }

    [Fact]
    public async Task 激活必须携带当前模板Revision并拒绝旧页面快照()
    {
        await SeedProductAndStoreAsync();
        var service = CreateService("admin-user", manageOrders: true);
        var template = await service.CreateTemplateAsync(new SavePreorderTemplateDto
        {
            Name = "版本门禁模板",
            Items = new() { new() { ProductCode = "P1", MinimumOrderQuantity = 1 } },
            StoreGuids = new() { "store-1" },
        });
        var updated = await service.UpdateTemplateAsync(template.TemplateGuid, new SavePreorderTemplateDto
        {
            Name = "版本门禁模板二版",
            ExpectedRevision = template.Revision,
            Items = new() { new() { ProductCode = "P1", MinimumOrderQuantity = 2 } },
            StoreGuids = new() { "store-1" },
        });

        foreach (var expectedRevision in new int?[] { null, template.Revision })
        {
            var error = await Assert.ThrowsAsync<PreorderBusinessException>(() =>
                service.ActivateAsync(template.TemplateGuid, new ActivatePreorderTemplateDto
                {
                    ExpectedRevision = expectedRevision,
                    StartAtUtc = Now.AddMinutes(-1),
                    EndAtUtc = Now.AddHours(1),
                })
            );
            Assert.Equal(409, error.StatusCode);
            Assert.Equal("PREORDER_TEMPLATE_CHANGED", error.ErrorCode);
        }

        Assert.Equal(2, updated.Revision);
        Assert.False(await _db.Queryable<PreorderActivation>().AnyAsync());
    }

    [Fact]
    public async Task 修改模板软删除旧商品和分店并保留Revision审计()
    {
        await SeedProductAndStoreAsync();
        var service = CreateService("admin-user", manageOrders: true);
        var template = await service.CreateTemplateAsync(new SavePreorderTemplateDto
        {
            Name = "审计模板",
            Items = new() { new() { ProductCode = "P1", MinimumOrderQuantity = 1 } },
            StoreGuids = new() { "store-1" },
        });
        var originalItem = await _db.Queryable<PreorderTemplateItem>()
            .SingleAsync(item => item.TemplateGuid == template.TemplateGuid);
        var originalStore = await _db.Queryable<PreorderTemplateStore>()
            .SingleAsync(item => item.TemplateGuid == template.TemplateGuid);

        await service.UpdateTemplateAsync(template.TemplateGuid, new SavePreorderTemplateDto
        {
            Name = "审计模板二版",
            ExpectedRevision = template.Revision,
            Items = new() { new() { ProductCode = "P1", MinimumOrderQuantity = 3 } },
            StoreGuids = new() { "store-1" },
        });

        var itemRows = await _db.Queryable<PreorderTemplateItem>()
            .Where(item => item.TemplateGuid == template.TemplateGuid)
            .ToListAsync();
        var storeRows = await _db.Queryable<PreorderTemplateStore>()
            .Where(item => item.TemplateGuid == template.TemplateGuid)
            .ToListAsync();
        var updatedTemplate = await _db.Queryable<PreorderTemplate>()
            .FirstAsync(item => item.TemplateGuid == template.TemplateGuid);
        Assert.Equal("System", updatedTemplate.UpdatedBy);
        Assert.Equal(2, itemRows.Count);
        Assert.True(itemRows.Single(item => item.TemplateItemGuid == originalItem.TemplateItemGuid).IsDeleted);
        Assert.Equal(3, itemRows.Single(item => !item.IsDeleted).MinimumOrderQuantity);
        Assert.Equal(2, storeRows.Count);
        Assert.True(storeRows.Single(item => item.TemplateStoreGuid == originalStore.TemplateStoreGuid).IsDeleted);
        Assert.Single(storeRows, item => !item.IsDeleted);
    }

    [Theory]
    [InlineData(PreorderWarehouseOrderStatuses.Processing)]
    [InlineData(PreorderWarehouseOrderStatuses.Completed)]
    [InlineData(PreorderWarehouseOrderStatuses.Cancelled)]
    public async Task Preorder订单CAS状态更新同时写入UpdatedBy(string targetStatus)
    {
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = $"audit-{targetStatus}-activation",
            TemplateGuid = "audit-status-template",
            PeriodNumber = 1,
            ActivationCode = $"PRE-AUDIT-{targetStatus}",
            TemplateNameSnapshot = "状态审计",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderWarehouseOrder
        {
            OrderGuid = $"audit-{targetStatus}-order",
            ActivationGuid = $"audit-{targetStatus}-activation",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
            OrderNo = $"PRE-AUDIT-{targetStatus}",
            Status = PreorderWarehouseOrderStatuses.Submitted,
        }).ExecuteCommandAsync();

        await CreateService("admin-user", manageOrders: true).UpdateOrderStatusAsync(
            $"audit-{targetStatus}-order",
            new UpdatePreorderOrderStatusDto { Status = targetStatus }
        );

        var persisted = await _db.Queryable<PreorderWarehouseOrder>()
            .FirstAsync(item => item.OrderGuid == $"audit-{targetStatus}-order");
        Assert.Equal(targetStatus, persisted.Status);
        Assert.Equal("System", persisted.UpdatedBy);
    }

    [Theory]
    [InlineData(PreorderWarehouseOrderStatuses.Submitted)]
    [InlineData(PreorderWarehouseOrderStatuses.NoDemand)]
    public async Task 仓库可在激活期内退回已响应订单并重新阻塞门禁(string sourceStatus)
    {
        await SeedProductAndStoreAsync();
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "return-active",
            TemplateGuid = "return-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-RETURN",
            TemplateNameSnapshot = "退回修改",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = "return-store",
            ActivationGuid = "return-active",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderWarehouseOrder
        {
            OrderGuid = "return-order",
            ActivationGuid = "return-active",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
            OrderNo = "PRE-RETURN-S01",
            Status = sourceStatus,
            DraftRevision = 4,
        }).ExecuteCommandAsync();

        var service = CreateService("admin-user", manageOrders: true);
        var returned = await service.UpdateOrderStatusAsync("return-order", new UpdatePreorderOrderStatusDto
        {
            Status = PreorderWarehouseOrderStatuses.ReturnedForRevision,
            WarehouseNotes = "请补充两箱饮料",
            ExpectedStatus = sourceStatus,
            ExpectedDraftRevision = 4,
        });

        Assert.Equal(PreorderWarehouseOrderStatuses.ReturnedForRevision, returned.Status);
        Assert.Equal(5, returned.DraftRevision);
        var gate = await service.GetActiveAsync("S01");
        Assert.True(gate.NormalOrderBlocked);
        Assert.Single(gate.Activations);
        var statistics = await service.GetStatisticsAsync("return-active");
        Assert.Equal(1, statistics.PendingCount);
        Assert.Single(statistics.PendingStores);

        var saved = await service.SaveDraftAsync("return-active", new SavePreorderDraftDto
        {
            StoreCode = "S01",
            ExpectedDraftRevision = 5,
            Items = new(),
        });
        Assert.Equal(PreorderWarehouseOrderStatuses.ReturnedForRevision, saved.OrderStatus);
        Assert.Equal(6, saved.DraftRevision);
        Assert.Equal("请补充两箱饮料", saved.WarehouseNotes);
        var resubmitted = await service.SubmitAsync("return-active", new SubmitPreorderDto
        {
            StoreCode = "S01",
            ExpectedDraftRevision = 6,
            ConfirmNoDemand = true,
            Items = new(),
        });
        Assert.Equal(PreorderWarehouseOrderStatuses.NoDemand, resubmitted.Status);
        Assert.False((await service.GetActiveAsync("S01")).NormalOrderBlocked);
    }

    [Theory]
    [InlineData(PreorderWarehouseOrderStatuses.Processing)]
    [InlineData(PreorderWarehouseOrderStatuses.Completed)]
    [InlineData(PreorderWarehouseOrderStatuses.Cancelled)]
    public async Task 仓库不能退回已进入后续流程或取消的订单(string sourceStatus)
    {
        await SeedProductAndStoreAsync();
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "return-invalid",
            TemplateGuid = "return-invalid-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-RETURN-INVALID",
            TemplateNameSnapshot = "不可退回",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = "return-invalid-store",
            ActivationGuid = "return-invalid",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderWarehouseOrder
        {
            OrderGuid = "return-invalid-order",
            ActivationGuid = "return-invalid",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
            OrderNo = "PRE-RETURN-INVALID-S01",
            Status = sourceStatus,
            DraftRevision = 2,
        }).ExecuteCommandAsync();

        var error = await Assert.ThrowsAsync<PreorderBusinessException>(() =>
            CreateService("admin-user", manageOrders: true).UpdateOrderStatusAsync(
                "return-invalid-order",
                new UpdatePreorderOrderStatusDto
                {
                    Status = PreorderWarehouseOrderStatuses.ReturnedForRevision,
                    WarehouseNotes = "请调整数量",
                    ExpectedStatus = sourceStatus,
                    ExpectedDraftRevision = 2,
                }
            )
        );
        Assert.Equal("PREORDER_INVALID_STATUS_TRANSITION", error.ErrorCode);
    }

    [Fact]
    public async Task 仓库退回订单拒绝过期批次和过时Revision()
    {
        await SeedProductAndStoreAsync();
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "return-expired",
            TemplateGuid = "return-expired-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-RETURN-EXPIRED",
            TemplateNameSnapshot = "过期退回",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-2),
            EndAtUtc = Now,
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = "return-expired-store",
            ActivationGuid = "return-expired",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderWarehouseOrder
        {
            OrderGuid = "return-expired-order",
            ActivationGuid = "return-expired",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
            OrderNo = "PRE-RETURN-EXPIRED-S01",
            Status = PreorderWarehouseOrderStatuses.Submitted,
            DraftRevision = 3,
        }).ExecuteCommandAsync();
        var service = CreateService("admin-user", manageOrders: true);

        var expired = await Assert.ThrowsAsync<PreorderBusinessException>(() => service.UpdateOrderStatusAsync(
            "return-expired-order",
            new UpdatePreorderOrderStatusDto
            {
                Status = PreorderWarehouseOrderStatuses.ReturnedForRevision,
                WarehouseNotes = "已过期仍尝试退回",
                ExpectedStatus = PreorderWarehouseOrderStatuses.Submitted,
                ExpectedDraftRevision = 3,
            }
        ));
        Assert.Equal("PREORDER_NOT_ACTIVE", expired.ErrorCode);

        var extendedEnd = Now.AddHours(1);
        await _db.Updateable<PreorderActivation>()
            .SetColumns(item => item.EndAtUtc == extendedEnd)
            .Where(item => item.ActivationGuid == "return-expired")
            .ExecuteCommandAsync();
        var stale = await Assert.ThrowsAsync<PreorderBusinessException>(() => service.UpdateOrderStatusAsync(
            "return-expired-order",
            new UpdatePreorderOrderStatusDto
            {
                Status = PreorderWarehouseOrderStatuses.ReturnedForRevision,
                WarehouseNotes = "过时版本",
                ExpectedStatus = PreorderWarehouseOrderStatuses.Submitted,
                ExpectedDraftRevision = 2,
            }
        ));
        Assert.Equal("PREORDER_INVALID_STATUS_TRANSITION", stale.ErrorCode);
    }

    [Fact]
    public async Task 仓库退回等待StoreGate并在锁内重读目标快照()
    {
        await SeedProductAndStoreAsync();
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "return-target-race",
            TemplateGuid = "return-target-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-RETURN-TARGET",
            TemplateNameSnapshot = "目标竞态",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = "return-target-store",
            ActivationGuid = "return-target-race",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderWarehouseOrder
        {
            OrderGuid = "return-target-order",
            ActivationGuid = "return-target-race",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
            OrderNo = "PRE-RETURN-TARGET-S01",
            Status = PreorderWarehouseOrderStatuses.Submitted,
            DraftRevision = 3,
        }).ExecuteCommandAsync();

        var heldStoreGate = await PreorderMutationLock.AcquireProcessAsync("PreorderStoreGate:store-1");
        try
        {
            var returnTask = Task.Run(() => CreateService("admin-user", manageOrders: true)
                .UpdateOrderStatusAsync("return-target-order", new UpdatePreorderOrderStatusDto
                {
                    Status = PreorderWarehouseOrderStatuses.ReturnedForRevision,
                    WarehouseNotes = "请修改",
                    ExpectedStatus = PreorderWarehouseOrderStatuses.Submitted,
                    ExpectedDraftRevision = 3,
                }));
            var firstCompleted = await Task.WhenAny(returnTask, Task.Delay(TimeSpan.FromMilliseconds(500)));
            Assert.NotSame(returnTask, firstCompleted);

            await _db.Updateable<PreorderActivationStore>()
                .SetColumns(item => item.IsDeleted == true)
                .Where(item => item.ActivationGuid == "return-target-race")
                .ExecuteCommandAsync();
            await _db.Insertable(new PreorderActivationStore
            {
                ActivationStoreGuid = "return-target-store-changed",
                ActivationGuid = "return-target-race",
                StoreGuid = "store-1",
                StoreCode = "S01",
                StoreName = "一店",
            }).ExecuteCommandAsync();
            await heldStoreGate.DisposeAsync();

            var error = await Assert.ThrowsAsync<PreorderBusinessException>(async () => await returnTask);
            Assert.Equal("PREORDER_INVALID_STATUS_TRANSITION", error.ErrorCode);
        }
        finally
        {
            await heldStoreGate.DisposeAsync();
        }

        var persisted = await _db.Queryable<PreorderWarehouseOrder>()
            .FirstAsync(item => item.OrderGuid == "return-target-order");
        Assert.Equal(PreorderWarehouseOrderStatuses.Submitted, persisted.Status);
        Assert.Equal(3, persisted.DraftRevision);
    }

    [Fact]
    public async Task 分店访问只允许显式ManageOrders或UserStore已分配分店()
    {
        await SeedProductAndStoreAsync();
        await SeedActiveTargetAsync();

        var assigned = CreateService("assigned-user", manageOrders: false);
        await _db.Insertable(new UserStore
        {
            UserStoreGUID = "us-1",
            UserGUID = "assigned-user",
            StoreGUID = "store-1",
        }).ExecuteCommandAsync();
        Assert.True((await assigned.GetActiveAsync("S01")).NormalOrderBlocked);

        var roleOnly = CreateService("warehouse-role-only", manageOrders: false, role: "WarehouseManager");
        var forbidden = await Assert.ThrowsAsync<BlazorApp.Api.Interfaces.React.PreorderBusinessException>(
            () => roleOnly.GetActiveAsync("S01")
        );
        Assert.Equal(403, forbidden.StatusCode);
        Assert.Equal("FORBIDDEN", forbidden.ErrorCode);

        var global = CreateService("global-user", manageOrders: true);
        Assert.True((await global.GetActiveAsync("S01")).NormalOrderBlocked);
    }

    [Fact]
    public async Task 公开Active对不存在删除及停用分店统一FailClosed()
    {
        await SeedProductAndStoreAsync();
        await _db.Insertable(new Store[]
        {
            new()
            {
                StoreGUID = "store-inactive",
                StoreCode = "INACTIVE",
                StoreName = "停用店",
                IsActive = false,
            },
            new()
            {
                StoreGUID = "store-deleted",
                StoreCode = "DELETED",
                StoreName = "删除店",
                IsActive = true,
                IsDeleted = true,
            },
        }).ExecuteCommandAsync();
        var service = CreateService("global-user", manageOrders: true);

        foreach (var storeCode in new[] { "MISSING", "DELETED", "INACTIVE" })
        {
            var error = await Assert.ThrowsAsync<PreorderBusinessException>(
                () => service.GetActiveAsync(storeCode)
            );
            Assert.Equal(503, error.StatusCode);
            Assert.Equal("PREORDER_GATE_UNAVAILABLE", error.ErrorCode);
        }
    }

    [Fact]
    public async Task 到时的Scheduled批次按时间窗口生效且门禁查询保持纯读()
    {
        await SeedProductAndStoreAsync();
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "scheduled-now",
            TemplateGuid = "scheduled-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-SCHEDULED",
            TemplateNameSnapshot = "定时批次",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddMinutes(-1),
            EndAtUtc = Now.AddHours(1),
            Status = PreorderActivationStatuses.Scheduled,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = "scheduled-store",
            ActivationGuid = "scheduled-now",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
        }).ExecuteCommandAsync();

        var gate = await CreateService("gate-user", manageOrders: false).CheckAsync("S01");

        Assert.True(gate.IsBlocked);
        Assert.Equal(PreorderActivationStatuses.Active, Assert.Single(gate.Activations).Status);
        var persisted = await _db.Queryable<PreorderActivation>()
            .FirstAsync(item => item.ActivationGuid == "scheduled-now");
        Assert.Equal(PreorderActivationStatuses.Scheduled, persisted.Status);
    }

    [Fact]
    public async Task 门禁查询对不存在分店FailClosed并返回稳定错误码()
    {
        var error = await Assert.ThrowsAsync<PreorderBusinessException>(
            () => CreateService("gate-user", manageOrders: false).CheckAsync("MISSING")
        );

        Assert.Equal(503, error.StatusCode);
        Assert.Equal("PREORDER_GATE_UNAVAILABLE", error.ErrorCode);
    }

    [Fact]
    public async Task 普通订单门禁锁从当前StoreCode解析不可变StoreGuid并failClosed()
    {
        await SeedProductAndStoreAsync();
        var original = await PreorderGateEvaluator.ResolveStoreLockResourceFailClosedAsync(
            _db,
            "S01",
            NullLogger.Instance
        );
        await _db.Updateable<Store>()
            .SetColumns(item => item.StoreCode == "NEW")
            .Where(item => item.StoreGUID == "store-1")
            .ExecuteCommandAsync();
        var renamed = await PreorderGateEvaluator.ResolveStoreLockResourceFailClosedAsync(
            _db,
            "NEW",
            NullLogger.Instance
        );

        Assert.Equal("PreorderStoreGate:store-1", original);
        Assert.Equal(original, renamed);
        foreach (var unavailableCode in new[] { "S01", "MISSING" })
        {
            var error = await Assert.ThrowsAsync<PreorderBusinessException>(() =>
                PreorderGateEvaluator.ResolveStoreLockResourceFailClosedAsync(
                    _db,
                    unavailableCode,
                    NullLogger.Instance
                )
            );
            Assert.Equal(503, error.StatusCode);
            Assert.Equal("PREORDER_GATE_UNAVAILABLE", error.ErrorCode);
        }
    }

    [Fact]
    public async Task 普通订单加锁后StoreCode改绑其他StoreGuid时身份漂移不可FailOpen()
    {
        await SeedProductAndStoreAsync();
        await _db.Updateable<Store>()
            .SetColumns(item => item.StoreCode == "OLD")
            .Where(item => item.StoreGUID == "store-1")
            .ExecuteCommandAsync();
        await _db.Insertable(new Store
        {
            StoreGUID = "store-2",
            StoreCode = "S01",
            StoreName = "重用编码分店",
            IsActive = true,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "rebind-target-activation",
            TemplateGuid = "rebind-target-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-REBIND-TARGET",
            TemplateNameSnapshot = "改绑目标店批次",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = "rebind-target-store",
            ActivationGuid = "rebind-target-activation",
            StoreGuid = "store-2",
            StoreCode = "S01",
            StoreName = "重用编码分店",
        }).ExecuteCommandAsync();
        await _db.Ado.BeginTranAsync();
        try
        {
            var error = await Assert.ThrowsAsync<PreorderBusinessException>(() =>
                PreorderGateEvaluator.EvaluateLockedForOrdinaryOrderWriteAsync(
                    _db,
                    "PreorderStoreGate:store-1",
                    "S01",
                    new FixedTimeProvider(Now),
                    "Test.React.SubmitOrder",
                    NullLogger.Instance
                )
            );
            Assert.Equal(StatusCodes.Status409Conflict, error.StatusCode);
            Assert.Equal("PREORDER_STORE_IDENTITY_CHANGED", error.ErrorCode);
        }
        finally
        {
            await _db.Ado.RollbackTranAsync();
        }
    }

    [Fact]
    public async Task StoreGate等待期间Scheduled开始后使用取锁后时间评估()
    {
        await SeedProductAndStoreAsync();
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "scheduled-while-lock-wait",
            TemplateGuid = "scheduled-while-lock-wait-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-SCHEDULED-WAIT",
            TemplateNameSnapshot = "等锁期间开始",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddMinutes(1),
            EndAtUtc = Now.AddHours(1),
            Status = PreorderActivationStatuses.Scheduled,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = "scheduled-while-lock-wait-store",
            ActivationGuid = "scheduled-while-lock-wait",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
        }).ExecuteCommandAsync();

        using var firstDb = CreateIndependentClient();
        using var secondDb = CreateIndependentClient();
        await firstDb.Ado.BeginTranAsync();
        await PreorderMutationLock.AcquireDatabaseAsync(
            firstDb,
            "PreorderStoreGate:store-1"
        );
        var clock = new MutableTimeProvider(Now);
        var evaluationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        PreorderGateEvaluation? evaluation = null;
        var secondTask = Task.Run(async () =>
        {
            // SQLite 的 BeginTran 是 BEGIN IMMEDIATE，第二个连接在开事务时就会等 firstDb 的写锁，
            // 发出任何 SQL 之前就已阻塞，所以只能在开事务前发信号。
            evaluationStarted.SetResult();
            var transaction = await secondDb.Ado.UseTranAsync(async () =>
            {
                evaluation = await PreorderGateEvaluator.EvaluateLockedFailClosedAsync(
                    secondDb,
                    "PreorderStoreGate:store-1",
                    "S01",
                    clock,
                    NullLogger.Instance
                );
            });
            Assert.True(transaction.IsSuccess, transaction.ErrorMessage);
        });
        // 防挂死上限：只等 Task.Run 被线程池调度到。原先 2 秒，在 CI runner 负载高时曾偶发超时。
        await evaluationStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        // 反向等待：给第二个连接进入等锁的时间，并确认它确实被挡住。
        // 负载高时它可能还没开始等锁，此时仍会在提交后才读时钟，断言照样成立，不会误报。
        await Task.Delay(100);
        Assert.False(secondTask.IsCompleted);

        clock.SetUtcNow(Now.AddMinutes(2));
        await firstDb.Ado.CommitTranAsync();
        await secondTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        Assert.NotNull(evaluation);
        Assert.True(evaluation.IsBlocked);
        Assert.Equal("scheduled-while-lock-wait", Assert.Single(
            evaluation.PendingActivations
        ).ActivationGuid);
    }

    [Fact]
    public async Task 公开Active查询数据库异常统一FailClosed为脱敏503()
    {
        await SeedProductAndStoreAsync();
        _db.DbMaintenance.DropTable<PreorderActivation>();

        var error = await Assert.ThrowsAsync<PreorderBusinessException>(() =>
            CreateService("global-user", manageOrders: true).GetActiveAsync("S01")
        );

        Assert.Equal(503, error.StatusCode);
        Assert.Equal("PREORDER_GATE_UNAVAILABLE", error.ErrorCode);
        Assert.DoesNotContain("PreorderActivation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 普通订单门禁查询数据库异常统一FailClosed为脱敏503()
    {
        await SeedProductAndStoreAsync();
        _db.DbMaintenance.DropTable<PreorderActivation>();

        var error = await Assert.ThrowsAsync<PreorderBusinessException>(() =>
            CreateService("gate-user", manageOrders: false).CheckAsync("S01")
        );

        Assert.Equal(503, error.StatusCode);
        Assert.Equal("PREORDER_GATE_UNAVAILABLE", error.ErrorCode);
        Assert.DoesNotContain("PreorderActivation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 未知订单状态不得解除门禁或计入已响应统计()
    {
        await SeedProductAndStoreAsync();
        await SeedActiveTargetAsync();
        await DisablePreorderStatusGuardsForLegacyStateTestAsync();
        await _db.Insertable(new PreorderWarehouseOrder
        {
            OrderGuid = "legacy-unknown-order",
            ActivationGuid = "gate-activation",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
            OrderNo = "PRE-LEGACY-UNKNOWN",
            Status = "Unexpected",
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationItem
        {
            ActivationItemGuid = "legacy-unknown-activation-item",
            ActivationGuid = "gate-activation",
            ProductCode = "P1",
            ItemNumber = "I1",
            ProductName = "旧状态商品",
            MinimumOrderQuantity = 1,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderWarehouseOrderItem
        {
            OrderItemGuid = "legacy-unknown-order-item",
            OrderGuid = "legacy-unknown-order",
            ActivationItemGuid = "legacy-unknown-activation-item",
            ProductCode = "P1",
            ItemNumber = "I1",
            ProductName = "旧状态商品",
            PackCount = 5,
            MinimumOrderQuantity = 1,
            OrderedQuantity = 5,
            ImportPrice = 2,
            RetailPrice = 3,
            ImportAmount = 10,
            RetailAmount = 15,
        }).ExecuteCommandAsync();

        var service = CreateService("admin-user", manageOrders: true);
        var gate = await service.CheckAsync("S01");
        var detail = await service.GetActivationAsync("gate-activation");
        var statistics = await service.GetStatisticsAsync("gate-activation");

        Assert.True(gate.IsBlocked);
        Assert.Equal(0, detail.RespondedStoreCount);
        Assert.Equal(1, statistics.PendingCount);
        Assert.Equal(0, Assert.Single(statistics.Products).TotalQuantity);
        Assert.Empty(statistics.StoreProductQuantities);
    }

    [Fact]
    public async Task 未知批次状态在详情中保留原值且不按时间提升为Active()
    {
        await DisablePreorderStatusGuardsForLegacyStateTestAsync();
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "legacy-unknown-activation",
            TemplateGuid = "template-1",
            PeriodNumber = 1,
            ActivationCode = "PRE-LEGACY-UNKNOWN",
            TemplateNameSnapshot = "未知状态",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = "Unexpected",
        }).ExecuteCommandAsync();

        var detail = await CreateService("admin-user", manageOrders: true)
            .GetActivationAsync("legacy-unknown-activation");

        Assert.Equal("Unexpected", detail.Status);
    }

    [Fact]
    public async Task 当前时间窗内的未知批次状态使Check和Active统一FailClosed()
    {
        await SeedProductAndStoreAsync();
        await DisablePreorderStatusGuardsForLegacyStateTestAsync();
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "legacy-unknown-gate-activation",
            TemplateGuid = "template-1",
            PeriodNumber = 1,
            ActivationCode = "PRE-LEGACY-UNKNOWN-GATE",
            TemplateNameSnapshot = "未知门禁状态",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = "Unexpected",
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = "legacy-unknown-gate-store",
            ActivationGuid = "legacy-unknown-gate-activation",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
        }).ExecuteCommandAsync();
        var service = CreateService("admin-user", manageOrders: true);

        foreach (var action in new Func<Task>[]
        {
            async () => await service.CheckAsync("S01"),
            async () => await service.GetActiveAsync("S01"),
        })
        {
            var error = await Assert.ThrowsAsync<PreorderBusinessException>(action);
            Assert.Equal(503, error.StatusCode);
            Assert.Equal("PREORDER_GATE_UNAVAILABLE", error.ErrorCode);
            Assert.DoesNotContain("Unexpected", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("legacy-unknown", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(PreorderActivationStatuses.Closed)]
    [InlineData(PreorderActivationStatuses.Cancelled)]
    public async Task 已关闭或取消批次即使位于当前时间窗也不阻塞门禁(string status)
    {
        await SeedProductAndStoreAsync();
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = $"historical-{status}",
            TemplateGuid = "template-1",
            PeriodNumber = 1,
            ActivationCode = $"PRE-HISTORICAL-{status}",
            TemplateNameSnapshot = "历史批次",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = status,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = $"historical-store-{status}",
            ActivationGuid = $"historical-{status}",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
        }).ExecuteCommandAsync();
        var service = CreateService("admin-user", manageOrders: true);

        var gate = await service.CheckAsync("S01");
        var active = await service.GetActiveAsync("S01");

        Assert.False(gate.IsBlocked);
        Assert.Empty(gate.Activations);
        Assert.False(active.NormalOrderBlocked);
        Assert.Empty(active.Activations);
    }

    [Fact]
    public async Task 原子门禁锁和查询异常统一返回脱敏503错误()
    {
        var lockError = await Assert.ThrowsAsync<PreorderBusinessException>(() =>
            PreorderGateEvaluator.AcquireDatabaseLockFailClosedAsync(
                _db,
                "Unsupported:resource",
                "store-1",
                "S01",
                NullLogger.Instance
            )
        );
        Assert.Equal(503, lockError.StatusCode);
        Assert.Equal("PREORDER_GATE_UNAVAILABLE", lockError.ErrorCode);
        Assert.DoesNotContain("Unsupported", lockError.Message, StringComparison.OrdinalIgnoreCase);

        _db.DbMaintenance.DropTable<Store>();
        var queryError = await Assert.ThrowsAsync<PreorderBusinessException>(() =>
            PreorderGateEvaluator.EvaluateFailClosedAsync(
                _db,
                "S01",
                Now,
                NullLogger.Instance
            )
        );
        Assert.Equal(503, queryError.StatusCode);
        Assert.Equal("PREORDER_GATE_UNAVAILABLE", queryError.ErrorCode);
        Assert.DoesNotContain("Store", queryError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 激活批次等待目标分店门禁锁并在完成后释放全部多店锁()
    {
        await SeedProductAndStoreAsync();
        await _db.Insertable(new Store
        {
            StoreGUID = "store-2",
            StoreCode = "S02",
            StoreName = "二店",
            IsActive = true,
        }).ExecuteCommandAsync();
        var service = CreateService("admin-user", manageOrders: true);
        var template = await service.CreateTemplateAsync(new SavePreorderTemplateDto
        {
            Name = "门禁锁模板",
            Items = new() { new() { ProductCode = "P1", MinimumOrderQuantity = 1 } },
            StoreGuids = new() { "store-1", "store-2" },
        });
        var heldLock = await PreorderMutationLock.AcquireProcessAsync("PreorderStoreGate:store-1");
        try
        {
            var activationTask = Task.Run(() => service.ActivateAsync(
                template.TemplateGuid,
                new ActivatePreorderTemplateDto
                {
                    ExpectedRevision = template.Revision,
                    StartAtUtc = Now.AddMinutes(-1),
                    EndAtUtc = Now.AddHours(1),
                }
            ));

            var firstCompleted = await Task.WhenAny(
                activationTask,
                Task.Delay(TimeSpan.FromSeconds(2))
            );
            Assert.NotSame(activationTask, firstCompleted);
            await heldLock.DisposeAsync();
            var activation = await activationTask;
            Assert.Equal(2, activation.TargetStoreCount);
        }
        finally
        {
            await heldLock.DisposeAsync();
        }

        Assert.Equal(0, PreorderMutationLock.ProcessLockCount);
    }

    [Fact]
    public async Task 激活大小写混合两店时数据库锁按规范化资源顺序取得()
    {
        await SeedProductAndStoreAsync();
        await _db.Insertable(new[]
        {
            new Store
            {
                StoreGUID = "a-Store",
                StoreCode = "S-A",
                StoreName = "大小写店 A",
                IsActive = true,
            },
            new Store
            {
                StoreGUID = "B-store",
                StoreCode = "S-B",
                StoreName = "大小写店 B",
                IsActive = true,
            },
        }).ExecuteCommandAsync();
        var service = CreateService("admin-user", manageOrders: true);
        var template = await service.CreateTemplateAsync(new SavePreorderTemplateDto
        {
            Name = "大小写锁序模板",
            Items = new() { new() { ProductCode = "P1", MinimumOrderQuantity = 1 } },
            StoreGuids = new() { "a-Store", "B-store" },
        });
        var databaseStoreLockKeys = new List<string>();
        _db.Aop.OnLogExecuting = (sql, parameters) =>
        {
            if (!sql.Contains("UPDATE \"Store\"", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            var key = parameters.FirstOrDefault(parameter =>
                parameter.ParameterName.Equals("@key", StringComparison.OrdinalIgnoreCase)
            )?.Value?.ToString();
            if (!string.IsNullOrWhiteSpace(key))
            {
                databaseStoreLockKeys.Add(key);
            }
        };
        try
        {
            await service.ActivateAsync(template.TemplateGuid, new ActivatePreorderTemplateDto
            {
                ExpectedRevision = template.Revision,
                StartAtUtc = Now.AddMinutes(-1),
                EndAtUtc = Now.AddHours(1),
            });
        }
        finally
        {
            _db.Aop.OnLogExecuting = null;
        }

        Assert.Equal(new[] { "a-store", "b-store" }, databaseStoreLockKeys);
    }

    [Fact]
    public async Task 激活等待StoreGate期间分店停用后重读失败且不写快照()
    {
        await SeedProductAndStoreAsync();
        var service = CreateService("admin-user", manageOrders: true);
        var template = await service.CreateTemplateAsync(new SavePreorderTemplateDto
        {
            Name = "激活分店竞态",
            Items = new() { new() { ProductCode = "P1", MinimumOrderQuantity = 1 } },
            StoreGuids = new() { "store-1" },
        });
        var initialStoreRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var observedInitialRead = false;
        _db.Aop.OnLogExecuted = (sql, _) =>
        {
            if (!observedInitialRead
                && sql.Contains("Store", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                observedInitialRead = true;
                initialStoreRead.TrySetResult();
            }
        };
        using var competingDb = CreateIndependentClient();
        var heldLock = await PreorderMutationLock.AcquireProcessAsync(
            "PreorderStoreGate:store-1"
        );
        Task<int>? deactivateTask = null;
        try
        {
            var activateTask = Task.Run(() => service.ActivateAsync(
                template.TemplateGuid,
                new ActivatePreorderTemplateDto
                {
                    ExpectedRevision = template.Revision,
                    StartAtUtc = Now.AddMinutes(-1),
                    EndAtUtc = Now.AddHours(1),
                }
            ));
            await initialStoreRead.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            deactivateTask = Task.Run(() => competingDb.Updateable<Store>()
                .SetColumns(item => item.IsActive == false)
                .Where(item => item.StoreGUID == "store-1")
                .ExecuteCommandAsync());
            // 激活此时只在等进程内 StoreGate、尚未开启数据库事务，停用不会被它阻塞。
            // 必须确认停用已提交再放开门禁：原先固定等 200ms，负载高时停用可能排到激活事务之后。
            Assert.Equal(1, await deactivateTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout));
            await heldLock.DisposeAsync();

            var error = await Assert.ThrowsAsync<PreorderBusinessException>(async () =>
                await activateTask
            );
            Assert.Equal("PREORDER_INVALID_REQUEST", error.ErrorCode);
        }
        finally
        {
            _db.Aop.OnLogExecuted = null;
            await heldLock.DisposeAsync();
            if (deactivateTask != null)
            {
                await deactivateTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            }
        }

        var activations = await _db.Queryable<PreorderActivation>()
            .Where(item => item.TemplateGuid == template.TemplateGuid)
            .ToListAsync();
        Assert.Empty(activations);
        Assert.False(await _db.Queryable<PreorderActivationStore>().AnyAsync());
    }

    [Fact]
    public async Task 默认分店锁前候选与模板数据库锁内集合不一致时拒绝激活()
    {
        await SeedProductAndStoreAsync();
        await _db.Insertable(new Store
        {
            StoreGUID = "store-2",
            StoreCode = "S02",
            StoreName = "二店",
            IsActive = true,
        }).ExecuteCommandAsync();
        var service = CreateService("admin-user", manageOrders: true);
        var template = await service.CreateTemplateAsync(new SavePreorderTemplateDto
        {
            Name = "默认分店竞态",
            Items = new() { new() { ProductCode = "P1", MinimumOrderQuantity = 1 } },
            StoreGuids = new() { "store-1" },
        });
        var candidateRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var observedCandidateRead = false;
        _db.Aop.OnLogExecuted = (sql, _) =>
        {
            if (!observedCandidateRead
                && sql.Contains("PreorderTemplateStore", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                observedCandidateRead = true;
                candidateRead.TrySetResult();
            }
        };
        using var competingDb = CreateIndependentClient();
        var heldLock = await PreorderMutationLock.AcquireProcessAsync(
            "PreorderStoreGate:store-1"
        );
        Task<int>? replaceDefaultTask = null;
        try
        {
            var activateTask = Task.Run(() => service.ActivateAsync(
                template.TemplateGuid,
                new ActivatePreorderTemplateDto
                {
                    ExpectedRevision = template.Revision,
                    StartAtUtc = Now.AddMinutes(-1),
                    EndAtUtc = Now.AddHours(1),
                }
            ));
            await candidateRead.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            replaceDefaultTask = Task.Run(() => competingDb
                .Updateable<PreorderTemplateStore>()
                .SetColumns(item => item.StoreGuid == "store-2")
                .Where(item => item.TemplateGuid == template.TemplateGuid)
                .ExecuteCommandAsync());
            Assert.Equal(1, await replaceDefaultTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout));
            await heldLock.DisposeAsync();

            var error = await Assert.ThrowsAsync<PreorderBusinessException>(async () =>
                await activateTask
            );
            Assert.Equal(409, error.StatusCode);
            Assert.Equal("PREORDER_TEMPLATE_CHANGED", error.ErrorCode);
        }
        finally
        {
            _db.Aop.OnLogExecuted = null;
            await heldLock.DisposeAsync();
            if (replaceDefaultTask != null)
            {
                await replaceDefaultTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            }
        }

        Assert.False(await _db.Queryable<PreorderActivation>()
            .AnyAsync(item => item.TemplateGuid == template.TemplateGuid));
        Assert.False(await _db.Queryable<PreorderActivationStore>().AnyAsync());
    }

    [Fact]
    public async Task Pda最终提交等待分店门禁锁并在激活出现后拒绝普通订单()
    {
        await SeedProductAndStoreAsync();
        await _db.Insertable(new WareHouseOrder
        {
            OrderGUID = "atomic-pda-order",
            StoreCode = "S01",
            OrderNo = "DRAFT-PDA",
            FlowStatus = 0,
        }).ExecuteCommandAsync();
        await _db.Insertable(new WareHouseOrderDetails
        {
            DetailGUID = "atomic-pda-detail",
            OrderGUID = "atomic-pda-order",
            StoreCode = "S01",
            ProductCode = "P1",
            Quantity = 1,
        }).ExecuteCommandAsync();
        var service = new PDAWarehouseOrderService(
            CreateSqlSugarContext(_db),
            Mock.Of<IMapper>(),
            NullLogger<PDAWarehouseOrderService>.Instance,
            Mock.Of<IWarehouseProductService>(),
            Mock.Of<IOrderNumberGenerator>()
        );
        var heldLock = await PreorderMutationLock.AcquireProcessAsync("PreorderStoreGate:store-1");
        try
        {
            var submitTask = Task.Run(() => service.SubmitOrderAsync(
                new SubmitPDAWarehouseOrderRequestDto { OrderGUID = "atomic-pda-order" },
                "S01",
                "device-1"
            ));
            var firstCompleted = await Task.WhenAny(submitTask, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.NotSame(submitTask, firstCompleted);

            await _db.Insertable(new PreorderActivation
            {
                ActivationGuid = "atomic-pda-activation",
                TemplateGuid = "atomic-pda-template",
                PeriodNumber = 1,
                ActivationCode = "PRE-ATOMIC-PDA",
                TemplateNameSnapshot = "原子门禁",
                SourceTemplateRevision = 1,
                StartAtUtc = DateTime.UtcNow.AddHours(-1),
                EndAtUtc = DateTime.UtcNow.AddHours(1),
                Status = PreorderActivationStatuses.Active,
            }).ExecuteCommandAsync();
            await _db.Insertable(new PreorderActivationStore
            {
                ActivationStoreGuid = "atomic-pda-store",
                ActivationGuid = "atomic-pda-activation",
                StoreGuid = "store-1",
                StoreCode = "S01",
                StoreName = "一店",
            }).ExecuteCommandAsync();
            await heldLock.DisposeAsync();

            var response = await submitTask;
            Assert.False(response.Success);
            Assert.Equal("PREORDER_REQUIRED", response.ErrorCode);
        }
        finally
        {
            await heldLock.DisposeAsync();
        }

        var persisted = await _db.Queryable<WareHouseOrder>()
            .FirstAsync(item => item.OrderGUID == "atomic-pda-order");
        Assert.Equal(0, persisted.FlowStatus);
    }

    [Fact]
    public async Task Pda持有StoreGuid锁后StoreCode改绑且目标店有Preorder时拒绝写入()
    {
        await SeedProductAndStoreAsync();
        await _db.Insertable(new WareHouseOrder
        {
            OrderGUID = "pda-code-rebind-order",
            StoreCode = "S01",
            OrderNo = "DRAFT-PDA-REBIND",
            FlowStatus = 0,
        }).ExecuteCommandAsync();
        await _db.Insertable(new WareHouseOrderDetails
        {
            DetailGUID = "pda-code-rebind-detail",
            OrderGUID = "pda-code-rebind-order",
            StoreCode = "S01",
            ProductCode = "P1",
            Quantity = 1,
        }).ExecuteCommandAsync();
        var service = new PDAWarehouseOrderService(
            CreateSqlSugarContext(_db),
            Mock.Of<IMapper>(),
            NullLogger<PDAWarehouseOrderService>.Instance,
            Mock.Of<IWarehouseProductService>(),
            Mock.Of<IOrderNumberGenerator>()
        );
        var resolverRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var observedResolverRead = false;
        _db.Aop.OnLogExecuted = (sql, _) =>
        {
            if (!observedResolverRead
                && sql.Contains("Store", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                observedResolverRead = true;
                resolverRead.TrySetResult();
            }
        };
        var heldLock = await PreorderMutationLock.AcquireProcessAsync(
            "PreorderStoreGate:store-1"
        );
        try
        {
            var submitTask = Task.Run(() => service.SubmitOrderAsync(
                new SubmitPDAWarehouseOrderRequestDto
                {
                    OrderGUID = "pda-code-rebind-order",
                },
                "S01",
                "device-1"
            ));
            await resolverRead.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

            await _db.Updateable<Store>()
                .SetColumns(item => item.StoreCode == "OLD")
                .Where(item => item.StoreGUID == "store-1")
                .ExecuteCommandAsync();
            await _db.Insertable(new Store
            {
                StoreGUID = "store-2",
                StoreCode = "S01",
                StoreName = "重用编码分店",
                IsActive = true,
            }).ExecuteCommandAsync();
            await _db.Insertable(new PreorderActivation
            {
                ActivationGuid = "pda-rebind-target-activation",
                TemplateGuid = "pda-rebind-target-template",
                PeriodNumber = 1,
                ActivationCode = "PRE-PDA-REBIND",
                TemplateNameSnapshot = "改绑目标店批次",
                SourceTemplateRevision = 1,
                StartAtUtc = DateTime.UtcNow.AddHours(-1),
                EndAtUtc = DateTime.UtcNow.AddHours(1),
                Status = PreorderActivationStatuses.Active,
            }).ExecuteCommandAsync();
            await _db.Insertable(new PreorderActivationStore
            {
                ActivationStoreGuid = "pda-rebind-target-store",
                ActivationGuid = "pda-rebind-target-activation",
                StoreGuid = "store-2",
                StoreCode = "S01",
                StoreName = "重用编码分店",
            }).ExecuteCommandAsync();
            await heldLock.DisposeAsync();

            var response = await submitTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            Assert.False(response.Success);
            Assert.Equal("PREORDER_STORE_IDENTITY_CHANGED", response.ErrorCode);
        }
        finally
        {
            _db.Aop.OnLogExecuted = null;
            await heldLock.DisposeAsync();
        }

        var persisted = await _db.Queryable<WareHouseOrder>()
            .FirstAsync(item => item.OrderGUID == "pda-code-rebind-order");
        Assert.Equal(0, persisted.FlowStatus);
    }

    [Fact]
    public async Task Pda跨店最终提交在绑定店有Preorder时仍优先返回403业务错误()
    {
        await SeedProductAndStoreAsync();
        await SeedActiveTargetAsync();
        await _db.Insertable(new WareHouseOrder
        {
            OrderGUID = "cross-store-pda-order",
            StoreCode = "S02",
            OrderNo = "DRAFT-CROSS-STORE",
            FlowStatus = 0,
        }).ExecuteCommandAsync();
        var service = new PDAWarehouseOrderService(
            CreateSqlSugarContext(_db),
            Mock.Of<IMapper>(),
            NullLogger<PDAWarehouseOrderService>.Instance,
            Mock.Of<IWarehouseProductService>(),
            Mock.Of<IOrderNumberGenerator>()
        );

        var response = await service.SubmitOrderAsync(
            new SubmitPDAWarehouseOrderRequestDto { OrderGUID = "cross-store-pda-order" },
            "S01",
            "device-1"
        );

        Assert.False(response.Success);
        Assert.Equal("PDA_ORDER_STORE_MISMATCH", response.ErrorCode);
        var persisted = await _db.Queryable<WareHouseOrder>()
            .FirstAsync(item => item.OrderGUID == "cross-store-pda-order");
        Assert.Equal(0, persisted.FlowStatus);
    }

    [Fact]
    public async Task 激活拒绝已经结束的有效期()
    {
        await SeedProductAndStoreAsync();
        var service = CreateService("admin-user", manageOrders: true);
        var template = await service.CreateTemplateAsync(new SavePreorderTemplateDto
        {
            Name = "过期批次",
            Items = new() { new() { ProductCode = "P1", MinimumOrderQuantity = 1 } },
            StoreGuids = new() { "store-1" },
        });

        var error = await Assert.ThrowsAsync<BlazorApp.Api.Interfaces.React.PreorderBusinessException>(
            () => service.ActivateAsync(template.TemplateGuid, new ActivatePreorderTemplateDto
            {
                StartAtUtc = Now.AddHours(-2),
                EndAtUtc = Now,
            })
        );

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("PREORDER_INVALID_REQUEST", error.ErrorCode);
    }

    [Fact]
    public async Task ResolveItems按ItemNumber大小写精确匹配()
    {
        await SeedProductAndStoreAsync();
        var service = CreateService("admin-user", manageOrders: true);

        var result = await service.ResolveItemsAsync(new ResolvePreorderItemsRequestDto
        {
            Rows = new() { new() { LineNumber = 1, ItemNumber = "i1", MinimumOrderQuantity = 1 } },
        });

        var row = Assert.Single(result.Rows);
        Assert.Equal("NotFound", row.Status);
        Assert.Equal("PREORDER_ITEM_NOT_FOUND", row.ErrorCode);
    }

    [Fact]
    public async Task ResolveItems自动合并相同货号和MOQ并拒绝不同MOQ()
    {
        await SeedProductAndStoreAsync();
        var service = CreateService("admin-user", manageOrders: true);

        var merged = await service.ResolveItemsAsync(new ResolvePreorderItemsRequestDto
        {
            Rows = new()
            {
                new() { LineNumber = 1, ItemNumber = "I1", MinimumOrderQuantity = 2 },
                new() { LineNumber = 2, ItemNumber = "I1", MinimumOrderQuantity = 2 },
            },
        });
        Assert.Single(merged.Rows);
        Assert.Equal(1, merged.Rows[0].LineNumber);
        Assert.Equal("Resolved", merged.Rows[0].Status);

        var conflict = await service.ResolveItemsAsync(new ResolvePreorderItemsRequestDto
        {
            Rows = new()
            {
                new() { LineNumber = 1, ItemNumber = "I1", MinimumOrderQuantity = 2 },
                new() { LineNumber = 2, ItemNumber = "I1", MinimumOrderQuantity = 3 },
            },
        });
        Assert.Equal(2, conflict.Rows.Count);
        Assert.All(conflict.Rows, row => Assert.Equal("PREORDER_MOQ_CONFLICT", row.ErrorCode));
    }

    [Fact]
    public async Task 已提交订单在批次到期后重试仍幂等返回原订单()
    {
        var logger = new CollectingLogger<PreorderReactService>();
        await SeedProductAndStoreAsync();
        await _db.Insertable(new UserStore
        {
            UserStoreGUID = "submit-user-store",
            UserGUID = "submit-user",
            StoreGUID = "store-1",
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "expired-activation",
            TemplateGuid = "expired-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-EXPIRED",
            TemplateNameSnapshot = "已结束",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddDays(-2),
            EndAtUtc = Now.AddDays(-1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = "expired-store",
            ActivationGuid = "expired-activation",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderWarehouseOrder
        {
            OrderGuid = "submitted-order",
            ActivationGuid = "expired-activation",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
            OrderNo = "PRE-EXPIRED-S01",
            Status = PreorderWarehouseOrderStatuses.Submitted,
            DraftRevision = 2,
        }).ExecuteCommandAsync();

        var result = await CreateService(
            "submit-user",
            manageOrders: false,
            logger: logger
        ).SubmitAsync(
            "expired-activation",
            new SubmitPreorderDto { StoreCode = "S01", ExpectedDraftRevision = 0 }
        );

        Assert.Equal("submitted-order", result.OrderGuid);
        Assert.Equal(PreorderWarehouseOrderStatuses.Submitted, result.Status);
        var telemetry = AssertSubmissionTelemetry(logger, "idempotent");
        Assert.Matches("^[a-f0-9]{32}$", Assert.IsType<string>(telemetry["SubmissionId"]));
        Assert.Equal(0, telemetry["ActivationItemCount"]);
        Assert.Equal(0, telemetry["OrderItemCount"]);
        Assert.Equal(9, telemetry["SqlRoundTrips"]);
        Assert.Equal(0, telemetry["PersistSqlRoundTrips"]);
        Assert.Equal(0, telemetry["FinalTransitionSqlRoundTrips"]);
    }

    [Fact]
    public async Task 提交与已保存草稿完全一致时保留Revision和明细标识()
    {
        var logger = new CollectingLogger<PreorderReactService>();
        await SeedProductAndStoreAsync();
        await _db.Insertable(new UserStore
        {
            UserStoreGUID = "unchanged-submit-user-store",
            UserGUID = "unchanged-submit-user",
            StoreGUID = "store-1",
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "unchanged-submit-activation",
            TemplateGuid = "unchanged-submit-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-UNCHANGED-SUBMIT",
            TemplateNameSnapshot = "提交复用草稿",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = "unchanged-submit-store",
            ActivationGuid = "unchanged-submit-activation",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationItem
        {
            ActivationItemGuid = "unchanged-submit-item",
            ActivationGuid = "unchanged-submit-activation",
            ProductCode = "P1",
            ItemNumber = "I1",
            ProductName = "商品",
            MinimumOrderQuantity = 2,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderWarehouseOrder
        {
            OrderGuid = "unchanged-submit-order",
            ActivationGuid = "unchanged-submit-activation",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
            OrderNo = "PRE-UNCHANGED-SUBMIT-S01",
            Status = PreorderWarehouseOrderStatuses.Draft,
            DraftRevision = 7,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderWarehouseOrderItem
        {
            OrderItemGuid = "unchanged-submit-detail",
            OrderGuid = "unchanged-submit-order",
            ActivationItemGuid = "unchanged-submit-item",
            ProductCode = "P1",
            ItemNumber = "I1",
            ProductName = "商品",
            MinimumOrderQuantity = 2,
            PackCount = 3,
            OrderedQuantity = 6,
        }).ExecuteCommandAsync();

        var result = await CreateService(
            "unchanged-submit-user",
            manageOrders: false,
            logger: logger,
            submissionId: "mobile-submit-123"
        ).SubmitAsync(
            "unchanged-submit-activation",
            new SubmitPreorderDto
            {
                StoreCode = "S01",
                ExpectedDraftRevision = 7,
                Items = new()
                {
                    new()
                    {
                        ActivationItemGuid = "unchanged-submit-item",
                        PackCount = 3,
                    },
                },
            }
        );

        var persistedOrder = await _db.Queryable<PreorderWarehouseOrder>()
            .SingleAsync(item => item.OrderGuid == "unchanged-submit-order");
        var persistedItem = await _db.Queryable<PreorderWarehouseOrderItem>()
            .SingleAsync(item => item.OrderGuid == "unchanged-submit-order");
        Assert.Equal(PreorderWarehouseOrderStatuses.Submitted, result.Status);
        Assert.Equal(7, result.DraftRevision);
        Assert.Equal(7, persistedOrder.DraftRevision);
        Assert.Equal("unchanged-submit-detail", persistedItem.OrderItemGuid);
        var telemetry = AssertSubmissionTelemetry(logger, "fast");
        Assert.Equal("mobile-submit-123", telemetry["SubmissionId"]);
        Assert.Equal(1, telemetry["ActivationItemCount"]);
        Assert.Equal(1, telemetry["OrderItemCount"]);
        Assert.Equal(11, telemetry["SqlRoundTrips"]);
        Assert.Equal(0, telemetry["PersistSqlRoundTrips"]);
        Assert.Equal(1, telemetry["FinalTransitionSqlRoundTrips"]);
    }

    [Fact]
    public async Task 提交相同份数但草稿规范字段异常时重建明细()
    {
        var seed = await SeedSubmitDraftAsync(
            "invalid-canonical-submit",
            storedPackCount: 3,
            storedOrderedQuantity: 999
        );

        var result = await CreateService(seed.UserGuid, manageOrders: false).SubmitAsync(
            seed.ActivationGuid,
            new SubmitPreorderDto
            {
                StoreCode = "S01",
                ExpectedDraftRevision = 7,
                Items = new()
                {
                    new()
                    {
                        ActivationItemGuid = seed.ActivationItemGuid,
                        PackCount = 3,
                    },
                },
            }
        );

        var persistedOrder = await _db.Queryable<PreorderWarehouseOrder>()
            .SingleAsync(item => item.OrderGuid == seed.OrderGuid);
        var persistedItem = await _db.Queryable<PreorderWarehouseOrderItem>()
            .SingleAsync(item => item.OrderGuid == seed.OrderGuid);
        Assert.Equal(PreorderWarehouseOrderStatuses.Submitted, result.Status);
        Assert.Equal(8, persistedOrder.DraftRevision);
        Assert.NotEqual(seed.OrderItemGuid, persistedItem.OrderItemGuid);
        Assert.Equal(6, persistedItem.OrderedQuantity);
    }

    [Fact]
    public async Task 提交Revision相同但份数变化时仍走Upsert()
    {
        var logger = new CollectingLogger<PreorderReactService>();
        var seed = await SeedSubmitDraftAsync(
            "changed-pack-submit",
            storedPackCount: 3,
            storedOrderedQuantity: 6
        );

        var result = await CreateService(
            seed.UserGuid,
            manageOrders: false,
            logger: logger,
            submissionId: "mobile-submit-slow"
        ).SubmitAsync(
            seed.ActivationGuid,
            new SubmitPreorderDto
            {
                StoreCode = "S01",
                ExpectedDraftRevision = 7,
                Items = new()
                {
                    new()
                    {
                        ActivationItemGuid = seed.ActivationItemGuid,
                        PackCount = 4,
                    },
                },
            }
        );

        var persistedOrder = await _db.Queryable<PreorderWarehouseOrder>()
            .SingleAsync(item => item.OrderGuid == seed.OrderGuid);
        var persistedItem = await _db.Queryable<PreorderWarehouseOrderItem>()
            .SingleAsync(item => item.OrderGuid == seed.OrderGuid);
        Assert.Equal(PreorderWarehouseOrderStatuses.Submitted, result.Status);
        Assert.Equal(8, persistedOrder.DraftRevision);
        Assert.NotEqual(seed.OrderItemGuid, persistedItem.OrderItemGuid);
        Assert.Equal(4, persistedItem.PackCount);
        Assert.Equal(8, persistedItem.OrderedQuantity);
        Assert.Equal(10m, persistedItem.ImportAmount);
        Assert.Equal(20m, persistedItem.RetailAmount);
        var telemetry = AssertSubmissionTelemetry(logger, "slow");
        Assert.Equal("mobile-submit-slow", telemetry["SubmissionId"]);
        Assert.Equal(1, telemetry["ActivationItemCount"]);
        Assert.Equal(1, telemetry["OrderItemCount"]);
        Assert.Equal(14, telemetry["SqlRoundTrips"]);
        Assert.Equal(3, telemetry["PersistSqlRoundTrips"]);
        Assert.Equal(1, telemetry["FinalTransitionSqlRoundTrips"]);
    }

    [Theory]
    [InlineData("unsafe\r\nsubmission")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task 提交性能ID拒绝不安全或超长Header(string unsafeSubmissionId)
    {
        var logger = new CollectingLogger<PreorderReactService>();
        var seed = await SeedSubmitDraftAsync(
            $"unsafe-id-{Guid.NewGuid():N}",
            storedPackCount: 3,
            storedOrderedQuantity: 6
        );
        await _db.Updateable<PreorderWarehouseOrder>()
            .SetColumns(item => item.Status == PreorderWarehouseOrderStatuses.Submitted)
            .Where(item => item.OrderGuid == seed.OrderGuid)
            .ExecuteCommandAsync();

        await CreateService(
            seed.UserGuid,
            manageOrders: false,
            logger: logger,
            submissionId: unsafeSubmissionId
        ).SubmitAsync(
            seed.ActivationGuid,
            new SubmitPreorderDto { StoreCode = "S01", ExpectedDraftRevision = 0 }
        );

        var telemetry = AssertSubmissionTelemetry(logger, "idempotent");
        var actualSubmissionId = Assert.IsType<string>(telemetry["SubmissionId"]);
        Assert.Matches("^[a-f0-9]{32}$", actualSubmissionId);
        Assert.NotEqual(unsafeSubmissionId, actualSubmissionId);
    }

    [Fact]
    public async Task 提交失败仍记录一次脱敏性能摘要()
    {
        var logger = new CollectingLogger<PreorderReactService>();
        var seed = await SeedSubmitDraftAsync(
            "failed-submit-telemetry",
            storedPackCount: 3,
            storedOrderedQuantity: 6
        );

        var error = await Assert.ThrowsAsync<PreorderBusinessException>(() => CreateService(
            seed.UserGuid,
            manageOrders: false,
            logger: logger,
            submissionId: "failed-submit-123"
        ).SubmitAsync(
            seed.ActivationGuid,
            new SubmitPreorderDto
            {
                StoreCode = "S01",
                ExpectedDraftRevision = 7,
                Items = new()
                {
                    new()
                    {
                        ActivationItemGuid = seed.ActivationItemGuid,
                        PackCount = 0,
                    },
                },
            }
        ));

        Assert.Equal("PREORDER_CONFIRM_NO_DEMAND_REQUIRED", error.ErrorCode);
        var telemetry = AssertSubmissionTelemetry(logger, "failed");
        Assert.Equal("failed-submit-123", telemetry["SubmissionId"]);
        Assert.Equal(1, telemetry["ActivationItemCount"]);
        Assert.Equal(1, telemetry["OrderItemCount"]);
        Assert.DoesNotContain(telemetry.Keys, key => key.Contains("Product", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(telemetry.Keys, key => key.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task 提交慢路径在锁内复用已加载的激活商品订单和持久化明细()
    {
        var logger = new CollectingLogger<PreorderReactService>();
        var seed = await SeedSubmitDraftAsync(
            "submit-query-reuse",
            storedPackCount: 3,
            storedOrderedQuantity: 6
        );
        var selectCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var observedSelects = new List<string>();
        var storeAuthorizationJoinCount = 0;
        var observedSqlRoundTrips = 0;
        _db.Aop.OnLogExecuting = (sql, _) =>
        {
            observedSqlRoundTrips++;
            if (!sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            observedSelects.Add(sql);
            if (sql.Contains("FROM `Store`", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("JOIN `UserStore`", StringComparison.OrdinalIgnoreCase))
            {
                storeAuthorizationJoinCount++;
            }
            foreach (var table in new[]
            {
                "PreorderActivationItem",
                "PreorderWarehouseOrderItem",
                "PreorderWarehouseOrder",
                "Store",
                "UserStore",
            })
            {
                if (sql.Contains($"FROM `{table}`", StringComparison.OrdinalIgnoreCase))
                {
                    selectCounts[table] = selectCounts.GetValueOrDefault(table) + 1;
                }
            }
        };
        try
        {
            await CreateService(
                seed.UserGuid,
                manageOrders: false,
                logger: logger
            ).SubmitAsync(
                seed.ActivationGuid,
                new SubmitPreorderDto
                {
                    StoreCode = "S01",
                    ExpectedDraftRevision = 7,
                    Items = new()
                    {
                        new() { ActivationItemGuid = seed.ActivationItemGuid, PackCount = 4 },
                    },
                }
            );
        }
        finally
        {
            _db.Aop.OnLogExecuting = null;
        }

        var evidence = string.Join("\n---\n", observedSelects);
        Assert.True(
            selectCounts.GetValueOrDefault("PreorderActivationItem") == 1,
            $"PreorderActivationItem SELECT 次数不符：\n{evidence}"
        );
        Assert.True(
            selectCounts.GetValueOrDefault("PreorderWarehouseOrderItem") == 1,
            $"PreorderWarehouseOrderItem SELECT 次数不符：\n{evidence}"
        );
        Assert.True(
            selectCounts.GetValueOrDefault("PreorderWarehouseOrder") == 1,
            $"PreorderWarehouseOrder SELECT 次数不符：\n{evidence}"
        );
        Assert.True(
            selectCounts.GetValueOrDefault("Store") == 2,
            $"锁前和锁内应各执行一次 Store 授权查询：\n{evidence}"
        );
        Assert.True(
            selectCounts.GetValueOrDefault("UserStore") == 0,
            $"UserStore 不应再独立往返：\n{evidence}"
        );
        Assert.True(
            storeAuthorizationJoinCount == 2,
            $"两次 Store 读取都必须在同一 SQL 中完成 UserStore 授权：\n{evidence}"
        );
        var telemetry = AssertSubmissionTelemetry(logger, "slow");
        Assert.Equal(observedSqlRoundTrips, telemetry["SqlRoundTrips"]);
    }

    [Fact]
    public async Task 保存草稿复用事务内激活商品订单和新明细构建返回结果()
    {
        var seed = await SeedSubmitDraftAsync(
            "save-query-reuse",
            storedPackCount: 3,
            storedOrderedQuantity: 6
        );
        var activationItemSelects = 0;
        var orderItemSelects = 0;
        _db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("FROM `PreorderActivationItem`", StringComparison.OrdinalIgnoreCase))
            {
                activationItemSelects++;
            }
            if (sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("FROM `PreorderWarehouseOrderItem`", StringComparison.OrdinalIgnoreCase))
            {
                orderItemSelects++;
            }
        };
        PreorderActivationDetailDto saved;
        try
        {
            saved = await CreateService(seed.UserGuid, manageOrders: false).SaveDraftAsync(
                seed.ActivationGuid,
                new SavePreorderDraftDto
                {
                    StoreCode = "S01",
                    ExpectedDraftRevision = 7,
                    Items = new()
                    {
                        new() { ActivationItemGuid = seed.ActivationItemGuid, PackCount = 4 },
                    },
                }
            );
        }
        finally
        {
            _db.Aop.OnLogExecuting = null;
        }

        Assert.Equal(8, saved.DraftRevision);
        Assert.Equal(4, Assert.Single(saved.Items).PackCount);
        Assert.Equal(1, activationItemSelects);
        Assert.Equal(0, orderItemSelects);
    }

    [Fact]
    public async Task 提交过期Revision仍返回草稿冲突()
    {
        var seed = await SeedSubmitDraftAsync(
            "stale-revision-submit",
            storedPackCount: 3,
            storedOrderedQuantity: 6
        );

        var error = await Assert.ThrowsAsync<PreorderBusinessException>(() =>
            CreateService(seed.UserGuid, manageOrders: false).SubmitAsync(
                seed.ActivationGuid,
                new SubmitPreorderDto
                {
                    StoreCode = "S01",
                    ExpectedDraftRevision = 6,
                    Items = new()
                    {
                        new()
                        {
                            ActivationItemGuid = seed.ActivationItemGuid,
                            PackCount = 3,
                        },
                    },
                }
            )
        );

        var persistedOrder = await _db.Queryable<PreorderWarehouseOrder>()
            .SingleAsync(item => item.OrderGuid == seed.OrderGuid);
        var persistedItem = await _db.Queryable<PreorderWarehouseOrderItem>()
            .SingleAsync(item => item.OrderGuid == seed.OrderGuid);
        Assert.Equal(StatusCodes.Status409Conflict, error.StatusCode);
        Assert.Equal("PREORDER_DRAFT_CONFLICT", error.ErrorCode);
        Assert.Equal(PreorderWarehouseOrderStatuses.Draft, persistedOrder.Status);
        Assert.Equal(7, persistedOrder.DraftRevision);
        Assert.Equal(seed.OrderItemGuid, persistedItem.OrderItemGuid);
    }

    [Fact]
    public async Task Preorder提交等待目标StoreGate且完成后才解除普通提交门禁并保持独立订单()
    {
        await SeedProductAndStoreAsync();
        await _db.Insertable(new UserStore
        {
            UserStoreGUID = "atomic-submit-user-store",
            UserGUID = "atomic-submit-user",
            StoreGUID = "store-1",
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "atomic-preorder-submit",
            TemplateGuid = "atomic-preorder-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-ATOMIC-SUBMIT",
            TemplateNameSnapshot = "提交门禁锁",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = "atomic-preorder-submit-store",
            ActivationGuid = "atomic-preorder-submit",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationItem
        {
            ActivationItemGuid = "atomic-preorder-submit-item",
            ActivationGuid = "atomic-preorder-submit",
            ProductCode = "P1",
            ItemNumber = "I1",
            ProductName = "商品",
            MinimumOrderQuantity = 2,
        }).ExecuteCommandAsync();
        await _db.Insertable(new WareHouseOrder
        {
            OrderGUID = "ordinary-order-sentinel",
            StoreCode = "S01",
            OrderNo = "ORDINARY-DRAFT",
            FlowStatus = 0,
        }).ExecuteCommandAsync();
        await _db.Insertable(new WareHouseOrderDetails
        {
            DetailGUID = "ordinary-detail-sentinel",
            OrderGUID = "ordinary-order-sentinel",
            StoreCode = "S01",
            ProductCode = "P1",
            Quantity = 1,
        }).ExecuteCommandAsync();

        var service = CreateService("atomic-submit-user", manageOrders: false);
        var request = new SubmitPreorderDto
        {
            StoreCode = "S01",
            ExpectedDraftRevision = 0,
            Items = new()
            {
                new()
                {
                    ActivationItemGuid = "atomic-preorder-submit-item",
                    PackCount = 3,
                },
            },
        };
        IAsyncDisposable? heldLock = await PreorderMutationLock.AcquireProcessAsync(
            "PreorderStoreGate:store-1"
        );
        try
        {
            var submitTask = Task.Run(() => service.SubmitAsync(
                "atomic-preorder-submit",
                request
            ));
            var firstCompleted = await Task.WhenAny(
                submitTask,
                Task.Delay(TimeSpan.FromMilliseconds(500))
            );
            Assert.NotSame(submitTask, firstCompleted);
            Assert.True((await service.CheckAsync("S01")).IsBlocked);
            Assert.False(await _db.Queryable<PreorderWarehouseOrder>()
                .AnyAsync(item => item.ActivationGuid == "atomic-preorder-submit"));

            await heldLock.DisposeAsync();
            heldLock = null;
            var submitted = await submitTask;
            Assert.Equal(PreorderWarehouseOrderStatuses.Submitted, submitted.Status);
            Assert.False((await service.CheckAsync("S01")).IsBlocked);

            // 重复请求即使仍带旧 revision，也只幂等返回同一张 Preorder 独立订单。
            var retry = await service.SubmitAsync("atomic-preorder-submit", request);
            Assert.Equal(submitted.OrderGuid, retry.OrderGuid);
            Assert.Equal(1, await _db.Queryable<PreorderWarehouseOrder>()
                .CountAsync(item => item.ActivationGuid == "atomic-preorder-submit"));
        }
        finally
        {
            if (heldLock != null)
            {
                await heldLock.DisposeAsync();
            }
        }

        var ordinaryOrder = await _db.Queryable<WareHouseOrder>()
            .FirstAsync(item => item.OrderGUID == "ordinary-order-sentinel");
        Assert.Equal(0, ordinaryOrder.FlowStatus);
        Assert.Equal(1, await _db.Queryable<WareHouseOrder>().CountAsync());
        Assert.Equal(1, await _db.Queryable<WareHouseOrderDetails>().CountAsync());
        Assert.Equal(0, PreorderMutationLock.ProcessLockCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preorder提交对空或歧义目标分店快照FailClosed(bool duplicateTarget)
    {
        await SeedProductAndStoreAsync();
        await _db.Insertable(new UserStore
        {
            UserStoreGUID = "invalid-target-user-store",
            UserGUID = "invalid-target-user",
            StoreGUID = "store-1",
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "invalid-target-submit",
            TemplateGuid = "invalid-target-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-INVALID-TARGET",
            TemplateNameSnapshot = "异常目标快照",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        if (duplicateTarget)
        {
            // 模拟迁移前损坏数据；服务层仍必须在唯一索引之外独立 fail-closed。
            await _db.Ado.ExecuteCommandAsync(
                "DROP INDEX IF EXISTS \"UX_PreorderActivationStore_Activation_Store\""
            );
            await _db.Insertable(new[]
            {
                new PreorderActivationStore
                {
                    ActivationStoreGuid = "invalid-target-1",
                    ActivationGuid = "invalid-target-submit",
                    StoreGuid = "store-1",
                    StoreCode = "S01",
                    StoreName = "一店",
                },
                new PreorderActivationStore
                {
                    ActivationStoreGuid = "invalid-target-2",
                    ActivationGuid = "invalid-target-submit",
                    StoreGuid = "STORE-1",
                    StoreCode = "S01",
                    StoreName = "重复一店",
                },
            }).ExecuteCommandAsync();
        }

        var error = await Assert.ThrowsAsync<PreorderBusinessException>(() =>
            CreateService("invalid-target-user", manageOrders: false).SubmitAsync(
                "invalid-target-submit",
                new SubmitPreorderDto { StoreCode = "S01" }
            )
        );

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, error.StatusCode);
        Assert.Equal("PREORDER_GATE_UNAVAILABLE", error.ErrorCode);
        Assert.False(await _db.Queryable<PreorderWarehouseOrder>()
            .AnyAsync(item => item.ActivationGuid == "invalid-target-submit"));
    }

    [Fact]
    public async Task 导出分店商品矩阵按快照商品和自然分店顺序汇总有效数量()
    {
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "export-activation",
            TemplateGuid = "export-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-EXPORT",
            TemplateNameSnapshot = "导出",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationItem[]
        {
            new()
            {
                ActivationItemGuid = "export-item-first",
                ActivationGuid = "export-activation",
                ProductCode = "P1",
                ItemNumber = "I1",
                ProductName = "快照商品一",
                MinimumOrderQuantity = 4,
                SortOrder = 1,
                ImportPrice = 1.25m,
                RetailPrice = 2.50m,
            },
            new()
            {
                ActivationItemGuid = "export-item-second",
                ActivationGuid = "export-activation",
                ProductCode = "P2",
                ItemNumber = "I2",
                ProductName = "快照商品二",
                MinimumOrderQuantity = 6,
                SortOrder = 10,
                ImportPrice = 4.75m,
                RetailPrice = 9.25m,
            },
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore[]
        {
            new() { ActivationStoreGuid = "export-store-s2", ActivationGuid = "export-activation", StoreGuid = "s2", StoreCode = "S2", StoreName = "同名" },
            new() { ActivationStoreGuid = "export-store-s10", ActivationGuid = "export-activation", StoreGuid = "s10", StoreCode = "S10", StoreName = "同名" },
            new() { ActivationStoreGuid = "export-store-s20", ActivationGuid = "export-activation", StoreGuid = "s20", StoreCode = "S20", StoreName = "" },
            new() { ActivationStoreGuid = "export-store-draft", ActivationGuid = "export-activation", StoreGuid = "sd", StoreCode = "S3", StoreName = "草稿店" },
            new() { ActivationStoreGuid = "export-store-no-demand", ActivationGuid = "export-activation", StoreGuid = "sn", StoreCode = "S4", StoreName = "无需求店" },
            new() { ActivationStoreGuid = "export-store-cancelled", ActivationGuid = "export-activation", StoreGuid = "sc", StoreCode = "S5", StoreName = "取消店" },
        }).ExecuteCommandAsync();
        var orders = new[]
        {
            new PreorderWarehouseOrder { OrderGuid = "export-processing", ActivationGuid = "export-activation", StoreGuid = "s2", StoreCode = "S2", StoreName = "同名", OrderNo = "PRE-EXPORT-S2", Status = PreorderWarehouseOrderStatuses.Processing, SubmittedAtUtc = Now },
            new PreorderWarehouseOrder { OrderGuid = "export-submitted", ActivationGuid = "export-activation", StoreGuid = "s10", StoreCode = "S10", StoreName = "同名", OrderNo = "PRE-EXPORT-S10", Status = PreorderWarehouseOrderStatuses.Submitted, SubmittedAtUtc = Now },
            new PreorderWarehouseOrder { OrderGuid = "export-completed", ActivationGuid = "export-activation", StoreGuid = "s20", StoreCode = "S20", StoreName = "", OrderNo = "PRE-EXPORT-S20", Status = PreorderWarehouseOrderStatuses.Completed, SubmittedAtUtc = Now },
            new PreorderWarehouseOrder { OrderGuid = "export-draft", ActivationGuid = "export-activation", StoreGuid = "sd", StoreCode = "S3", StoreName = "草稿店", OrderNo = "PRE-EXPORT-S3", Status = PreorderWarehouseOrderStatuses.Draft, SubmittedAtUtc = Now },
            new PreorderWarehouseOrder { OrderGuid = "export-no-demand", ActivationGuid = "export-activation", StoreGuid = "sn", StoreCode = "S4", StoreName = "无需求店", OrderNo = "PRE-EXPORT-S4", Status = PreorderWarehouseOrderStatuses.NoDemand, SubmittedAtUtc = Now },
            new PreorderWarehouseOrder { OrderGuid = "export-cancelled", ActivationGuid = "export-activation", StoreGuid = "sc", StoreCode = "S5", StoreName = "取消店", OrderNo = "PRE-EXPORT-S5", Status = PreorderWarehouseOrderStatuses.Cancelled, SubmittedAtUtc = Now },
        };
        await _db.Insertable(orders).ExecuteCommandAsync();

        PreorderWarehouseOrderItem Detail(
            string orderGuid,
            string activationItemGuid,
            int packCount,
            int orderedQuantity
        ) => new()
        {
            OrderItemGuid = $"{orderGuid}-{activationItemGuid}",
            OrderGuid = orderGuid,
            ActivationItemGuid = activationItemGuid,
            ProductCode = activationItemGuid == "export-item-first" ? "P1" : "P2",
            ItemNumber = activationItemGuid == "export-item-first" ? "I1" : "I2",
            // 故意与激活快照不同，防止导出回读可变订单行价格或名称。
            ProductName = "订单行旧名称",
            MinimumOrderQuantity = 99,
            PackCount = packCount,
            OrderedQuantity = orderedQuantity,
            ImportPrice = 99m,
            RetailPrice = 199m,
            ImportAmount = 999m,
            RetailAmount = 1999m,
        };
        await _db.Insertable(new[]
        {
            Detail("export-processing", "export-item-first", 9, 0),
            Detail("export-processing", "export-item-second", 1, 6),
            // 有效明细 GUID 与激活快照仅大小写不同，矩阵仍应按同一商品匹配数量。
            Detail("export-submitted", "EXPORT-ITEM-FIRST", 1, 4),
            Detail("export-submitted", "export-item-second", 1, 6),
            Detail("export-completed", "export-item-first", 2, 8),
            Detail("export-completed", "export-item-second", 1, 6),
            Detail("export-draft", "export-item-first", 1, 4),
            Detail("export-draft", "export-item-second", 1, 6),
            Detail("export-no-demand", "export-item-first", 1, 4),
            Detail("export-no-demand", "export-item-second", 1, 6),
            Detail("export-cancelled", "export-item-first", 1, 4),
            Detail("export-cancelled", "export-item-second", 1, 6),
        }).ExecuteCommandAsync();

        var file = await CreateService("admin-user", manageOrders: true).ExportAsync("export-activation");
        using var stream = new MemoryStream(file.Content);
        using var workbook = new XLWorkbook(stream);
        var productSummary = workbook.Worksheet("商品汇总");
        var ordersSheet = workbook.Worksheet("分店订单");
        var sheet = workbook.Worksheet("分店商品明细");

        Assert.Equal(new[] { "商品汇总", "分店订单", "分店商品明细", "未提交分店" },
            workbook.Worksheets.Select(item => item.Name));
        Assert.Equal("总数量", productSummary.Cell(1, 6).GetString());
        Assert.Equal(new[] { "订单号", "分店", "状态", "提交人", "提交时间", "SKU数", "总份数", "总数量", "进口金额", "零售金额" },
            ordersSheet.Row(1).Cells(1, 10).Select(item => item.GetString()));
        Assert.Equal(orders.Select(item => item.OrderNo).OrderBy(item => item),
            ordersSheet.Column(1).CellsUsed().Skip(1).Select(item => item.GetString()).OrderBy(item => item));

        Assert.Equal(new[] { "货号", "名称", "MOQ", "进口价", "零售价", "同名 (S2)", "同名 (S10)", "S20" },
            sheet.Row(1).Cells(1, 8).Select(item => item.GetString()));
        Assert.Equal(1, sheet.SheetView.SplitRow);
        Assert.Equal(5, sheet.SheetView.SplitColumn);
        var lastColumn = sheet.LastColumnUsed();
        var lastRow = sheet.LastRowUsed();
        Assert.NotNull(lastColumn);
        Assert.NotNull(lastRow);
        Assert.Equal(8, lastColumn.ColumnNumber());
        Assert.Equal(3, lastRow.RowNumber());
        Assert.Equal(new[] { "I1", "快照商品一", "4" },
            sheet.Row(2).Cells(1, 3).Select(item => item.GetString()));
        Assert.Equal(new[] { "I2", "快照商品二", "6" },
            sheet.Row(3).Cells(1, 3).Select(item => item.GetString()));
        Assert.Equal(1.25m, sheet.Cell(2, 4).GetValue<decimal>());
        Assert.Equal(2.50m, sheet.Cell(2, 5).GetValue<decimal>());
        Assert.Equal(4.75m, sheet.Cell(3, 4).GetValue<decimal>());
        Assert.Equal(9.25m, sheet.Cell(3, 5).GetValue<decimal>());

        // S2、S10、S20 依自然代码顺序；每格是 OrderedQuantity 数值，0 也必须落格。
        Assert.Equal(0, sheet.Cell(2, 6).GetValue<int>());
        Assert.Equal(4, sheet.Cell(2, 7).GetValue<int>());
        Assert.Equal(8, sheet.Cell(2, 8).GetValue<int>());
        Assert.Equal(6, sheet.Cell(3, 6).GetValue<int>());
        Assert.Equal(6, sheet.Cell(3, 7).GetValue<int>());
        Assert.Equal(6, sheet.Cell(3, 8).GetValue<int>());
        Assert.All(sheet.Range(2, 6, 3, 8).Cells(), item => Assert.Equal(XLDataType.Number, item.DataType));
    }

    [Fact]
    public async Task 导出分店商品矩阵没有正数有效订货时仅保留固定表头()
    {
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "export-zero-activation",
            TemplateGuid = "export-zero-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-EXPORT-ZERO",
            TemplateNameSnapshot = "零数量导出",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationItem
        {
            ActivationItemGuid = "export-zero-item",
            ActivationGuid = "export-zero-activation",
            ProductCode = "P1",
            ItemNumber = "I1",
            ProductName = "零数量商品",
            MinimumOrderQuantity = 2,
            ImportPrice = 1m,
            RetailPrice = 2m,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderWarehouseOrder
        {
            OrderGuid = "export-zero-submitted",
            ActivationGuid = "export-zero-activation",
            StoreGuid = "s1",
            StoreCode = "S1",
            StoreName = "零数量店",
            OrderNo = "PRE-EXPORT-ZERO-S1",
            Status = PreorderWarehouseOrderStatuses.Submitted,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderWarehouseOrderItem
        {
            OrderItemGuid = "export-zero-detail",
            OrderGuid = "export-zero-submitted",
            ActivationItemGuid = "export-zero-item",
            ProductCode = "P1",
            ItemNumber = "I1",
            ProductName = "零数量商品",
            MinimumOrderQuantity = 2,
            OrderedQuantity = 0,
        }).ExecuteCommandAsync();

        var file = await CreateService("admin-user", manageOrders: true).ExportAsync("export-zero-activation");
        using var stream = new MemoryStream(file.Content);
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet("分店商品明细");

        Assert.Equal(new[] { "货号", "名称", "MOQ", "进口价", "零售价" },
            sheet.Row(1).Cells(1, 5).Select(item => item.GetString()));
        var lastColumn = sheet.LastColumnUsed();
        var lastRow = sheet.LastRowUsed();
        Assert.NotNull(lastColumn);
        Assert.NotNull(lastRow);
        Assert.Equal(5, lastColumn.ColumnNumber());
        Assert.Equal(1, lastRow.RowNumber());
    }

    [Fact]
    public async Task 导出分店商品矩阵拒绝仅大小写不同的重复商品分店键()
    {
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "export-duplicate-key-activation",
            TemplateGuid = "export-duplicate-key-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-EXPORT-DUPLICATE-KEY",
            TemplateNameSnapshot = "重复矩阵键",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationItem
        {
            ActivationItemGuid = "export-duplicate-key-item",
            ActivationGuid = "export-duplicate-key-activation",
            ProductCode = "P1",
            ItemNumber = "I1",
            ProductName = "重复键商品",
            MinimumOrderQuantity = 2,
            ImportPrice = 1m,
            RetailPrice = 2m,
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new PreorderWarehouseOrder
            {
                OrderGuid = "export-duplicate-key-order-lower",
                ActivationGuid = "export-duplicate-key-activation",
                StoreGuid = "export-duplicate-key-store",
                StoreCode = "S1",
                StoreName = "重复键店",
                OrderNo = "PRE-EXPORT-DUPLICATE-KEY-S1-A",
                Status = PreorderWarehouseOrderStatuses.Submitted,
            },
            new PreorderWarehouseOrder
            {
                OrderGuid = "export-duplicate-key-order-upper",
                ActivationGuid = "export-duplicate-key-activation",
                StoreGuid = "EXPORT-DUPLICATE-KEY-STORE",
                StoreCode = "S1",
                StoreName = "重复键店",
                OrderNo = "PRE-EXPORT-DUPLICATE-KEY-S1-B",
                Status = PreorderWarehouseOrderStatuses.Completed,
            },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new PreorderWarehouseOrderItem
            {
                OrderItemGuid = "export-duplicate-key-detail-lower",
                OrderGuid = "export-duplicate-key-order-lower",
                ActivationItemGuid = "export-duplicate-key-item",
                ProductCode = "P1",
                ItemNumber = "I1",
                ProductName = "重复键商品",
                MinimumOrderQuantity = 2,
                OrderedQuantity = 2,
            },
            new PreorderWarehouseOrderItem
            {
                OrderItemGuid = "export-duplicate-key-detail-upper",
                OrderGuid = "export-duplicate-key-order-upper",
                ActivationItemGuid = "EXPORT-DUPLICATE-KEY-ITEM",
                ProductCode = "P1",
                ItemNumber = "I1",
                ProductName = "重复键商品",
                MinimumOrderQuantity = 2,
                OrderedQuantity = 2,
            },
        }).ExecuteCommandAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService("admin-user", manageOrders: true)
                .ExportAsync("export-duplicate-key-activation")
        );

        Assert.Equal("导出商品矩阵存在重复的商品与分店明细", error.Message);
    }

    [Fact]
    public async Task 草稿必须完整携带本期全部商品份数()
    {
        await SeedProductAndStoreAsync();
        await _db.Insertable(new UserStore
        {
            UserStoreGUID = "draft-user-store",
            UserGUID = "draft-user",
            StoreGUID = "store-1",
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "complete-draft",
            TemplateGuid = "draft-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-DRAFT",
            TemplateNameSnapshot = "完整草稿",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = "complete-draft-store",
            ActivationGuid = "complete-draft",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new PreorderActivationItem { ActivationItemGuid = "draft-item-1", ActivationGuid = "complete-draft", ProductCode = "P1", ItemNumber = "I1", ProductName = "商品一", MinimumOrderQuantity = 1 },
            new PreorderActivationItem { ActivationItemGuid = "draft-item-2", ActivationGuid = "complete-draft", ProductCode = "P2", ItemNumber = "I2", ProductName = "商品二", MinimumOrderQuantity = 1 },
        }).ExecuteCommandAsync();

        var error = await Assert.ThrowsAsync<BlazorApp.Api.Interfaces.React.PreorderBusinessException>(
            () => CreateService("draft-user", manageOrders: false).SaveDraftAsync(
                "complete-draft",
                new SavePreorderDraftDto
                {
                    StoreCode = "S01",
                    Items = new() { new() { ActivationItemGuid = "draft-item-1", PackCount = 1 } },
                }
            )
        );

        Assert.Equal("PREORDER_INVALID_REQUEST", error.ErrorCode);
        Assert.False(await _db.Queryable<PreorderWarehouseOrder>()
            .AnyAsync(item => item.ActivationGuid == "complete-draft"));
    }

    [Fact]
    public async Task 进程锁统一资源大小写并在最后引用释放后回收()
    {
        var first = await PreorderMutationLock.AcquireProcessAsync("PreorderActivation:ABC");
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondTask = Task.Run(async () =>
        {
            await using var second = await PreorderMutationLock.AcquireProcessAsync(
                "preorderactivation:abc"
            );
            secondEntered.SetResult();
        });

        await Task.Delay(100);
        Assert.False(secondEntered.Task.IsCompleted);
        await first.DisposeAsync();
        await secondTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.True(secondEntered.Task.IsCompletedSuccessfully);
        Assert.Equal(0, PreorderMutationLock.ProcessLockCount);
    }

    [Fact]
    public async Task SQLite数据库锁在事务提交前阻止另一连接取得同一业务写锁()
    {
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "db-lock",
            TemplateGuid = "db-lock-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-LOCK",
            TemplateNameSnapshot = "数据库锁",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        using var firstDb = CreateIndependentClient();
        using var secondDb = CreateIndependentClient();
        var firstAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstTask = firstDb.Ado.UseTranAsync(async () =>
        {
            await PreorderMutationLock.AcquireDatabaseAsync(firstDb, "PreorderActivation:DB-LOCK");
            firstAcquired.SetResult();
            await releaseFirst.Task;
        });
        await firstAcquired.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        var secondTask = Task.Run(() => secondDb.Ado.UseTranAsync(async () =>
        {
            await PreorderMutationLock.AcquireDatabaseAsync(secondDb, "preorderactivation:db-lock");
            secondAcquired.SetResult();
        }));

        await Task.Delay(100);
        Assert.False(secondAcquired.Task.IsCompleted);
        releaseFirst.SetResult();
        Assert.True((await firstTask).IsSuccess);
        Assert.True((await secondTask).IsSuccess);
        Assert.True(secondAcquired.Task.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(DbType.SqlServer)]
    [InlineData(DbType.PostgreSQL)]
    [InlineData(DbType.Sqlite)]
    public void StoreGate行锁SQL使用StoreGuid且覆盖删除停用行(DbType dbType)
    {
        var sql = PreorderMutationLock.GetStoreIdentityRowLockSql(dbType);

        Assert.Contains("StoreGUID", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@key", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IsDeleted", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IsActive", sql, StringComparison.OrdinalIgnoreCase);
        if (dbType == DbType.SqlServer)
        {
            Assert.Contains("UPDLOCK", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("HOLDLOCK", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("WHERE [StoreGUID] = @key", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("LOWER(", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SqlServerStoreGate行锁保留数据库原始StoreGuid大小写()
    {
        var key = PreorderMutationLock.ResolveStoreIdentityRowLockKey(
            DbType.SqlServer,
            "mixed-store",
            "MiXeD-Store"
        );

        Assert.Equal("MiXeD-Store", key);
    }

    [Fact]
    public void 普通订单StoreGate从原始资源保留数据库StoreGuid大小写()
    {
        const string canonicalStoreGuid = "MiXeD-Store";
        var resource = PreorderGateEvaluator.GetStoreLockResourceByStoreGuid(
            canonicalStoreGuid
        );

        var extractedStoreGuid = PreorderGateEvaluator
            .GetCanonicalStoreGuidFromLockResource(resource);
        var sqlServerKey = PreorderMutationLock.ResolveStoreIdentityRowLockKey(
            DbType.SqlServer,
            "mixed-store",
            extractedStoreGuid
        );

        Assert.Equal(canonicalStoreGuid, extractedStoreGuid);
        Assert.Equal(canonicalStoreGuid, sqlServerKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("PreorderActivation:store-1")]
    [InlineData("PreorderStoreGate:")]
    [InlineData("PreorderStoreGate:store-1:extra")]
    public void 普通订单StoreGate无效资源稳定FailClosed(string resource)
    {
        var error = Assert.Throws<PreorderBusinessException>(() =>
            PreorderGateEvaluator.GetCanonicalStoreGuidFromLockResource(resource)
        );

        Assert.Equal(503, error.StatusCode);
        Assert.Equal("PREORDER_GATE_UNAVAILABLE", error.ErrorCode);
        Assert.Equal("Preorder 状态暂时无法确认，请稍后重试", error.Message);
    }

    [Fact]
    public void SqlServerStoreGate行锁拒绝与资源不匹配的原始StoreGuid()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            PreorderMutationLock.ResolveStoreIdentityRowLockKey(
                DbType.SqlServer,
                "store-1",
                "store-2"
            )
        );

        Assert.Contains("StoreGuid", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DbType.PostgreSQL)]
    [InlineData(DbType.Sqlite)]
    public void 非SqlServerStoreGate继续使用规范化锁键(DbType dbType)
    {
        var key = PreorderMutationLock.ResolveStoreIdentityRowLockKey(
            dbType,
            "mixed-store",
            "MiXeD-Store"
        );

        Assert.Equal("mixed-store", key);
    }

    [Fact]
    public async Task SQLiteStoreGate锁在身份行不存在时显式失败()
    {
        var transaction = await _db.Ado.UseTranAsync(async () =>
        {
            await PreorderMutationLock.AcquireDatabaseAsync(
                _db,
                "PreorderStoreGate:missing-store"
            );
        });

        var error = Assert.IsType<InvalidOperationException>(transaction.ErrorException);
        Assert.Contains("Store", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DbType.MySql)]
    [InlineData(DbType.Oracle)]
    public void Preorder数据库锁对未支持Provider显式失败(DbType dbType)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            PreorderMutationLock.EnsureSupportedProvider(dbType)
        );
        Assert.Contains("Preorder", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SQLiteStoreGate持有到事务结束并阻止删除分店身份修改()
    {
        await SeedProductAndStoreAsync();
        await _db.Updateable<Store>()
            .SetColumns(item => item.IsDeleted == true)
            .SetColumns(item => item.IsActive == false)
            .Where(item => item.StoreGUID == "store-1")
            .ExecuteCommandAsync();
        using var firstDb = CreateIndependentClient();
        using var secondDb = CreateIndependentClient();
        var firstAcquired = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var mutationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var firstTask = firstDb.Ado.UseTranAsync(async () =>
        {
            await PreorderMutationLock.AcquireDatabaseAsync(
                firstDb,
                "PreorderStoreGate:store-1"
            );
            firstAcquired.SetResult();
            await releaseFirst.Task;
        });
        await firstAcquired.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        var mutationTask = Task.Run(async () =>
        {
            mutationStarted.SetResult();
            return await secondDb.Ado.UseTranAsync(async () =>
            {
                await secondDb.Updateable<Store>()
                    .SetColumns(item => item.StoreCode == "RENAMED")
                    .Where(item => item.StoreGUID == "store-1")
                    .ExecuteCommandAsync();
            });
        });
        await mutationStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        await Task.Delay(100);
        Assert.False(mutationTask.IsCompleted);

        releaseFirst.SetResult();
        Assert.True((await firstTask).IsSuccess);
        Assert.True((await mutationTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout)).IsSuccess);
        using var verifyDb = CreateIndependentClient();
        var renamed = await verifyDb.Queryable<Store>()
            .FirstAsync(item => item.StoreGUID == "store-1");
        Assert.Equal("RENAMED", renamed.StoreCode);
    }

    [Fact]
    public async Task 仓库订单取消状态更新使用原状态条件拒绝并发覆盖()
    {
        await _db.Insertable(new PreorderWarehouseOrder
        {
            OrderGuid = "status-race",
            ActivationGuid = "status-activation",
            StoreGuid = "status-store",
            StoreCode = "S01",
            StoreName = "一店",
            OrderNo = "PRE-STATUS-S01",
            Status = PreorderWarehouseOrderStatuses.Submitted,
        }).ExecuteCommandAsync();
        using var competingDb = CreateIndependentClient();
        var injected = false;
        _db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (injected
                || !sql.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                || !sql.Contains("PreorderWarehouseOrder", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            injected = true;
            competingDb.Updateable<PreorderWarehouseOrder>()
                .SetColumns(item => item.Status == PreorderWarehouseOrderStatuses.Completed)
                .Where(item => item.OrderGuid == "status-race")
                .ExecuteCommand();
        };
        try
        {
            var error = await Assert.ThrowsAsync<BlazorApp.Api.Interfaces.React.PreorderBusinessException>(
                () => CreateService("admin-user", manageOrders: true).UpdateOrderStatusAsync(
                    "status-race",
                    new UpdatePreorderOrderStatusDto { Status = PreorderWarehouseOrderStatuses.Cancelled }
                )
            );
            Assert.Equal("PREORDER_INVALID_STATUS_TRANSITION", error.ErrorCode);
        }
        finally
        {
            _db.Aop.OnLogExecuting = null;
        }
        var persisted = await _db.Queryable<PreorderWarehouseOrder>()
            .FirstAsync(item => item.OrderGuid == "status-race");
        Assert.Equal(PreorderWarehouseOrderStatuses.Completed, persisted.Status);
    }

    [Fact]
    public async Task 取消流程持有批次锁时订单处理状态等待并在取得锁后重新验证批次()
    {
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "cancel-status-race",
            TemplateGuid = "cancel-status-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-CANCEL-STATUS",
            TemplateNameSnapshot = "取消竞态",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderWarehouseOrder
        {
            OrderGuid = "cancel-status-order",
            ActivationGuid = "cancel-status-race",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
            OrderNo = "PRE-CANCEL-STATUS-S01",
            Status = PreorderWarehouseOrderStatuses.Submitted,
        }).ExecuteCommandAsync();
        var activationResource = "PreorderActivation:cancel-status-race";
        var heldLock = await PreorderMutationLock.AcquireProcessAsync(activationResource);
        try
        {
            var updateTask = Task.Run(() =>
                CreateService("admin-user", manageOrders: true).UpdateOrderStatusAsync(
                    "cancel-status-order",
                    new UpdatePreorderOrderStatusDto
                    {
                        Status = PreorderWarehouseOrderStatuses.Processing,
                    }
                )
            );

            // 没有共用批次锁时状态更新会立即完成；正确实现必须等待取消流程释放该锁。
            var firstCompleted = await Task.WhenAny(updateTask, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.NotSame(updateTask, firstCompleted);
            await _db.Updateable<PreorderActivation>()
                .SetColumns(item => item.Status == PreorderActivationStatuses.Cancelled)
                .Where(item => item.ActivationGuid == "cancel-status-race")
                .ExecuteCommandAsync();
            await heldLock.DisposeAsync();

            var error = await Assert.ThrowsAsync<BlazorApp.Api.Interfaces.React.PreorderBusinessException>(
                async () => await updateTask
            );
            Assert.Equal("PREORDER_NOT_ACTIVE", error.ErrorCode);
        }
        finally
        {
            await heldLock.DisposeAsync();
        }

        var persisted = await _db.Queryable<PreorderWarehouseOrder>()
            .FirstAsync(item => item.OrderGuid == "cancel-status-order");
        Assert.Equal(PreorderWarehouseOrderStatuses.Submitted, persisted.Status);
    }

    [Fact]
    public async Task 批次未来结束时间只允许延长不允许缩短()
    {
        await SeedProductAndStoreAsync();
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "future-shrink",
            TemplateGuid = "future-shrink-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-SHRINK",
            TemplateNameSnapshot = "不可缩短",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(2),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = "future-shrink-store",
            ActivationGuid = "future-shrink",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
        }).ExecuteCommandAsync();

        var error = await Assert.ThrowsAsync<BlazorApp.Api.Interfaces.React.PreorderBusinessException>(
            () => CreateService("admin-user", manageOrders: true).CloseActivationAsync(
                "future-shrink",
                new ClosePreorderActivationDto { EndAtUtc = Now.AddHours(1) }
            )
        );

        Assert.Equal("PREORDER_INVALID_REQUEST", error.ErrorCode);
        var persisted = await _db.Queryable<PreorderActivation>()
            .FirstAsync(item => item.ActivationGuid == "future-shrink");
        Assert.Equal(Now.AddHours(2), persisted.EndAtUtc);
    }

    [Fact]
    public async Task 延长和提前关闭都等待所有目标分店门禁锁并在锁内改变门禁状态()
    {
        await SeedProductAndStoreAsync();
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "extend-store-gate",
            TemplateGuid = "extend-store-gate-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-EXTEND-STORE-GATE",
            TemplateNameSnapshot = "延长门禁锁",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = "extend-store-gate-store",
            ActivationGuid = "extend-store-gate",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
        }).ExecuteCommandAsync();

        var heldLock = await PreorderMutationLock.AcquireProcessAsync("PreorderStoreGate:store-1");
        try
        {
            var service = CreateService("admin-user", manageOrders: true);
            var extendTask = Task.Run(() => service.CloseActivationAsync(
                "extend-store-gate",
                new ClosePreorderActivationDto { EndAtUtc = Now.AddHours(2) }
            ));

            var firstCompleted = await Task.WhenAny(extendTask, Task.Delay(TimeSpan.FromMilliseconds(500)));
            Assert.NotSame(extendTask, firstCompleted);
            await heldLock.DisposeAsync();
            var extended = await extendTask;
            Assert.Equal(Now.AddHours(2), extended.EndAtUtc);

            // 提前关闭会立即解除普通订单门禁，必须等待同店 Cart submit 释放 StoreGate。
            heldLock = await PreorderMutationLock.AcquireProcessAsync("PreorderStoreGate:store-1");
            var closeTask = Task.Run(() => service.CloseActivationAsync(
                "extend-store-gate",
                new ClosePreorderActivationDto()
            ));
            var closeCompleted = await Task.WhenAny(
                closeTask,
                Task.Delay(TimeSpan.FromMilliseconds(500))
            );
            Assert.NotSame(closeTask, closeCompleted);
            Assert.Equal(
                PreorderActivationStatuses.Active,
                (await _db.Queryable<PreorderActivation>()
                    .FirstAsync(item => item.ActivationGuid == "extend-store-gate")).Status
            );
            Assert.True((await service.CheckAsync("S01")).IsBlocked);

            await heldLock.DisposeAsync();
            var closed = await closeTask;
            Assert.Equal(PreorderActivationStatuses.Closed, closed.Status);
            Assert.False((await service.CheckAsync("S01")).IsBlocked);
        }
        finally
        {
            await heldLock.DisposeAsync();
        }

        Assert.Equal(0, PreorderMutationLock.ProcessLockCount);
    }

    [Fact]
    public async Task 取消批次等待所有目标分店StoreGate且等待期间不会提前解除Cart门禁()
    {
        await SeedProductAndStoreAsync();
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "cancel-store-gate",
            TemplateGuid = "cancel-store-gate-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-CANCEL-STORE-GATE",
            TemplateNameSnapshot = "取消门禁锁",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = "cancel-store-gate-store",
            ActivationGuid = "cancel-store-gate",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = "cancel-store-gate-store-2",
            ActivationGuid = "cancel-store-gate",
            StoreGuid = "store-2",
            StoreCode = "S02",
            StoreName = "二店",
        }).ExecuteCommandAsync();

        var service = CreateService("admin-user", manageOrders: true);
        IAsyncDisposable? heldLock = await PreorderMutationLock.AcquireProcessAsync(
            // 持有排序靠后的第二家分店，证明取消必须取得批次内全部 StoreGate。
            "PreorderStoreGate:store-2"
        );
        try
        {
            var cancelTask = Task.Run(() => service.CancelActivationAsync("cancel-store-gate"));
            var firstCompleted = await Task.WhenAny(
                cancelTask,
                Task.Delay(TimeSpan.FromMilliseconds(500))
            );
            Assert.NotSame(cancelTask, firstCompleted);
            Assert.Equal(
                PreorderActivationStatuses.Active,
                (await _db.Queryable<PreorderActivation>()
                    .FirstAsync(item => item.ActivationGuid == "cancel-store-gate")).Status
            );
            Assert.True((await service.CheckAsync("S01")).IsBlocked);

            await heldLock.DisposeAsync();
            heldLock = null;
            Assert.Equal(
                PreorderActivationStatuses.Cancelled,
                (await cancelTask).Status
            );
            Assert.False((await service.CheckAsync("S01")).IsBlocked);
        }
        finally
        {
            if (heldLock != null)
            {
                await heldLock.DisposeAsync();
            }
        }

        Assert.Equal(0, PreorderMutationLock.ProcessLockCount);
    }

    [Fact]
    public async Task 延长批次使用不可变StoreGuid而不依赖快照或当前StoreCode()
    {
        await SeedProductAndStoreAsync();
        await _db.Updateable<Store>()
            .SetColumns(item => item.StoreCode == "NEW")
            .Where(item => item.StoreGUID == "store-1")
            .ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "extend-renamed-store",
            TemplateGuid = "extend-renamed-store-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-EXTEND-RENAMED",
            TemplateNameSnapshot = "分店改名门禁锁",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = "extend-renamed-store-target",
            ActivationGuid = "extend-renamed-store",
            StoreGuid = "store-1",
            StoreCode = "OLD",
            StoreName = "旧店名",
        }).ExecuteCommandAsync();

        var service = CreateService("admin-user", manageOrders: true);
        var storeGuidLock = await PreorderMutationLock.AcquireProcessAsync(
            "PreorderStoreGate:store-1"
        );
        try
        {
            var extendTask = Task.Run(() => service.CloseActivationAsync(
                "extend-renamed-store",
                new ClosePreorderActivationDto { EndAtUtc = Now.AddHours(2) }
            ));
            var firstCompleted = await Task.WhenAny(
                extendTask,
                Task.Delay(TimeSpan.FromMilliseconds(500))
            );
            Assert.NotSame(extendTask, firstCompleted);
            await storeGuidLock.DisposeAsync();
            Assert.Equal(Now.AddHours(2), (await extendTask).EndAtUtc);
        }
        finally
        {
            await storeGuidLock.DisposeAsync();
        }

        var snapshotCodeLock = await PreorderMutationLock.AcquireProcessAsync(
            "PreorderStoreGate:OLD"
        );
        try
        {
            // 快照编码仅用于历史展示，分店改名后延长不得再等待旧编码锁。
            var extended = await service.CloseActivationAsync(
                "extend-renamed-store",
                new ClosePreorderActivationDto { EndAtUtc = Now.AddHours(3) }
            ).WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            Assert.Equal(Now.AddHours(3), extended.EndAtUtc);
        }
        finally
        {
            await snapshotCodeLock.DisposeAsync();
        }

        Assert.Equal(0, PreorderMutationLock.ProcessLockCount);
    }

    [Fact]
    public async Task 延长批次在当前分店不存在时仍锁定历史目标StoreGuid()
    {
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "extend-missing-current-store",
            TemplateGuid = "extend-missing-current-store-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-EXTEND-MISSING-STORE",
            TemplateNameSnapshot = "目标分店已失效",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = "extend-missing-current-store-target",
            ActivationGuid = "extend-missing-current-store",
            StoreGuid = "missing-store-guid",
            StoreCode = "OLD",
            StoreName = "已失效分店",
        }).ExecuteCommandAsync();

        var heldLock = await PreorderMutationLock.AcquireProcessAsync(
            "PreorderStoreGate:missing-store-guid"
        );
        try
        {
            var extendTask = Task.Run(() => CreateService(
                "admin-user",
                manageOrders: true
            ).CloseActivationAsync(
                "extend-missing-current-store",
                new ClosePreorderActivationDto { EndAtUtc = Now.AddHours(2) }
            ));
            var firstCompleted = await Task.WhenAny(
                extendTask,
                Task.Delay(TimeSpan.FromMilliseconds(500))
            );
            Assert.NotSame(extendTask, firstCompleted);
            await heldLock.DisposeAsync();
            Assert.Equal(Now.AddHours(2), (await extendTask).EndAtUtc);
        }
        finally
        {
            await heldLock.DisposeAsync();
        }

        var persisted = await _db.Queryable<PreorderActivation>()
            .FirstAsync(item => item.ActivationGuid == "extend-missing-current-store");
        Assert.Equal(Now.AddHours(2), persisted.EndAtUtc);
    }

    [Fact]
    public async Task 延长等待门禁锁跨过旧截止时间后不能重新激活()
    {
        await SeedProductAndStoreAsync();
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "extend-after-expiry",
            TemplateGuid = "extend-after-expiry-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-EXTEND-AFTER-EXPIRY",
            TemplateNameSnapshot = "跨截止时间",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddMinutes(1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = "extend-after-expiry-store",
            ActivationGuid = "extend-after-expiry",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
        }).ExecuteCommandAsync();

        var clock = new MutableTimeProvider(Now);
        var heldLock = await PreorderMutationLock.AcquireProcessAsync("PreorderStoreGate:store-1");
        try
        {
            var extendTask = Task.Run(() => CreateService(
                "admin-user",
                manageOrders: true,
                timeProvider: clock
            ).CloseActivationAsync(
                "extend-after-expiry",
                new ClosePreorderActivationDto { EndAtUtc = Now.AddHours(2) }
            ));
            var firstCompleted = await Task.WhenAny(
                extendTask,
                Task.Delay(TimeSpan.FromMilliseconds(500))
            );
            Assert.NotSame(extendTask, firstCompleted);

            clock.SetUtcNow(Now.AddMinutes(2));
            await heldLock.DisposeAsync();
            var error = await Assert.ThrowsAsync<PreorderBusinessException>(async () =>
                await extendTask
            );
            Assert.Equal("PREORDER_NOT_ACTIVE", error.ErrorCode);
        }
        finally
        {
            await heldLock.DisposeAsync();
        }

        var persisted = await _db.Queryable<PreorderActivation>()
            .FirstAsync(item => item.ActivationGuid == "extend-after-expiry");
        Assert.Equal(Now.AddMinutes(1), persisted.EndAtUtc);
        Assert.Equal(0, PreorderMutationLock.ProcessLockCount);
    }

    [Fact]
    public async Task Preorder时间输出将数据库无Kind值恢复为UTC并带Z序列化()
    {
        await SeedProductAndStoreAsync();
        var start = DateTime.SpecifyKind(Now.AddHours(-1), DateTimeKind.Unspecified);
        var end = DateTime.SpecifyKind(Now.AddHours(1), DateTimeKind.Unspecified);
        var submittedAt = DateTime.SpecifyKind(Now.AddMinutes(-10), DateTimeKind.Unspecified);
        var updatedAt = DateTime.SpecifyKind(Now.AddMinutes(-20), DateTimeKind.Unspecified);
        await _db.Insertable(new PreorderTemplate
        {
            TemplateGuid = "utc-output-template",
            Name = "UTC输出模板",
            UpdatedAt = updatedAt,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "utc-output",
            TemplateGuid = "utc-output-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-UTC-OUTPUT",
            TemplateNameSnapshot = "UTC输出",
            SourceTemplateRevision = 1,
            StartAtUtc = start,
            EndAtUtc = end,
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderWarehouseOrder
        {
            OrderGuid = "utc-output-order",
            ActivationGuid = "utc-output",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
            OrderNo = "PRE-UTC-OUTPUT-S01",
            Status = PreorderWarehouseOrderStatuses.Submitted,
            SubmittedAtUtc = submittedAt,
        }).ExecuteCommandAsync();

        var service = CreateService("admin-user", manageOrders: true);
        var templates = await service.GetTemplatesAsync(
            new PreorderTemplateQueryDto { Keyword = "UTC输出模板" }
        );
        var template = Assert.Single(templates.Items!);
        var activation = await service.GetActivationAsync("utc-output");
        var order = Assert.Single(await service.GetActivationOrdersAsync("utc-output"));

        Assert.Equal(DateTimeKind.Utc, template.UpdatedAt!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, activation.StartAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, activation.EndAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, order.SubmittedAt!.Value.Kind);

        var json = JsonSerializer.Serialize(
            new { template, activation, order },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        );
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.EndsWith("Z", root.GetProperty("template").GetProperty("updatedAt").GetString());
        Assert.EndsWith("Z", root.GetProperty("activation").GetProperty("startAtUtc").GetString());
        Assert.EndsWith("Z", root.GetProperty("activation").GetProperty("endAtUtc").GetString());
        Assert.EndsWith("Z", root.GetProperty("order").GetProperty("submittedAt").GetString());
    }

    [Fact]
    public async Task 自然过期批次不能通过延长结束时间重新激活()
    {
        await SeedProductAndStoreAsync();
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "expired-reopen",
            TemplateGuid = "expired-reopen-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-EXPIRED-REOPEN",
            TemplateNameSnapshot = "已过期",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddDays(-2),
            EndAtUtc = Now.AddDays(-1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = "expired-reopen-store",
            ActivationGuid = "expired-reopen",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
        }).ExecuteCommandAsync();

        var error = await Assert.ThrowsAsync<BlazorApp.Api.Interfaces.React.PreorderBusinessException>(
            () => CreateService("admin-user", manageOrders: true).CloseActivationAsync(
                "expired-reopen",
                new ClosePreorderActivationDto { EndAtUtc = Now.AddHours(1) }
            )
        );

        Assert.Equal(409, error.StatusCode);
        Assert.Equal("PREORDER_NOT_ACTIVE", error.ErrorCode);
        var persisted = await _db.Queryable<PreorderActivation>()
            .FirstAsync(item => item.ActivationGuid == "expired-reopen");
        Assert.Equal(Now.AddDays(-1), persisted.EndAtUtc);
    }

    [Fact]
    public async Task 分店批次详情只返回当前分店而管理员仍可查看全部目标分店()
    {
        await SeedProductAndStoreAsync();
        await _db.Insertable(new Store
        {
            StoreGUID = "store-2",
            StoreCode = "S02",
            StoreName = "二店",
            IsActive = true,
        }).ExecuteCommandAsync();
        await _db.Insertable(new UserStore
        {
            UserStoreGUID = "detail-user-store",
            UserGUID = "detail-user",
            StoreGUID = "store-1",
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "store-isolation",
            TemplateGuid = "store-isolation-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-STORE-ISOLATION",
            TemplateNameSnapshot = "分店隔离",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new PreorderActivationStore { ActivationStoreGuid = "isolation-store-1", ActivationGuid = "store-isolation", StoreGuid = "store-1", StoreCode = "S01", StoreName = "一店" },
            new PreorderActivationStore { ActivationStoreGuid = "isolation-store-2", ActivationGuid = "store-isolation", StoreGuid = "store-2", StoreCode = "S02", StoreName = "二店" },
        }).ExecuteCommandAsync();

        var storeDetail = await CreateService("detail-user", manageOrders: false)
            .GetActivationAsync("store-isolation", "S01");
        var store = Assert.Single(storeDetail.Stores);
        Assert.Equal("S01", store.StoreCode);

        var adminDetail = await CreateService("admin-user", manageOrders: true)
            .GetActivationAsync("store-isolation");
        Assert.Equal(new[] { "S01", "S02" }, adminDetail.Stores.Select(item => item.StoreCode));
    }

    [Fact]
    public async Task 取消批次后订单仅保留审计且不再计入有效数量或继续处理()
    {
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "cancel-audit",
            TemplateGuid = "cancel-audit-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-CANCEL-AUDIT",
            TemplateNameSnapshot = "取消审计",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = "cancel-audit-store",
            ActivationGuid = "cancel-audit",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationItem
        {
            ActivationItemGuid = "cancel-audit-item",
            ActivationGuid = "cancel-audit",
            ProductCode = "P1",
            ItemNumber = "I1",
            ProductName = "商品",
            MinimumOrderQuantity = 2,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderWarehouseOrder
        {
            OrderGuid = "cancel-audit-order",
            ActivationGuid = "cancel-audit",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
            OrderNo = "PRE-CANCEL-AUDIT-S01",
            Status = PreorderWarehouseOrderStatuses.Submitted,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderWarehouseOrderItem
        {
            OrderItemGuid = "cancel-audit-detail",
            OrderGuid = "cancel-audit-order",
            ActivationItemGuid = "cancel-audit-item",
            ProductCode = "P1",
            ItemNumber = "I1",
            ProductName = "商品",
            PackCount = 3,
            MinimumOrderQuantity = 2,
            OrderedQuantity = 6,
            ImportPrice = 4m,
            RetailPrice = 6m,
            ImportAmount = 24m,
            RetailAmount = 36m,
        }).ExecuteCommandAsync();
        var service = CreateService("admin-user", manageOrders: true);

        await service.CancelActivationAsync("cancel-audit");

        var statistics = await service.GetStatisticsAsync("cancel-audit");
        var product = Assert.Single(statistics.Products);
        Assert.Equal(0, product.TotalPackCount);
        Assert.Equal(0, product.TotalQuantity);
        Assert.Equal(0m, product.TotalImportAmount);
        Assert.Empty(statistics.StoreProductQuantities);

        var statusError = await Assert.ThrowsAsync<BlazorApp.Api.Interfaces.React.PreorderBusinessException>(
            () => service.UpdateOrderStatusAsync(
                "cancel-audit-order",
                new UpdatePreorderOrderStatusDto { Status = PreorderWarehouseOrderStatuses.Processing }
            )
        );
        Assert.Equal("PREORDER_NOT_ACTIVE", statusError.ErrorCode);
        Assert.True(await _db.Queryable<PreorderWarehouseOrder>()
            .AnyAsync(item => item.OrderGuid == "cancel-audit-order"));
        Assert.True(await _db.Queryable<PreorderWarehouseOrderItem>()
            .AnyAsync(item => item.OrderGuid == "cancel-audit-order"));

        var export = await service.ExportAsync("cancel-audit");
        using var stream = new MemoryStream(export.Content);
        using var workbook = new XLWorkbook(stream);
        var orders = workbook.Worksheet("分店订单");
        Assert.Equal("PRE-CANCEL-AUDIT-S01", orders.Cell(2, 1).GetString());
        Assert.Equal(0, orders.Cell(2, 6).GetValue<int>());
        Assert.Equal(0, orders.Cell(2, 7).GetValue<int>());
        Assert.Equal(0, orders.Cell(2, 8).GetValue<int>());
        var matrix = workbook.Worksheet("分店商品明细");
        Assert.Equal(new[] { "货号", "名称", "MOQ", "进口价", "零售价" },
            matrix.Row(1).Cells(1, 5).Select(item => item.GetString()));
        var lastColumn = matrix.LastColumnUsed();
        var lastRow = matrix.LastRowUsed();
        Assert.NotNull(lastColumn);
        Assert.NotNull(lastRow);
        Assert.Equal(5, lastColumn.ColumnNumber());
        Assert.Equal(1, lastRow.RowNumber());
    }

    [Fact]
    public async Task 统计端点在并发取消订单时保持同一数据库快照()
    {
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "statistics-snapshot",
            TemplateGuid = "statistics-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-STAT-SNAPSHOT",
            TemplateNameSnapshot = "统计快照",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = "statistics-store",
            ActivationGuid = "statistics-snapshot",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationItem
        {
            ActivationItemGuid = "statistics-item",
            ActivationGuid = "statistics-snapshot",
            ProductCode = "P1",
            ItemNumber = "I1",
            ProductName = "商品",
            MinimumOrderQuantity = 1,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderWarehouseOrder
        {
            OrderGuid = "statistics-order",
            ActivationGuid = "statistics-snapshot",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
            OrderNo = "PRE-STAT-S01",
            Status = PreorderWarehouseOrderStatuses.Submitted,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderWarehouseOrderItem
        {
            OrderItemGuid = "statistics-detail",
            OrderGuid = "statistics-order",
            ActivationItemGuid = "statistics-item",
            ProductCode = "P1",
            ItemNumber = "I1",
            ProductName = "商品",
            PackCount = 1,
            MinimumOrderQuantity = 1,
            OrderedQuantity = 1,
        }).ExecuteCommandAsync();

        using var competingDb = CreateIndependentClient();
        var startCompetingWrite = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var competingWrite = Task.Run(async () =>
        {
            await startCompetingWrite.Task;
            var transaction = await competingDb.Ado.UseTranAsync(async () =>
            {
                await competingDb.Updateable<PreorderWarehouseOrderItem>()
                    .SetColumns(item => item.PackCount == 5)
                    .SetColumns(item => item.OrderedQuantity == 5)
                    .Where(item => item.OrderItemGuid == "statistics-detail")
                    .ExecuteCommandAsync();
                await competingDb.Updateable<PreorderWarehouseOrder>()
                    .SetColumns(item => item.Status == PreorderWarehouseOrderStatuses.Cancelled)
                    .Where(item => item.OrderGuid == "statistics-order")
                    .ExecuteCommandAsync();
            });
            Assert.True(transaction.IsSuccess, transaction.ErrorMessage);
        });
        _db.Aop.OnLogExecuted = (sql, _) =>
        {
            if (sql.Contains("PreorderWarehouseOrder", StringComparison.OrdinalIgnoreCase)
                && !sql.Contains("PreorderWarehouseOrderItem", StringComparison.OrdinalIgnoreCase))
            {
                startCompetingWrite.TrySetResult();
            }
        };

        PreorderActivationStatisticsDto statistics;
        try
        {
            statistics = await CreateService("admin-user", manageOrders: true)
                .GetStatisticsAsync("statistics-snapshot");
        }
        finally
        {
            _db.Aop.OnLogExecuted = null;
            startCompetingWrite.TrySetResult();
        }
        await competingWrite.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.Equal(1, statistics.SubmittedCount);
        Assert.Equal(0, statistics.CancelledCount);
        Assert.Equal(1, Assert.Single(statistics.Products).TotalQuantity);
        Assert.Equal(
            PreorderWarehouseOrderStatuses.Submitted,
            Assert.Single(statistics.Orders).Status
        );
        Assert.Equal(
            PreorderWarehouseOrderStatuses.Cancelled,
            (await _db.Queryable<PreorderWarehouseOrder>()
                .FirstAsync(item => item.OrderGuid == "statistics-order")).Status
        );
    }

    [Theory]
    [InlineData(DbType.MySql)]
    [InlineData(DbType.Oracle)]
    public async Task PreorderSchema对未支持Provider在启动期显式失败(DbType dbType)
    {
        var unsupportedDb = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "Server=unsupported;Database=unsupported;",
            DbType = dbType,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PreorderSchemaBootstrap.EnsureIndexesAsync(unsupportedDb)
        );

        Assert.Contains("Preorder", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Provider", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DbType.MySql)]
    [InlineData(DbType.Oracle)]
    public async Task StartupSchemaMigrator真实启动路径在EarlyReturn前拒绝未支持Provider(DbType dbType)
    {
        var unsupportedDb = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "Server=unsupported;Database=unsupported;",
            DbType = dbType,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StartupSchemaMigrator.EnsureAsync(unsupportedDb, NullLogger.Instance)
        );

        Assert.Contains("Preorder", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Provider", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DbType.PostgreSQL)]
    [InlineData(DbType.Sqlite)]
    public async Task StartupSchemaMigrator对支持的非SqlServerProvider保持EarlyReturn(DbType dbType)
    {
        var supportedDb = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = dbType == DbType.Sqlite
                ? "Data Source=:memory:"
                : "Host=unused;Database=unused;Username=unused;Password=unused",
            DbType = dbType,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });

        await StartupSchemaMigrator.EnsureAsync(supportedDb, NullLogger.Instance);
    }

    [Fact]
    public async Task 多商品多分店汇总数量超过Int上限时使用Long保持精确()
    {
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "long-total",
            TemplateGuid = "long-total-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-LONG-TOTAL",
            TemplateNameSnapshot = "大数量",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new PreorderActivationItem { ActivationItemGuid = "long-item-1", ActivationGuid = "long-total", ProductCode = "P1", ItemNumber = "I1", ProductName = "商品一", MinimumOrderQuantity = 1 },
            new PreorderActivationItem { ActivationItemGuid = "long-item-2", ActivationGuid = "long-total", ProductCode = "P2", ItemNumber = "I2", ProductName = "商品二", MinimumOrderQuantity = 1 },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new PreorderActivationStore { ActivationStoreGuid = "long-store-1", ActivationGuid = "long-total", StoreGuid = "store-1", StoreCode = "S01", StoreName = "一店" },
            new PreorderActivationStore { ActivationStoreGuid = "long-store-2", ActivationGuid = "long-total", StoreGuid = "store-2", StoreCode = "S02", StoreName = "二店" },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new PreorderWarehouseOrder { OrderGuid = "long-order-1", ActivationGuid = "long-total", StoreGuid = "store-1", StoreCode = "S01", StoreName = "一店", OrderNo = "PRE-LONG-S01", Status = PreorderWarehouseOrderStatuses.Submitted },
            new PreorderWarehouseOrder { OrderGuid = "long-order-2", ActivationGuid = "long-total", StoreGuid = "store-2", StoreCode = "S02", StoreName = "二店", OrderNo = "PRE-LONG-S02", Status = PreorderWarehouseOrderStatuses.Submitted },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new PreorderWarehouseOrderItem { OrderItemGuid = "long-detail-1", OrderGuid = "long-order-1", ActivationItemGuid = "long-item-1", ProductCode = "P1", ItemNumber = "I1", ProductName = "商品一", MinimumOrderQuantity = 1, PackCount = int.MaxValue, OrderedQuantity = int.MaxValue },
            new PreorderWarehouseOrderItem { OrderItemGuid = "long-detail-2", OrderGuid = "long-order-1", ActivationItemGuid = "long-item-2", ProductCode = "P2", ItemNumber = "I2", ProductName = "商品二", MinimumOrderQuantity = 1, PackCount = int.MaxValue, OrderedQuantity = int.MaxValue },
            new PreorderWarehouseOrderItem { OrderItemGuid = "long-detail-3", OrderGuid = "long-order-2", ActivationItemGuid = "long-item-1", ProductCode = "P1", ItemNumber = "I1", ProductName = "商品一", MinimumOrderQuantity = 1, PackCount = int.MaxValue, OrderedQuantity = int.MaxValue },
        }).ExecuteCommandAsync();
        var service = CreateService("admin-user", manageOrders: true);

        var order = (await service.GetActivationOrdersAsync("long-total"))
            .Single(item => item.OrderGuid == "long-order-1");
        Assert.Equal(4_294_967_294L, order.TotalPackCount);
        Assert.Equal(4_294_967_294L, order.TotalQuantity);
        var statistics = await service.GetStatisticsAsync("long-total");
        Assert.Equal(2, statistics.Orders.Count);
        Assert.Equal(
            4_294_967_294L,
            statistics.Orders.Single(item => item.OrderGuid == "long-order-1").TotalQuantity
        );
        var product = statistics.Products
            .Single(item => item.ProductCode == "P1");
        Assert.Equal(4_294_967_294L, product.TotalPackCount);
        Assert.Equal(4_294_967_294L, product.TotalQuantity);
    }

    [Fact]
    public async Task Scheduled批次可以直接提前关闭并保留原有效期快照()
    {
        await SeedProductAndStoreAsync();
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "scheduled-close",
            TemplateGuid = "scheduled-close-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-SCHEDULED-CLOSE",
            TemplateNameSnapshot = "提前关闭",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(1),
            EndAtUtc = Now.AddHours(2),
            Status = PreorderActivationStatuses.Scheduled,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = "scheduled-close-store",
            ActivationGuid = "scheduled-close",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
        }).ExecuteCommandAsync();

        var result = await CreateService("admin-user", manageOrders: true).CloseActivationAsync(
            "scheduled-close",
            new ClosePreorderActivationDto()
        );

        Assert.Equal(PreorderActivationStatuses.Closed, result.Status);
        var persisted = await _db.Queryable<PreorderActivation>()
            .FirstAsync(item => item.ActivationGuid == "scheduled-close");
        Assert.Equal(Now.AddHours(1), persisted.StartAtUtc);
        Assert.Equal(Now.AddHours(2), persisted.EndAtUtc);
        Assert.Equal(Now, persisted.ClosedAtUtc);
    }

    [Fact]
    public async Task 更新激活分店使用忽略大小写CAS并保存当前分店快照()
    {
        await SeedProductAndStoreAsync();
        await _db.Insertable(new Store
        {
            StoreGUID = "store-2",
            StoreCode = "S02",
            StoreName = "二店新名称",
            IsActive = true,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "update-stores-success",
            TemplateGuid = "template-1",
            PeriodNumber = 1,
            ActivationCode = "PRE-UPDATE-STORES",
            TemplateNameSnapshot = "变更分店",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = "update-stores-old-target",
            ActivationGuid = "update-stores-success",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "旧一店快照",
        }).ExecuteCommandAsync();

        var detail = await CreateService("admin-user", manageOrders: true)
            .UpdateActivationStoresAsync(
                "update-stores-success",
                new UpdatePreorderActivationStoresDto
                {
                    ExpectedStoreGuids = new() { "STORE-1" },
                    StoreGuids = new() { "store-2" },
                }
            );

        var store = Assert.Single(detail.Stores);
        Assert.Equal("store-2", store.StoreGuid);
        Assert.Equal("S02", store.StoreCode);
        Assert.Equal("二店新名称", store.StoreName);
        var allTargets = await _db.Queryable<PreorderActivationStore>()
            .Where(item => item.ActivationGuid == "update-stores-success")
            .ToListAsync();
        Assert.Equal(2, allTargets.Count);
        Assert.True(allTargets.Single(item => item.StoreGuid == "store-1").IsDeleted);
        Assert.False(allTargets.Single(item => item.StoreGuid == "store-2").IsDeleted);
    }

    [Fact]
    public async Task 更新激活分店CAS不匹配返回稳定冲突且不写入()
    {
        await SeedProductAndStoreAsync();
        await SeedActivationTargetForStoreUpdateAsync("update-stores-cas");

        var error = await Assert.ThrowsAsync<PreorderBusinessException>(() =>
            CreateService("admin-user", manageOrders: true).UpdateActivationStoresAsync(
                "update-stores-cas",
                new UpdatePreorderActivationStoresDto
                {
                    ExpectedStoreGuids = new() { "other-store" },
                    StoreGuids = new() { "store-1" },
                }
            )
        );

        Assert.Equal(409, error.StatusCode);
        Assert.Equal("PREORDER_ACTIVATION_STORES_CHANGED", error.ErrorCode);
        Assert.False((await _db.Queryable<PreorderActivationStore>()
            .SingleAsync(item => item.ActivationGuid == "update-stores-cas")).IsDeleted);
    }

    [Theory]
    [InlineData(PreorderWarehouseOrderStatuses.Draft)]
    [InlineData(PreorderWarehouseOrderStatuses.ReturnedForRevision)]
    [InlineData(PreorderWarehouseOrderStatuses.Submitted)]
    [InlineData(PreorderWarehouseOrderStatuses.NoDemand)]
    [InlineData(PreorderWarehouseOrderStatuses.Processing)]
    [InlineData(PreorderWarehouseOrderStatuses.Completed)]
    [InlineData(PreorderWarehouseOrderStatuses.Cancelled)]
    public async Task 任意状态订单都阻止移除分店并保持事务原子(string orderStatus)
    {
        await SeedProductAndStoreAsync();
        await _db.Insertable(new Store
        {
            StoreGUID = "store-2",
            StoreCode = "S02",
            StoreName = "二店",
            IsActive = true,
        }).ExecuteCommandAsync();
        var activationGuid = $"update-stores-order-{orderStatus}";
        await SeedActivationTargetForStoreUpdateAsync(activationGuid);
        await _db.Insertable(new PreorderWarehouseOrder
        {
            OrderGuid = $"order-{orderStatus}",
            ActivationGuid = activationGuid,
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
            OrderNo = $"PRE-{orderStatus}",
            Status = orderStatus,
        }).ExecuteCommandAsync();

        var error = await Assert.ThrowsAsync<PreorderBusinessException>(() =>
            CreateService("admin-user", manageOrders: true).UpdateActivationStoresAsync(
                activationGuid,
                new UpdatePreorderActivationStoresDto
                {
                    ExpectedStoreGuids = new() { "store-1" },
                    StoreGuids = new() { "store-2" },
                }
            )
        );

        Assert.Equal(409, error.StatusCode);
        Assert.Equal("PREORDER_ACTIVATION_STORE_HAS_ORDER", error.ErrorCode);
        var activeTargets = await _db.Queryable<PreorderActivationStore>()
            .Where(item => !item.IsDeleted && item.ActivationGuid == activationGuid)
            .ToListAsync();
        Assert.Equal("store-1", Assert.Single(activeTargets).StoreGuid);
    }

    [Theory]
    [InlineData(PreorderActivationStatuses.Closed, 1)]
    [InlineData(PreorderActivationStatuses.Cancelled, 1)]
    [InlineData(PreorderActivationStatuses.Active, -1)]
    [InlineData(PreorderActivationStatuses.Scheduled, -1)]
    public async Task 非可编辑状态或自然过期批次拒绝变更分店(string status, int endOffsetHours)
    {
        await SeedProductAndStoreAsync();
        await SeedActivationTargetForStoreUpdateAsync(
            "update-stores-inactive",
            status,
            Now.AddHours(endOffsetHours)
        );

        var error = await Assert.ThrowsAsync<PreorderBusinessException>(() =>
            CreateService("admin-user", manageOrders: true).UpdateActivationStoresAsync(
                "update-stores-inactive",
                new UpdatePreorderActivationStoresDto
                {
                    ExpectedStoreGuids = new() { "store-1" },
                    StoreGuids = new() { "store-1" },
                }
            )
        );

        Assert.Equal(409, error.StatusCode);
        Assert.Equal("PREORDER_NOT_ACTIVE", error.ErrorCode);
    }

    [Fact]
    public async Task 更新激活分店等待排序StoreGate并在锁内重读新增分店状态()
    {
        await SeedProductAndStoreAsync();
        await _db.Insertable(new Store
        {
            StoreGUID = "store-2",
            StoreCode = "S02",
            StoreName = "二店",
            IsActive = true,
        }).ExecuteCommandAsync();
        await SeedActivationTargetForStoreUpdateAsync("update-stores-reread");
        var heldStoreGate = await PreorderMutationLock.AcquireProcessAsync(
            "PreorderStoreGate:store-1"
        );
        try
        {
            var updateTask = Task.Run(() => CreateService("admin-user", manageOrders: true)
                .UpdateActivationStoresAsync(
                    "update-stores-reread",
                    new UpdatePreorderActivationStoresDto
                    {
                        ExpectedStoreGuids = new() { "store-1" },
                        StoreGuids = new() { "store-2" },
                    }
                ));
            Assert.NotSame(updateTask, await Task.WhenAny(
                updateTask,
                Task.Delay(TimeSpan.FromMilliseconds(300))
            ));
            await _db.Updateable<Store>()
                .SetColumns(item => item.IsActive == false)
                .Where(item => item.StoreGUID == "store-2")
                .ExecuteCommandAsync();
            await heldStoreGate.DisposeAsync();

            var error = await Assert.ThrowsAsync<PreorderBusinessException>(async () =>
                await updateTask
            );
            Assert.Equal("PREORDER_INVALID_REQUEST", error.ErrorCode);
        }
        finally
        {
            await heldStoreGate.DisposeAsync();
        }

        Assert.Equal("store-1", (await _db.Queryable<PreorderActivationStore>()
            .SingleAsync(item => !item.IsDeleted
                && item.ActivationGuid == "update-stores-reread")).StoreGuid);
        Assert.Equal(0, PreorderMutationLock.ProcessLockCount);
    }

    [Fact]
    public async Task 激活请求保存预计到货日期并以DateOnly返回()
    {
        await SeedProductAndStoreAsync();
        var service = CreateService("admin-user", manageOrders: true);
        var template = await service.CreateTemplateAsync(new SavePreorderTemplateDto
        {
            Name = "预计到货日期模板",
            Items = new() { new() { ProductCode = "P1", MinimumOrderQuantity = 1 } },
            StoreGuids = new() { "store-1" },
        });
        var expectedDate = new DateOnly(2026, 8, 5);

        var summary = await service.ActivateAsync(
            template.TemplateGuid,
            new ActivatePreorderTemplateDto
            {
                ExpectedRevision = template.Revision,
                StartAtUtc = Now.AddMinutes(-1),
                EndAtUtc = Now.AddHours(1),
                EstimatedArrivalDate = expectedDate,
            }
        );
        var detail = await service.GetActivationAsync(summary.ActivationGuid);
        var active = await service.GetActiveAsync("S01");
        var persisted = await _db.Queryable<PreorderActivation>()
            .SingleAsync(item => item.ActivationGuid == summary.ActivationGuid);

        Assert.Equal(expectedDate, summary.EstimatedArrivalDate);
        Assert.Equal(expectedDate, detail.EstimatedArrivalDate);
        Assert.Equal(
            expectedDate,
            Assert.Single(active.Activations).EstimatedArrivalDate
        );
        Assert.Equal(expectedDate.ToDateTime(TimeOnly.MinValue), persisted.EstimatedArrivalDate);
        var productionJsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        Assert.Contains(
            "\"estimatedArrivalDate\":\"2026-08-05\"",
            JsonSerializer.Serialize(summary, productionJsonOptions),
            StringComparison.Ordinal
        );
        Assert.Contains(
            "\"estimatedArrivalDate\":null",
            JsonSerializer.Serialize(
                new PreorderActivationSummaryDto(),
                productionJsonOptions
            ),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task 预计到货日期支持CAS更新和显式清空()
    {
        await SeedProductAndStoreAsync();
        await SeedActivationTargetForStoreUpdateAsync("arrival-date-update");
        var service = CreateService("admin-user", manageOrders: true);
        var expectedDate = new DateOnly(2026, 8, 6);

        var updated = await service.UpdateActivationEstimatedArrivalDateAsync(
            "arrival-date-update",
            new UpdatePreorderActivationEstimatedArrivalDateDto
            {
                ExpectedEstimatedArrivalDate = null,
                EstimatedArrivalDate = expectedDate,
            }
        );
        var cleared = await service.UpdateActivationEstimatedArrivalDateAsync(
            "arrival-date-update",
            new UpdatePreorderActivationEstimatedArrivalDateDto
            {
                ExpectedEstimatedArrivalDate = expectedDate,
                EstimatedArrivalDate = null,
            }
        );

        Assert.Equal(expectedDate, updated.EstimatedArrivalDate);
        Assert.Null(cleared.EstimatedArrivalDate);
        Assert.Null((await _db.Queryable<PreorderActivation>()
            .SingleAsync(item => item.ActivationGuid == "arrival-date-update"))
            .EstimatedArrivalDate);
    }

    [Fact]
    public async Task 预计到货日期CAS冲突返回稳定错误且不覆盖新值()
    {
        await SeedProductAndStoreAsync();
        await SeedActivationTargetForStoreUpdateAsync("arrival-date-conflict");
        var persistedDate = new DateOnly(2026, 8, 7);
        await _db.Updateable<PreorderActivation>()
            .SetColumns(item => item.EstimatedArrivalDate == persistedDate.ToDateTime(TimeOnly.MinValue))
            .Where(item => item.ActivationGuid == "arrival-date-conflict")
            .ExecuteCommandAsync();

        var error = await Assert.ThrowsAsync<PreorderBusinessException>(() =>
            CreateService("admin-user", manageOrders: true)
                .UpdateActivationEstimatedArrivalDateAsync(
                    "arrival-date-conflict",
                    new UpdatePreorderActivationEstimatedArrivalDateDto
                    {
                        ExpectedEstimatedArrivalDate = null,
                        EstimatedArrivalDate = new DateOnly(2026, 8, 8),
                    }
                )
        );

        Assert.Equal(409, error.StatusCode);
        Assert.Equal("PREORDER_ACTIVATION_ARRIVAL_DATE_CHANGED", error.ErrorCode);
        Assert.Equal(persistedDate.ToDateTime(TimeOnly.MinValue),
            (await _db.Queryable<PreorderActivation>()
                .SingleAsync(item => item.ActivationGuid == "arrival-date-conflict"))
            .EstimatedArrivalDate);
    }

    [Theory]
    [InlineData(PreorderActivationStatuses.Closed, 1)]
    [InlineData(PreorderActivationStatuses.Cancelled, 1)]
    [InlineData(PreorderActivationStatuses.Active, -1)]
    [InlineData(PreorderActivationStatuses.Scheduled, -1)]
    public async Task 非可编辑状态或自然过期批次拒绝更新预计到货日期(
        string status,
        int endOffsetHours
    )
    {
        await SeedProductAndStoreAsync();
        await SeedActivationTargetForStoreUpdateAsync(
            "arrival-date-inactive",
            status,
            Now.AddHours(endOffsetHours)
        );

        var error = await Assert.ThrowsAsync<PreorderBusinessException>(() =>
            CreateService("admin-user", manageOrders: true)
                .UpdateActivationEstimatedArrivalDateAsync(
                    "arrival-date-inactive",
                    new UpdatePreorderActivationEstimatedArrivalDateDto
                    {
                        ExpectedEstimatedArrivalDate = null,
                        EstimatedArrivalDate = new DateOnly(2026, 8, 9),
                    }
                )
        );

        Assert.Equal(409, error.StatusCode);
        Assert.Equal("PREORDER_NOT_ACTIVE", error.ErrorCode);
    }

    [Fact]
    public async Task 预计到货日期等待批次锁并在事务内重读CAS()
    {
        await SeedProductAndStoreAsync();
        await SeedActivationTargetForStoreUpdateAsync("arrival-date-reread");
        var competingDate = new DateOnly(2026, 8, 10);
        var heldActivationLock = await PreorderMutationLock.AcquireProcessAsync(
            "PreorderActivation:arrival-date-reread"
        );
        try
        {
            var updateTask = Task.Run(() => CreateService("admin-user", manageOrders: true)
                .UpdateActivationEstimatedArrivalDateAsync(
                    "arrival-date-reread",
                    new UpdatePreorderActivationEstimatedArrivalDateDto
                    {
                        ExpectedEstimatedArrivalDate = null,
                        EstimatedArrivalDate = new DateOnly(2026, 8, 11),
                    }
                ));
            Assert.NotSame(updateTask, await Task.WhenAny(
                updateTask,
                Task.Delay(TimeSpan.FromMilliseconds(300))
            ));
            await _db.Updateable<PreorderActivation>()
                .SetColumns(item => item.EstimatedArrivalDate
                    == competingDate.ToDateTime(TimeOnly.MinValue))
                .Where(item => item.ActivationGuid == "arrival-date-reread")
                .ExecuteCommandAsync();
            await heldActivationLock.DisposeAsync();

            var error = await Assert.ThrowsAsync<PreorderBusinessException>(async () =>
                await updateTask
            );
            Assert.Equal("PREORDER_ACTIVATION_ARRIVAL_DATE_CHANGED", error.ErrorCode);
        }
        finally
        {
            await heldActivationLock.DisposeAsync();
        }

        Assert.Equal(competingDate.ToDateTime(TimeOnly.MinValue),
            (await _db.Queryable<PreorderActivation>()
                .SingleAsync(item => item.ActivationGuid == "arrival-date-reread"))
            .EstimatedArrivalDate);
        Assert.Equal(0, PreorderMutationLock.ProcessLockCount);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private PreorderReactService CreateService(
        string userGuid,
        bool manageOrders,
        string? role = null,
        TimeProvider? timeProvider = null,
        ILogger<PreorderReactService>? logger = null,
        string? submissionId = null
    )
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userGuid), new("userId", userGuid) };
        if (role != null) claims.Add(new Claim(ClaimTypes.Role, role));
        var http = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")),
            },
        };
        if (submissionId != null)
        {
            http.HttpContext!.Request.Headers["X-Preorder-Submission-Id"] = submissionId;
        }
        return new PreorderReactService(
            CreateSqlSugarContext(_db),
            new CurrentUserService(http),
            new FakeAuthorizationService(manageOrders),
            http,
            logger ?? NullLogger<PreorderReactService>.Instance,
            timeProvider ?? new FixedTimeProvider(Now)
        );
    }

    private async Task SeedProductAndStoreAsync()
    {
        if (!await _db.Queryable<Product>().AnyAsync(item => item.ProductCode == "P1"))
        {
            await _db.Insertable(new Product
            {
                UUID = "product-1",
                ProductCode = "P1",
                ItemNumber = "I1",
                ProductName = "旧名称",
            }).ExecuteCommandAsync();
            await _db.Insertable(new WarehouseProduct
            {
                ProductCode = "P1",
                ImportPrice = 3.25m,
                OEMPrice = 5.5m,
            }).ExecuteCommandAsync();
        }
        if (!await _db.Queryable<Store>().AnyAsync(item => item.StoreGUID == "store-1"))
        {
            await _db.Insertable(new Store
            {
                StoreGUID = "store-1",
                StoreCode = "S01",
                StoreName = "一店",
                IsActive = true,
            }).ExecuteCommandAsync();
        }
    }

    private async Task SeedActiveTargetAsync()
    {
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = "gate-activation",
            TemplateGuid = "gate-template",
            PeriodNumber = 1,
            ActivationCode = "PRE-GATE",
            TemplateNameSnapshot = "Gate",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = "gate-store",
            ActivationGuid = "gate-activation",
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
        }).ExecuteCommandAsync();
    }

    private async Task SeedActivationTargetForStoreUpdateAsync(
        string activationGuid,
        string status = PreorderActivationStatuses.Active,
        DateTime? endAtUtc = null
    )
    {
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = activationGuid,
            TemplateGuid = "template-1",
            PeriodNumber = 1,
            ActivationCode = $"PRE-{activationGuid}",
            TemplateNameSnapshot = "变更分店测试",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-2),
            EndAtUtc = endAtUtc ?? Now.AddHours(2),
            Status = status,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = $"{activationGuid}-store",
            ActivationGuid = activationGuid,
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
        }).ExecuteCommandAsync();
    }

    private async Task<(
        string UserGuid,
        string ActivationGuid,
        string ActivationItemGuid,
        string OrderGuid,
        string OrderItemGuid
    )> SeedSubmitDraftAsync(string prefix, int storedPackCount, int storedOrderedQuantity)
    {
        await SeedProductAndStoreAsync();
        var userGuid = $"{prefix}-user";
        var activationGuid = $"{prefix}-activation";
        var activationItemGuid = $"{prefix}-item";
        var orderGuid = $"{prefix}-order";
        var orderItemGuid = $"{prefix}-detail";
        await _db.Insertable(new UserStore
        {
            UserStoreGUID = $"{prefix}-user-store",
            UserGUID = userGuid,
            StoreGUID = "store-1",
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivation
        {
            ActivationGuid = activationGuid,
            TemplateGuid = $"{prefix}-template",
            PeriodNumber = 1,
            ActivationCode = $"PRE-{prefix}",
            TemplateNameSnapshot = "提交草稿规范字段校验",
            SourceTemplateRevision = 1,
            StartAtUtc = Now.AddHours(-1),
            EndAtUtc = Now.AddHours(1),
            Status = PreorderActivationStatuses.Active,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationStore
        {
            ActivationStoreGuid = $"{prefix}-store",
            ActivationGuid = activationGuid,
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderActivationItem
        {
            ActivationItemGuid = activationItemGuid,
            ActivationGuid = activationGuid,
            ProductCode = "P1",
            ItemNumber = "I1",
            ProductName = "商品",
            ProductImage = "/P1.jpg",
            MinimumOrderQuantity = 2,
            ImportPrice = 1.25m,
            RetailPrice = 2.50m,
        }).ExecuteCommandAsync();
        await _db.Insertable(new PreorderWarehouseOrder
        {
            OrderGuid = orderGuid,
            ActivationGuid = activationGuid,
            StoreGuid = "store-1",
            StoreCode = "S01",
            StoreName = "一店",
            OrderNo = $"PRE-{prefix}-S01",
            Status = PreorderWarehouseOrderStatuses.Draft,
            DraftRevision = 7,
        }).ExecuteCommandAsync();
        var canonicalQuantity = storedPackCount * 2;
        await _db.Insertable(new PreorderWarehouseOrderItem
        {
            OrderItemGuid = orderItemGuid,
            OrderGuid = orderGuid,
            ActivationItemGuid = activationItemGuid,
            ProductCode = "P1",
            ItemNumber = "I1",
            ProductName = "商品",
            ProductImage = "/P1.jpg",
            MinimumOrderQuantity = 2,
            PackCount = storedPackCount,
            OrderedQuantity = storedOrderedQuantity,
            ImportPrice = 1.25m,
            RetailPrice = 2.50m,
            ImportAmount = 1.25m * canonicalQuantity,
            RetailAmount = 2.50m * canonicalQuantity,
        }).ExecuteCommandAsync();
        return (userGuid, activationGuid, activationItemGuid, orderGuid, orderItemGuid);
    }

    private async Task DisablePreorderStatusGuardsForLegacyStateTestAsync()
    {
        // 仅用于构造升级前可能遗留的非法状态，验证应用层仍然 fail-closed。
        foreach (var trigger in new[]
        {
            "CK_PreorderActivation_Status_Insert",
            "CK_PreorderActivation_Status_Update",
            "CK_PreorderWarehouseOrder_Status_Insert",
            "CK_PreorderWarehouseOrder_Status_Update",
        })
        {
            await _db.Ado.ExecuteCommandAsync($"DROP TRIGGER IF EXISTS \"{trigger}\"");
        }
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    private SqlSugarClient CreateIndependentClient() => new(new ConnectionConfig
    {
        // Default Timeout 同时是 SQLite 忙等锁的上限；用例会故意让另一连接等锁，属于防挂死预算。
        ConnectionString = $"Data Source={_dbPath};Default Timeout={(int)AsyncTestWaitSupport.DefaultTimeout.TotalSeconds}",
        DbType = DbType.Sqlite,
        IsAutoCloseConnection = true,
        InitKeyType = InitKeyType.Attribute,
    });

    private sealed class FakeAuthorizationService(bool manageOrders) : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Failed());

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(
                manageOrders && policyName == Permissions.Warehouse.ManageOrders
                    ? AuthorizationResult.Success()
                    : AuthorizationResult.Failed()
            );
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class MutableTimeProvider(DateTime utcNow) : TimeProvider
    {
        private DateTime _utcNow = utcNow;

        public void SetUtcNow(DateTime value) => _utcNow = value;

        public override DateTimeOffset GetUtcNow() => new(_utcNow, TimeSpan.Zero);
    }

    private static IReadOnlyDictionary<string, object?> AssertSubmissionTelemetry(
        CollectingLogger<PreorderReactService> logger,
        string expectedPath
    )
    {
        var entry = Assert.Single(logger.Entries, item =>
            item.Properties.TryGetValue("{OriginalFormat}", out var format)
            && string.Equals(
                format?.ToString(),
                "Preorder 提交性能: SubmissionId={SubmissionId}, Path={Path}, PreLockMs={PreLockMs}, ProcessActivationLockWaitMs={ProcessActivationLockWaitMs}, ProcessStoreLockWaitMs={ProcessStoreLockWaitMs}, DatabaseLockMs={DatabaseLockMs}, LockedReadMs={LockedReadMs}, ItemLoadMs={ItemLoadMs}, PersistMs={PersistMs}, FinalTransitionMs={FinalTransitionMs}, TransactionMs={TransactionMs}, PreLockSqlRoundTrips={PreLockSqlRoundTrips}, DatabaseLockSqlRoundTrips={DatabaseLockSqlRoundTrips}, LockedReadSqlRoundTrips={LockedReadSqlRoundTrips}, ItemLoadSqlRoundTrips={ItemLoadSqlRoundTrips}, PersistSqlRoundTrips={PersistSqlRoundTrips}, FinalTransitionSqlRoundTrips={FinalTransitionSqlRoundTrips}, SqlRoundTrips={SqlRoundTrips}, ActivationItemCount={ActivationItemCount}, OrderItemCount={OrderItemCount}, TotalMs={TotalMs}",
                StringComparison.Ordinal
            )
        );
        Assert.Equal(expectedPath, entry.Properties["Path"]);
        Assert.True(Assert.IsType<long>(entry.Properties["PreLockMs"]) >= 0);
        Assert.True(Assert.IsType<long>(entry.Properties["ProcessActivationLockWaitMs"]) >= 0);
        Assert.True(Assert.IsType<long>(entry.Properties["ProcessStoreLockWaitMs"]) >= 0);
        Assert.True(Assert.IsType<long>(entry.Properties["DatabaseLockMs"]) >= 0);
        Assert.True(Assert.IsType<long>(entry.Properties["LockedReadMs"]) >= 0);
        Assert.True(Assert.IsType<long>(entry.Properties["ItemLoadMs"]) >= 0);
        Assert.True(Assert.IsType<long>(entry.Properties["PersistMs"]) >= 0);
        Assert.True(Assert.IsType<long>(entry.Properties["FinalTransitionMs"]) >= 0);
        Assert.True(Assert.IsType<long>(entry.Properties["TransactionMs"]) >= 0);
        Assert.Equal(2, entry.Properties["PreLockSqlRoundTrips"]);
        Assert.Equal(2, entry.Properties["DatabaseLockSqlRoundTrips"]);
        Assert.Equal(4, entry.Properties["LockedReadSqlRoundTrips"]);
        Assert.True(Assert.IsType<int>(entry.Properties["ItemLoadSqlRoundTrips"]) >= 1);
        Assert.True(Assert.IsType<int>(entry.Properties["PersistSqlRoundTrips"]) >= 0);
        Assert.True(Assert.IsType<int>(entry.Properties["FinalTransitionSqlRoundTrips"]) >= 0);
        Assert.True(Assert.IsType<int>(entry.Properties["SqlRoundTrips"]) >= 9);
        Assert.True(Assert.IsType<long>(entry.Properties["TotalMs"]) >= 0);
        return entry.Properties;
    }

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<CollectedLogEntry> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            Entries.Add(new CollectedLogEntry(logLevel, formatter(state, exception), properties));
        }
    }

    private sealed record CollectedLogEntry(
        LogLevel Level,
        string Message,
        IReadOnlyDictionary<string, object?> Properties
    );

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose() { }
    }

    private sealed class SqliteIndexInfo
    {
        public string name { get; set; } = string.Empty;
        public int partial { get; set; }
    }

    private sealed class SqliteIndexColumnInfo
    {
        public string name { get; set; } = string.Empty;
    }

    private sealed class SqliteTableColumnInfo
    {
        public string name { get; set; } = string.Empty;
        public string type { get; set; } = string.Empty;
        public int notnull { get; set; }
    }
}
