using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace BlazorApp.Api.Tests;

/// <summary>App 推送测试共用的假时钟：可前进，所有时间判断都走它。</summary>
internal sealed class PushTestClock(DateTime now) : TimeProvider
{
    public DateTime Now { get; set; } = now;

    public void Advance(TimeSpan by) => Now += by;

    public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc), TimeSpan.Zero);
}

/// <summary>可变的配置监视器：测试里随时翻开关，等价于配置热更新。</summary>
internal sealed class PushOptionsMonitor(ExpoPushOptions value) : IOptionsMonitor<ExpoPushOptions>
{
    public ExpoPushOptions Value { get; set; } = value;

    public ExpoPushOptions CurrentValue => Value;

    public ExpoPushOptions Get(string? name) => Value;

    public IDisposable? OnChange(Action<ExpoPushOptions, string?> listener) => null;
}

internal sealed record PushRecordedRequest(
    HttpMethod Method,
    Uri Uri,
    string? Authorization,
    string Accept,
    string? ContentType,
    string Body
);

/// <summary>假的 Expo 服务器：先把请求体读出来存好（客户端发完会释放请求内容），再按回调给响应。</summary>
internal sealed class PushStubHandler(Func<PushRecordedRequest, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<PushRecordedRequest> Requests { get; } = [];

    public IReadOnlyList<PushRecordedRequest> SendRequests =>
        Requests.Where(request => request.Uri.AbsolutePath.EndsWith("/send", StringComparison.Ordinal)).ToList();

    public IReadOnlyList<PushRecordedRequest> ReceiptRequests =>
        Requests.Where(request => request.Uri.AbsolutePath.EndsWith("/getReceipts", StringComparison.Ordinal)).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new PushRecordedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            request.Headers.Accept.ToString(),
            request.Content?.Headers.ContentType?.ToString(),
            body);
        lock (Requests)
        {
            Requests.Add(recorded);
        }

        return await respond(recorded);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>对 /send 的请求按消息顺序回 N 个 ok ticket，ticket id 形如 <c>t-{令牌最后 6 位}</c>，便于断言顺序映射。</summary>
    public static Task<HttpResponseMessage> OkTicketsFor(PushRecordedRequest request)
    {
        var tokens = System.Text.Json.JsonDocument.Parse(request.Body).RootElement.EnumerateArray()
            .Select(message => message.GetProperty("to").GetString()!)
            .ToList();
        var items = tokens.Select(token => $"{{\"status\":\"ok\",\"id\":\"t-{TicketSuffix(token)}\"}}");
        return Task.FromResult(Json(HttpStatusCode.OK, $"{{\"data\":[{string.Join(",", items)}]}}"));
    }

    public static string TicketSuffix(string token) => token[^7..^1];
}

