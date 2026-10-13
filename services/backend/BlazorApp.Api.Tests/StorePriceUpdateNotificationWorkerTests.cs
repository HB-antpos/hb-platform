using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services.Background;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// N3-a 门店价格更新合并推送：后台 worker 每轮的核心逻辑（SQLite 夹具，发布走真实的通知发布器 + 接收人解析）。
/// 时间全部由测试传入 RunOnceAsync，通知发布器用同一个可冻结的 TimeProvider。
/// </summary>
public sealed class StorePriceUpdateNotificationWorkerTests : IDisposable
{
    private const string Store1013 = "1013";
    private const string Store2001 = "2001";
    private const string Store3001 = "3001";
    private const string ManagerGuid = "user-manager";
    private const string CashierGuid = "user-cashier";
    private const string InactiveGuid = "user-inactive";
    private const string Manager2001Guid = "user-manager-2001";

    private static readonly DateTime T0 = new(2026, 10, 15, 1, 0, 0, DateTimeKind.Utc);

    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;
    private readonly MutableTimeProvider _time = new(T0);
    private readonly Mock<IRoleService> _roleService = new(MockBehavior.Strict);
    private readonly RecordingDispatcher _dispatcher = new();
    private readonly SpyPublisher _publisher;
    private readonly ServiceProvider _provider;
    private int _productSeq;

    public StorePriceUpdateNotificationWorkerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
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
            typeof(User),
            typeof(Store),
            typeof(UserStore),
            typeof(AppNotification),
            typeof(AppNotificationRecipient),
            typeof(StorePriceUpdateNotice)
        );
        // 任务表走与生产同款的迁移器（SQLite 分支），含“同店同商品至多一条 Pending”的过滤唯一索引。
        StorePriceUpdateTaskSchemaMigrator.EnsureAsync(_db, NullLogger.Instance).GetAwaiter().GetResult();
        SeedUsersAndStores();

        _roleService
            .Setup(service => service.UserHasPermissionAsync(ManagerGuid, Permissions.StoreProducts.PriceUpdates))
            .ReturnsAsync(ApiResponse<bool>.OK(true));
        _roleService
            .Setup(service => service.UserHasPermissionAsync(CashierGuid, Permissions.StoreProducts.PriceUpdates))
            .ReturnsAsync(ApiResponse<bool>.OK(false));
        _roleService
            .Setup(service => service.UserHasPermissionAsync(Manager2001Guid, Permissions.StoreProducts.PriceUpdates))
            .ReturnsAsync(ApiResponse<bool>.OK(true));

        var context = CreateSqlSugarContext(_db);
        _publisher = new SpyPublisher(
            new NotificationPublisher(
                context,
                new NotificationRecipientResolver(
                    context,
                    _roleService.Object,
                    NullLogger<NotificationRecipientResolver>.Instance),
                _dispatcher,
                _time,
                NullLogger<NotificationPublisher>.Instance),
            _db);

        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddSingleton<INotificationPublisher>(_publisher);
        _provider = services.BuildServiceProvider();
    }

    // ---------------------------------------------------------------- 合并窗口

    [Fact]
    public async Task 合并窗口未满_本轮不发也不写Notice()
    {
        MarkBaselineDone();
        AddTask(Store1013, T0.AddMinutes(-29));

        await RunAsync(T0);

        Assert.Empty(await _db.Queryable<AppNotification>().ToListAsync());
        Assert.Empty(_publisher.Requests);
        Assert.Empty(await RealNoticesAsync());
    }

    [Fact]
    public async Task 窗口刚好满足就发_最早一条等够_窗口内后来的任务一并带上_其他门店的新任务不带()
    {
        MarkBaselineDone();
        var ripe = AddTask(Store1013, T0.AddMinutes(-30));
        var fresh = AddTask(Store1013, T0.AddMinutes(-5), StorePriceUpdateTaskKinds.LabelOnly);
        var otherStoreFresh = AddTask(Store2001, T0.AddMinutes(-5));

        await RunAsync(T0);

        var notification = Assert.Single(await _db.Queryable<AppNotification>().ToListAsync());
        Assert.Equal(Store1013, notification.StoreCode);
        var request = Assert.Single(_publisher.Requests);
        Assert.Equal("1", request.Data!["priceUpdateCount"]);
        Assert.Equal("1", request.Data["labelOnlyCount"]);
        var notices = await RealNoticesAsync();
        Assert.Equal(new[] { ripe, fresh }.Order(), notices.Select(n => n.TaskId).Order());
        Assert.DoesNotContain(notices, n => n.TaskId == otherStoreFresh);
    }

    [Fact]
    public async Task 同一门店多条任务只发一条_只发给持有权限的门店成员()
    {
        MarkBaselineDone();
        for (var i = 0; i < 5; i++)
        {
            AddTask(Store1013, T0.AddMinutes(-40 - i));
        }

        await RunAsync(T0);

        var notification = Assert.Single(await _db.Queryable<AppNotification>().ToListAsync());
        var recipients = await _db.Queryable<AppNotificationRecipient>()
            .Where(row => row.NotificationId == notification.NotificationId)
            .Select(row => row.UserGUID)
            .ToListAsync();
        // 收银员没有 StoreProducts.PriceUpdates，停用账号不收。
        Assert.Equal([ManagerGuid], recipients);
        Assert.Equal(5, (await RealNoticesAsync()).Count);
    }

    [Fact]
    public async Task 两家店各发各的_只有窗口满的那家发()
    {
        MarkBaselineDone();
        AddTask(Store1013, T0.AddMinutes(-60));
        AddTask(Store2001, T0.AddMinutes(-45));
        AddTask(Store2001, T0.AddMinutes(-44));

        await RunAsync(T0);

        var notifications = await _db.Queryable<AppNotification>().OrderBy(n => n.StoreCode).ToListAsync();
        Assert.Equal([Store1013, Store2001], notifications.Select(n => n.StoreCode));
        var for2001 = await _db.Queryable<AppNotificationRecipient>()
            .Where(row => row.NotificationId == notifications[1].NotificationId)
            .Select(row => row.UserGUID)
            .ToListAsync();
        Assert.Equal([Manager2001Guid], for2001);
        Assert.Equal("2", _publisher.Requests.Single(r => r.StoreCode == Store2001).Data!["priceUpdateCount"]);

        // 另一家店窗口没满，只有 1013 的新任务不会触发 2001。
        AddTask(Store1013, T0.AddMinutes(-1));
        await RunAsync(T0.AddMinutes(1));
        Assert.Equal(2, await _db.Queryable<AppNotification>().CountAsync());
    }

    [Fact]
    public async Task MergeWindowMinutes配置生效_默认30分钟_可改小()
    {
        MarkBaselineDone();
        AddTask(Store1013, T0.AddMinutes(-6));

        await RunAsync(T0);
        Assert.Empty(_publisher.Requests);

        var shortWindow = CreateWorker(new Dictionary<string, string?> { ["StorePriceUpdateNotifications:MergeWindowMinutes"] = "5" });
        _time.Now = T0;
        await shortWindow.RunOnceAsync(T0, CancellationToken.None);
        Assert.Single(_publisher.Requests);
    }

    // ---------------------------------------------------------------- 未通知集合

    [Fact]
    public async Task 已通知后不重发_再跑多少轮都不再发布()
    {
        MarkBaselineDone();
        AddTask(Store1013, T0.AddMinutes(-40));

        await RunAsync(T0);
        await RunAsync(T0.AddMinutes(5));
        await RunAsync(T0.AddMinutes(60));

        Assert.Single(_publisher.Requests);
        Assert.Single(_dispatcher.Sent);
        Assert.Equal(1, await _db.Queryable<AppNotification>().CountAsync());
    }

    [Fact]
    public async Task ChangeCount变大后重新进入未通知_等窗口满再发新的一条()
    {
        MarkBaselineDone();
        var taskId = AddTask(Store1013, T0.AddMinutes(-40));
        await RunAsync(T0);
        Assert.Equal(1, (await RealNoticesAsync()).Single().ChangeCount);

        // 仓库再次调价：变更次数 +1，InitiatedAtUtc 刷新为最新一次变更的时间。
        BumpChangeCount(taskId, changeCount: 2, initiatedAtUtc: T0.AddMinutes(1));

        await RunAsync(T0.AddMinutes(2));
        Assert.Single(_publisher.Requests);

        await RunAsync(T0.AddMinutes(32));
        Assert.Equal(2, _publisher.Requests.Count);
        Assert.NotEqual(_publisher.Requests[0].DedupeKey, _publisher.Requests[1].DedupeKey);
        var notice = Assert.Single(await RealNoticesAsync());
        Assert.Equal(taskId, notice.TaskId);
        Assert.Equal(2, notice.ChangeCount);
    }

    [Fact]
    public async Task 对账worker整行重写任务_UpdatedAtUtc变化不会让已通知的任务再次通知()
    {
        MarkBaselineDone();
        var taskId = AddTask(Store1013, T0.AddMinutes(-40));
        await RunAsync(T0);

        var rewrittenAt = T0.AddMinutes(10);
        _db.Updateable<StorePriceUpdateTask>()
            .SetColumns(task => new StorePriceUpdateTask { UpdatedAtUtc = rewrittenAt })
            .Where(task => task.Id == taskId)
            .ExecuteCommand();
        await RunAsync(T0.AddMinutes(60));

        Assert.Single(_publisher.Requests);
    }

    [Fact]
    public async Task 已取消和已完成的任务不计入_也不会单独触发通知()
    {
        MarkBaselineDone();
        AddTask(Store1013, T0.AddMinutes(-60), status: StorePriceUpdateTaskStatuses.Cancelled);
        AddTask(Store1013, T0.AddMinutes(-60), status: StorePriceUpdateTaskStatuses.Completed);

        await RunAsync(T0);
        Assert.Empty(_publisher.Requests);

        AddTask(Store1013, T0.AddMinutes(-50));
        await RunAsync(T0.AddMinutes(1));

        var request = Assert.Single(_publisher.Requests);
        Assert.Equal("1", request.Data!["priceUpdateCount"]);
        Assert.Equal("0", request.Data["labelOnlyCount"]);
        Assert.Single(await RealNoticesAsync());
    }

    [Fact]
    public async Task 需改价与仅换标签分别计数_正文是英文且包含两个数量()
    {
        MarkBaselineDone();
        for (var i = 0; i < 3; i++)
        {
            AddTask(Store1013, T0.AddMinutes(-60), StorePriceUpdateTaskKinds.PriceUpdate);
        }

        for (var i = 0; i < 2; i++)
        {
            AddTask(Store1013, T0.AddMinutes(-60), StorePriceUpdateTaskKinds.LabelOnly);
        }

        await RunAsync(T0);

        var request = Assert.Single(_publisher.Requests);
        Assert.Equal(AppNotificationTypes.StorePriceUpdates, request.Type);
        Assert.Equal("store.price_updates", request.Type);
        Assert.Equal("Price updates for your store", request.Title);
        Assert.Equal(
            "3 products need a price change and 2 need a new shelf label. Open Price Updates to review.",
            request.Body);
        Assert.Equal(AppNotificationPriorities.Normal, request.Priority);
        Assert.Equal(Store1013, request.StoreCode);
        Assert.Equal("system:store-price-updates", request.CreatedBy);
        Assert.Equal(Store1013, request.Audience.StoreCode);
        Assert.Equal(Permissions.StoreProducts.PriceUpdates, request.Audience.RequiredPermission);
        Assert.Empty(request.Audience.UserGuids);
        // 跳转参数只有标识与数量。
        Assert.Equal(
            ["labelOnlyCount=2", "priceUpdateCount=3", "storeCode=1013"],
            request.Data!.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
        Assert.All(request.Title + request.Body, ch => Assert.True(ch < 128, "通知文案只用英文"));
    }

    [Theory]
    [InlineData(1, 0, "1 product needs a price change. Open Price Updates to review.")]
    [InlineData(4, 0, "4 products need a price change. Open Price Updates to review.")]
    [InlineData(0, 1, "1 product needs a new shelf label. Open Price Updates to review.")]
    [InlineData(0, 4, "4 products need a new shelf label. Open Price Updates to review.")]
    [InlineData(1, 1, "1 product needs a price change and 1 needs a new shelf label. Open Price Updates to review.")]
    [InlineData(2, 1, "2 products need a price change and 1 needs a new shelf label. Open Price Updates to review.")]
    [InlineData(1, 2, "1 product needs a price change and 2 need a new shelf label. Open Price Updates to review.")]
    public void BuildBody_数量为0的部分省略措辞_单复数正确(int price, int label, string expected)
    {
        Assert.Equal(expected, StorePriceUpdateNotificationWorker.BuildBody(price, label));
    }

    [Fact]
    public void BuildBody_极端数量也不超过正文长度上限()
    {
        var body = StorePriceUpdateNotificationWorker.BuildBody(int.MaxValue, int.MaxValue);

        Assert.True(body.Length <= NotificationPublisher.MaxBodyLength);
        Assert.True(StorePriceUpdateNotificationWorker.NotificationTitle.Length <= NotificationPublisher.MaxTitleLength);
    }

    // ---------------------------------------------------------------- 没有接收人 / 幂等键

    [Fact]
    public async Task 没有接收人时不产生通知_但仍写Notice_之后不再对同一批重算()
    {
        MarkBaselineDone();
        var taskId = AddTask(Store3001, T0.AddMinutes(-40));

        await RunAsync(T0);

        Assert.Single(_publisher.Requests);
        Assert.Equal(0, await _db.Queryable<AppNotification>().CountAsync());
        var notice = Assert.Single(await RealNoticesAsync());
        Assert.Equal(taskId, notice.TaskId);
        Assert.Equal(1, notice.ChangeCount);

        await RunAsync(T0.AddMinutes(5));
        Assert.Single(_publisher.Requests);
    }

    [Fact]
    public void DedupeKey_内容确定_与任务顺序无关_变更次数变化则换键_并锁定哈希算法()
    {
        var key = StorePriceUpdateNotificationWorker.BuildDedupeKey(Store1013, [(1, 1), (2, 3), (10, 1)]);

        Assert.Matches("^store-price-updates:1013:[0-9a-f]{16}$", key);
        Assert.Equal(key, StorePriceUpdateNotificationWorker.BuildDedupeKey(Store1013, [(10, 1), (1, 1), (2, 3)]));
        Assert.NotEqual(key, StorePriceUpdateNotificationWorker.BuildDedupeKey(Store1013, [(1, 1), (2, 4), (10, 1)]));
        Assert.NotEqual(key, StorePriceUpdateNotificationWorker.BuildDedupeKey(Store1013, [(1, 1), (2, 3)]));
        Assert.StartsWith("store-price-updates:2001:", StorePriceUpdateNotificationWorker.BuildDedupeKey(Store2001, [(1, 1), (2, 3), (10, 1)]));
        // 输入是按 "id:changeCount" 字符串（序号比较，':' 大于数字，所以 "10:1" 排在 "1:1" 前）排序后用逗号拼接的
        // "10:1,1:1,2:3"，取 SHA-256 前 16 位小写十六进制。
        Assert.Equal("store-price-updates:1013:3f5717ea537b8621", key);
        Assert.Equal("store-price-updates:1013:a5886f21fcb5f028", StorePriceUpdateNotificationWorker.BuildDedupeKey(Store1013, [(5, 1)]));
    }

    [Fact]
    public async Task 同一集合两次发布得到同一幂等键_发布后写Notice前崩溃重试不会重复通知()
    {
        MarkBaselineDone();
        AddTask(Store1013, T0.AddMinutes(-40));
        AddTask(Store1013, T0.AddMinutes(-39));
        await RunAsync(T0);

        // 模拟“发布成功、Notice 还没写进去就崩溃”：把 Notice 行删掉再跑一轮。
        _db.Deleteable<StorePriceUpdateNotice>().Where(n => n.TaskId > 0).ExecuteCommand();
        await RunAsync(T0.AddMinutes(5));

        Assert.Equal(2, _publisher.Requests.Count);
        Assert.Equal(_publisher.Requests[0].DedupeKey, _publisher.Requests[1].DedupeKey);
        Assert.Equal(1, await _db.Queryable<AppNotification>().CountAsync());
        Assert.Single(_dispatcher.Sent);
        // 幂等命中也要把 Notice 补回来，之后不再重算。
        Assert.Equal(2, (await RealNoticesAsync()).Count);
        await RunAsync(T0.AddMinutes(10));
        Assert.Equal(2, _publisher.Requests.Count);
    }

    [Fact]
    public async Task 撤回后同键永不再发_Notice丢失重算也只命中旧通知()
    {
        MarkBaselineDone();
        AddTask(Store1013, T0.AddMinutes(-40));
        await RunAsync(T0);
        var dedupeKey = _publisher.Requests.Single().DedupeKey!;
        Assert.True(await _publisher.RevokeAsync(dedupeKey));

        _db.Deleteable<StorePriceUpdateNotice>().Where(n => n.TaskId > 0).ExecuteCommand();
        await RunAsync(T0.AddMinutes(5));

        Assert.Equal(1, await _db.Queryable<AppNotification>().CountAsync());
        Assert.Single(_dispatcher.Sent);
        Assert.Single(await RealNoticesAsync());
    }

    // ---------------------------------------------------------------- 硬约束：事务外 / 查询时变更次数 / 单店隔离

    [Fact]
    public async Task 发布始终在事务之外调用()
    {
        MarkBaselineDone();
        AddTask(Store1013, T0.AddMinutes(-40));
        AddTask(Store2001, T0.AddMinutes(-40));

        await RunAsync(T0);

        Assert.Equal(2, _publisher.InTransactionAtCall.Count);
        Assert.All(_publisher.InTransactionAtCall, inTransaction => Assert.False(inTransaction));
    }

    [Fact]
    public async Task 发布期间任务又被改_Notice记录查询时的变更次数_下一轮重新通知()
    {
        MarkBaselineDone();
        var taskId = AddTask(Store1013, T0.AddMinutes(-40));
        _publisher.BeforePublish = _ =>
        {
            BumpChangeCount(taskId, changeCount: 2, initiatedAtUtc: T0.AddMinutes(1));
            return Task.CompletedTask;
        };

        await RunAsync(T0);
        _publisher.BeforePublish = null;

        Assert.Equal(1, (await RealNoticesAsync()).Single().ChangeCount);
        await RunAsync(T0.AddMinutes(32));
        Assert.Equal(2, _publisher.Requests.Count);
        Assert.Equal(2, (await RealNoticesAsync()).Single().ChangeCount);
    }

    [Fact]
    public async Task 单店发布抛异常不影响其他店_失败的店不写Notice_下一轮重试()
    {
        MarkBaselineDone();
        AddTask(Store1013, T0.AddMinutes(-40));
        var task2001 = AddTask(Store2001, T0.AddMinutes(-40));
        _publisher.BeforePublish = request =>
            request.StoreCode == Store1013 ? throw new InvalidOperationException("boom") : Task.CompletedTask;

        await RunAsync(T0);

        var notification = Assert.Single(await _db.Queryable<AppNotification>().ToListAsync());
        Assert.Equal(Store2001, notification.StoreCode);
        Assert.Equal([task2001], (await RealNoticesAsync()).Select(n => n.TaskId));

        _publisher.BeforePublish = null;
        await RunAsync(T0.AddMinutes(5));

        Assert.Equal(2, await _db.Queryable<AppNotification>().CountAsync());
        Assert.Equal(2, (await RealNoticesAsync()).Count);
    }

    [Fact]
    public async Task 单店保存Notice失败同样只影响这一家()
    {
        MarkBaselineDone();
        AddTask(Store1013, T0.AddMinutes(-40));
        AddTask(Store2001, T0.AddMinutes(-40));
        // 1013 发布成功后、写 Notice 之前把表弄坏（模拟写库失败）；2001 在其之后处理，表已恢复。
        _publisher.AfterPublish = request =>
        {
            if (request.StoreCode == Store1013)
            {
                _db.Ado.ExecuteCommand("ALTER TABLE StorePriceUpdateNotice RENAME TO StorePriceUpdateNotice_x");
            }
            else
            {
                _db.Ado.ExecuteCommand("ALTER TABLE StorePriceUpdateNotice_x RENAME TO StorePriceUpdateNotice");
            }

            return Task.CompletedTask;
        };

        await RunAsync(T0);
        _publisher.AfterPublish = null;

        Assert.Equal(2, await _db.Queryable<AppNotification>().CountAsync());
        // 1013 没有写进 Notice，2001 写进去了。
        Assert.Equal(1, (await RealNoticesAsync()).Count);
        // 下一轮 1013 用同一幂等键重试：不再产生第二条通知，只补 Notice。
        await RunAsync(T0.AddMinutes(5));
        Assert.Equal(2, await _db.Queryable<AppNotification>().CountAsync());
        Assert.Equal(2, (await RealNoticesAsync()).Count);
    }

    [Fact]
    public async Task 大批量任务分块写入_一千二百条任务只发一条_再次变更全部重新通知()
    {
        MarkBaselineDone();
        var rows = Enumerable.Range(0, 1200)
            .Select(i => new StorePriceUpdateTask
            {
                StoreCode = Store1013,
                ProductCode = $"BULK{i:D5}",
                Status = StorePriceUpdateTaskStatuses.Pending,
                Kind = i % 3 == 0 ? StorePriceUpdateTaskKinds.LabelOnly : StorePriceUpdateTaskKinds.PriceUpdate,
                InitiatorName = "t",
                InitiatorSource = "t",
                InitiatedAtUtc = T0.AddMinutes(-60),
                ChangeCount = 1,
                CreatedAtUtc = T0.AddMinutes(-60),
                UpdatedAtUtc = T0.AddMinutes(-60),
            })
            .ToList();
        foreach (var chunk in rows.Chunk(100))
        {
            _db.Insertable(chunk.ToList()).ExecuteCommand();
        }

        await RunAsync(T0);

        var request = Assert.Single(_publisher.Requests);
        Assert.Equal("400", request.Data!["labelOnlyCount"]);
        Assert.Equal("800", request.Data["priceUpdateCount"]);
        Assert.Equal(1200, (await RealNoticesAsync()).Count);

        var refreshedAt = T0.AddMinutes(1);
        _db.Updateable<StorePriceUpdateTask>()
            .SetColumns(task => new StorePriceUpdateTask { ChangeCount = 2, InitiatedAtUtc = refreshedAt })
            .Where(task => task.Status == StorePriceUpdateTaskStatuses.Pending)
            .ExecuteCommand();
        await RunAsync(T0.AddMinutes(40));

        Assert.Equal(2, _publisher.Requests.Count);
        var notices = await RealNoticesAsync();
        Assert.Equal(1200, notices.Count);
        Assert.All(notices, notice => Assert.Equal(2, notice.ChangeCount));
    }

    // ---------------------------------------------------------------- 首次启用基线

    [Fact]
    public async Task 首次启用静默基线_积压任务只写Notice不发布_之后新增的任务正常通知()
    {
        var first = AddTask(Store1013, T0.AddMinutes(-600));
        var second = AddTask(Store2001, T0.AddMinutes(-600), changeCount: 3);
        AddTask(Store1013, T0.AddMinutes(-600), status: StorePriceUpdateTaskStatuses.Cancelled);

        await RunAsync(T0);

        Assert.Empty(_publisher.Requests);
        Assert.Equal(0, await _db.Queryable<AppNotification>().CountAsync());
        var notices = await _db.Queryable<StorePriceUpdateNotice>().OrderBy(n => n.TaskId).ToListAsync();
        // TaskId=0 是基线完成标记；其余是当时的 Pending 任务，记录当时的变更次数。
        Assert.Equal([0L, first, second], notices.Select(n => n.TaskId));
        Assert.Equal([0, 1, 3], notices.Select(n => n.ChangeCount));
        Assert.All(notices, notice => Assert.Equal(T0, notice.NotifiedAtUtc));

        // 基线之后的变更与新任务才会通知。
        await RunAsync(T0.AddMinutes(60));
        Assert.Empty(_publisher.Requests);
        AddTask(Store1013, T0.AddMinutes(30));
        await RunAsync(T0.AddMinutes(61));
        Assert.Single(_publisher.Requests);
    }

    [Fact]
    public async Task 启用时恰好没有Pending任务_也写入基线标记_之后第一批真实任务不会被误当基线吞掉()
    {
        await RunAsync(T0);
        var marker = Assert.Single(await _db.Queryable<StorePriceUpdateNotice>().ToListAsync());
        Assert.Equal(StorePriceUpdateNotice.BaselineMarkerTaskId, marker.TaskId);

        AddTask(Store1013, T0.AddMinutes(100));
        await RunAsync(T0.AddMinutes(140));

        Assert.Single(_publisher.Requests);
    }

    [Fact]
    public async Task Notice表已有记录时不再做基线()
    {
        // 表里已经有别的记录（比如被人手工补过）：按正常流程处理，不静默吞掉。
        _db.Insertable(new StorePriceUpdateNotice { TaskId = 9999, ChangeCount = 1, NotifiedAtUtc = T0 }).ExecuteCommand();
        AddTask(Store1013, T0.AddMinutes(-40));

        await RunAsync(T0);

        Assert.Single(_publisher.Requests);
    }

    // ---------------------------------------------------------------- 清理

    [Fact]
    public async Task 清理只删非Pending且早于30天的Notice_Pending任务_较新的_基线标记都保留()
    {
        MarkBaselineDone(markerAt: T0.AddDays(-100));
        var completedOld = AddTask(Store1013, T0.AddDays(-50), status: StorePriceUpdateTaskStatuses.Completed);
        var cancelledOld = AddTask(Store1013, T0.AddDays(-50), status: StorePriceUpdateTaskStatuses.Cancelled);
        var pendingOld = AddTask(Store1013, T0.AddDays(-50));
        var completedRecent = AddTask(Store1013, T0.AddDays(-20), status: StorePriceUpdateTaskStatuses.Completed);
        const long missingTask = 777;
        _db.Insertable(new List<StorePriceUpdateNotice>
        {
            new() { TaskId = completedOld, ChangeCount = 1, NotifiedAtUtc = T0.AddDays(-31) },
            new() { TaskId = cancelledOld, ChangeCount = 1, NotifiedAtUtc = T0.AddDays(-31) },
            new() { TaskId = pendingOld, ChangeCount = 1, NotifiedAtUtc = T0.AddDays(-60) },
            new() { TaskId = completedRecent, ChangeCount = 1, NotifiedAtUtc = T0.AddDays(-29) },
            // 对应任务行已被任务服务过期清理：同样视为“不再是 Pending”。
            new() { TaskId = missingTask, ChangeCount = 1, NotifiedAtUtc = T0.AddDays(-40) },
        }).ExecuteCommand();

        await RunAsync(T0);

        var remaining = (await _db.Queryable<StorePriceUpdateNotice>().ToListAsync()).Select(n => n.TaskId).Order().ToList();
        Assert.Equal(new[] { 0L, pendingOld, completedRecent }.Order(), remaining);
        // 仍是 Pending 的旧任务不会因为 Notice 清理或保留而被当成新任务再通知。
        Assert.Empty(_publisher.Requests);
    }

    [Fact]
    public async Task 清理每轮最多删五千行()
    {
        MarkBaselineDone();
        for (var offset = 0; offset < StorePriceUpdateNotificationWorker.CleanupBatchLimit + 5; offset += 300)
        {
            var count = Math.Min(300, StorePriceUpdateNotificationWorker.CleanupBatchLimit + 5 - offset);
            _db.Insertable(Enumerable.Range(offset, count)
                .Select(i => new StorePriceUpdateNotice { TaskId = 100_000 + i, ChangeCount = 1, NotifiedAtUtc = T0.AddDays(-45) })
                .ToList()).ExecuteCommand();
        }

        var removed = await StorePriceUpdateNotificationWorker.CleanupAsync(_db, T0, CancellationToken.None);

        Assert.Equal(StorePriceUpdateNotificationWorker.CleanupBatchLimit, removed);
        Assert.Equal(5 + 1, await _db.Queryable<StorePriceUpdateNotice>().CountAsync());
    }

    // ---------------------------------------------------------------- BackgroundService 外壳

    [Fact]
    public async Task Enabled为false时不运行_既不基线也不发布()
    {
        AddTask(Store1013, T0.AddMinutes(-600));
        var worker = CreateWorker(
            new Dictionary<string, string?> { ["StorePriceUpdateNotifications:Enabled"] = "false" },
            startupDelay: TimeSpan.Zero);

        await worker.StartAsync(CancellationToken.None);
        var finished = await Task.WhenAny(worker.ExecuteTask!, Task.Delay(TimeSpan.FromSeconds(10)));
        await worker.StopAsync(CancellationToken.None);

        Assert.Same(worker.ExecuteTask, finished);
        Assert.Empty(_publisher.Requests);
        Assert.Equal(0, await _db.Queryable<StorePriceUpdateNotice>().CountAsync());
    }

    [Fact]
    public async Task 默认启用_启动延迟过后按真实时钟跑一轮_停机信号可正常退出()
    {
        // 基线已完成 + 一条早已满足窗口的任务（InitiatedAtUtc 取很久以前，与真实时钟比较）。
        MarkBaselineDone();
        AddTask(Store1013, DateTime.UtcNow.AddHours(-3));
        var worker = CreateWorker(settings: null, startupDelay: TimeSpan.Zero, timeProvider: TimeProvider.System);

        await worker.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var published = false;
        while (DateTime.UtcNow < deadline && !published)
        {
            // worker 在后台线程使用 _db，这里用独立连接读，避免同一个客户端被两个线程同时使用。
            await using var probe = new SqliteConnection(_connection.ConnectionString);
            await probe.OpenAsync();
            await using var command = probe.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM AppNotification";
            published = Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
            if (!published)
            {
                await Task.Delay(50);
            }
        }

        await worker.StopAsync(CancellationToken.None);
        var finished = await Task.WhenAny(worker.ExecuteTask!, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(published, "启动延迟过后应已跑过一轮并发布通知");
        Assert.Same(worker.ExecuteTask, finished);
    }

    // ---------------------------------------------------------------- 夹具

    private StorePriceUpdateNotificationWorker CreateWorker(
        Dictionary<string, string?>? settings = null,
        TimeSpan? startupDelay = null,
        TimeProvider? timeProvider = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build();
        return new StorePriceUpdateNotificationWorker(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            configuration,
            timeProvider ?? _time,
            NullLogger<StorePriceUpdateNotificationWorker>.Instance)
        {
            StartupDelay = startupDelay ?? TimeSpan.FromMinutes(2),
        };
    }

    private Task RunAsync(DateTime nowUtc)
    {
        // 发布器用同一个可冻结的时钟，通知的 CreatedAtUtc 与本轮时间一致。
        _time.Now = nowUtc;
        return CreateWorker().RunOnceAsync(nowUtc, CancellationToken.None);
    }

    /// <summary>模拟“首次启用基线已完成”：写入 TaskId=0 的标记行，避免第一轮被当作基线静默掉。</summary>
    private void MarkBaselineDone(DateTime? markerAt = null)
    {
        _db.Insertable(new StorePriceUpdateNotice
        {
            TaskId = StorePriceUpdateNotice.BaselineMarkerTaskId,
            ChangeCount = 0,
            NotifiedAtUtc = markerAt ?? T0.AddDays(-1),
        }).ExecuteCommand();
    }

    /// <summary>模拟仓库再次调价：变更次数变大，InitiatedAtUtc 刷新（SqlSugar 表达式里不能直接引用私有静态字段，所以先算成局部变量）。</summary>
    private void BumpChangeCount(long taskId, int changeCount, DateTime initiatedAtUtc)
    {
        _db.Updateable<StorePriceUpdateTask>()
            .SetColumns(task => new StorePriceUpdateTask { ChangeCount = changeCount, InitiatedAtUtc = initiatedAtUtc })
            .Where(task => task.Id == taskId)
            .ExecuteCommand();
    }

    /// <summary>真实任务的 Notice 行（不含基线标记）。</summary>
    private async Task<List<StorePriceUpdateNotice>> RealNoticesAsync() =>
        await _db.Queryable<StorePriceUpdateNotice>()
            .Where(notice => notice.TaskId > 0)
            .OrderBy(notice => notice.TaskId)
            .ToListAsync();

    private long AddTask(
        string storeCode,
        DateTime initiatedAtUtc,
        string kind = StorePriceUpdateTaskKinds.PriceUpdate,
        int changeCount = 1,
        string status = StorePriceUpdateTaskStatuses.Pending)
    {
        return _db.Insertable(new StorePriceUpdateTask
        {
            StoreCode = storeCode,
            // 同店同商品至多一条 Pending（过滤唯一索引），每条任务用不同商品。
            ProductCode = $"P{++_productSeq:D5}",
            Status = status,
            Kind = kind,
            InitiatorName = "tester",
            InitiatorSource = "Test",
            InitiatedAtUtc = initiatedAtUtc,
            ChangeCount = changeCount,
            CreatedAtUtc = initiatedAtUtc,
            UpdatedAtUtc = initiatedAtUtc,
        }).ExecuteReturnBigIdentity();
    }

    private void SeedUsersAndStores()
    {
        _db.Insertable(new List<User>
        {
            new() { UserGUID = ManagerGuid, Username = "manager", IsActive = true },
            new() { UserGUID = CashierGuid, Username = "cashier", IsActive = true },
            new() { UserGUID = InactiveGuid, Username = "inactive", IsActive = false },
            new() { UserGUID = Manager2001Guid, Username = "manager2001", IsActive = true },
        }).ExecuteCommand();
        _db.Insertable(new List<Store>
        {
            new() { StoreGUID = "store-1013", StoreCode = Store1013 },
            new() { StoreGUID = "store-2001", StoreCode = Store2001 },
            // 3001 没有任何成员：发布返回“没有接收人”。
            new() { StoreGUID = "store-3001", StoreCode = Store3001 },
        }).ExecuteCommand();
        _db.Insertable(new List<UserStore>
        {
            new() { UserGUID = ManagerGuid, StoreGUID = "store-1013" },
            new() { UserGUID = CashierGuid, StoreGUID = "store-1013" },
            new() { UserGUID = InactiveGuid, StoreGUID = "store-1013" },
            new() { UserGUID = Manager2001Guid, StoreGUID = "store-2001" },
        }).ExecuteCommand();
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    public void Dispose()
    {
        _provider.Dispose();
        _db.Dispose();
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    private sealed class MutableTimeProvider(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    private sealed class RecordingDispatcher : INotificationRealtimeDispatcher
    {
        public List<(IReadOnlyCollection<string> UserGuids, NotificationDto Notification)> Sent { get; } = [];

        public Task SendToUsersAsync(
            IReadOnlyCollection<string> userGuids,
            NotificationDto notification,
            CancellationToken cancellationToken = default
        )
        {
            Sent.Add((userGuids, notification));
            return Task.CompletedTask;
        }
    }

    /// <summary>包住真实发布器：记录每次请求、调用时是否处于事务内，并允许在发布前后注入行为（抛异常 / 改库）。</summary>
    private sealed class SpyPublisher(INotificationPublisher inner, ISqlSugarClient db) : INotificationPublisher
    {
        public List<NotificationPublishRequest> Requests { get; } = [];

        public List<bool> InTransactionAtCall { get; } = [];

        public Func<NotificationPublishRequest, Task>? BeforePublish { get; set; }

        public Func<NotificationPublishRequest, Task>? AfterPublish { get; set; }

        public async Task<NotificationPublishResult> PublishAsync(
            NotificationPublishRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Requests.Add(request);
            InTransactionAtCall.Add(db.Ado.IsAnyTran());
            if (BeforePublish != null)
            {
                await BeforePublish(request);
            }

            var result = await inner.PublishAsync(request, cancellationToken);
            if (AfterPublish != null)
            {
                await AfterPublish(request);
            }

            return result;
        }

        public Task<bool> RevokeAsync(string dedupeKey, CancellationToken cancellationToken = default) =>
            inner.RevokeAsync(dedupeKey, cancellationToken);
    }
}
