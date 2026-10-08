using Hbpos.Api.Data;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.Stores;
using SqlSugar;

namespace Hbpos.Api.Services;

/// <summary>
/// 总部「下发」的小票资料快照读写。快照表由 HBweb 的版本号迁移创建（StoreReceiptProfileRelease、
/// PosReceiptProfileAck），Hbpos.Api 不建表；两张表尚不存在（部署先后顺序出错）时一律按「从未下发」处理，
/// 不能因此让收银端的接口报 500。
/// </summary>
public interface IStoreReceiptProfileReleaseRepository
{
    /// <summary>该门店最新下发版本号；从未下发或表不存在时为 0。</summary>
    Task<int> GetLatestVersionAsync(string storeCode, CancellationToken cancellationToken);

    /// <summary>该门店最新下发快照；从未下发或表不存在时为 null。</summary>
    Task<StoreReceiptProfileDto?> GetLatestAsync(string storeCode, CancellationToken cancellationToken);

    /// <summary>
    /// 写入设备回执并返回写入后该设备记录的已应用版本。
    /// 同一门店内单调不降；设备换店（行内 StoreCode 与本次不同）时整行覆盖为新店的版本，
    /// 因为版本号是「每家店各自从 1 递增」的，旧店的高版本号对新店没有意义。
    /// </summary>
    Task<int> UpsertAckAsync(
        string deviceCode,
        string storeCode,
        int version,
        string clientKind,
        DateTime nowUtc,
        CancellationToken cancellationToken);
}

public sealed record StoreReceiptProfileSyncLookupResult(
    StoreReceiptProfileSyncDto? Sync,
    string? ErrorCode = null,
    string? Message = null);

public sealed record StoreReceiptProfileAckLookupResult(
    StoreReceiptProfileAckResultDto? Result,
    string? ErrorCode = null,
    string? Message = null);

public interface IStoreReceiptProfileReleaseService
{
    Task<StoreReceiptProfileSyncLookupResult> GetSyncAsync(
        string storeCode,
        int knownVersion,
        CancellationToken cancellationToken);

    Task<StoreReceiptProfileAckLookupResult> AckAsync(
        string storeCode,
        string deviceCode,
        string? deviceSystem,
        int version,
        CancellationToken cancellationToken);
}

/// <summary>由认证声明里的设备系统推导回执的端类型，HBweb 的「设备应用情况」按它展示。</summary>
public static class StoreReceiptProfileClientKinds
{
    public const string Wpf = "wpf";
    public const string Handheld = "handheld";
    public const string Ipad = "ipad";
    public const string Other = "other";

    public static string FromDeviceSystem(string? deviceSystem)
    {
        var value = deviceSystem?.Trim();
        if (string.Equals(value, DeviceSystems.Windows, StringComparison.OrdinalIgnoreCase))
        {
            return Wpf;
        }

        if (string.Equals(value, DeviceSystems.IpadOs, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, DeviceSystems.Ios, StringComparison.OrdinalIgnoreCase))
        {
            return Ipad;
        }

        return string.Equals(value, DeviceSystems.Android, StringComparison.OrdinalIgnoreCase)
            ? Handheld
            : Other;
    }
}

