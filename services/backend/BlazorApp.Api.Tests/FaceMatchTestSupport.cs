using BlazorApp.Api.Data;
using BlazorApp.Api.Features.FaceMatch;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using SqlSugar;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 人脸比对测试共用：SQLite 临时库（实体 CodeFirst 建表，与迁移同名的过滤唯一索引手工补建）、
/// 腾讯云 / 对象存储 / 通知发布器替身。全部使用明显的假值，不连接任何真实服务；时间不依赖机器时区。
/// </summary>
internal sealed class FaceMatchTestDatabase : IDisposable
{
    public const string QldStore = "1013";   // 昆士兰 Orion：Australia/Brisbane（UTC+10，无夏令时）
    public const string NswStore = "2001";   // 新南威尔士：Australia/Sydney
    public const string DeletedStore = "9999";

    public static readonly DateTime Now = new(2026, 10, 14, 2, 0, 0, DateTimeKind.Utc);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
    private readonly SqliteConnection? _connection;
    private readonly Action? _onDispose;

    public SqlSugarClient Db { get; }
    public SqlSugarContext Context { get; }

    /// <summary>
    /// 用外部准备好的库（真实 SQL Server，表已按迁移脚本建好）代替 SQLite；只补种门店数据。
    /// onDispose 负责删库。
    /// </summary>
    public FaceMatchTestDatabase(SqlSugarClient externalDb, Action onDispose)
    {
        Db = externalDb;
        _onDispose = onDispose;
        Context = FaceCameraAdminTests.CreateSqlSugarContext(Db);
        SeedStores();
    }

    public FaceMatchTestDatabase()
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
            typeof(Store), typeof(User), typeof(UserStore),
            typeof(FaceStoreProfile), typeof(FaceConsentRecord), typeof(FaceEnrollment), typeof(FaceVisitGroup),
            typeof(FaceTrafficBucket), typeof(FaceTrafficHourly), typeof(FaceMatchEvaluation),
            typeof(FaceUnwelcomeEntry), typeof(FaceMatchTask), typeof(FaceCaptureEvent),
            typeof(AppNotification), typeof(AppNotificationRecipient));
        // 与迁移 20261014.001-face-match 同名的过滤唯一索引：每个员工同时只能有一条有效登记。
        Db.Ado.ExecuteCommand("CREATE UNIQUE INDEX UX_FaceEnrollment_UserGuid_Active ON FaceEnrollment (UserGuid) WHERE Status = 1");
        // 审计表的 bigint 自增主键 SQLite CodeFirst 建不出来（只允许 INTEGER PRIMARY KEY AUTOINCREMENT），按迁移的列手工建。
        Db.Ado.ExecuteCommand("""
CREATE TABLE FaceAuditLog (
    AuditId INTEGER PRIMARY KEY AUTOINCREMENT,
    AtUtc datetime2 NOT NULL,
    ActorId nvarchar(100) NOT NULL,
    ActorName nvarchar(100) NOT NULL,
    Action nvarchar(48) NOT NULL,
    StoreCode nvarchar(32) NULL,
    TargetRef nvarchar(100) NULL,
    Result nvarchar(32) NOT NULL
)
""");
        Db.Ado.ExecuteCommand("CREATE TABLE FaceCaptureImage (ImageId char(32) NOT NULL PRIMARY KEY, CaptureId char(32) NOT NULL, Kind tinyint NOT NULL DEFAULT 0, PartIndex int NOT NULL DEFAULT 0)");
        Context = FaceCameraAdminTests.CreateSqlSugarContext(Db);
        SeedStores();
    }

    private void SeedStores() =>
        Db.Insertable(new List<Store>
        {
            new() { StoreGUID = "s-1013", StoreCode = QldStore, StoreName = "Orion", Address = "Springfield QLD 4300", TimeZoneId = "Australia/Brisbane" },
            new() { StoreGUID = "s-2001", StoreCode = NswStore, StoreName = "Bankstown", Address = "Bankstown NSW 2200", TimeZoneId = "Australia/Sydney" },
            new() { StoreGUID = "s-del", StoreCode = DeletedStore, StoreName = "Closed", IsDeleted = true },
        }).ExecuteCommand();

    public User AddUser(string guid, string name, params string[] storeGuids)
    {
        var user = new User { UserGUID = guid, Username = guid, Email = $"{guid}@example.test", PasswordHash = "x", FullName = name, IsActive = true };
        Db.Insertable(user).ExecuteCommand();
        foreach (var storeGuid in storeGuids)
        {
            Db.Insertable(new UserStore { UserGUID = guid, StoreGUID = storeGuid }).ExecuteCommand();
        }

        return user;
    }

    public void Dispose()
    {
        Db.Dispose();
        _connection?.Dispose();
        SqliteConnection.ClearAllPools();
        if (_connection != null)
        {
            SqliteTempFileCleanup.DeleteIfExists(_dbPath);
        }

        _onDispose?.Invoke();
    }
}

