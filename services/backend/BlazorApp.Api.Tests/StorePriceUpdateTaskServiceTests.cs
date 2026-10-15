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

    private sealed class RecordingHqEnqueuer : IWarehouseStorePriceHqSyncEnqueuer
    {
        public List<(string ProductCode, List<string> StoreCodes, List<string> FieldMask, string Source, bool InTransaction)> Calls { get; } = new();
        public Exception? ThrowOnEnqueue { get; set; }

        public Task<ProductHqSyncOperationStatusDto?> EnqueueAsync(
            ISqlSugarClient transactionDb,
            string productCode,
            IEnumerable<string?> storeCodes,
            IReadOnlyCollection<string> fieldMask,
            string updatedBy,
            string source,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((
                productCode,
                storeCodes.Where(code => code != null).Select(code => code!).ToList(),
                fieldMask.ToList(),
                source,
                transactionDb.Ado.Transaction != null));
            if (ThrowOnEnqueue != null)
            {
                throw ThrowOnEnqueue;
            }
            return Task.FromResult<ProductHqSyncOperationStatusDto?>(
                new ProductHqSyncOperationStatusDto { OperationId = $"label-op-{Calls.Count}" });
        }
    }

    /// <summary>仓库改价后 1001 / 1002 各有一条待办任务；把 1001 的价改成一致，使其成为「待换标签」。</summary>
    private async Task<(long LabelTaskId, long OtherTaskId)> PrepareLabelOnlyTaskForStore1001Async(StorePriceUpdateTaskService service)
    {
        await SetWarehouseRetailAsync(12m);
        await service.OnWarehousePriceChangedAsync(new[] { ProductCode }, new PriceTaskInitiator("张伟", "WarehouseProducts"));
        await SetStoreRetailAsync("1001", 12m);
        await service.ReconcilePendingAsync("1001");
        var labelId = (await _db.Queryable<StorePriceUpdateTask>().Where(t => t.StoreCode == "1001").SingleAsync()).Id;
        var otherId = (await _db.Queryable<StorePriceUpdateTask>().Where(t => t.StoreCode == "1002").SingleAsync()).Id;
        return (labelId, otherId);
    }

    [Fact]
    public async Task 标记换标签完成时才把该分店商品同步HQ_未执行的分店不入队()
    {
        var enqueuer = new RecordingHqEnqueuer();
        var service = CreateService(enqueuer);
        var (labelTaskId, otherTaskId) = await PrepareLabelOnlyTaskForStore1001Async(service);

        var marked = await service.MarkLabelsAsync(
            new MarkStorePriceUpdateTaskLabelsRequestDto { StoreCode = "1001", TaskIds = { labelTaskId } }, "李四");

        Assert.True(Assert.Single(marked.Items).Success);
        Assert.Equal(1, marked.HqSyncSubmittedCount);
        var call = Assert.Single(enqueuer.Calls);
        Assert.Equal(ProductCode, call.ProductCode);
        Assert.Equal(new[] { "1001" }, call.StoreCodes);
        Assert.Contains(ProductMaintenanceHqFieldMasks.StoreRetailPrice, call.FieldMask);
        Assert.Contains(ProductMaintenanceHqFieldMasks.StoreDiscountRate, call.FieldMask);
        Assert.Equal("price-task-label-done", call.Source);
        Assert.True(call.InTransaction, "入队必须与标记完成同事务");

        var done = await _db.Queryable<StorePriceUpdateTask>().SingleAsync(t => t.Id == labelTaskId);
        Assert.Equal(StorePriceUpdateTaskStatuses.Completed, done.Status);
        Assert.Equal("label-op-1", done.HqSyncOperationKey);
        // 1002 的任务没人执行：保持待办，也没有任何 HQ 入队。
        var untouched = await _db.Queryable<StorePriceUpdateTask>().SingleAsync(t => t.Id == otherTaskId);
        Assert.Equal(StorePriceUpdateTaskStatuses.Pending, untouched.Status);
        Assert.Null(untouched.HqSyncOperationKey);
    }

    [Fact]
    public async Task 标记换标签时入队失败_任务保持待办并提示重试()
    {
        var enqueuer = new RecordingHqEnqueuer { ThrowOnEnqueue = new InvalidOperationException("outbox 不可用") };
        var service = CreateService(enqueuer);
        var (labelTaskId, _) = await PrepareLabelOnlyTaskForStore1001Async(service);

        var marked = await service.MarkLabelsAsync(
            new MarkStorePriceUpdateTaskLabelsRequestDto { StoreCode = "1001", TaskIds = { labelTaskId } }, "李四");

        var item = Assert.Single(marked.Items);
        Assert.False(item.Success);
        Assert.Equal(StorePriceUpdateTaskResultCodes.Failed, item.Code);
        var task = await _db.Queryable<StorePriceUpdateTask>().SingleAsync(t => t.Id == labelTaskId);
        Assert.Equal(StorePriceUpdateTaskStatuses.Pending, task.Status);
        Assert.Null(task.CompletionMode);
        Assert.Null(task.HqSyncOperationKey);
    }

    [Fact]
    public async Task 总部同步开关关闭_标记换标签照常完成但不入队()
    {
        _settings["StorePriceUpdateTasks:SyncToHq"] = "false";
        var enqueuer = new RecordingHqEnqueuer();
        var service = CreateService(enqueuer);
        var (labelTaskId, _) = await PrepareLabelOnlyTaskForStore1001Async(service);

        var marked = await service.MarkLabelsAsync(
            new MarkStorePriceUpdateTaskLabelsRequestDto { StoreCode = "1001", TaskIds = { labelTaskId } }, "李四");

        Assert.True(Assert.Single(marked.Items).Success);
        Assert.Empty(enqueuer.Calls);
        Assert.Equal(0, marked.HqSyncSubmittedCount);
    }

    [Fact]
    public async Task 通知页已改价且已入过HQ队列的任务_标记换标签不重复入队()
    {
        var enqueuer = new RecordingHqEnqueuer();
        var service = CreateService(enqueuer);
        var (labelTaskId, _) = await PrepareLabelOnlyTaskForStore1001Async(service);
        await _db.Updateable<StorePriceUpdateTask>()
            .SetColumns(t => new StorePriceUpdateTask { PriceAppliedAtUtc = DateTime.UtcNow, HqSyncOperationKey = "applied-op" })
            .Where(t => t.Id == labelTaskId)
            .ExecuteCommandAsync();

        var marked = await service.MarkLabelsAsync(
            new MarkStorePriceUpdateTaskLabelsRequestDto { StoreCode = "1001", TaskIds = { labelTaskId } }, "李四");

        Assert.True(Assert.Single(marked.Items).Success);
        Assert.Empty(enqueuer.Calls);
        var task = await _db.Queryable<StorePriceUpdateTask>().SingleAsync(t => t.Id == labelTaskId);
        Assert.Equal("applied-op", task.HqSyncOperationKey);
    }

    [Fact]
    public async Task 已完成任务再打一张标签_只累加打印次数不再入队()
    {
        var enqueuer = new RecordingHqEnqueuer();
        var service = CreateService(enqueuer);
        var (labelTaskId, _) = await PrepareLabelOnlyTaskForStore1001Async(service);
        var request = new MarkStorePriceUpdateTaskLabelsRequestDto { StoreCode = "1001", TaskIds = { labelTaskId } };

        await service.MarkLabelsAsync(request, "李四");
        await service.MarkLabelsAsync(request, "李四");

        Assert.Single(enqueuer.Calls);
        var task = await _db.Queryable<StorePriceUpdateTask>().SingleAsync(t => t.Id == labelTaskId);
        Assert.Equal(2, task.LabelPrintCount);
    }

    [Fact]
    public async Task 审计收口挂钩_取消生成价格任务时只记审计不建任务()
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
            new WarehouseProductChangeHistoryContextDto
            {
                Action = "Update", Source = "WarehouseProducts", ActorName = "张伟", SkipPriceTasks = true,
            });

        Assert.Equal(1, recorded); // 审计照记
        Assert.Empty(await _db.Queryable<StorePriceUpdateTask>().ToListAsync());
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

    // ---------------------------------------------------------------------
    // 超期（任务须在 7 天内完成）：从首次发起起算，移动端提醒 / 锁报表与 Web 监控共用同一口径
    // ---------------------------------------------------------------------

    [Fact]
    public async Task 超期从首次发起算_仓库再次改价不重新计时()
    {
        var service = CreateService();
        await SetWarehouseRetailAsync(12m);
        await service.OnWarehousePriceChangedAsync(new[] { ProductCode }, new PriceTaskInitiator("张伟", "WarehouseProducts"));
        await SetTaskCreatedAtAsync("1001", DateTime.UtcNow.AddDays(-8));

        // 仓库再次改价：任务被刷新（InitiatedAtUtc 重写、ChangeCount 加一），但首次发起时间不能被重置。
        await SetWarehouseRetailAsync(13m);
        await service.OnWarehousePriceChangedAsync(new[] { ProductCode }, new PriceTaskInitiator("李四", "WarehouseProducts"));

        var page = await service.GetPageAsync(
            new StorePriceUpdateTaskQueryDto { StoreCode = "1001", OverdueOnly = true },
            null
        );
        var task = Assert.Single(page.Items);
        Assert.Equal(2, task.ChangeCount);
        Assert.True(task.InitiatedAtUtc > task.FirstInitiatedAtUtc.AddDays(7), "刷新后的发起时间应明显晚于首次发起时间");
        Assert.True(task.IsOverdue);
        Assert.Equal(8, task.WaitingDays);
        Assert.Equal(1, task.OverdueByDays);
        Assert.Equal(1, page.PendingOverdueCount);
        Assert.Equal(7, page.OverdueDays);
    }

    [Fact]
    public async Task 超期时限默认7天_可用配置调整_未超期的任务不计入()
    {
        await SetWarehouseRetailAsync(12m);
        var defaultService = CreateService();
        await defaultService.OnWarehousePriceChangedAsync(new[] { ProductCode }, new PriceTaskInitiator("张伟", "WarehouseProducts"));
        await SetTaskCreatedAtAsync("1001", DateTime.UtcNow.AddDays(-5));

        var before = await defaultService.GetPageAsync(new StorePriceUpdateTaskQueryDto { StoreCode = "1001" }, null);
        var notOverdue = Assert.Single(before.Items);
        Assert.False(notOverdue.IsOverdue);
        Assert.Equal(0, notOverdue.OverdueByDays);
        Assert.Equal(5, notOverdue.WaitingDays);
        Assert.Equal(0, before.PendingOverdueCount);
        Assert.Empty((await defaultService.GetPageAsync(
            new StorePriceUpdateTaskQueryDto { StoreCode = "1001", OverdueOnly = true }, null)).Items);

        _settings["StorePriceUpdateTasks:OverdueDays"] = "3";
        var strictService = CreateService();
        var after = await strictService.GetPageAsync(new StorePriceUpdateTaskQueryDto { StoreCode = "1001" }, null);
        var overdue = Assert.Single(after.Items);
        Assert.True(overdue.IsOverdue);
        Assert.Equal(2, overdue.OverdueByDays);
        Assert.Equal(3, after.OverdueDays);
    }

    [Fact]
    public async Task 已完成的任务不算超期()
    {
        var service = CreateService();
        await SetWarehouseRetailAsync(12m);
        await service.OnWarehousePriceChangedAsync(new[] { ProductCode }, new PriceTaskInitiator("张伟", "WarehouseProducts"));
        await SetTaskCreatedAtAsync("1001", DateTime.UtcNow.AddDays(-30));
        var taskId = (await _db.Queryable<StorePriceUpdateTask>().Where(t => t.StoreCode == "1001").SingleAsync()).Id;
        await service.KeepStorePriceAsync(new StorePriceUpdateTaskIdsRequestDto { StoreCode = "1001", TaskIds = { taskId } }, "王五");

        var completed = await service.GetPageAsync(
            new StorePriceUpdateTaskQueryDto { StoreCode = "1001", Status = "Completed" },
            null
        );
        var task = Assert.Single(completed.Items);
        Assert.False(task.IsOverdue);
        Assert.Equal(0, task.WaitingDays);
        Assert.Equal(0, task.OverdueByDays);
        var summaries = await service.GetStoreSummariesAsync(null);
        Assert.Equal(0, summaries.Stores.Single(row => row.StoreCode == "1001").OverdueCount);
    }

    [Fact]
    public async Task 按分店汇总_统计未完成与超期_只返回可访问的启用分店()
    {
        var service = CreateService();
        await SetWarehouseRetailAsync(12m);
        await service.OnWarehousePriceChangedAsync(new[] { ProductCode }, new PriceTaskInitiator("张伟", "WarehouseProducts"));
        await SetTaskCreatedAtAsync("1001", DateTime.UtcNow.AddDays(-8));

        var all = await service.GetStoreSummariesAsync(null);
        Assert.Equal(7, all.OverdueDays);
        // 没有任务的分店（1003 是特殊商品，被跳过）也要返回，各计数为 0，方便前端显示「全部完成」。
        Assert.Equal(new[] { "1001", "1002", "1003" }, all.Stores.Select(row => row.StoreCode).ToArray());
        var store1001 = all.Stores.Single(row => row.StoreCode == "1001");
        Assert.Equal("分店1001", store1001.StoreName);
        Assert.Equal(1, store1001.PendingPriceUpdateCount);
        Assert.Equal(0, store1001.PendingLabelOnlyCount);
        Assert.Equal(1, store1001.PendingCount);
        Assert.Equal(1, store1001.OverdueCount);
        var store1002 = all.Stores.Single(row => row.StoreCode == "1002");
        Assert.Equal(1, store1002.PendingCount);
        Assert.Equal(0, store1002.OverdueCount);
        var store1003 = all.Stores.Single(row => row.StoreCode == "1003");
        Assert.Equal(0, store1003.PendingCount);
        Assert.Equal(0, store1003.OverdueCount);

        var scoped = await service.GetStoreSummariesAsync(new[] { "1002", "9999" });
        Assert.Equal("1002", Assert.Single(scoped.Stores).StoreCode);
        Assert.Empty((await service.GetStoreSummariesAsync(Array.Empty<string>())).Stores);
    }

    [Fact]
    public async Task 按分店汇总_区分需改价与待换标签_停用分店不返回()
    {
        var service = CreateService();
        await SetWarehouseRetailAsync(12m);
        await service.OnWarehousePriceChangedAsync(new[] { ProductCode }, new PriceTaskInitiator("张伟", "WarehouseProducts"));
        // 1002 店员先把价格改成与仓库一致：任务变成待换标签（货架标签仍是旧价）
        await SetStoreRetailAsync("1002", 12m);
        await service.ReconcilePendingAsync("1002");
        _db.Ado.ExecuteCommand("UPDATE Store SET IsActive = 0 WHERE StoreCode = '1003'");

        var summaries = await service.GetStoreSummariesAsync(null);
        Assert.Equal(new[] { "1001", "1002" }, summaries.Stores.Select(row => row.StoreCode).ToArray());
        var store1002 = summaries.Stores.Single(row => row.StoreCode == "1002");
        Assert.Equal(0, store1002.PendingPriceUpdateCount);
        Assert.Equal(1, store1002.PendingLabelOnlyCount);
        Assert.Equal(1, store1002.PendingCount);
    }

    [Fact]
    public async Task 超期筛选_等得最久的排最前_不限分店时跨店排序()
    {
        var service = CreateService();
        await SetWarehouseRetailAsync(12m);
        await service.OnWarehousePriceChangedAsync(new[] { ProductCode }, new PriceTaskInitiator("张伟", "WarehouseProducts"));
        await SetTaskCreatedAtAsync("1001", DateTime.UtcNow.AddDays(-8));
        await SetTaskCreatedAtAsync("1002", DateTime.UtcNow.AddDays(-10));

        var page = await service.GetPageAsync(new StorePriceUpdateTaskQueryDto { OverdueOnly = true }, null);

        Assert.Equal(new[] { "1002", "1001" }, page.Items.Select(item => item.StoreCode).ToArray());
        Assert.Equal(2, page.Total);
        Assert.Equal(new[] { 3, 1 }, page.Items.Select(item => item.OverdueByDays).ToArray());
    }

    [Fact]
    public async Task Web监控的超期数与移动端同口径_按首次发起起算()
    {
        var service = CreateService();
        await SetWarehouseRetailAsync(12m);
        await service.OnWarehousePriceChangedAsync(new[] { ProductCode }, new PriceTaskInitiator("张伟", "WarehouseProducts"));
        await SetTaskCreatedAtAsync("1001", DateTime.UtcNow.AddDays(-8));
        // 再次改价刷新发起时间：旧口径（按 InitiatedAtUtc）会因此把它算成「刚发起」，不再超期。
        await SetWarehouseRetailAsync(13m);
        await service.OnWarehousePriceChangedAsync(new[] { ProductCode }, new PriceTaskInitiator("李四", "WarehouseProducts"));

        var summary = await service.GetSummaryAsync(new StorePriceUpdateTaskQueryDto());

        Assert.Equal(7, summary.OverdueDays);
        Assert.Equal(1, summary.OverdueCount);
        Assert.Equal(1, summary.OverdueStoreCount);
    }

    private Task SetTaskCreatedAtAsync(string storeCode, DateTime createdAtUtc) =>
        _db.Updateable<StorePriceUpdateTask>()
            .SetColumns(task => task.CreatedAtUtc == createdAtUtc)
            .Where(task => task.StoreCode == storeCode)
            .ExecuteCommandAsync();

    // ---------------------------------------------------------------------

    private StorePriceUpdateTaskService CreateService(IWarehouseStorePriceHqSyncEnqueuer? hqEnqueuer = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(_settings).Build();
        var provider = new Mock<IServiceProvider>();
        provider
            .Setup(p => p.GetService(typeof(IStoreProductMaintenanceReactService)))
            .Returns(_maintenance.Object);
        if (hqEnqueuer != null)
        {
            // 与生产一致：任务服务通过容器延迟取入队器（入队器依赖链会回到本服务，不能构造期注入）。
            provider
                .Setup(p => p.GetService(typeof(IWarehouseStorePriceHqSyncEnqueuer)))
                .Returns(hqEnqueuer);
        }
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