public sealed class StoreReceiptProfileReleaseService(
    IStoreReceiptProfileReleaseRepository repository,
    TimeProvider? timeProvider = null) : IStoreReceiptProfileReleaseService
{
    public const string StoreCodeRequiredCode = StoreReceiptProfileService.StoreCodeRequiredCode;
    public const string InvalidCharactersCode = StoreReceiptProfileService.InvalidCharactersCode;
    public const string DeviceCodeRequiredCode = "DEVICE_CODE_CLAIM_MISSING";
    public const string VersionInvalidCode = "RECEIPT_PROFILE_VERSION_INVALID";

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<StoreReceiptProfileSyncLookupResult> GetSyncAsync(
        string storeCode,
        int knownVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return new StoreReceiptProfileSyncLookupResult(null, StoreCodeRequiredCode, "storeCode 不能为空");
        }

        // 客户端可能传来负数或乱码（控制器已按 0 处理非法值），这里再兜一层。
        var known = Math.Max(0, knownVersion);

        // 高频轮询只走一次按主键前缀的 MAX 查询；版本没变时不读快照内容。
        var latestVersion = await repository.GetLatestVersionAsync(storeCode, cancellationToken);
        if (latestVersion <= 0)
        {
            // 从未下发：本机的手工设置继续生效，不返回任何资料。
            return new StoreReceiptProfileSyncLookupResult(new StoreReceiptProfileSyncDto(false, 0, null));
        }

        if (latestVersion == known)
        {
            return new StoreReceiptProfileSyncLookupResult(
                new StoreReceiptProfileSyncDto(false, latestVersion, null));
        }

        // 版本不同（含客户端版本号反而更大的异常情形）一律以服务端为准。
        var profile = await repository.GetLatestAsync(storeCode, cancellationToken);
        if (profile is null)
        {
            // 两次查询之间快照被清理的极端竞态：当作没有变化，下一轮再看。
            return new StoreReceiptProfileSyncLookupResult(
                new StoreReceiptProfileSyncDto(false, latestVersion, null));
        }

        // 与现有接口同口径：快照含非法控制字符就整体拒绝，避免污染收银端小票草稿。
        if (!StoreReceiptProfileGuard.IsValid(profile))
        {
            return new StoreReceiptProfileSyncLookupResult(null, InvalidCharactersCode, "门店资料包含不可打印控制字符");
        }

        return new StoreReceiptProfileSyncLookupResult(
            new StoreReceiptProfileSyncDto(true, profile.Version, profile));
    }

    public async Task<StoreReceiptProfileAckLookupResult> AckAsync(
        string storeCode,
        string deviceCode,
        string? deviceSystem,
        int version,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return new StoreReceiptProfileAckLookupResult(null, StoreCodeRequiredCode, "storeCode 不能为空");
        }

        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            return new StoreReceiptProfileAckLookupResult(null, DeviceCodeRequiredCode, "设备编号认证声明缺失");
        }

        // 回执只能是该门店真实存在过的下发版本；表不存在时 latest=0，任何版本都无效。
        var latestVersion = await repository.GetLatestVersionAsync(storeCode, cancellationToken);
        if (version < 1 || version > latestVersion)
        {
            return new StoreReceiptProfileAckLookupResult(null, VersionInvalidCode, "下发版本号无效");
        }

        var applied = await repository.UpsertAckAsync(
            deviceCode.Trim(),
            storeCode.Trim(),
            version,
            StoreReceiptProfileClientKinds.FromDeviceSystem(deviceSystem),
            _timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);

        return new StoreReceiptProfileAckLookupResult(new StoreReceiptProfileAckResultDto(applied));
    }
}

/// <summary>仓储的 SQL Server 实现：原生 SQL，读前先判断表是否存在。</summary>
public sealed class SqlSugarStoreReceiptProfileReleaseRepository(
    HbposSqlSugarContext dbContext,
    ILogger<SqlSugarStoreReceiptProfileReleaseRepository>? logger = null) : IStoreReceiptProfileReleaseRepository
{
    public async Task<int> GetLatestVersionAsync(string storeCode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await StoreReceiptProfileReleaseQueries.GetLatestVersionAsync(dbContext.MainDb, storeCode);
    }

    public async Task<StoreReceiptProfileDto?> GetLatestAsync(
        string storeCode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await StoreReceiptProfileReleaseQueries.GetLatestAsync(dbContext.MainDb, storeCode, logger);
    }

    public async Task<int> UpsertAckAsync(
        string deviceCode,
        string storeCode,
        int version,
        string clientKind,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // MERGE + HOLDLOCK：同一设备并发首次回执时只会有一个 INSERT，另一个走 UPDATE，不会撞主键。
        // UPDATE 的右值读取的是更新前的列值，所以「是否换店」的判断可以和覆盖写在同一条语句里。
        var applied = await dbContext.MainDb.Ado.SqlQuerySingleAsync<int>(
            """
            SET NOCOUNT ON;
            MERGE [dbo].[PosReceiptProfileAck] WITH (HOLDLOCK) AS target
            USING (SELECT @DeviceCode AS DeviceCode) AS source
                ON target.[DeviceCode] = source.[DeviceCode]
            WHEN MATCHED THEN UPDATE SET
                [AppliedVersion] = CASE
                    WHEN target.[StoreCode] <> @StoreCode OR @Version > target.[AppliedVersion]
                        THEN @Version ELSE target.[AppliedVersion] END,
                [AppliedAtUtc] = CASE
                    WHEN target.[StoreCode] <> @StoreCode OR @Version > target.[AppliedVersion]
                        THEN @NowUtc ELSE target.[AppliedAtUtc] END,
                [StoreCode] = @StoreCode,
                [ClientKind] = @ClientKind
            WHEN NOT MATCHED THEN INSERT
                ([DeviceCode], [StoreCode], [AppliedVersion], [AppliedAtUtc], [ClientKind])
                VALUES (@DeviceCode, @StoreCode, @Version, @NowUtc, @ClientKind);

            SELECT [AppliedVersion] FROM [dbo].[PosReceiptProfileAck] WHERE [DeviceCode] = @DeviceCode;
            """,
            new SugarParameter("@DeviceCode", deviceCode),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@Version", version),
            new SugarParameter("@ClientKind", clientKind),
            new SugarParameter("@NowUtc", nowUtc));

        return applied;
    }
}

