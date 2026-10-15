using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class StorePriceUpdateTaskServiceTests : IDisposable
{
    private const string ProductCode = "P001";
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;
    private readonly PriceNotificationSummaryAccessor _summary = new();
    private readonly Mock<IStoreProductMaintenanceReactService> _maintenance = new(MockBehavior.Strict);
    private readonly Dictionary<string, string?> _settings = new();

    public StorePriceUpdateTaskServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        _db = new SqlSugarClient(
            new ConnectionConfig
            {
                ConnectionString = _connection.ConnectionString,
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = false,
                InitKeyType = InitKeyType.Attribute,
            }
        );
        _db.CodeFirst.InitTables(
            typeof(Product),
            typeof(WarehouseProduct),
            typeof(DomesticProduct),
            typeof(Store),
            typeof(StoreRetailPrice),
            typeof(ProductHqSyncOutbox)
        );
        _db.Ado.ExecuteCommand(
            """
            CREATE TABLE WarehouseProductChangeHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                EventGuid TEXT NOT NULL,
                ProductCode TEXT NOT NULL,
                Action TEXT NOT NULL,
                Source TEXT NOT NULL,
                SourceReference TEXT NULL,
                BatchGuid TEXT NULL,
                ActorUserGuid TEXT NULL,
                ActorName TEXT NOT NULL,
                ActorType TEXT NOT NULL,
                OccurredAtUtc TEXT NOT NULL,
                ChangesJson TEXT NOT NULL
            )
            """
        );
        StorePriceUpdateTaskSchemaMigrator.EnsureAsync(_db, NullLogger.Instance).GetAwaiter().GetResult();

        _db.Insertable(new Product { ProductCode = ProductCode, ProductName = "保温杯", ItemNumber = "HB-1", RetailPrice = 10m })
            .ExecuteCommand();
        InsertStore("1001");
        InsertStore("1002");
        InsertStore("1003");
        InsertStorePrice("1001", 10m, null);
        InsertStorePrice("1002", 10m, null);
        InsertStorePrice("1003", 9m, null, isSpecial: true);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Theory]
    [InlineData(10, 12, false, true)]
    [InlineData(10.004, 10.0, false, false)] // 金额按 2 位小数比较
    [InlineData(10, 12, true, false)] // 自动定价的分店不比较零售价
    public void Evaluate_零售价比较规则(decimal store, decimal target, bool autoPricing, bool expected)
    {
        var evaluation = StorePriceUpdateTaskService.Evaluate(store, null, autoPricing, target, null, store, null);
        Assert.Equal(expected, evaluation.NeedsRetail);
    }

    [Fact]
    public void Evaluate_建议折扣为空时不比较_为零时表示明确无折扣()
    {
        Assert.False(StorePriceUpdateTaskService.Evaluate(10m, 0.1m, false, 10m, null, 10m, 0.1m).NeedsDiscount);
        Assert.True(StorePriceUpdateTaskService.Evaluate(10m, 0.1m, false, 10m, 0m, 10m, 0.1m).NeedsDiscount);
        // 分店折扣 null 视同 0
        Assert.False(StorePriceUpdateTaskService.Evaluate(10m, null, false, 10m, 0m, 10m, null).NeedsDiscount);
    }

    [Fact]
    public async Task 仓库改价后价格不同的分店生成需改价任务_特殊商品跳过()
    {
        var service = CreateService();
        await SetWarehouseRetailAsync(12m);

        await service.OnWarehousePriceChangedAsync(new[] { ProductCode }, new PriceTaskInitiator("张伟", "WarehouseProducts"));

        var tasks = await _db.Queryable<StorePriceUpdateTask>().OrderBy(t => t.StoreCode).ToListAsync();
        Assert.Equal(new[] { "1001", "1002" }, tasks.Select(t => t.StoreCode));
        Assert.All(tasks, task =>
        {
            Assert.Equal(StorePriceUpdateTaskStatuses.Pending, task.Status);
            Assert.Equal(StorePriceUpdateTaskKinds.PriceUpdate, task.Kind);
            Assert.Equal(10m, task.ShelfRetailPrice);
            Assert.Equal(12m, task.TargetRetailPrice);
            Assert.Equal("张伟", task.InitiatorName);
        });
        var summary = _summary.GetSummary()!;
        Assert.Equal(2, summary.NeedsPriceUpdateStores);
        Assert.Equal(1, summary.SkippedSpecialStores);
    }

    [Fact]
    public async Task 仓库价改回去_未处理的通知自动取消()
    {
        var service = CreateService();
        await SetWarehouseRetailAsync(12m);
        await service.OnWarehousePriceChangedAsync(new[] { ProductCode }, new PriceTaskInitiator("张伟", "WarehouseProducts"));

        await SetWarehouseRetailAsync(10m);
        await service.OnWarehousePriceChangedAsync(new[] { ProductCode }, new PriceTaskInitiator("张伟", "WarehouseProducts"));

        var tasks = await _db.Queryable<StorePriceUpdateTask>().ToListAsync();
        Assert.Equal(2, tasks.Count);
        Assert.All(tasks, task =>
        {
            Assert.Equal(StorePriceUpdateTaskStatuses.Cancelled, task.Status);
            Assert.Equal(StorePriceUpdateTaskCancelReasons.Reverted, task.CancelReason);
        });
        Assert.Equal(2, _summary.GetSummary()!.CancelledStores);
    }

    [Fact]
    public async Task 仓库多次改价_同一任务刷新目标并累计次数_不新增()
    {
        var service = CreateService();
        await SetWarehouseRetailAsync(12m);
        await service.OnWarehousePriceChangedAsync(new[] { ProductCode }, new PriceTaskInitiator("张伟", "WarehouseProducts"));
        await SetWarehouseRetailAsync(11m);
        await service.OnWarehousePriceChangedAsync(new[] { ProductCode }, new PriceTaskInitiator("陈静", "BatchUpdate"));

        var task = await _db.Queryable<StorePriceUpdateTask>().Where(t => t.StoreCode == "1001").SingleAsync();
        Assert.Equal(11m, task.TargetRetailPrice);
        Assert.Equal(2, task.ChangeCount);
        Assert.Equal("陈静", task.InitiatorName);
    }

    [Fact]
    public async Task 分店价被覆盖_登记待换标签_再覆盖回货架价则取消()
    {
        var service = CreateService();
        // 模拟仓库自动下发：仓库与分店 1001 同时变为 12
        await SetWarehouseRetailAsync(12m);
        await SetStoreRetailAsync("1001", 12m);
        await service.RecordStoreOverwritesAsync(
            new[] { new StorePriceOverwrite("1001", ProductCode, 10m, null) },
            new PriceTaskInitiator("张伟", "WarehouseProducts")
        );

        var task = await _db.Queryable<StorePriceUpdateTask>().Where(t => t.StoreCode == "1001").SingleAsync();
        Assert.Equal(StorePriceUpdateTaskKinds.LabelOnly, task.Kind);
        Assert.Equal(10m, task.ShelfRetailPrice);
        Assert.Equal(1, _summary.GetSummary()!.LabelOnlyStores);

        await SetWarehouseRetailAsync(10m);
        await SetStoreRetailAsync("1001", 10m);
        await service.RecordStoreOverwritesAsync(
            new[] { new StorePriceOverwrite("1001", ProductCode, 12m, null) },
            new PriceTaskInitiator("张伟", "WarehouseProducts")
        );

        task = await _db.Queryable<StorePriceUpdateTask>().Where(t => t.StoreCode == "1001").SingleAsync();
        Assert.Equal(StorePriceUpdateTaskStatuses.Cancelled, task.Status);
    }

    [Fact]
    public async Task 设置建议折扣后_折扣不同的分店生成任务()
    {
        var service = CreateService();
        Assert.True(await service.SetSuggestedDiscountAsync(ProductCode, 0.2m, "tester"));
        Assert.False(await service.SetSuggestedDiscountAsync(ProductCode, 0.2m, "tester"));

        await service.OnWarehousePriceChangedAsync(new[] { ProductCode }, new PriceTaskInitiator("陈静", "WarehouseProducts"));

        var page = await service.GetPageAsync(new StorePriceUpdateTaskQueryDto { StoreCode = "1001" }, null);
        var item = Assert.Single(page.Items);
        Assert.Equal(new[] { "discountRate" }, item.ChangedFields);
        Assert.Equal(0.2m, item.TargetDiscountRate);
        Assert.Equal("保温杯", item.ProductName);
        Assert.Equal(1, page.PendingPriceUpdateCount);
    }

    [Fact]
    public async Task 店员自行改价一致后_列表对账转为待换标签_处理标签后完成()
    {
        var service = CreateService();
        await SetWarehouseRetailAsync(12m);
        await service.OnWarehousePriceChangedAsync(new[] { ProductCode }, new PriceTaskInitiator("张伟", "WarehouseProducts"));
        var taskId = (await _db.Queryable<StorePriceUpdateTask>().Where(t => t.StoreCode == "1001").SingleAsync()).Id;

        // 价格还没改就处理标签：拒绝
        var rejected = await service.MarkLabelsAsync(
            new MarkStorePriceUpdateTaskLabelsRequestDto { StoreCode = "1001", TaskIds = { taskId } }, "李四");
        Assert.Equal(StorePriceUpdateTaskResultCodes.NotApplicable, Assert.Single(rejected.Items).Code);

        await SetStoreRetailAsync("1001", 12m);
        var page = await service.GetPageAsync(new StorePriceUpdateTaskQueryDto { StoreCode = "1001" }, null);
        Assert.Equal(StorePriceUpdateTaskKinds.LabelOnly, Assert.Single(page.Items).Kind);

        var marked = await service.MarkLabelsAsync(
            new MarkStorePriceUpdateTaskLabelsRequestDto { StoreCode = "1001", TaskIds = { taskId } }, "李四");
        var done = Assert.Single(marked.Items);
        Assert.True(done.Success);
        Assert.Equal(StorePriceUpdateTaskStatuses.Completed, done.Task!.Status);
        Assert.Equal(StorePriceUpdateTaskCompletionModes.Printed, done.Task.CompletionMode);
        Assert.Equal("李四", done.Task.CompletedBy);
        Assert.Equal(1, done.Task.LabelPrintCount);
    }

    [Fact]
    public async Task 保持本店价后完成_仓库再次改价会新建任务()
    {
        var service = CreateService();
        await SetWarehouseRetailAsync(12m);
        await service.OnWarehousePriceChangedAsync(new[] { ProductCode }, new PriceTaskInitiator("张伟", "WarehouseProducts"));
        var taskId = (await _db.Queryable<StorePriceUpdateTask>().Where(t => t.StoreCode == "1001").SingleAsync()).Id;

        var kept = await service.KeepStorePriceAsync(
            new StorePriceUpdateTaskIdsRequestDto { StoreCode = "1001", TaskIds = { taskId } }, "王五");
        Assert.Equal(StorePriceUpdateTaskCompletionModes.KeptStorePrice, Assert.Single(kept.Items).Task!.CompletionMode);

        // 对账不会把"保持本店价"的任务重新拉起
        await service.ReconcilePendingAsync("1001");
        Assert.Equal(0, await service.GetPendingCountAsync("1001"));

        await SetWarehouseRetailAsync(13m);
        await service.OnWarehousePriceChangedAsync(new[] { ProductCode }, new PriceTaskInitiator("张伟", "WarehouseProducts"));
        Assert.Equal(1, await service.GetPendingCountAsync("1001"));
    }

    [Fact]
    public async Task 通知页改价_校验目标值_按开关决定是否同步HQ_改价后转待换标签()
    {
        var service = CreateService();
        await SetWarehouseRetailAsync(12m);
        await service.OnWarehousePriceChangedAsync(new[] { ProductCode }, new PriceTaskInitiator("张伟", "WarehouseProducts"));
        var taskId = (await _db.Queryable<StorePriceUpdateTask>().Where(t => t.StoreCode == "1001").SingleAsync()).Id;

        // 列表打开后仓库又改价：旧目标必须被拒绝
        var stale = await service.ApplyAsync(
            new ApplyStorePriceUpdateTasksRequestDto
            {
                StoreCode = "1001",
                Items = { new ApplyStorePriceUpdateTaskItemDto { TaskId = taskId, ExpectedTargetRetailPrice = 11m } },
            },
            "user-1", "李四", null);
        Assert.Equal(StorePriceUpdateTaskResultCodes.TargetChanged, Assert.Single(stale.Items).Code);

        bool? hqFlag = null;
        _maintenance
            .Setup(m => m.UpdateStorePriceAsync(
                It.IsAny<string>(), It.IsAny<UpdateStoreProductPriceDto>(), "user-1", null, It.IsAny<bool>()))
            .Returns<string, UpdateStoreProductPriceDto, string, List<string>?, bool>(
                async (uuid, dto, _, _, enqueueHq) =>
                {
                    hqFlag = enqueueHq;
                    await _db.Updateable<StoreRetailPrice>()
                        .SetColumns(p => p.StoreRetailPriceValue == dto.RetailPrice)
                        .Where(p => p.UUID == uuid)
                        .ExecuteCommandAsync();
                    return ApiResponse<StoreProductStorePriceDto>.OK(
                        new StoreProductStorePriceDto
                        {
                            HqSync = new ProductHqSyncOperationStatusDto { OperationId = "op-1", Status = "pending" },
                        },
                        "ok");
                });

        var applied = await service.ApplyAsync(
            new ApplyStorePriceUpdateTasksRequestDto
            {
                StoreCode = "1001",
                Items = { new ApplyStorePriceUpdateTaskItemDto { TaskId = taskId, ExpectedTargetRetailPrice = 12m } },
            },
            "user-1", "李四", null);

        var item = Assert.Single(applied.Items);
        Assert.True(item.Success);
        Assert.True(hqFlag);
        Assert.Equal(1, applied.HqSyncSubmittedCount);
        Assert.Equal(StorePriceUpdateTaskKinds.LabelOnly, item.Task!.Kind);
        Assert.Equal("李四", item.Task.PriceAppliedBy);
        Assert.Equal("op-1", item.Task.HqSyncOperationId);

        // 关闭 HQ 同步后：不入队，也不向前端暴露同步信息
        _settings["StorePriceUpdateTasks:SyncToHq"] = "false";
        var disabled = CreateService();
        var otherId = (await _db.Queryable<StorePriceUpdateTask>().Where(t => t.StoreCode == "1002").SingleAsync()).Id;
        var second = await disabled.ApplyAsync(
            new ApplyStorePriceUpdateTasksRequestDto
            {
                StoreCode = "1002",
                Items = { new ApplyStorePriceUpdateTaskItemDto { TaskId = otherId, ExpectedTargetRetailPrice = 12m } },
            },
            "user-1", "李四", null);
        Assert.False(hqFlag);
        Assert.False(second.HqSyncEnabled);
    }

    [Fact]
    public async Task 审计收口挂钩_记录零售价变更时同步生成任务_新建商品不生成()
    {
        var taskService = CreateService();
        var history = new WarehouseProductChangeHistoryService(
            CreateContext(), NullLogger<WarehouseProductChangeHistoryService>.Instance,
            Mock.Of<ICurrentUserService>(), taskService);

        var before = await history.CaptureSnapshotsAsync(new[] { ProductCode });
        await SetWarehouseRetailAsync(12m);
        var after = await history.CaptureSnapshotsAsync(new[] { ProductCode });
        var recorded = await history.RecordChangesAsync(
            before, after,
            new WarehouseProductChangeHistoryContextDto { Action = "Update", Source = "WarehouseProducts", ActorName = "张伟" });

        Assert.Equal(1, recorded);
        var tasks = await _db.Queryable<StorePriceUpdateTask>().ToListAsync();
        Assert.Equal(2, tasks.Count);
        Assert.All(tasks, task => Assert.Equal("WarehouseProducts", task.InitiatorSource));
    }

    [Fact]
    public async Task 设置建议折扣走完整审计_写入历史并为折扣不同的分店生成任务()
    {
        var service = CreateService();

        var changed = await service.SetSuggestedDiscountsWithHistoryAsync(
            new[] { ProductCode, "NOT-EXISTS" }, 0.2m, "陈静", "WarehouseProducts");

        Assert.Equal(1, changed); // 不存在的商品被忽略，不留孤儿折扣
        var history = await _db.Queryable<WarehouseProductChangeHistory>().SingleAsync();
        Assert.Contains("suggestedDiscountRate", history.ChangesJson);
        Assert.Equal("陈静", history.ActorName);
        var tasks = await _db.Queryable<StorePriceUpdateTask>().ToListAsync();
        Assert.Equal(2, tasks.Count);
        Assert.All(tasks, task => Assert.Equal(0.2m, task.TargetDiscountRate));

        // 值未变化：不重复写历史
        Assert.Equal(0, await service.SetSuggestedDiscountsWithHistoryAsync(
            new[] { ProductCode }, 0.2m, "陈静", "WarehouseProducts"));
        Assert.Equal(1, await _db.Queryable<WarehouseProductChangeHistory>().CountAsync());

        // 清空建议折扣（null = 不比较）：未处理的折扣任务自动取消
        Assert.Equal(1, await service.SetSuggestedDiscountsWithHistoryAsync(
            new[] { ProductCode }, null, "陈静", "WarehouseProducts"));
        Assert.Equal(0, await service.GetPendingCountAsync("1001"));
    }

    [Fact]
    public async Task Web监控_按分店与按商品聚合()
    {
        var service = CreateService();
        await SetWarehouseRetailAsync(12m);
        await service.OnWarehousePriceChangedAsync(new[] { ProductCode }, new PriceTaskInitiator("张伟", "WarehouseProducts"));
        var taskId = (await _db.Queryable<StorePriceUpdateTask>().Where(t => t.StoreCode == "1001").SingleAsync()).Id;
        await service.KeepStorePriceAsync(new StorePriceUpdateTaskIdsRequestDto { StoreCode = "1001", TaskIds = { taskId } }, "王五");

        var byStore = await service.GetByStoreAsync(new StorePriceUpdateTaskQueryDto());
        Assert.Equal("1002", byStore[0].StoreCode); // 未完成多的排最前
        Assert.Equal(1, byStore[0].PendingPriceUpdateCount);
        var store1001 = byStore.Single(row => row.StoreCode == "1001");
        Assert.Equal(1, store1001.CompletedCount);
        Assert.Equal(1m, store1001.CompletionRate);
        Assert.Equal("王五", store1001.LastCompletedBy);

        var byProduct = await service.GetByProductAsync(new StorePriceUpdateTaskQueryDto(), onlyIncomplete: true);
        var row = Assert.Single(byProduct.Items);
        Assert.Equal(2, row.StoreCount);
        Assert.Equal(1, row.CompletedStoreCount);
        Assert.Contains(row.Stores, store => store.StoreCode == "1003" && store.State == "Skipped");

        var summary = await service.GetSummaryAsync(new StorePriceUpdateTaskQueryDto());
        Assert.Equal(1, summary.PendingCount);
        Assert.Equal(1, summary.CompletedCount);
        Assert.Equal(0.5m, summary.CompletionRate);
    }

    [Fact]
    public async Task Web监控_多店过滤_StoreCodes与StoreCode取并集()
    {
        var service = CreateService();
        await SetWarehouseRetailAsync(12m);
        await service.OnWarehousePriceChangedAsync(new[] { ProductCode }, new PriceTaskInitiator("张伟", "WarehouseProducts"));

        // 全部：1001 与 1002 各一条未完成
        Assert.Equal(2, (await service.GetByStoreAsync(new StorePriceUpdateTaskQueryDto())).Count);

        // 逗号分隔的多店（含空白、重复、空段）只保留所选分店
        var picked = new StorePriceUpdateTaskQueryDto { StoreCodes = " 1001 ,,1001," };
        var byStore = await service.GetByStoreAsync(picked);
        Assert.Equal("1001", Assert.Single(byStore).StoreCode);
        Assert.Equal(1, (await service.GetSummaryAsync(picked)).PendingCount);
        var tasks = await service.GetPageAsync(picked, null);
        Assert.Equal("1001", Assert.Single(tasks.Items).StoreCode);
        Assert.Equal(1, tasks.PendingCount);

        // 旧的单店参数与新的多店参数合并（并集），选满两家即等同全部
        var union = new StorePriceUpdateTaskQueryDto { StoreCode = "1001", StoreCodes = "1002" };
        Assert.Equal(2, (await service.GetByStoreAsync(union)).Count);
        Assert.Equal(2, (await service.GetPageAsync(union, null)).Total);
        var byProduct = await service.GetByProductAsync(union, onlyIncomplete: true);
        Assert.Equal(2, Assert.Single(byProduct.Items).StoreCount);
    }

    // ---------- 手动撤销 ----------

    [Fact]
    public async Task 撤销_勾选任务_只撤销未完成_已完成与不存在的逐条回报_且不改任何价格()
    {
        var service = CreateService();
        await SetWarehouseRetailAsync(12m);
        await service.OnWarehousePriceChangedAsync(new[] { ProductCode }, new PriceTaskInitiator("张伟", "WarehouseProducts"));
        var task1001 = await _db.Queryable<StorePriceUpdateTask>().Where(t => t.StoreCode == "1001").SingleAsync();
        var task1002 = await _db.Queryable<StorePriceUpdateTask>().Where(t => t.StoreCode == "1002").SingleAsync();
        await service.KeepStorePriceAsync(new StorePriceUpdateTaskIdsRequestDto { StoreCode = "1001", TaskIds = { task1001.Id } }, "王五");

        var result = await service.CancelAsync(
            new CancelStorePriceUpdateTasksRequestDto { TaskIds = { task1001.Id, task1002.Id, 999999 }, Note = new string('备', 300) },
            "管理员A"
        );

        Assert.Equal(1, result.CancelledCount);
        Assert.Equal(2, result.SkippedCount);
        Assert.Equal(StorePriceUpdateTaskResultCodes.NotPending, result.Items.Single(i => i.TaskId == task1001.Id).Code);
        Assert.Equal(StorePriceUpdateTaskResultCodes.NotFound, result.Items.Single(i => i.TaskId == 999999).Code);
        Assert.True(result.Items.Single(i => i.TaskId == task1002.Id).Success);

        var cancelled = await _db.Queryable<StorePriceUpdateTask>().Where(t => t.Id == task1002.Id).SingleAsync();
        Assert.Equal(StorePriceUpdateTaskStatuses.Cancelled, cancelled.Status);
        Assert.Equal(StorePriceUpdateTaskCancelReasons.ManualCancel, cancelled.CancelReason);
        Assert.Equal("管理员A", cancelled.CancelledBy);
        Assert.NotNull(cancelled.CancelledAtUtc);
        Assert.Equal(CancelStorePriceUpdateTasksRequestDto.MaxNoteLength, cancelled.CancelNote!.Length);

        // 已完成的任务原样保留，且撤销不碰价格
        var completed = await _db.Queryable<StorePriceUpdateTask>().Where(t => t.Id == task1001.Id).SingleAsync();
        Assert.Equal(StorePriceUpdateTaskStatuses.Completed, completed.Status);
        Assert.Null(completed.CancelledBy);
        var prices = await _db.Queryable<StoreRetailPrice>().ToListAsync();
        Assert.All(prices, price => Assert.True(price.StoreRetailPriceValue is 10m or 9m));
    }

    [Fact]
    public async Task 撤销_按商品_可限定分店_重复撤销不再命中()
    {
        var service = CreateService();
        await SetWarehouseRetailAsync(12m);
        await service.OnWarehousePriceChangedAsync(new[] { ProductCode }, new PriceTaskInitiator("张伟", "WarehouseProducts"));

        var first = await service.CancelAsync(
            new CancelStorePriceUpdateTasksRequestDto { ProductCode = ProductCode, StoreCodes = "1001" },
            "管理员A"
        );
        Assert.Equal(1, first.CancelledCount);
        Assert.Equal(
            StorePriceUpdateTaskStatuses.Pending,
            (await _db.Queryable<StorePriceUpdateTask>().Where(t => t.StoreCode == "1002").SingleAsync()).Status
        );

        // 1001 已无未完成任务：再次撤销什么也不命中
        var again = await service.CancelAsync(
            new CancelStorePriceUpdateTasksRequestDto { ProductCode = ProductCode, StoreCodes = "1001" },
            "管理员A"
        );
        Assert.Empty(again.Items);

        // 不限定分店：撤销该商品剩余的全部未完成任务
        var rest = await service.CancelAsync(new CancelStorePriceUpdateTasksRequestDto { ProductCode = ProductCode }, "管理员A");
        Assert.Equal(1, rest.CancelledCount);
        Assert.Equal(0, await _db.Queryable<StorePriceUpdateTask>().Where(t => t.Status == StorePriceUpdateTaskStatuses.Pending).CountAsync());
    }

    [Fact]
    public async Task 撤销后_对账不复活_不计入完成率分母_明细可见撤销留痕_仓库再改价会生成新任务()
    {
        var service = CreateService();
        await SetWarehouseRetailAsync(12m);
        await service.OnWarehousePriceChangedAsync(new[] { ProductCode }, new PriceTaskInitiator("张伟", "WarehouseProducts"));
        await service.CancelAsync(new CancelStorePriceUpdateTasksRequestDto { ProductCode = ProductCode }, "管理员A");

        // 后台对账只看 Pending：已撤销的任务不会被复活
        await service.ReconcilePendingAsync(null);
        Assert.Equal(0, await _db.Queryable<StorePriceUpdateTask>().Where(t => t.Status == StorePriceUpdateTaskStatuses.Pending).CountAsync());

        var summary = await service.GetSummaryAsync(new StorePriceUpdateTaskQueryDto());
        Assert.Equal(0, summary.PendingCount);
        Assert.Equal(0, summary.CompletedCount);
        Assert.Equal(0m, summary.CompletionRate);

        var page = await service.GetPageAsync(new StorePriceUpdateTaskQueryDto { Status = "All" }, null);
        Assert.Equal(2, page.Total);
        Assert.All(page.Items, item =>
        {
            Assert.Equal(StorePriceUpdateTaskStatuses.Cancelled, item.Status);
            Assert.Equal(StorePriceUpdateTaskCancelReasons.ManualCancel, item.CancelReason);
            Assert.Equal("管理员A", item.CancelledBy);
            Assert.NotNull(item.CancelledAtUtc);
        });

        // 仓库再次改价：同店同商品可重新生成 Pending（过滤唯一索引只约束 Pending，不冲突）
        await SetWarehouseRetailAsync(13m);
        await service.OnWarehousePriceChangedAsync(new[] { ProductCode }, new PriceTaskInitiator("张伟", "WarehouseProducts"));
        Assert.Equal(2, await _db.Queryable<StorePriceUpdateTask>().Where(t => t.Status == StorePriceUpdateTaskStatuses.Pending).CountAsync());
    }

    // ---------------------------------------------------------------------

    private StorePriceUpdateTaskService CreateService()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(_settings).Build();
        var provider = new Mock<IServiceProvider>();
        provider
            .Setup(p => p.GetService(typeof(IStoreProductMaintenanceReactService)))
            .Returns(_maintenance.Object);
        StorePriceUpdateTaskService? service = null;
        // 与生产一致：审计服务依赖任务服务，任务服务再通过容器延迟取回审计服务。
        provider
            .Setup(p => p.GetService(typeof(IWarehouseProductChangeHistoryService)))
            .Returns(() => new WarehouseProductChangeHistoryService(
                CreateContext(),
                NullLogger<WarehouseProductChangeHistoryService>.Instance,
                Mock.Of<ICurrentUserService>(),
                service));
        service = new StorePriceUpdateTaskService(
            CreateContext(),
            NullLogger<StorePriceUpdateTaskService>.Instance,
            configuration,
            _summary,
            provider.Object
        );
        return service;
    }

    private SqlSugarContext CreateContext()
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, _db);
        return context;
    }

    private void InsertStore(string storeCode) =>
        _db.Insertable(new Store { StoreCode = storeCode, StoreName = $"分店{storeCode}", IsActive = true })
            .ExecuteCommand();

    private void InsertStorePrice(string storeCode, decimal retail, decimal? discount, bool isSpecial = false) =>
        _db.Insertable(
                new StoreRetailPrice
                {
                    StoreCode = storeCode,
                    ProductCode = ProductCode,
                    StoreProductCode = storeCode + ProductCode,
                    StoreRetailPriceValue = retail,
                    DiscountRate = discount,
                    IsSpecialProduct = isSpecial,
                    IsActive = true,
                }
            )
            .ExecuteCommand();

    private Task SetWarehouseRetailAsync(decimal retail) =>
        _db.Updateable<Product>()
            .SetColumns(p => p.RetailPrice == retail)
            .Where(p => p.ProductCode == ProductCode)
            .ExecuteCommandAsync();

    private Task SetStoreRetailAsync(string storeCode, decimal retail) =>
        _db.Updateable<StoreRetailPrice>()
            .SetColumns(p => p.StoreRetailPriceValue == retail)
            .Where(p => p.StoreCode == storeCode && p.ProductCode == ProductCode)
            .ExecuteCommandAsync();
}
