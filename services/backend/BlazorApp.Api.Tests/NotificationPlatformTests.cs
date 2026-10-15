using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class NotificationPlatformTests : IDisposable
{
    private const string StoreCode = "1013";
    private const string StoreGuid = "store-1013";
    private const string ManagerGuid = "user-manager";
    private const string CashierGuid = "user-cashier";
    private const string InactiveGuid = "user-inactive";
    private const string OtherStoreGuid = "user-other-store";
    private const string PricePermission = "Store.PriceTasks";

    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;
    private readonly MutableTimeProvider _time = new(new DateTime(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc));
    private readonly Mock<IRoleService> _roleService = new(MockBehavior.Strict);
    private readonly RecordingDispatcher _dispatcher = new();

    public NotificationPlatformTests()
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
            typeof(AppNotificationRecipient)
        );
        SeedUsersAndStores();

        _roleService
            .Setup(service => service.UserHasPermissionAsync(ManagerGuid, PricePermission))
            .ReturnsAsync(ApiResponse<bool>.OK(true));
        _roleService
            .Setup(service => service.UserHasPermissionAsync(CashierGuid, PricePermission))
            .ReturnsAsync(ApiResponse<bool>.OK(false));
    }

    [Fact]
    public async Task Publish_门店成员按权限筛选_与指定用户取并集_停用与他店账号不收()
    {
        var result = await CreatePublisher().PublishAsync(Request(
            NotificationAudience.ForStore(StoreCode, PricePermission) with
            {
                UserGuids = [OtherStoreGuid, InactiveGuid, "missing-user"],
            }));

        Assert.NotNull(result.NotificationId);
        Assert.False(result.Deduplicated);
        Assert.Equal(2, result.RecipientCount);
        var recipients = await _db.Queryable<AppNotificationRecipient>()
            .Where(row => row.NotificationId == result.NotificationId)
            .Select(row => row.UserGUID)
            .ToListAsync();
        Assert.Equal([ManagerGuid, OtherStoreGuid], recipients.Order(StringComparer.Ordinal));

        var sent = Assert.Single(_dispatcher.Sent);
        Assert.Equal([ManagerGuid, OtherStoreGuid], sent.UserGuids.Order(StringComparer.Ordinal));
        Assert.Equal(result.NotificationId, sent.Notification.Id);
        Assert.Equal("12", sent.Notification.Data["count"]);
    }

    [Fact]
    public async Task Publish_门店不带权限要求时_全部启用成员都收到()
    {
        var result = await CreatePublisher().PublishAsync(Request(NotificationAudience.ForStore(StoreCode)));

        Assert.Equal(2, result.RecipientCount);
        _roleService.Verify(
            service => service.UserHasPermissionAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task Publish_同一幂等键只发一次_第二次不落库也不投递()
    {
        var publisher = CreatePublisher();
        var first = await publisher.PublishAsync(Request(NotificationAudience.ForUsers(ManagerGuid)) with
        {
            DedupeKey = "approval:submitted:42",
        });
        var second = await publisher.PublishAsync(Request(NotificationAudience.ForUsers(ManagerGuid)) with
        {
            DedupeKey = " approval:submitted:42 ",
        });

        Assert.False(first.Deduplicated);
        Assert.True(second.Deduplicated);
        Assert.Equal(first.NotificationId, second.NotificationId);
        Assert.Equal(1, await _db.Queryable<AppNotification>().CountAsync());
        Assert.Single(_dispatcher.Sent);
    }

    [Fact]
    public async Task Publish_没有接收人时不产生通知()
    {
        var result = await CreatePublisher().PublishAsync(Request(NotificationAudience.ForUsers(InactiveGuid)));

        Assert.Null(result.NotificationId);
        Assert.Equal(0, result.RecipientCount);
        Assert.Equal(0, await _db.Queryable<AppNotification>().CountAsync());
        Assert.Empty(_dispatcher.Sent);
    }

    [Fact]
    public async Task Publish_在显式业务事务内调用_直接抛异常_外层回滚照常生效且不留通知()
    {
        await _db.Ado.ExecuteCommandAsync("CREATE TABLE BizProbe (Id INTEGER PRIMARY KEY)");
        await _db.Ado.BeginTranAsync();
        await _db.Ado.ExecuteCommandAsync("INSERT INTO BizProbe (Id) VALUES (1)");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreatePublisher().PublishAsync(Request(NotificationAudience.ForUsers(ManagerGuid))));
        await _db.Ado.RollbackTranAsync();

        // 没有防护时内层提交会把业务行一并提交，外层回滚形同虚设（SQL Server 实测）。
        Assert.Equal(0, await _db.Ado.GetIntAsync("SELECT COUNT(*) FROM BizProbe"));
        Assert.Equal(0, await _db.Queryable<AppNotification>().CountAsync());
        Assert.Empty(_dispatcher.Sent);
    }

    [Fact]
    public async Task Publish_在UseTranAsync回调内调用_直接抛异常_业务失败时整体回滚()
    {
        await _db.Ado.ExecuteCommandAsync("CREATE TABLE BizProbe (Id INTEGER PRIMARY KEY)");

        var outer = await _db.Ado.UseTranAsync(async () =>
        {
            await _db.Ado.ExecuteCommandAsync("INSERT INTO BizProbe (Id) VALUES (1)");
            // 业务代码若误把发布放进事务，异常会冒泡并让外层回滚，而不是留下半提交的数据。
            await CreatePublisher().PublishAsync(Request(NotificationAudience.ForUsers(ManagerGuid)));
        });

        Assert.False(outer.IsSuccess);
        Assert.IsType<InvalidOperationException>(outer.ErrorException);
        Assert.Equal(0, await _db.Ado.GetIntAsync("SELECT COUNT(*) FROM BizProbe"));
        Assert.Equal(0, await _db.Queryable<AppNotification>().CountAsync());
        Assert.Empty(_dispatcher.Sent);
    }

    [Fact]
    public async Task Publish_业务事务提交之后调用_正常发布()
    {
        await _db.Ado.ExecuteCommandAsync("CREATE TABLE BizProbe (Id INTEGER PRIMARY KEY)");
        await _db.Ado.BeginTranAsync();
        await _db.Ado.ExecuteCommandAsync("INSERT INTO BizProbe (Id) VALUES (1)");
        await _db.Ado.CommitTranAsync();

        var result = await CreatePublisher().PublishAsync(Request(NotificationAudience.ForUsers(ManagerGuid)));

        Assert.NotNull(result.NotificationId);
        Assert.Equal(1, await _db.Ado.GetIntAsync("SELECT COUNT(*) FROM BizProbe"));
        Assert.Single(_dispatcher.Sent);
    }

    [Fact]
    public async Task Publish_实时投递失败不影响发布_站内消息仍可读()
    {
        _dispatcher.ThrowOnSend = true;

        var result = await CreatePublisher().PublishAsync(Request(NotificationAudience.ForUsers(ManagerGuid)));

        Assert.NotNull(result.NotificationId);
        var page = await CreateInbox().ListAsync(ManagerGuid, cursor: null, take: 20, unreadOnly: false);
        Assert.Equal(result.NotificationId, Assert.Single(page.Items).Id);
        Assert.Equal(1, page.UnreadCount);
    }

    [Theory]
    [InlineData("", "标题", "正文", 0)]
    [InlineData("price.changed", " ", "正文", 0)]
    [InlineData("price.changed", "标题", "", 0)]
    [InlineData("price.changed", "标题", "正文", 7)]
    public async Task Publish_必填字段为空或优先级无效时拒绝(string type, string title, string body, int priority)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => CreatePublisher().PublishAsync(
            new NotificationPublishRequest
            {
                Type = type,
                Title = title,
                Body = body,
                Priority = priority,
                Audience = NotificationAudience.ForUsers(ManagerGuid),
            }));
        Assert.Equal(0, await _db.Queryable<AppNotification>().CountAsync());
    }

    [Fact]
    public async Task Publish_标题超长时拒绝()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => CreatePublisher().PublishAsync(
            Request(NotificationAudience.ForUsers(ManagerGuid)) with
            {
                Title = new string('价', NotificationPublisher.MaxTitleLength + 1),
            }));
    }

    [Fact]
    public async Task Revoke_撤回后不再出现在收件箱与未读数中_重复撤回返回false()
    {
        var publisher = CreatePublisher();
        await publisher.PublishAsync(Request(NotificationAudience.ForUsers(ManagerGuid)) with
        {
            DedupeKey = "approval:submitted:7",
        });

        Assert.True(await publisher.RevokeAsync("approval:submitted:7"));
        Assert.False(await publisher.RevokeAsync("approval:submitted:7"));
        Assert.False(await publisher.RevokeAsync("missing"));

        var inbox = CreateInbox();
        Assert.Empty((await inbox.ListAsync(ManagerGuid, null, 20, unreadOnly: false)).Items);
        Assert.Equal(0, await inbox.GetUnreadCountAsync(ManagerGuid));
    }

    [Fact]
    public async Task Inbox_过期通知不显示()
    {
        await CreatePublisher().PublishAsync(Request(NotificationAudience.ForUsers(ManagerGuid)) with
        {
            ExpiresAtUtc = _time.Now.AddHours(1),
        });
        var inbox = CreateInbox();
        Assert.Equal(1, await inbox.GetUnreadCountAsync(ManagerGuid));

        _time.Now = _time.Now.AddHours(2);

        Assert.Equal(0, await inbox.GetUnreadCountAsync(ManagerGuid));
        Assert.Empty((await inbox.ListAsync(ManagerGuid, null, 20, unreadOnly: false)).Items);
    }

    [Fact]
    public async Task Inbox_按时间倒序游标翻页_不重复不遗漏_同一时刻的通知也能翻完()
    {
        var publisher = CreatePublisher();
        var published = new List<string>();
        for (var index = 0; index < 5; index++)
        {
            // 两两同一时刻发布，验证游标在时间相同时按 Id 继续往下翻。
            _time.Now = _time.Now.AddSeconds(index % 2 == 0 ? 1 : 0);
            var result = await publisher.PublishAsync(Request(NotificationAudience.ForUsers(ManagerGuid)) with
            {
                Title = $"通知 {index}",
            });
            published.Add(result.NotificationId!);
        }

        var inbox = CreateInbox();
        var seen = new List<NotificationDto>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await inbox.ListAsync(ManagerGuid, cursor, take: 2, unreadOnly: false);
            seen.AddRange(page.Items);
            cursor = page.NextCursor;
            pages++;
            Assert.Equal(5, page.UnreadCount);
        }
        while (cursor != null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(published.Order(StringComparer.Ordinal), seen.Select(item => item.Id).Order(StringComparer.Ordinal));
        Assert.Equal(seen.Count, seen.Select(item => item.Id).Distinct().Count());
        for (var index = 1; index < seen.Count; index++)
        {
            var previous = seen[index - 1];
            var current = seen[index];
            Assert.True(
                previous.CreatedAtUtc > current.CreatedAtUtc
                || (previous.CreatedAtUtc == current.CreatedAtUtc
                    && string.CompareOrdinal(previous.Id, current.Id) > 0));
        }
    }

    [Fact]
    public async Task Inbox_标记已读只影响本人_未读筛选与未读数同步变化()
    {
        var publisher = CreatePublisher();
        var shared = await publisher.PublishAsync(Request(NotificationAudience.ForUsers(ManagerGuid, OtherStoreGuid)));
        var mine = await publisher.PublishAsync(Request(NotificationAudience.ForUsers(ManagerGuid)));
        var inbox = CreateInbox();

        // 他人的通知 Id 改不到本人以外的记录。
        var foreign = await inbox.MarkReadAsync(CashierGuid, [shared.NotificationId!]);
        Assert.Equal(0, foreign.Updated);

        var marked = await inbox.MarkReadAsync(ManagerGuid, [shared.NotificationId!, shared.NotificationId!]);
        Assert.Equal(1, marked.Updated);
        Assert.Equal(1, marked.UnreadCount);
        Assert.Equal(1, await inbox.GetUnreadCountAsync(OtherStoreGuid));

        var again = await inbox.MarkReadAsync(ManagerGuid, [shared.NotificationId!]);
        Assert.Equal(0, again.Updated);

        var unread = await inbox.ListAsync(ManagerGuid, null, 20, unreadOnly: true);
        Assert.Equal(mine.NotificationId, Assert.Single(unread.Items).Id);
        var all = await inbox.ListAsync(ManagerGuid, null, 20, unreadOnly: false);
        Assert.NotNull(all.Items.Single(item => item.Id == shared.NotificationId).ReadAtUtc);

        var allRead = await inbox.MarkAllReadAsync(ManagerGuid);
        Assert.Equal(1, allRead.Updated);
        Assert.Equal(0, allRead.UnreadCount);
        Assert.Equal(1, await inbox.GetUnreadCountAsync(OtherStoreGuid));
    }

    // ---------- 按类型过滤（人脸比对合并入口的红色数字：unread-count?type=face.unwelcome-hit） ----------

    [Fact]
    public async Task Inbox_按类型统计未读_缺省是全部_已撤回与已过期的不计()
    {
        var publisher = CreatePublisher();
        for (var i = 0; i < 3; i++)
        {
            await publisher.PublishAsync(Request(NotificationAudience.ForUsers(ManagerGuid)) with
            {
                Type = AppNotificationTypes.FaceUnwelcomeHit,
                DedupeKey = $"unwelcome-hit:t{i}",
            });
        }

        await publisher.PublishAsync(Request(NotificationAudience.ForUsers(ManagerGuid)));   // price.changed
        await publisher.PublishAsync(Request(NotificationAudience.ForUsers(OtherStoreGuid)) with { Type = AppNotificationTypes.FaceUnwelcomeHit });
        var inbox = CreateInbox();

        Assert.Equal(4, await inbox.GetUnreadCountAsync(ManagerGuid));                                           // 缺省 = 全部（原行为）
        Assert.Equal(3, await inbox.GetUnreadCountAsync(ManagerGuid, AppNotificationTypes.FaceUnwelcomeHit));
        Assert.Equal(1, await inbox.GetUnreadCountAsync(ManagerGuid, "price.changed"));
        Assert.Equal(0, await inbox.GetUnreadCountAsync(ManagerGuid, "no.such.type"));
        Assert.Equal(1, await inbox.GetUnreadCountAsync(OtherStoreGuid, AppNotificationTypes.FaceUnwelcomeHit));

        // 撤回一条：与收件箱同一可见口径，类型未读数跟着减少。
        Assert.True(await publisher.RevokeAsync("unwelcome-hit:t0"));
        Assert.Equal(2, await inbox.GetUnreadCountAsync(ManagerGuid, AppNotificationTypes.FaceUnwelcomeHit));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a b")]
    [InlineData("face/unwelcome")]
    [InlineData("face'unwelcome")]
    [InlineData("中文")]
    public async Task Inbox_类型格式不合法_未读数与全部已读都抛ArgumentException(string type)
    {
        var inbox = CreateInbox();

        await Assert.ThrowsAsync<ArgumentException>(() => inbox.GetUnreadCountAsync(ManagerGuid, type));
        await Assert.ThrowsAsync<ArgumentException>(() => inbox.MarkAllReadAsync(ManagerGuid, type));
    }

    [Fact]
    public async Task Inbox_类型长度上限64_超过抛异常()
    {
        var inbox = CreateInbox();

        Assert.Equal(0, await inbox.GetUnreadCountAsync(ManagerGuid, new string('a', 64)));
        await Assert.ThrowsAsync<ArgumentException>(() => inbox.GetUnreadCountAsync(ManagerGuid, new string('a', 65)));
    }

    [Fact]
    public async Task Inbox_按类型全部已读_只标该类型_返回该类型剩余未读数_不带类型时行为不变()
    {
        var publisher = CreatePublisher();
        for (var i = 0; i < 3; i++)
        {
            await publisher.PublishAsync(Request(NotificationAudience.ForUsers(ManagerGuid, OtherStoreGuid)) with
            {
                Type = AppNotificationTypes.FaceUnwelcomeHit,
                DedupeKey = $"unwelcome-hit:t{i}",
            });
        }

        await publisher.PublishAsync(Request(NotificationAudience.ForUsers(ManagerGuid)));   // price.changed
        var inbox = CreateInbox();

        var byType = await inbox.MarkAllReadAsync(ManagerGuid, AppNotificationTypes.FaceUnwelcomeHit);

        Assert.Equal(3, byType.Updated);
        Assert.Equal(0, byType.UnreadCount);                                                    // 返回的是该类型的剩余未读数
        Assert.Equal(1, await inbox.GetUnreadCountAsync(ManagerGuid));                          // 其他类型没被动
        Assert.Equal(3, await inbox.GetUnreadCountAsync(OtherStoreGuid, AppNotificationTypes.FaceUnwelcomeHit));   // 别人的行没被动

        // 再标一次：幂等，什么都不更新。
        var again = await inbox.MarkAllReadAsync(ManagerGuid, AppNotificationTypes.FaceUnwelcomeHit);
        Assert.Equal(0, again.Updated);

        // 不带类型：原行为，标全部，unreadCount 是总未读数。
        var all = await inbox.MarkAllReadAsync(ManagerGuid);
        Assert.Equal(1, all.Updated);
        Assert.Equal(0, all.UnreadCount);
    }

    [Fact]
    public async Task Inbox_按类型全部已读_未读很多时分批更新也能全部标完()
    {
        var count = 1200;   // 超过单批 500 的分块大小
        _db.Insertable(Enumerable.Range(0, count).Select(i => new AppNotification
        {
            NotificationId = $"n{i:D6}",
            Type = AppNotificationTypes.FaceUnwelcomeHit,
            Title = "t",
            Body = "b",
            CreatedAtUtc = _time.Now,
            CreatedBy = "test",
        }).ToList()).ExecuteCommand();
        _db.Insertable(Enumerable.Range(0, count).Select(i => new AppNotificationRecipient
        {
            NotificationId = $"n{i:D6}",
            UserGUID = ManagerGuid,
            CreatedAtUtc = _time.Now,
        }).ToList()).ExecuteCommand();
        var inbox = CreateInbox();
        Assert.Equal(count, await inbox.GetUnreadCountAsync(ManagerGuid, AppNotificationTypes.FaceUnwelcomeHit));

        var result = await inbox.MarkAllReadAsync(ManagerGuid, AppNotificationTypes.FaceUnwelcomeHit);

        Assert.Equal(count, result.Updated);
        Assert.Equal(0, result.UnreadCount);
    }

    [Fact]
    public async Task 控制器_未读数与全部已读的type参数格式不合法返回400_合法时透传类型()
    {
        var publisher = CreatePublisher();
        await publisher.PublishAsync(Request(NotificationAudience.ForUsers(ManagerGuid)) with { Type = AppNotificationTypes.FaceUnwelcomeHit });
        await publisher.PublishAsync(Request(NotificationAudience.ForUsers(ManagerGuid)));
        var controller = new BlazorApp.Api.Controllers.React.NotificationsController(
            CreateInbox(), Mock.Of<INotificationPublisher>(), Mock.Of<IAppPushDeviceService>(), NullLogger<BlazorApp.Api.Controllers.React.NotificationsController>.Instance)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(
                        new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("userId", ManagerGuid)], "test")),
                },
            },
        };

        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(await controller.UnreadCount("bad type!", default));
        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(await controller.MarkAllRead("a/b", default));

        var total = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.UnreadCount(null, default));
        Assert.Equal(2, Assert.IsType<ApiResponse<int>>(total.Value).Data);
        var typed = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.UnreadCount(AppNotificationTypes.FaceUnwelcomeHit, default));
        Assert.Equal(1, Assert.IsType<ApiResponse<int>>(typed.Value).Data);

        var read = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.MarkAllRead(AppNotificationTypes.FaceUnwelcomeHit, default));
        var readData = Assert.IsType<ApiResponse<NotificationMarkReadResultDto>>(read.Value).Data!;
        Assert.Equal(1, readData.Updated);
        Assert.Equal(0, readData.UnreadCount);                       // 该类型的剩余未读数
        Assert.Equal(1, await CreateInbox().GetUnreadCountAsync(ManagerGuid));   // 另一类型仍未读
    }

    [Fact]
    public async Task Inbox_一次标记超过上限时拒绝()
    {
        var ids = Enumerable.Range(0, NotificationInboxService.MaxMarkReadIds + 1).Select(i => $"id-{i}").ToList();

        await Assert.ThrowsAsync<ArgumentException>(() => CreateInbox().MarkReadAsync(ManagerGuid, ids));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData("MTIz")] // "123"：缺分隔符
    [InlineData("LTE6YWJj")] // "-1:abc"：负数时间
    public void Cursor_无效游标按首页处理(string? cursor)
    {
        Assert.False(NotificationInboxService.TryDecodeCursor(cursor, out _, out _));
    }

    [Fact]
    public void Cursor_编码后可还原()
    {
        var createdAt = new DateTime(2026, 10, 12, 1, 2, 3, DateTimeKind.Utc).AddTicks(4567);
        var cursor = NotificationInboxService.EncodeCursor(new AppNotification
        {
            NotificationId = "0123456789abcdef0123456789abcdef",
            CreatedAtUtc = createdAt,
        });

        Assert.True(NotificationInboxService.TryDecodeCursor(cursor, out var decodedAt, out var decodedId));
        Assert.Equal(createdAt, decodedAt);
        Assert.Equal("0123456789abcdef0123456789abcdef", decodedId);
    }

    [Fact]
    public void Hub_个人分组名不区分大小写()
    {
        Assert.Equal(NotificationHub.UserGroup("ABC-def"), NotificationHub.UserGroup(" abc-DEF "));
    }

    private NotificationPublishRequest Request(NotificationAudience audience) => new()
    {
        Type = "price.changed",
        Title = "本店有商品需要改价",
        Body = "12 个商品需改价或换标签",
        Audience = audience,
        StoreCode = StoreCode,
        Data = new Dictionary<string, string> { ["count"] = "12" },
    };

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

    private NotificationInboxService CreateInbox() => new(CreateSqlSugarContext(_db), _time);

    private void SeedUsersAndStores()
    {
        _db.Insertable(new List<User>
        {
            new() { UserGUID = ManagerGuid, Username = "manager", IsActive = true },
            new() { UserGUID = CashierGuid, Username = "cashier", IsActive = true },
            new() { UserGUID = InactiveGuid, Username = "inactive", IsActive = false },
            new() { UserGUID = OtherStoreGuid, Username = "other", IsActive = true },
        }).ExecuteCommand();
        _db.Insertable(new List<Store>
        {
            new() { StoreGUID = StoreGuid, StoreCode = StoreCode },
            new() { StoreGUID = "store-2001", StoreCode = "2001" },
        }).ExecuteCommand();
        _db.Insertable(new List<UserStore>
        {
            new() { UserGUID = ManagerGuid, StoreGUID = StoreGuid },
            new() { UserGUID = CashierGuid, StoreGUID = StoreGuid },
            new() { UserGUID = InactiveGuid, StoreGUID = StoreGuid },
            new() { UserGUID = OtherStoreGuid, StoreGUID = "store-2001" },
            // 已解除的门店关联不算成员。
            new() { UserGUID = OtherStoreGuid, StoreGUID = StoreGuid, IsDeleted = true },
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
        public bool ThrowOnSend { get; set; }

        public List<(IReadOnlyCollection<string> UserGuids, NotificationDto Notification)> Sent { get; } = [];

        public Task SendToUsersAsync(
            IReadOnlyCollection<string> userGuids,
            NotificationDto notification,
            CancellationToken cancellationToken = default
        )
        {
            if (ThrowOnSend)
            {
                throw new InvalidOperationException("hub down");
            }

            Sent.Add((userGuids, notification));
            return Task.CompletedTask;
        }
    }
}