/// <summary>
/// 下发快照的共用查询。门店资料接口（叠加最新快照）与同步接口共用，保证两处读到的是同一份口径。
/// </summary>
internal static class StoreReceiptProfileReleaseQueries
{
    // IF OBJECT_ID 包住查询：表不存在时整批不返回结果集（SqlSugar 得到 null / 0），而不是抛「对象名无效」。
    private const string LatestVersionSql = """
        IF OBJECT_ID(N'[dbo].[StoreReceiptProfileRelease]', N'U') IS NOT NULL
            SELECT ISNULL(MAX([Version]), 0)
            FROM [dbo].[StoreReceiptProfileRelease]
            WHERE [StoreCode] = @StoreCode
        ELSE
            SELECT 0
        """;

    // 含新列 VoucherTerms / InstallmentTerms：Release 表的新列由 HBweb 迁移 20261008.002 添加，
    // 先发 Hbpos.Api、后跑迁移时读这两列会抛 207，GetLatestAsync 会回退到下面不含新列的旧 SQL。
    internal const string LatestReleaseSql = """
        IF OBJECT_ID(N'[dbo].[StoreReceiptProfileRelease]', N'U') IS NOT NULL
            SELECT TOP (1)
                [StoreCode],
                [Version],
                [StoreName],
                [BrandName],
                [Address],
                [Phone],
                [ABN] AS Abn,
                [ReturnPolicy],
                [VoucherTerms],
                [InstallmentTerms],
                [PublishedAtUtc]
            FROM [dbo].[StoreReceiptProfileRelease]
            WHERE [StoreCode] = @StoreCode
            ORDER BY [Version] DESC
        """;

    // 降级兜底：不含新列的旧 SQL，新字段读出为 null（旧快照同样如此），收银端按默认文案打印。
    internal const string LatestReleaseLegacySql = """
        IF OBJECT_ID(N'[dbo].[StoreReceiptProfileRelease]', N'U') IS NOT NULL
            SELECT TOP (1)
                [StoreCode],
                [Version],
                [StoreName],
                [BrandName],
                [Address],
                [Phone],
                [ABN] AS Abn,
                [ReturnPolicy],
                [PublishedAtUtc]
            FROM [dbo].[StoreReceiptProfileRelease]
            WHERE [StoreCode] = @StoreCode
            ORDER BY [Version] DESC
        """;

    public static async Task<int> GetLatestVersionAsync(ISqlSugarClient db, string storeCode)
    {
        return await db.Ado.SqlQuerySingleAsync<int>(
            LatestVersionSql,
            new SugarParameter("@StoreCode", storeCode));
    }

    public static async Task<StoreReceiptProfileDto?> GetLatestAsync(
        ISqlSugarClient db,
        string storeCode,
        ILogger? logger = null)
    {
        // 先读含新列的 SQL，列还不存在（207）时回退旧 SQL；这条读取被 60 秒轮询的同步接口复用，不能因此 500。
        var row = await StoreReceiptProfileColumnFallback.QueryAsync(
            () => db.Ado.SqlQuerySingleAsync<StoreReceiptProfileReleaseRow>(
                LatestReleaseSql,
                new SugarParameter("@StoreCode", storeCode)),
            () => db.Ado.SqlQuerySingleAsync<StoreReceiptProfileReleaseRow>(
                LatestReleaseLegacySql,
                new SugarParameter("@StoreCode", storeCode)),
            "Release",
            logger);

        return row is null
            ? null
            : new StoreReceiptProfileDto(
                row.StoreCode,
                row.StoreName,
                row.BrandName,
                row.Address,
                row.Phone,
                row.Abn,
                row.ReturnPolicy,
                row.Version,
                // 库里存的是 UTC 墙钟时间（datetime2，无时区），显式标成 UTC 再对外。
                new DateTimeOffset(DateTime.SpecifyKind(row.PublishedAtUtc, DateTimeKind.Utc)),
                row.VoucherTerms,
                row.InstallmentTerms);
    }
}

public sealed class StoreReceiptProfileReleaseRow
{
    public string StoreCode { get; set; } = string.Empty;

    public int Version { get; set; }

    public string StoreName { get; set; } = string.Empty;

    public string? BrandName { get; set; }

    public string? Address { get; set; }

    public string? Phone { get; set; }

    public string? Abn { get; set; }

    public string? ReturnPolicy { get; set; }

    public string? VoucherTerms { get; set; }

    public string? InstallmentTerms { get; set; }

    public DateTime PublishedAtUtc { get; set; }
}
