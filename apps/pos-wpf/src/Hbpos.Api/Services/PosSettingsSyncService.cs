using System.Text.Json;
using BlazorApp.Shared.Constants;
using Hbpos.Api.Data;
using Hbpos.Contracts.PosSettings;
using SqlSugar;

namespace Hbpos.Api.Services;

/// <summary>三层已设置值（只含清单里认识的键）。</summary>
public sealed record PosSettingLayers(
    IReadOnlyDictionary<string, bool> Global,
    IReadOnlyDictionary<string, bool> Store,
    IReadOnlyDictionary<string, bool> Device);

/// <summary>
/// 设置下发的数据读写。三张表由 HBweb 迁移 20261114.001-pos-settings 创建，Hbpos.Api 不建表；
/// 表尚不存在（部署先后顺序出错）时按「没有任何设置」处理，不能让每分钟一次的轮询报 500。
/// </summary>
public interface IPosSettingsRepository
{
    Task<PosSettingLayers> GetLayersAsync(string storeCode, string deviceCode, CancellationToken cancellationToken);

    /// <summary>
    /// 写入设备回执（每台设备一行）。离线临时修改次数只增不减；次数增加时记下这次上报的时间作为「最近一次」。
    /// </summary>
    Task UpsertAckAsync(PosSettingsAckWrite write, CancellationToken cancellationToken);
}

public sealed record PosSettingsAckWrite(
    string DeviceCode,
    string StoreCode,
    string Hash,
    string? EffectiveJson,
    string ClientKind,
    string? AppVersion,
    int OfflineOverrideCount,
    DateTime NowUtc);

public sealed record PosSettingsSyncLookupResult(
    PosSettingsSyncDto? Sync,
    string? ErrorCode = null,
    string? Message = null);

public sealed record PosSettingsAckLookupResult(
    PosSettingsAckResultDto? Result,
    string? ErrorCode = null,
    string? Message = null);

public interface IPosSettingsSyncService
{
    Task<PosSettingsSyncLookupResult> GetSyncAsync(
        string storeCode,
        string deviceCode,
        string? deviceSystem,
        string? knownHash,
        CancellationToken cancellationToken);

    Task<PosSettingsAckLookupResult> AckAsync(
        string storeCode,
        string deviceCode,
        string? deviceSystem,
        PosSettingsAckRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// 把设备所属门店、设备编号两层与全局层合并，按端过滤后给收银机下发；回执写回每台设备一行。
/// 合并与指纹用 <see cref="PosSettingMerger"/>，与后台算「设备是否已应用」是同一份代码。
/// 门店与设备都只取认证声明，不接受客户端指定。
/// </summary>
public sealed class PosSettingsSyncService(
    IPosSettingsRepository repository,
    TimeProvider? timeProvider = null) : IPosSettingsSyncService
{
    public const string StoreCodeRequiredCode = "STORE_CODE_CLAIM_MISSING";
    public const string DeviceCodeRequiredCode = "DEVICE_CODE_CLAIM_MISSING";
    public const string HashInvalidCode = "POS_SETTINGS_HASH_INVALID";

    private const int MaximumAppliedKeys = 64;
    private const int MaximumOfflineOverrideCount = 1_000_000;

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<PosSettingsSyncLookupResult> GetSyncAsync(
        string storeCode,
        string deviceCode,
        string? deviceSystem,
        string? knownHash,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return new PosSettingsSyncLookupResult(null, StoreCodeRequiredCode, "storeCode 不能为空");
        }

        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            return new PosSettingsSyncLookupResult(null, DeviceCodeRequiredCode, "设备编号认证声明缺失");
        }

        var layers = await repository.GetLayersAsync(storeCode.Trim(), deviceCode.Trim(), cancellationToken);
        var managed = PosSettingMerger.Managed(PosSettingMerger.Merge(
            layers.Global,
            layers.Store,
            layers.Device,
            PosSettingMerger.ClientKindFromDeviceSystem(deviceSystem)));
        var hash = PosSettingMerger.ComputeHash(managed);

        // 指纹相同就不回内容：每分钟一次的轮询在没有改动时只返回几十个字节。
        // 客户端首次同步（knownHash 为空）一定不相等，会拿到完整响应（可能是空集合）。
        if (string.Equals(knownHash?.Trim(), hash, StringComparison.Ordinal))
        {
            return new PosSettingsSyncLookupResult(new PosSettingsSyncDto(false, hash, null));
        }

        var settings = managed.ToDictionary(
            item => item.Key,
            item => new PosSettingValueDto(item.Value.Value == true, item.Value.Source),
            StringComparer.Ordinal);
        return new PosSettingsSyncLookupResult(new PosSettingsSyncDto(true, hash, settings));
    }

