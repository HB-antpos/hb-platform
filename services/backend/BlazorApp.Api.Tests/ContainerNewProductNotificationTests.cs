using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.Background;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 新品到店每日推送（门店本地 8 点）：worker 的时间窗口、基线、累计已见集合、幂等与故障隔离，
/// 以及无鉴权内核与 GetAsync 的一致性。SQLite 夹具，发布走真实的 NotificationPublisher（验证幂等键与接收人）。
/// </summary>
public sealed class ContainerNewProductNotificationTests : IDisposable
{
    private const string NswStore = "S-NSW";
    private const string QldStore = "S-QLD";
    private const string VicStore = "S-VIC";
    private const string ManagerGuid = "user-manager";
    private const string CashierGuid = "user-cashier";
    private const string Sydney = "Australia/Sydney";

    // 门店本地日期（只取日期部分）。查询表达式里引用的字段必须是公开的：SqlSugar 解析不了私有字段。
    public static readonly DateTime D0715 = new(2026, 7, 15);
    public static readonly DateTime D0716 = new(2026, 7, 16);
    public static readonly DateTime D0717 = new(2026, 7, 17);

    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;
    private readonly MutableTimeProvider _time = new(new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc));
    private readonly Mock<IRoleService> _roleService = new(MockBehavior.Strict);
    private readonly RecordingDispatcher _dispatcher = new();
    private readonly ContainerNewProductNotificationOptions _options = new();
    private readonly CapturingLogger<ContainerNewProductNotificationWorker> _workerLog = new();
    private readonly SwitchableScopeFactory _scopeFactory = new();
    private readonly ContainerNewProductNotificationWorker _worker;

    public ContainerNewProductNotificationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"container-new-notice-{Guid.NewGuid():N}.db");
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
            typeof(ContainerNewProductSeen),
            typeof(ContainerNewProductRun),
            typeof(Container),
            typeof(ContainerDetail),
            typeof(DomesticProduct),
            typeof(WarehouseProduct),
            typeof(Product),
            typeof(StoreRetailPrice)
        );
        _db.Ado.ExecuteCommand("CREATE TABLE WarehouseProductChangeHistory (Id INTEGER PRIMARY KEY, EventGuid TEXT NOT NULL, ProductCode TEXT NOT NULL, Action TEXT NOT NULL, Source TEXT NOT NULL, SourceReference TEXT NULL, BatchGuid TEXT NULL, ActorUserGuid TEXT NULL, ActorName TEXT NOT NULL, ActorType TEXT NOT NULL, OccurredAtUtc TEXT NOT NULL, ChangesJson TEXT NOT NULL)");
        SeedStoresAndUsers();

        // 同一个 worker 跑完所有轮次：状态不明门店的「每天一条日志」节流字典要跨轮保留；作用域工厂每轮换成新的 DI 容器。
        var optionsMonitor = new Mock<IOptionsMonitor<ContainerNewProductNotificationOptions>>();
        optionsMonitor.SetupGet(monitor => monitor.CurrentValue).Returns(_options);
        _worker = new ContainerNewProductNotificationWorker(_scopeFactory, optionsMonitor.Object, _time, _workerLog);

        _roleService
            .Setup(service => service.UserHasPermissionAsync(ManagerGuid, Permissions.Container.MobileNewProductsView))
            .ReturnsAsync(ApiResponse<bool>.OK(true));
        _roleService
            .Setup(service => service.UserHasPermissionAsync(CashierGuid, Permissions.Container.MobileNewProductsView))
            .ReturnsAsync(ApiResponse<bool>.OK(false));
    }

    // ---------- 时间窗口 ----------

    [Fact]
    public async Task RunOnce_本地8点前不处理_8点处理一次_同日第二轮不重复_20点起当天不再补发()
    {
        AddContainer("C-1", new DateTime(2026, 7, 15), "P1", "P2");

        // 07:59 不处理。
        await RunAtAsync(Local(Sydney, 2026, 7, 15, 7, 59));
        Assert.Empty(await _db.Queryable<ContainerNewProductRun>().ToListAsync());
        Assert.Empty(await _db.Queryable<ContainerNewProductSeen>().ToListAsync());

        // 08:00 处理（首次基线），写 Seen 与 Run。
        await RunAtAsync(Local(Sydney, 2026, 7, 15, 8, 0));
        Assert.Equal(2, await _db.Queryable<ContainerNewProductSeen>().Where(row => row.StoreCode == NswStore).CountAsync());
        Assert.Single(await _db.Queryable<ContainerNewProductRun>().Where(row => row.StoreCode == NswStore).ToListAsync());

        // 同日第二轮：即便列表里又多了新品，也不再处理（每店每个本地日期只处理一次）。
        AddContainer("C-2", new DateTime(2026, 7, 15), "P3");
        await RunAtAsync(Local(Sydney, 2026, 7, 15, 8, 5));
        Assert.Equal(2, await _db.Queryable<ContainerNewProductSeen>().Where(row => row.StoreCode == NswStore).CountAsync());
        Assert.Single(await _db.Queryable<ContainerNewProductRun>().Where(row => row.StoreCode == NswStore).ToListAsync());
        Assert.Empty(_dispatcher.Sent);

        // 次日 20:00 起不再补发；19:59 仍在窗口内。
        await RunAtAsync(Local(Sydney, 2026, 7, 16, 20, 0));
        Assert.Single(await _db.Queryable<ContainerNewProductRun>().Where(row => row.StoreCode == NswStore).ToListAsync());
        Assert.Equal(0, await _db.Queryable<AppNotification>().CountAsync());

        await RunAtAsync(Local(Sydney, 2026, 7, 16, 19, 59));
        Assert.Equal(2, await _db.Queryable<ContainerNewProductRun>().Where(row => row.StoreCode == NswStore).CountAsync());
        var notification = Assert.Single(await _db.Queryable<AppNotification>().ToListAsync());
        Assert.Equal("1", ReadData(notification)["count"]);

        // 同一个窗口内再跑一轮不重复。
        await RunAtAsync(Local(Sydney, 2026, 7, 16, 19, 59));
        Assert.Equal(2, await _db.Queryable<ContainerNewProductRun>().Where(row => row.StoreCode == NswStore).CountAsync());
        Assert.Equal(1, await _db.Queryable<AppNotification>().CountAsync());
    }

    [Fact]
    public async Task RunOnce_夏令时期间悉尼比布里斯班早一小时_各按各自本地8点触发()
    {
        // 2026-10-20 悉尼夏令时 UTC+11、布里斯班 UTC+10：UTC 21:30 悉尼 08:30、布里斯班 07:30。
        AddQldStore();
        AddContainer("C-1", new DateTime(2026, 10, 20), "P1");
        var utc = new DateTime(2026, 10, 19, 21, 30, 0, DateTimeKind.Utc);

        await RunAtAsync(utc);
        var nswRun = Assert.Single(await _db.Queryable<ContainerNewProductRun>().ToListAsync());
        Assert.Equal(NswStore, nswRun.StoreCode);
        Assert.Equal(new DateTime(2026, 10, 20), nswRun.LocalDate.Date);

        // 再过一小时：布里斯班 08:30，轮到 QLD；NSW 已处理过，不重复。
        await RunAtAsync(utc.AddHours(1));
        var runs = (await _db.Queryable<ContainerNewProductRun>().ToListAsync()).OrderBy(row => row.StoreCode).ToList();
        Assert.Equal([NswStore, QldStore], runs.Select(row => row.StoreCode));
        Assert.All(runs, row => Assert.Equal(new DateTime(2026, 10, 20), row.LocalDate.Date));
        // 墨尔本门店状态不明、被停用与被删除的门店都没有 Run。
        Assert.DoesNotContain(runs, row => row.StoreCode is VicStore or "S-OFF" or "S-DEL");
    }

    [Fact]
    public async Task RunOnce_非夏令时两地UTC偏移相同_同一轮里一起处理()
    {
        AddQldStore();
        AddContainer("C-1", new DateTime(2026, 7, 15), "P1");

        // 2026-07-14 22:30 UTC：悉尼与布里斯班都是 UTC+10，本地 08:30。
        await RunAtAsync(new DateTime(2026, 7, 14, 22, 30, 0, DateTimeKind.Utc));

        var runs = (await _db.Queryable<ContainerNewProductRun>().ToListAsync()).OrderBy(row => row.StoreCode).ToList();
        Assert.Equal([NswStore, QldStore], runs.Select(row => row.StoreCode));
        Assert.All(runs, row => Assert.Equal(new DateTime(2026, 7, 15), row.LocalDate.Date));
    }

    // ---------- 基线与累计已见集合 ----------

    [Fact]
    public async Task RunOnce_首次基线静默_只登记当前列表_写Seen与Run但不发布()
    {
        AddContainer("C-1", new DateTime(2026, 7, 15), "P1", "P2");

        await RunAtAsync(Local(Sydney, 2026, 7, 15, 9, 0));

        Assert.Equal(0, await _db.Queryable<AppNotification>().CountAsync());
        Assert.Empty(_dispatcher.Sent);
        var seen = await _db.Queryable<ContainerNewProductSeen>().Where(row => row.StoreCode == NswStore).ToListAsync();
        Assert.Equal(["P1", "P2"], seen.Select(row => row.ProductCode).Order(StringComparer.Ordinal));
        Assert.All(seen, row =>
        {
            Assert.Equal(new DateTime(2026, 7, 15), row.FirstLocalDate.Date);
            Assert.Equal("C-1", row.ContainerCode);
            Assert.Null(row.NotificationId);
        });
        var run = Assert.Single(await _db.Queryable<ContainerNewProductRun>().Where(row => row.StoreCode == NswStore).ToListAsync());
        Assert.True(run.IsBaseline);
        Assert.Equal(0, run.NewCount);
        Assert.Null(run.NotificationId);
    }

    [Fact]
    public async Task RunOnce_基线之后新增商品才推送_只推未见过的_前天已推的商品不会重复推()
    {
        AddContainer("C-1", new DateTime(2026, 7, 15), "P1", "P2");
        await RunAtAsync(Local(Sydney, 2026, 7, 15, 9, 0)); // 基线

        // 第二天：新增 P3，推送 1 条（只算 P3）。
        AddContainer("C-2", new DateTime(2026, 7, 16), "P3");
        await RunAtAsync(Local(Sydney, 2026, 7, 16, 9, 0));
        var first = Assert.Single(await _db.Queryable<AppNotification>().ToListAsync());
        Assert.Equal(AppNotificationTypes.ContainerNewProducts, first.Type);
        Assert.Equal("container.new_products", first.Type);
        Assert.Equal("New products arriving soon", first.Title);
        Assert.Equal("1 new product is on its way to your store. Open HB new arrivals to see it.", first.Body);
        Assert.Equal(NswStore, first.StoreCode);
        Assert.Equal($"container-new-products:{NswStore}:2026-07-16", first.DedupeKey);
        var data = ReadData(first);
        Assert.Equal(
            new Dictionary<string, string> { ["storeCode"] = NswStore, ["localDate"] = "2026-07-16", ["count"] = "1" },
            data);

        // 第三天：列表还是 P1~P3（P3 前一天已推过），没有新增 → 不发布，Run 记 0。
        await RunAtAsync(Local(Sydney, 2026, 7, 17, 9, 0));
        Assert.Equal(1, await _db.Queryable<AppNotification>().CountAsync());
        var idle = await _db.Queryable<ContainerNewProductRun>()
            .Where(row => row.StoreCode == NswStore && row.LocalDate == D0717)
            .FirstAsync();
        Assert.Equal(0, idle.NewCount);
        Assert.False(idle.IsBaseline);
        Assert.Null(idle.NotificationId);

        // 第四天：新增 P4、P5，只推这两个，P3 不再算。
        AddContainer("C-3", new DateTime(2026, 7, 17), "P4", "P5");
        await RunAtAsync(Local(Sydney, 2026, 7, 18, 9, 0));
        var notifications = (await _db.Queryable<AppNotification>().ToListAsync()).OrderBy(row => row.CreatedAtUtc).ToList();
        Assert.Equal(2, notifications.Count);
        Assert.Equal("2", ReadData(notifications[1])["count"]);
        Assert.Equal("2 new products are on their way to your store. Open HB new arrivals to see them.", notifications[1].Body);

        // 累计已见集合：每个商品只在第一次被看到的那天登记，并带上当天通知编号。
        var seen = (await _db.Queryable<ContainerNewProductSeen>().Where(row => row.StoreCode == NswStore).ToListAsync())
            .ToDictionary(row => row.ProductCode);
        Assert.Equal(5, seen.Count);
        Assert.Equal(new DateTime(2026, 7, 16), seen["P3"].FirstLocalDate.Date);
        Assert.Equal(first.NotificationId, seen["P3"].NotificationId);
        Assert.Equal(new DateTime(2026, 7, 18), seen["P4"].FirstLocalDate.Date);
        Assert.Equal(notifications[1].NotificationId, seen["P4"].NotificationId);
        Assert.Equal(notifications[1].NotificationId, seen["P5"].NotificationId);
        Assert.Null(seen["P1"].NotificationId);
    }

    [Fact]
    public async Task RunOnce_通知只发给持有新品权限的门店成员()
    {
        AddContainer("C-1", new DateTime(2026, 7, 15), "P1");
        await RunAtAsync(Local(Sydney, 2026, 7, 15, 9, 0));
        AddContainer("C-2", new DateTime(2026, 7, 16), "P2");

        await RunAtAsync(Local(Sydney, 2026, 7, 16, 9, 0));

        var notification = Assert.Single(await _db.Queryable<AppNotification>().ToListAsync());
        var recipients = await _db.Queryable<AppNotificationRecipient>()
            .Where(row => row.NotificationId == notification.NotificationId)
            .Select(row => row.UserGUID)
            .ToListAsync();
        Assert.Equal([ManagerGuid], recipients);
        var sent = Assert.Single(_dispatcher.Sent);
        Assert.Equal([ManagerGuid], sent.UserGuids);
    }

    [Fact]
    public async Task RunOnce_同日同店幂等键唯一_不同门店与不同日期各有各的键()
    {
        AddQldStore();
        AddContainer("C-1", new DateTime(2026, 7, 15), "P1");
        await RunAtAsync(Local(Sydney, 2026, 7, 15, 9, 0));
        AddContainer("C-2", new DateTime(2026, 7, 16), "P2");
        await RunAtAsync(Local(Sydney, 2026, 7, 16, 9, 0));
        AddContainer("C-3", new DateTime(2026, 7, 17), "P3");
        await RunAtAsync(Local(Sydney, 2026, 7, 17, 9, 0));

        var keys = (await _db.Queryable<AppNotification>().ToListAsync())
            .Select(row => row.DedupeKey!)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            [
                $"container-new-products:{NswStore}:2026-07-16",
                $"container-new-products:{NswStore}:2026-07-17",
                $"container-new-products:{QldStore}:2026-07-16",
                $"container-new-products:{QldStore}:2026-07-17",
            ],
            keys);
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public async Task RunOnce_没有新增时只写Run不发布()
    {
        AddContainer("C-1", new DateTime(2026, 7, 15), "P1");
        await RunAtAsync(Local(Sydney, 2026, 7, 15, 9, 0));

        await RunAtAsync(Local(Sydney, 2026, 7, 16, 9, 0));

        Assert.Equal(0, await _db.Queryable<AppNotification>().CountAsync());
        Assert.Empty(_dispatcher.Sent);
        Assert.Equal(1, await _db.Queryable<ContainerNewProductSeen>().Where(row => row.StoreCode == NswStore).CountAsync());
        var run = await _db.Queryable<ContainerNewProductRun>()
            .Where(row => row.StoreCode == NswStore && row.LocalDate == D0716)
            .FirstAsync();
        Assert.Equal(0, run.NewCount);
        Assert.False(run.IsBaseline);
    }

    [Fact]
    public async Task RunOnce_没有接收人时仍写Seen与Run_下一天不会再把它当新品()
    {
        AddStore("S-EMPTY", "g-empty", "Brisbane QLD 4000"); // 没有任何成员
        AddContainer("C-1", new DateTime(2026, 7, 15), "P1");
        await RunAtAsync(Local(Sydney, 2026, 7, 15, 9, 0));
        AddContainer("C-2", new DateTime(2026, 7, 16), "P2");

        await RunAtAsync(Local(Sydney, 2026, 7, 16, 9, 0));

        // 无接收人：发布器不产生通知，但新品照常登记为已见、Run 记新增数。
        Assert.Equal(0, await _db.Queryable<AppNotification>().Where(row => row.StoreCode == "S-EMPTY").CountAsync());
        var seen = await _db.Queryable<ContainerNewProductSeen>().Where(row => row.StoreCode == "S-EMPTY" && row.ProductCode == "P2").FirstAsync();
        Assert.Null(seen.NotificationId);
        var run = await _db.Queryable<ContainerNewProductRun>()
            .Where(row => row.StoreCode == "S-EMPTY" && row.LocalDate == D0716)
            .FirstAsync();
        Assert.Equal(1, run.NewCount);
        Assert.Null(run.NotificationId);

        await RunAtAsync(Local(Sydney, 2026, 7, 17, 9, 0));
        var next = await _db.Queryable<ContainerNewProductRun>()
            .Where(row => row.StoreCode == "S-EMPTY" && row.LocalDate == D0717)
            .FirstAsync();
        Assert.Equal(0, next.NewCount);
    }

    // ---------- 幂等与崩溃重试 ----------

    [Fact]
    public async Task RunOnce_同日崩溃重试_发布被幂等键去重_补写Seen与Run_撤回后同键也不会再发()
    {
        AddContainer("C-1", new DateTime(2026, 7, 15), "P1");
        await RunAtAsync(Local(Sydney, 2026, 7, 15, 9, 0));
        AddContainer("C-2", new DateTime(2026, 7, 16), "P2", "P3");
        var now = Local(Sydney, 2026, 7, 16, 9, 0);
        await RunAtAsync(now);
        var published = Assert.Single(await _db.Queryable<AppNotification>().ToListAsync());

        // 模拟「发布之后、落记录之前崩溃」：当天的 Run 与新品的 Seen 都没落下来。
        await _db.Deleteable<ContainerNewProductRun>()
            .Where(row => row.StoreCode == NswStore && row.LocalDate == D0716)
            .ExecuteCommandAsync();
        await _db.Deleteable<ContainerNewProductSeen>()
            .Where(row => row.StoreCode == NswStore && (row.ProductCode == "P2" || row.ProductCode == "P3"))
            .ExecuteCommandAsync();

        await RunAtAsync(now.AddMinutes(5));

        // 重新算出同一集合，发布被去重（没有第二条通知、没有第二次实时投递），随后补写 Seen / Run 并关联原通知。
        Assert.Equal(1, await _db.Queryable<AppNotification>().CountAsync());
        Assert.Single(_dispatcher.Sent);
        var run = await _db.Queryable<ContainerNewProductRun>()
            .Where(row => row.StoreCode == NswStore && row.LocalDate == D0716)
            .FirstAsync();
        Assert.Equal(2, run.NewCount);
        Assert.Equal(published.NotificationId, run.NotificationId);
        Assert.Equal(
            2,
            await _db.Queryable<ContainerNewProductSeen>()
                .Where(row => row.StoreCode == NswStore && row.NotificationId == published.NotificationId)
                .CountAsync());

        // 通知被撤回之后再次「崩溃重试」：同键永远不可再发，不会冒出第二条通知。
        Assert.True(await CreatePublisher().RevokeAsync(published.DedupeKey!));
        await _db.Deleteable<ContainerNewProductRun>()
            .Where(row => row.StoreCode == NswStore && row.LocalDate == D0716)
            .ExecuteCommandAsync();
        await _db.Deleteable<ContainerNewProductSeen>()
            .Where(row => row.StoreCode == NswStore && (row.ProductCode == "P2" || row.ProductCode == "P3"))
            .ExecuteCommandAsync();

        await RunAtAsync(now.AddMinutes(10));

        Assert.Equal(1, await _db.Queryable<AppNotification>().CountAsync());
        Assert.Single(_dispatcher.Sent);
    }

    // ---------- 跳过与隔离 ----------

    [Fact]
    public async Task RunOnce_状态未知_已删除_已停用的门店被跳过且不写Run_状态未知日志每店每天一条()
    {
        AddQldStore();
        AddContainer("C-1", new DateTime(2026, 7, 15), "P1");

        await RunAtAsync(Local(Sydney, 2026, 7, 15, 9, 0));
        await RunAtAsync(Local(Sydney, 2026, 7, 15, 9, 5));

        var runStores = (await _db.Queryable<ContainerNewProductRun>().ToListAsync()).Select(row => row.StoreCode).Distinct().Order().ToList();
        Assert.Equal([NswStore, QldStore], runStores);
        Assert.Equal(1, _workerLog.Count(entry => entry.Contains(VicStore, StringComparison.Ordinal)));

        // 次日再记一条。
        await RunAtAsync(Local(Sydney, 2026, 7, 16, 9, 0));
        Assert.Equal(2, _workerLog.Count(entry => entry.Contains(VicStore, StringComparison.Ordinal)));
        Assert.DoesNotContain(
            await _db.Queryable<ContainerNewProductSeen>().ToListAsync(),
            row => row.StoreCode is VicStore or "S-OFF" or "S-DEL");
    }

    [Fact]
    public async Task RunOnce_单店异常不影响其他门店_恢复后下一轮补上()
    {
        AddQldStore();
        AddContainer("C-1", new DateTime(2026, 7, 15), "P1");

        await RunAtAsync(Local(Sydney, 2026, 7, 15, 9, 0), queryDecorator: inner => new FailingQuery(inner, NswStore));

        var run = Assert.Single(await _db.Queryable<ContainerNewProductRun>().ToListAsync());
        Assert.Equal(QldStore, run.StoreCode);
        Assert.Contains(_workerLog.Entries, entry => entry.Contains(NswStore, StringComparison.Ordinal));

        await RunAtAsync(Local(Sydney, 2026, 7, 15, 9, 5));
        Assert.Equal(
            [NswStore, QldStore],
            (await _db.Queryable<ContainerNewProductRun>().ToListAsync()).Select(row => row.StoreCode).Order());
    }

    [Fact]
    public async Task RunOnce_发布失败时不写Seen与Run_下一轮重试成功且各店只发一条()
    {
        AddQldStore();
        AddContainer("C-1", new DateTime(2026, 7, 15), "P1");
        await RunAtAsync(Local(Sydney, 2026, 7, 15, 9, 0)); // 基线
        AddContainer("C-2", new DateTime(2026, 7, 16), "P2");

        await RunAtAsync(
            Local(Sydney, 2026, 7, 16, 9, 0),
            publisherDecorator: inner => new FailingPublisher(inner, NswStore));

        // QLD 照常发布；NSW 发布失败，P2 没有被登记为已见，当天也没有 Run，下一轮才能重试。
        Assert.Equal(1, await _db.Queryable<AppNotification>().CountAsync());
        Assert.Equal(QldStore, (await _db.Queryable<AppNotification>().FirstAsync()).StoreCode);
        Assert.Equal(0, await _db.Queryable<ContainerNewProductSeen>().Where(row => row.StoreCode == NswStore && row.ProductCode == "P2").CountAsync());
        Assert.Equal(0, await _db.Queryable<ContainerNewProductRun>().Where(row => row.StoreCode == NswStore && row.LocalDate == D0716).CountAsync());

        await RunAtAsync(Local(Sydney, 2026, 7, 16, 9, 5));

        var notifications = await _db.Queryable<AppNotification>().ToListAsync();
        Assert.Equal([NswStore, QldStore], notifications.Select(row => row.StoreCode).Order());
        Assert.Equal(2, _dispatcher.Sent.Count);
    }

    [Fact]
    public async Task RunOnce_并发实例抢先写入Run_本次事务整体回滚且不抛异常_不留下半截Seen()
    {
        AddContainer("C-1", new DateTime(2026, 7, 15), "P1", "P2");

        // 另一实例在我们查询之后、落记录之前抢先完成了同店同日的处理：Run 已落库。
        // 我们的 Seen 批量写入本身能成功，随后 Run 主键冲突 → 整个事务必须回滚，Seen 里不能留下我们写的那两行。
        await RunAtAsync(
            Local(Sydney, 2026, 7, 15, 9, 0),
            queryDecorator: inner => new RacingQuery(inner, store =>
            {
                if (store.StoreCode != NswStore)
                {
                    return;
                }

                _db.Insertable(new ContainerNewProductRun
                {
                    StoreCode = NswStore,
                    LocalDate = D0715,
                    ProcessedAtUtc = _time.Now,
                    NewCount = 0,
                    IsBaseline = true,
                }).ExecuteCommand();
            }));

        Assert.Empty(await _db.Queryable<ContainerNewProductSeen>().Where(row => row.StoreCode == NswStore).ToListAsync());
        Assert.Single(await _db.Queryable<ContainerNewProductRun>().Where(row => row.StoreCode == NswStore).ToListAsync());
        Assert.Contains(_workerLog.Entries, entry => entry.Contains("抢先写入", StringComparison.Ordinal));
        // 冲突是正常的并发结果，不是失败：不记「处理失败」。
        Assert.DoesNotContain(_workerLog.Entries, entry => entry.Contains("处理失败", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 事务外发布守卫自检_事务里发布会被拒绝_worker的发布不在事务里()
    {
        AddContainer("C-1", new DateTime(2026, 7, 15), "P1");
        await RunAtAsync(Local(Sydney, 2026, 7, 15, 9, 0)); // 基线
        AddContainer("C-2", new DateTime(2026, 7, 16), "P2");

        // 守卫本身有效：在 UseTran 里调用发布会抛异常。
        var guard = new OutsideTransactionPublisher(CreatePublisher(), _db);
        var tran = await _db.Ado.UseTranAsync(async () =>
            await guard.PublishAsync(new NotificationPublishRequest
            {
                Type = "t",
                Title = "t",
                Body = "t",
                Audience = NotificationAudience.ForUsers(ManagerGuid),
            }));
        Assert.False(tran.IsSuccess);
        Assert.IsType<InvalidOperationException>(tran.ErrorException);

        // worker 的发布不在事务里：守卫放行，通知照常发出（RunAtAsync 里所有发布都经过守卫）。
        await RunAtAsync(Local(Sydney, 2026, 7, 16, 9, 0));
        Assert.Equal(1, await _db.Queryable<AppNotification>().CountAsync());
    }

    [Fact]
    public async Task RunOnce_Enabled为false时整轮不运行()
    {
        AddContainer("C-1", new DateTime(2026, 7, 15), "P1");
        _options.Enabled = false;

        await RunAtAsync(Local(Sydney, 2026, 7, 15, 9, 0));

        Assert.Empty(await _db.Queryable<ContainerNewProductRun>().ToListAsync());
        Assert.Empty(await _db.Queryable<ContainerNewProductSeen>().ToListAsync());
        Assert.Equal(0, await _db.Queryable<AppNotification>().CountAsync());
    }

    [Fact]
    public async Task RunOnce_窗口可配置_LocalHour与LatestLocalHour生效()
    {
        AddContainer("C-1", new DateTime(2026, 7, 15), "P1");
        _options.LocalHour = 10;
        _options.LatestLocalHour = 12;

        await RunAtAsync(Local(Sydney, 2026, 7, 15, 9, 59));
        await RunAtAsync(Local(Sydney, 2026, 7, 15, 12, 0));
        Assert.Empty(await _db.Queryable<ContainerNewProductRun>().ToListAsync());

        await RunAtAsync(Local(Sydney, 2026, 7, 15, 11, 59));
        Assert.Equal(NswStore, Assert.Single(await _db.Queryable<ContainerNewProductRun>().ToListAsync()).StoreCode);
    }

    [Theory]
    [InlineData(8, 20, 8, 20)]
    [InlineData(-3, 50, 0, 24)]
    [InlineData(10, 5, 10, 11)]
    [InlineData(23, 0, 23, 24)]
    public void ResolveHourRange_越界与倒置的配置被收敛到合法区间(int localHour, int latestHour, int expectedStart, int expectedEnd)
    {
        var range = ContainerNewProductNotificationWorker.ResolveHourRange(new ContainerNewProductNotificationOptions
        {
            LocalHour = localHour,
            LatestLocalHour = latestHour,
        });

        Assert.Equal((expectedStart, expectedEnd), range);
    }

    [Fact]
    public void Options_默认值()
    {
        var options = new ContainerNewProductNotificationOptions();

        Assert.True(options.Enabled);
        Assert.Equal(5, options.IntervalMinutes);
        Assert.Equal(8, options.LocalHour);
        Assert.Equal(20, options.LatestLocalHour);
        Assert.Equal("ContainerNewProductNotifications", ContainerNewProductNotificationOptions.SectionName);
    }

    // ---------- 无鉴权内核与 GetAsync 的一致性 ----------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 内核与GetAsync返回一致_本地今天由注入的时间决定(bool includeExisting)
    {
        // UTC 2026-10-19 21:30：悉尼已是 10-20 08:30，布里斯班 10-20 07:30，本地今天都是 10-20。
        _time.Now = new DateTime(2026, 10, 19, 21, 30, 0, DateTimeKind.Utc);
        AddContainer("C-1", new DateTime(2026, 10, 20), "P-NEW", "P-OLD");
        _db.Insertable(new WarehouseProduct { ProductCode = "P-OLD" }).ExecuteCommand();
        _db.Insertable(new StoreRetailPrice { StoreCode = NswStore, ProductCode = "P-NEW", StoreRetailPriceValue = 5.99m }).ExecuteCommand();
        var store = await _db.Queryable<Store>().Where(row => row.StoreCode == NswStore).FirstAsync();

        var viaService = await CreateReactService(ManagerGuid).GetAsync(" " + NswStore + " ", includeExisting);
        var viaKernel = await new ContainerNewProductsQuery(CreateSqlSugarContext(_db))
            .GetForStoreAsync(store, includeExisting, _time.Now);

        Assert.Equal(JsonSerializer.Serialize(viaService), JsonSerializer.Serialize(viaKernel));
        Assert.Equal(new DateOnly(2026, 10, 20), viaKernel.LocalToday);
        Assert.Equal("NSW", viaKernel.StateCode);
        Assert.Contains(viaKernel.Items, item => item.ProductCode == "P-NEW" && item.RetailPrice == 5.99m);
        Assert.Equal(includeExisting, viaKernel.Items.Any(item => item.ProductCode == "P-OLD"));
    }

    [Fact]
    public async Task 内核的本地今天按门店所在州的时区算_布里斯班与悉尼夏令时相差一天()
    {
        AddQldStore();
        // UTC 2026-10-19 13:30：悉尼 10-20 00:30（已是次日），布里斯班 10-19 23:30。
        var nowUtc = new DateTime(2026, 10, 19, 13, 30, 0, DateTimeKind.Utc);
        var query = new ContainerNewProductsQuery(CreateSqlSugarContext(_db));
        var nsw = await _db.Queryable<Store>().Where(row => row.StoreCode == NswStore).FirstAsync();
        var qld = await _db.Queryable<Store>().Where(row => row.StoreCode == QldStore).FirstAsync();

        Assert.Equal(new DateOnly(2026, 10, 20), (await query.GetForStoreAsync(nsw, false, nowUtc)).LocalToday);
        Assert.Equal(new DateOnly(2026, 10, 19), (await query.GetForStoreAsync(qld, false, nowUtc)).LocalToday);
    }

    [Fact]
    public async Task 内核对状态不明的门店抛异常_GetAsync对鉴权与不存在的门店行为不变()
    {
        var vic = await _db.Queryable<Store>().Where(row => row.StoreCode == VicStore).FirstAsync();
        _db.Insertable(new UserStore { UserGUID = ManagerGuid, StoreGUID = vic.StoreGUID }).ExecuteCommand();
        var query = new ContainerNewProductsQuery(CreateSqlSugarContext(_db));

        await Assert.ThrowsAsync<ContainerNewProductsStateUnknownException>(() => query.GetForStoreAsync(vic, false, _time.Now));
        await Assert.ThrowsAsync<ContainerNewProductsStateUnknownException>(() => CreateReactService(ManagerGuid).GetAsync(VicStore));
        // 鉴权仍在 GetAsync 里：没有关联该门店的用户拿不到数据，哪怕后台内核对同一门店可读。
        await Assert.ThrowsAsync<ContainerNewProductsForbiddenException>(() => CreateReactService("user-stranger").GetAsync(NswStore));
        // 门店不存在只有通过了范围校验（管理员）才会走到：非管理员访问不到的门店一律先报无权限。
        await Assert.ThrowsAsync<ContainerNewProductsForbiddenException>(() => CreateReactService(ManagerGuid).GetAsync("S-MISSING-STORE"));
        await Assert.ThrowsAsync<ContainerNewProductsStoreNotFoundException>(() => CreateReactService(ManagerGuid, isAdmin: true).GetAsync("S-MISSING-STORE"));
    }

    // ---------- 夹具 ----------

    private async Task RunAtAsync(
        DateTime nowUtc,
        Func<IContainerNewProductsQuery, IContainerNewProductsQuery>? queryDecorator = null,
        Func<INotificationPublisher, INotificationPublisher>? publisherDecorator = null
    )
    {
        _time.Now = nowUtc;
        var services = new ServiceCollection();
        services.AddSingleton(CreateSqlSugarContext(_db));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(_roleService.Object);
        services.AddSingleton<INotificationRealtimeDispatcher>(_dispatcher);
        services.AddSingleton<TimeProvider>(_time);
        services.AddScoped<INotificationRecipientResolver, NotificationRecipientResolver>();
        services.AddScoped<INotificationPublisher>(provider =>
        {
            // 所有用例的发布都经过守卫：发布时连接上有未结束的事务就抛异常（发布器自己开事务，嵌套会破坏外层回滚）。
            INotificationPublisher publisher = new OutsideTransactionPublisher(
                ActivatorUtilities.CreateInstance<NotificationPublisher>(provider),
                provider.GetRequiredService<SqlSugarContext>().Db);
            return publisherDecorator?.Invoke(publisher) ?? publisher;
        });
        services.AddScoped<IContainerNewProductsQuery>(provider =>
        {
            IContainerNewProductsQuery query = new ContainerNewProductsQuery(provider.GetRequiredService<SqlSugarContext>());
            return queryDecorator?.Invoke(query) ?? query;
        });
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        _scopeFactory.Inner = provider.GetRequiredService<IServiceScopeFactory>();

        await _worker.RunOnceAsync(nowUtc, CancellationToken.None);
    }

    private NotificationPublisher CreatePublisher()
    {
        var context = CreateSqlSugarContext(_db);
        return new NotificationPublisher(
            context,
            new NotificationRecipientResolver(
                context,
                _roleService.Object,
                NullLogger<NotificationRecipientResolver>.Instance),
            _dispatcher,
            _time,
            NullLogger<NotificationPublisher>.Instance);
    }

    private ContainerNewProductsReactService CreateReactService(string userGuid, bool isAdmin = false)
    {
        var scope = new Mock<ICurrentUserManageableStoreScopeService>();
        scope.Setup(service => service.GetScopeAsync())
            .ReturnsAsync(new CurrentUserManageableStoreScope { IsAuthenticated = true, IsAdmin = isAdmin, UserGuid = userGuid });
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(service => service.GetCurrentUserGuid()).Returns(userGuid);
        return new ContainerNewProductsReactService(CreateSqlSugarContext(_db), scope.Object, currentUser.Object, _time);
    }

    private void SeedStoresAndUsers()
    {
        AddStore(NswStore, "g-nsw", "Sydney NSW 2000");
        // 墨尔本：配置之外的州，内核抛状态不明。
        AddStore(VicStore, "g-vic", "Melbourne VIC 3000");
        // 已停用、已删除的门店不参与。
        AddStore("S-OFF", "g-off", "Sydney NSW 2000", isActive: false);
        AddStore("S-DEL", "g-del", "Sydney NSW 2000", isDeleted: true);

        _db.Insertable(new List<User>
        {
            new() { UserGUID = ManagerGuid, Username = "manager", IsActive = true },
            new() { UserGUID = CashierGuid, Username = "cashier", IsActive = true },
        }).ExecuteCommand();
        _db.Insertable(new List<UserStore>
        {
            new() { UserGUID = ManagerGuid, StoreGUID = "g-nsw" },
            new() { UserGUID = CashierGuid, StoreGUID = "g-nsw" },
            new() { UserGUID = ManagerGuid, StoreGUID = "g-off" },
            new() { UserGUID = ManagerGuid, StoreGUID = "g-del" },
        }).ExecuteCommand();
    }

    /// <summary>布里斯班门店：需要双州对照的用例才加，其余用例只有悉尼门店，通知条数一目了然。</summary>
    private void AddQldStore()
    {
        AddStore(QldStore, "g-qld", "Brisbane QLD 4000");
        _db.Insertable(new List<UserStore>
        {
            new() { UserGUID = ManagerGuid, StoreGUID = "g-qld" },
            new() { UserGUID = CashierGuid, StoreGUID = "g-qld" },
        }).ExecuteCommand();
    }

    private void AddStore(string storeCode, string storeGuid, string address, bool isActive = true, bool isDeleted = false) =>
        _db.Insertable(new Store
        {
            StoreGUID = storeGuid,
            StoreCode = storeCode,
            StoreName = storeCode,
            Address = address,
            IsActive = isActive,
            IsDeleted = isDeleted,
        }).ExecuteCommand();

    /// <summary>加一个货柜与它的明细；商品不在仓库里，按「新品」处理。</summary>
    private void AddContainer(string containerCode, DateTime arrivalDate, params string[] productCodes)
    {
        _db.Insertable(new Container { ContainerCode = containerCode, ContainerNumber = containerCode, ActualArrivalDate = arrivalDate }).ExecuteCommand();
        _db.Insertable(productCodes
            .Select(productCode => new ContainerDetail
            {
                DetailCode = $"{containerCode}-{productCode}",
                ContainerCode = containerCode,
                ProductCode = productCode,
            })
            .ToArray()).ExecuteCommand();
    }

    private static Dictionary<string, string> ReadData(AppNotification notification) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(notification.DataJson!)!;

    private static DateTime Local(string timeZoneId, int year, int month, int day, int hour, int minute) =>
        TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified),
            TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));

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
        _db.Dispose();
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private sealed class MutableTimeProvider(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc), TimeSpan.Zero);
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

    /// <summary>对指定门店抛异常，其余门店委托给真实内核。</summary>
    private sealed class FailingQuery(IContainerNewProductsQuery inner, string failingStoreCode) : IContainerNewProductsQuery
    {
        public Task<ContainerNewProductsResponseDto> GetForStoreAsync(
            Store store,
            bool includeExisting,
            DateTime nowUtc,
            CancellationToken cancellationToken = default
        ) => store.StoreCode == failingStoreCode
            ? throw new InvalidOperationException("boom")
            : inner.GetForStoreAsync(store, includeExisting, nowUtc, cancellationToken);
    }

    /// <summary>真实内核查询之后立刻执行一段代码，模拟「查询之后、落记录之前」被别的实例抢先写入。</summary>
    private sealed class RacingQuery(IContainerNewProductsQuery inner, Action<Store> afterQuery) : IContainerNewProductsQuery
    {
        public async Task<ContainerNewProductsResponseDto> GetForStoreAsync(
            Store store,
            bool includeExisting,
            DateTime nowUtc,
            CancellationToken cancellationToken = default
        )
        {
            var result = await inner.GetForStoreAsync(store, includeExisting, nowUtc, cancellationToken);
            afterQuery(store);
            return result;
        }
    }

    /// <summary>发布时连接上有未结束的事务就抛异常：硬约束「PublishAsync 必须在任何业务事务之外调用」的测试守卫。</summary>
    private sealed class OutsideTransactionPublisher(INotificationPublisher inner, ISqlSugarClient db) : INotificationPublisher
    {
        public Task<NotificationPublishResult> PublishAsync(
            NotificationPublishRequest request,
            CancellationToken cancellationToken = default
        ) => db.Ado.IsAnyTran()
            ? throw new InvalidOperationException("通知必须在业务事务之外发布")
            : inner.PublishAsync(request, cancellationToken);

        public Task<bool> RevokeAsync(string dedupeKey, CancellationToken cancellationToken = default) =>
            inner.RevokeAsync(dedupeKey, cancellationToken);
    }

    /// <summary>发布指定门店的通知时抛异常，其余委托给真实发布器。</summary>
    private sealed class FailingPublisher(INotificationPublisher inner, string failingStoreCode) : INotificationPublisher
    {
        public Task<NotificationPublishResult> PublishAsync(
            NotificationPublishRequest request,
            CancellationToken cancellationToken = default
        ) => request.StoreCode == failingStoreCode
            ? throw new InvalidOperationException("publish down")
            : inner.PublishAsync(request, cancellationToken);

        public Task<bool> RevokeAsync(string dedupeKey, CancellationToken cancellationToken = default) =>
            inner.RevokeAsync(dedupeKey, cancellationToken);
    }

    /// <summary>worker 只构造一次，每轮把底下的 DI 容器换成新的（带本轮的装饰器）。</summary>
    private sealed class SwitchableScopeFactory : IServiceScopeFactory
    {
        public IServiceScopeFactory Inner { get; set; } = null!;

        public IServiceScope CreateScope() => Inner.CreateScope();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = [];

        public int Count(Func<string, bool> predicate) => Entries.Count(predicate);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Entries.Add(formatter(state, exception));
    }
}