internal sealed class PushStubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>
/// 用 SQLite 文件库模拟生产表结构的测试库：通知 / 接收人 / 用户 / 门店由 CodeFirst 建，推送两张表额外补上与迁移一致的唯一索引
/// （活动令牌过滤唯一、(通知, 设备) 唯一），这样唯一冲突、转移等行为与 SQL Server 一致。
/// </summary>
internal sealed class PushTestDatabase : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;

    public PushTestDatabase()
    {
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        Db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        Db.CodeFirst.InitTables(
            typeof(User),
            typeof(Store),
            typeof(UserStore),
            typeof(AppNotification),
            typeof(AppNotificationRecipient),
            typeof(AppPushDevice),
            typeof(AppPushDelivery)
        );
        Db.Ado.ExecuteCommand(
            "CREATE UNIQUE INDEX UX_AppPushDevice_ActiveToken ON AppPushDevice (ExpoPushToken) WHERE RevokedAtUtc IS NULL");
        Db.Ado.ExecuteCommand(
            "CREATE UNIQUE INDEX UX_AppPushDelivery_Notification_Device ON AppPushDelivery (NotificationId, PushDeviceId)");
    }

    public SqlSugarClient Db { get; }

    public SqlSugarContext Context => CreateSqlSugarContext(Db);

    /// <summary>另开一个到同一 SQLite 文件的独立连接（模拟多个 API 实例同时领取发件箱记录）。</summary>
    public SqlSugarContext NewConnectionContext()
    {
        var client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });
        return CreateSqlSugarContext(client);
    }

    public static readonly DateTime Start = new(2026, 10, 17, 1, 0, 0, DateTimeKind.Utc);

    public void SeedUser(string userGuid, bool active = true)
    {
        Db.Insertable(new User { UserGUID = userGuid, Username = userGuid, IsActive = active }).ExecuteCommand();
    }

    public AppPushDevice SeedDevice(
        string userGuid,
        string token,
        DateTime? lastSeen = null,
        DateTime? revokedAt = null,
        string? revokeReason = null,
        string platform = "ios")
    {
        var device = new AppPushDevice
        {
            PushDeviceId = Guid.NewGuid().ToString("N"),
            UserGUID = userGuid,
            Platform = platform,
            ExpoPushToken = token,
            CreatedAtUtc = lastSeen ?? Start,
            LastSeenAtUtc = lastSeen ?? Start,
            RevokedAtUtc = revokedAt,
            RevokeReason = revokeReason,
        };
        Db.Insertable(device).ExecuteCommand();
        return device;
    }

    public AppNotification SeedNotification(
        string type = "store.price_updates",
        string title = "Price updates for your store",
        string body = "12 products need price updates.",
        int priority = AppNotificationPriorities.Normal,
        string? dataJson = "{\"count\":\"12\"}",
        DateTime? createdAt = null,
        DateTime? expiresAt = null,
        DateTime? revokedAt = null)
    {
        var notification = new AppNotification
        {
            NotificationId = Guid.NewGuid().ToString("N"),
            Type = type,
            Priority = priority,
            Title = title,
            Body = body,
            DataJson = dataJson,
            CreatedAtUtc = createdAt ?? Start,
            ExpiresAtUtc = expiresAt,
            RevokedAtUtc = revokedAt,
            CreatedBy = "test",
        };
        Db.Insertable(notification).ExecuteCommand();
        return notification;
    }

    public AppPushDelivery SeedDelivery(
        string notificationId,
        AppPushDevice device,
        int state = AppPushDeliveryStates.Pending,
        DateTime? createdAt = null,
        DateTime? nextAttemptAt = null,
        int attempts = 0,
        string? ticketId = null,
        DateTime? sentAt = null,
        DateTime? lockedUntil = null)
    {
        var delivery = new AppPushDelivery
        {
            DeliveryId = Guid.NewGuid().ToString("N"),
            NotificationId = notificationId,
            PushDeviceId = device.PushDeviceId,
            UserGUID = device.UserGUID,
            State = state,
            Attempts = attempts,
            NextAttemptAtUtc = nextAttemptAt ?? Start,
            LockedUntilUtc = lockedUntil,
            TicketId = ticketId,
            CreatedAtUtc = createdAt ?? Start,
            SentAtUtc = sentAt,
        };
        Db.Insertable(delivery).ExecuteCommand();
        return delivery;
    }

    public AppPushDelivery Delivery(string deliveryId) =>
        Db.Queryable<AppPushDelivery>().Where(row => row.DeliveryId == deliveryId).First();

    public AppPushDevice Device(string pushDeviceId) =>
        Db.Queryable<AppPushDevice>().Where(row => row.PushDeviceId == pushDeviceId).First();

    public static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    public void Dispose()
    {
        Db.Dispose();
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
}

/// <summary>一个合法的测试令牌：<c>ExponentPushToken[</c> + 22 位字符 + <c>]</c>，末 6 位用作 ticket 后缀。</summary>
internal static class PushTestTokens
{
    public static string Make(string seed)
    {
        // 区分用的 seed 放在末尾：测试用 ticket 后缀取令牌内层最后 6 位，保证不同 seed（≤ 6 位）得到不同后缀。
        var inner = (new string('x', 22) + seed)[^22..];
        return $"ExponentPushToken[{inner}]";
    }
}