    public async Task<PosSettingsAckLookupResult> AckAsync(
        string storeCode,
        string deviceCode,
        string? deviceSystem,
        PosSettingsAckRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return new PosSettingsAckLookupResult(null, StoreCodeRequiredCode, "storeCode 不能为空");
        }

        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            return new PosSettingsAckLookupResult(null, DeviceCodeRequiredCode, "设备编号认证声明缺失");
        }

        var hash = request.Hash?.Trim() ?? string.Empty;
        if (!IsHexSha256(hash))
        {
            return new PosSettingsAckLookupResult(null, HashInvalidCode, "指纹格式无效");
        }

        // 上报的实际生效值只保留清单里认识的键，且限制数量与离线次数的范围，不让客户端的脏值进库。
        var applied = (request.Applied ?? new Dictionary<string, bool>())
            .Where(item => PosSettingCatalog.IsKnownKey(item.Key))
            .Take(MaximumAppliedKeys)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var kind = PosSettingMerger.ClientKindFromDeviceSystem(deviceSystem);
        var appVersion = request.AppVersion?.Trim();
        if (appVersion is { Length: > 64 })
        {
            appVersion = appVersion[..64];
        }

        await repository.UpsertAckAsync(
            new PosSettingsAckWrite(
                deviceCode.Trim(),
                storeCode.Trim(),
                hash,
                JsonSerializer.Serialize(applied),
                kind,
                string.IsNullOrEmpty(appVersion) ? null : appVersion,
                Math.Clamp(request.OfflineOverrideCount, 0, MaximumOfflineOverrideCount),
                clock.GetUtcNow().UtcDateTime),
            cancellationToken);
        return new PosSettingsAckLookupResult(new PosSettingsAckResultDto(hash));
    }

    private static bool IsHexSha256(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>SQL Server 实现：原生 SQL，读写前先判断表是否存在。</summary>
public sealed class SqlSugarPosSettingsRepository(HbposSqlSugarContext dbContext) : IPosSettingsRepository
{
    // 表不存在时返回空结果集（而不是抛「对象名无效」或没有结果集）：部署先后顺序出错时轮询照常返回「没有设置」。
    internal const string LayersSql = """
        IF OBJECT_ID(N'[dbo].[PosSettingValue]', N'U') IS NOT NULL
            SELECT [Scope], [ScopeKey], [SettingKey], [Value]
            FROM [dbo].[PosSettingValue]
            WHERE [Scope] = N'global'
               OR ([Scope] = N'store' AND [ScopeKey] = @StoreCode)
               OR ([Scope] = N'device' AND [ScopeKey] = @DeviceCode)
        ELSE
            SELECT CAST(N'' AS nvarchar(16)) AS [Scope], CAST(N'' AS nvarchar(100)) AS [ScopeKey],
                   CAST(N'' AS nvarchar(100)) AS [SettingKey], CAST(0 AS bit) AS [Value]
            WHERE 1 = 0
        """;

    public async Task<PosSettingLayers> GetLayersAsync(
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rows = await dbContext.MainDb.Ado.SqlQueryAsync<LayerRow>(
            LayersSql,
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@DeviceCode", deviceCode));

        Dictionary<string, bool> Pick(string scope) =>
            rows.Where(row => string.Equals(row.Scope, scope, StringComparison.Ordinal)
                              && PosSettingCatalog.IsKnownKey(row.SettingKey))
                .ToDictionary(row => row.SettingKey, row => row.Value, StringComparer.Ordinal);

        return new PosSettingLayers(
            Pick(PosSettingScopes.Global),
            Pick(PosSettingScopes.Store),
            Pick(PosSettingScopes.Device));
    }

    public async Task UpsertAckAsync(PosSettingsAckWrite write, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // MERGE + HOLDLOCK：同一设备并发首次回执只会有一个 INSERT。表不存在时整批直接返回（部署顺序出错也不报 500）。
        // 离线临时修改次数只增不减，增加时才更新「最近一次」时间。
        await dbContext.MainDb.Ado.ExecuteCommandAsync(
            """
            SET NOCOUNT ON;
            IF OBJECT_ID(N'[dbo].[PosSettingAck]', N'U') IS NULL
                RETURN;
            MERGE [dbo].[PosSettingAck] WITH (HOLDLOCK) AS target
            USING (SELECT @DeviceCode AS DeviceCode) AS source
                ON target.[DeviceCode] = source.[DeviceCode]
            WHEN MATCHED THEN UPDATE SET
                [LastOfflineOverrideAtUtc] = CASE
                    WHEN @OfflineOverrideCount > target.[OfflineOverrideCount] THEN @NowUtc
                    ELSE target.[LastOfflineOverrideAtUtc] END,
                [OfflineOverrideCount] = CASE
                    WHEN @OfflineOverrideCount > target.[OfflineOverrideCount] THEN @OfflineOverrideCount
                    ELSE target.[OfflineOverrideCount] END,
                [StoreCode] = @StoreCode,
                [AppliedHash] = @Hash,
                [EffectiveJson] = @EffectiveJson,
                [ClientKind] = @ClientKind,
                [AppVersion] = @AppVersion,
                [AppliedAtUtc] = @NowUtc
            WHEN NOT MATCHED THEN INSERT
                ([DeviceCode], [StoreCode], [AppliedHash], [EffectiveJson], [ClientKind], [AppVersion],
                 [AppliedAtUtc], [OfflineOverrideCount], [LastOfflineOverrideAtUtc])
                VALUES (@DeviceCode, @StoreCode, @Hash, @EffectiveJson, @ClientKind, @AppVersion,
                        @NowUtc, @OfflineOverrideCount,
                        CASE WHEN @OfflineOverrideCount > 0 THEN @NowUtc ELSE NULL END);
            """,
            new SugarParameter("@DeviceCode", write.DeviceCode),
            new SugarParameter("@StoreCode", write.StoreCode),
            new SugarParameter("@Hash", write.Hash),
            new SugarParameter("@EffectiveJson", write.EffectiveJson) { DbType = System.Data.DbType.String },
            new SugarParameter("@ClientKind", write.ClientKind),
            new SugarParameter("@AppVersion", write.AppVersion) { DbType = System.Data.DbType.String },
            new SugarParameter("@OfflineOverrideCount", write.OfflineOverrideCount),
            new SugarParameter("@NowUtc", write.NowUtc) { DbType = System.Data.DbType.DateTime2 });
    }

    private sealed class LayerRow
    {
        public string Scope { get; set; } = string.Empty;
        public string ScopeKey { get; set; } = string.Empty;
        public string SettingKey { get; set; } = string.Empty;
        public bool Value { get; set; }
    }
}