/// <summary>可控的运行配置：用真实的 <see cref="FaceMatchSettings.Resolve"/> 解析，测试配置错误时功能关闭的行为。</summary>
internal sealed class FakeFaceMatchSettings : IFaceMatchSettingsProvider
{
    public FaceMatchOptions Options { get; } = new()
    {
        Tencent = new FaceMatchTencentOptions { Region = "ap-guangzhou", AdminSecretId = "FAKE-ADMIN-ID", AdminSecretKey = "fake-admin-key-material" },
    };

    public FaceMatchResolvedSettings Resolved => FaceMatchSettings.Resolve(Options);
}

/// <summary>内存式腾讯云人脸库：记录调用、可注入失败，行为（幂等建库 / 删除不存在视为成功）与真实实现的契约一致。</summary>
internal sealed class FakeFaceLibrary : IFaceLibrary
{
    public bool Available { get; set; } = true;
    public List<string> Calls { get; } = [];
    public Dictionary<string, HashSet<string>> Groups { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> PersonGroup { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> PersonFaceCount { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> PersonNames { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, byte[]> PersonImages { get; } = new(StringComparer.Ordinal);

    /// <summary>按操作名返回要抛的异常（null 表示成功）；操作名同 IFaceLibrary 方法名去掉 Async。</summary>
    public Func<string, Exception?>? FailOn { get; set; }

    /// <summary>CreateFaces 时让最后 N 张失败（模拟部分照片不可用）。</summary>
    public int CreateFacesDrop { get; set; }

    public bool IsAvailable => Available;

    public Task EnsureGroupAsync(string groupId, string groupName, CancellationToken cancellationToken = default)
    {
        Enter($"EnsureGroup:{groupId}", "EnsureGroup");
        Groups.TryAdd(groupId, []);
        return Task.CompletedTask;
    }

    public Task<string> CreatePersonAsync(string groupId, string personId, string personName, byte[] image, CancellationToken cancellationToken = default)
    {
        Enter($"CreatePerson:{personId}", "CreatePerson");
        if (!Groups.TryGetValue(groupId, out var group))
        {
            throw new FaceLibraryException(FaceLibraryFailureKind.NotFound, "InvalidParameterValue.GroupIdNotExist", "no group");
        }

        group.Add(personId);
        PersonGroup[personId] = groupId;
        PersonNames[personId] = personName;
        PersonImages[personId] = image;
        PersonFaceCount[personId] = 1;
        return Task.FromResult("face-" + personId);
    }

    public Task<IReadOnlyList<string>> CreateFacesAsync(string personId, IReadOnlyList<byte[]> images, CancellationToken cancellationToken = default)
    {
        Enter($"CreateFaces:{personId}:{images.Count}", "CreateFaces");
        var ok = Math.Max(0, images.Count - CreateFacesDrop);
        PersonFaceCount[personId] = PersonFaceCount.GetValueOrDefault(personId) + ok;
        return Task.FromResult<IReadOnlyList<string>>(Enumerable.Range(0, ok).Select(i => $"face-{personId}-{i}").ToList());
    }

    public Task DeletePersonAsync(string personId, CancellationToken cancellationToken = default)
    {
        Enter($"DeletePerson:{personId}", "DeletePerson");
        if (PersonGroup.Remove(personId, out var groupId))
        {
            Groups[groupId].Remove(personId);
        }

        PersonFaceCount.Remove(personId);
        return Task.CompletedTask;
    }

    public Task DeleteGroupAsync(string groupId, CancellationToken cancellationToken = default)
    {
        Enter($"DeleteGroup:{groupId}", "DeleteGroup");
        Groups.Remove(groupId);
        return Task.CompletedTask;
    }

    public Task<int?> GetPersonCountAsync(string groupId, CancellationToken cancellationToken = default)
    {
        Enter($"GetPersonCount:{groupId}", "GetPersonCount");
        return Task.FromResult<int?>(Groups.TryGetValue(groupId, out var group) ? group.Count : null);
    }

    public Task<IReadOnlyList<FaceLibraryGroup>> ListGroupsAsync(CancellationToken cancellationToken = default)
    {
        Enter("ListGroups", "ListGroups");
        return Task.FromResult<IReadOnlyList<FaceLibraryGroup>>(Groups.Keys.Select(id => new FaceLibraryGroup(id, id)).ToList());
    }

    private void Enter(string call, string operation)
    {
        Calls.Add(call);
        var failure = FailOn?.Invoke(operation);
        if (failure != null)
        {
            throw failure;
        }
    }

    public static FaceLibraryException Failure(FaceLibraryFailureKind kind, string code = "Fake") => new(kind, code, "fake failure");
}

/// <summary>内存式私有对象存储替身。</summary>
internal sealed class FakeFaceMatchObjectStore : IFaceMatchObjectStore
{
    public bool Configured { get; set; } = true;
    public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
    public List<string> Puts { get; } = [];
    public List<string> Reads { get; } = [];
    public List<string> Deletes { get; } = [];
    public Func<string, string, Exception?>? FailOn { get; set; }

    public bool IsConfigured => Configured;

    public Task PutAsync(string objectKey, byte[] content, string contentType, CancellationToken cancellationToken = default)
    {
        Puts.Add(objectKey);
        Throw("put", objectKey);
        Objects[objectKey] = content;
        return Task.CompletedTask;
    }

    public Task<byte[]> ReadAsync(string objectKey, int maxBytes, CancellationToken cancellationToken = default)
    {
        Reads.Add(objectKey);
        Throw("read", objectKey);
        return Objects.TryGetValue(objectKey, out var bytes)
            ? Task.FromResult(bytes)
            : throw new FaceMatchStoreException(FaceMatchStoreFailureKind.NotFound, "missing");
    }

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        Deletes.Add(objectKey);
        Throw("delete", objectKey);
        Objects.Remove(objectKey);
        return Task.CompletedTask;
    }

    private void Throw(string operation, string key)
    {
        var failure = FailOn?.Invoke(operation, key);
        if (failure != null)
        {
            throw failure;
        }
    }
}

/// <summary>记录发布请求的通知发布器替身；可注入异常或「没有接收人」结果。</summary>
internal sealed class FakeNotificationPublisher : INotificationPublisher
{
    public List<NotificationPublishRequest> Requests { get; } = [];
    public Exception? Failure { get; set; }
    public bool NoRecipients { get; set; }

    /// <summary>发布瞬间探测调用方是否身处数据库事务里（通知发布器自带 UseTranAsync，绝不能被外层事务包住）。</summary>
    public Func<bool>? TransactionProbe { get; set; }

    public List<bool> InTransaction { get; } = [];

    public Task<NotificationPublishResult> PublishAsync(NotificationPublishRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        InTransaction.Add(TransactionProbe?.Invoke() ?? false);
        if (Failure != null)
        {
            throw Failure;
        }

        if (request.DedupeKey != null && Requests.Count(r => r.DedupeKey == request.DedupeKey) > 1)
        {
            return Task.FromResult(new NotificationPublishResult("existing", 0, Deduplicated: true));
        }

        return Task.FromResult(NoRecipients
            ? new NotificationPublishResult(null, 0, Deduplicated: false)
            : new NotificationPublishResult(Guid.NewGuid().ToString("N"), 1, Deduplicated: false));
    }

    public Task<bool> RevokeAsync(string dedupeKey, CancellationToken cancellationToken = default) => Task.FromResult(false);
}

/// <summary>
/// 一次装配好的服务集合（共用同一个库 / 替身 / 时间 / 日志）。每个字段都可在测试里按需替换配置或注入失败。
/// </summary>
internal sealed class FaceMatchTestHarness : IDisposable
{
    public FaceMatchTestDatabase Database { get; }
    public FakeFaceMatchSettings Settings { get; } = new();
    public FakeFaceLibrary Library { get; } = new();
    public FakeFaceMatchObjectStore Store { get; } = new();
    public FakeNotificationPublisher Publisher { get; } = new();
    public FaceCameraAdminTests.MutableTimeProvider Time { get; } = new(FaceMatchTestDatabase.Now);
    public AuditCapturingLoggerProvider Logs { get; } = new();

    public FaceMatchAudit Audit { get; }
    public FaceStoreProfileService Profiles { get; }
    public FaceStaffService Staff { get; }
    public FaceTrafficService Traffic { get; }
    public FaceUnwelcomeService Unwelcome { get; }
    public FaceMatchMaintenance Maintenance { get; }
    public FaceHitNotifier Notifier { get; }

    public static readonly FaceMatchActor Admin = new("admin-1", "admin.tester", IsAdmin: true);

    public FaceMatchTestHarness() : this(new FaceMatchTestDatabase())
    {
    }

    public FaceMatchTestHarness(FaceMatchTestDatabase database)
    {
        Database = database;
        var ctx = Database.Context;
        Audit = new FaceMatchAudit(ctx, Time, Logs.CreateLogger<FaceMatchAudit>());
        Profiles = new FaceStoreProfileService(ctx, Settings, Store, Audit, Time);
        Staff = new FaceStaffService(ctx, Library, Store, Settings, Audit, Time, Logs.CreateLogger<FaceStaffService>());
        Traffic = new FaceTrafficService(ctx, Time);
        Unwelcome = new FaceUnwelcomeService(ctx, Library, Store, Settings, Audit, Time, Logs.CreateLogger<FaceUnwelcomeService>());
        Maintenance = new FaceMatchMaintenance(
            ctx, Library, Store, Settings, Staff, Unwelcome, Publisher, Audit, Time, Logs.CreateLogger<FaceMatchMaintenance>());
        Notifier = new FaceHitNotifier(ctx, Publisher, Time, Logs.CreateLogger<FaceHitNotifier>());
    }

    public ISqlSugarClient Db => Database.Db;

    public void Dispose() => Database.Dispose();

    /// <summary>当前时间之后的偏移（相对 <see cref="FaceMatchTestDatabase.Now"/> 的固定基准）。</summary>
    public DateTime At(TimeSpan offset) => FaceMatchTestDatabase.Now + offset;

    public static FaceMatchUpload Jpeg(string name = "p.jpg") => new(name, "image/jpeg", FaceImageTestData.Jpeg);

    public static FaceMatchUpload Png(string name = "p.png") => new(name, "image/png", FaceImageTestData.Png);

    public static List<FaceMatchUpload> Photos(int count) =>
        Enumerable.Range(0, count).Select(i => i % 2 == 0 ? Jpeg($"p{i}.jpg") : Png($"p{i}.png")).ToList();
}
