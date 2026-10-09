using System.Security.Cryptography;
using BlazorApp.Shared.Security;
using Hbpos.Api.Data;
using SqlSugar;

namespace Hbpos.Api.Services;

/// <summary>
/// 旧版单终端链路（POSM_LinklyCloudCredential.Password / POSM_LinklyCloudBackendTerminal.Secret）的落库保护。
/// 这两张表早于多终端凭据保护，原先明文保存；现在写入时统一用多终端同一套 Linkly 专用 ring 加密，
/// 读取时兼容尚未迁移的明文旧值（没有前缀），启动时再由 <see cref="ILinklyCloudLegacyCredentialProtectionMigrator"/> 批量补加密。
/// </summary>
public static class LinklyCloudLegacyCredentialProtection
{
    /// <summary>
    /// 已加密值的前缀，用来区分“已加密”与“历史明文”。
    /// DataProtection 输出本身是 base64url（不含冒号），所以带前缀的值不会和 Linkly 实际下发的明文混淆。
    /// </summary>
    public const string ProtectedPrefix = "hbdp1:";

    public static bool IsProtected(string? storedValue) =>
        storedValue is not null && storedValue.StartsWith(ProtectedPrefix, StringComparison.Ordinal);

    public static string ProtectPassword(ILinklyCloudTerminalCredentialProtector protector, string password) =>
        ProtectedPrefix + protector.ProtectPassword(password);

    public static string ProtectSecret(ILinklyCloudTerminalCredentialProtector protector, string secret) =>
        ProtectedPrefix + protector.ProtectSecret(secret);

    public static string? UnprotectPassword(ILinklyCloudTerminalCredentialProtector protector, string? storedValue) =>
        Unprotect(storedValue, protector.UnprotectPassword);

    public static string? UnprotectSecret(ILinklyCloudTerminalCredentialProtector protector, string? storedValue) =>
        Unprotect(storedValue, protector.UnprotectSecret);

    private static string? Unprotect(string? storedValue, Func<string, string> unprotect)
    {
        if (string.IsNullOrWhiteSpace(storedValue) || !IsProtected(storedValue))
        {
            // 没有前缀：迁移前写入的明文旧值，原样返回（启动迁移会补加密）。
            return storedValue;
        }

        try
        {
            return unprotect(storedValue[ProtectedPrefix.Length..]);
        }
        catch (CryptographicException ex)
        {
            // 密文存在但解不开，几乎一定是 Admin / POS 两侧 Linkly 专用密钥目录不一致或密钥丢失。
            throw new LinklyCloudLegacyCredentialUnprotectException(ex);
        }
    }
}

/// <summary>
/// 旧版凭据密文无法解密。保留内部 CryptographicException（含缺失的 key id，不含密文/明文），便于定位密钥目录问题。
/// </summary>
public sealed class LinklyCloudLegacyCredentialUnprotectException(CryptographicException inner)
    : InvalidOperationException(
        "Legacy Linkly Cloud credential cannot be decrypted; the Linkly credential Data Protection key ring "
        + "(LinklyCloudCredentialDataProtection:KeysPath) differs between Admin and POS or a key is missing: "
        + inner.Message,
        inner);

/// <summary>读写时透明加解密的旧版门店凭据仓储装饰器；SQL 与并发语义完全沿用内部仓储。</summary>
public sealed class ProtectingLinklyCloudCredentialRepository(
    ILinklyCloudCredentialRepository inner,
    ILinklyCloudTerminalCredentialProtector protector) : ILinklyCloudCredentialRepository
{
    public async Task<LinklyCloudCredentialRecord?> GetByStoreCodeAsync(
        string storeCode,
        string environment,
        CancellationToken cancellationToken)
    {
        var record = await inner.GetByStoreCodeAsync(storeCode, environment, cancellationToken);
        if (record is not null)
        {
            record.Password = LinklyCloudLegacyCredentialProtection.UnprotectPassword(protector, record.Password);
        }

        return record;
    }

    public async Task<LinklyCloudCredentialRecord> UpsertAsync(
        string storeCode,
        string environment,
        string username,
        string password,
        DateTime updatedAt,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        var record = await inner.UpsertAsync(
            storeCode,
            environment,
            username,
            LinklyCloudLegacyCredentialProtection.ProtectPassword(protector, password),
            updatedAt,
            updatedBy,
            cancellationToken);
        record.Password = LinklyCloudLegacyCredentialProtection.UnprotectPassword(protector, record.Password);
        return record;
    }
}

/// <summary>读写时透明加解密的旧版终端 secret 仓储装饰器；租约相关方法原样委托。</summary>
public sealed class ProtectingLinklyCloudBackendTerminalCredentialRepository(
    ILinklyCloudBackendTerminalCredentialRepository inner,
    ILinklyCloudTerminalCredentialProtector protector) : ILinklyCloudBackendTerminalCredentialRepository
{
    public async Task<LinklyCloudBackendTerminalCredentialRecord?> GetByDeviceAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken) =>
        Unprotect(await inner.GetByDeviceAsync(environment, storeCode, deviceCode, cancellationToken));

    public async Task<LinklyCloudBackendTerminalCredentialRecord> UpsertAsync(
        string environment,
        string storeCode,
        string deviceCode,
        string secret,
        string posId,
        DateTime updatedAt,
        string? updatedBy,
        CancellationToken cancellationToken) =>
        Unprotect(await inner.UpsertAsync(
            environment,
            storeCode,
            deviceCode,
            LinklyCloudLegacyCredentialProtection.ProtectSecret(protector, secret),
            posId,
            updatedAt,
            updatedBy,
            cancellationToken))!;

    public Task AcquireLegacyPairingLeaseAsync(
        string environment,
        string storeCode,
        Guid attemptId,
        DateTime leaseExpiresAt,
        DateTime now,
        CancellationToken cancellationToken) =>
        inner.AcquireLegacyPairingLeaseAsync(
            environment,
            storeCode,
            attemptId,
            leaseExpiresAt,
            now,
            cancellationToken);

    public Task ReleaseLegacyPairingLeaseAsync(
        string environment,
        string storeCode,
        Guid attemptId,
        CancellationToken cancellationToken) =>
        inner.ReleaseLegacyPairingLeaseAsync(environment, storeCode, attemptId, cancellationToken);

    public async Task<LinklyCloudBackendTerminalCredentialRecord> CompleteLegacyPairingAsync(
        string environment,
        string storeCode,
        string deviceCode,
        Guid attemptId,
        DateTime now,
        string secret,
        string posId,
        string? updatedBy,
        CancellationToken cancellationToken) =>
        Unprotect(await inner.CompleteLegacyPairingAsync(
            environment,
            storeCode,
            deviceCode,
            attemptId,
            now,
            LinklyCloudLegacyCredentialProtection.ProtectSecret(protector, secret),
            posId,
            updatedBy,
            cancellationToken))!;

    private LinklyCloudBackendTerminalCredentialRecord? Unprotect(LinklyCloudBackendTerminalCredentialRecord? record)
    {
        if (record is not null)
        {
            record.Secret = LinklyCloudLegacyCredentialProtection.UnprotectSecret(protector, record.Secret);
        }

        return record;
    }
}

public interface ILinklyCloudLegacyCredentialProtectionMigrator
{
    /// <summary>把两张旧表里仍是明文的行就地加密，返回本次加密的行数。可重复执行。</summary>
    Task<int> MigrateAsync(CancellationToken cancellationToken = default);
}

public sealed class SqlSugarLinklyCloudLegacyCredentialProtectionMigrator(
    HbposSqlSugarContext dbContext,
    ILinklyCloudTerminalCredentialProtector protector,
    ILogger<SqlSugarLinklyCloudLegacyCredentialProtectionMigrator> logger)
    : ILinklyCloudLegacyCredentialProtectionMigrator
{
    internal const string SelectPlaintextPasswordsSql = """
        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudCredential]', N'U') IS NOT NULL
            SELECT [Id], [Password] AS [Value]
            FROM [dbo].[POSM_LinklyCloudCredential]
            WHERE NULLIF(LTRIM(RTRIM([Password])), N'') IS NOT NULL
              AND LEFT([Password], 6) <> N'hbdp1:';
        ELSE
            SELECT CAST(NULL AS BIGINT) AS [Id], CAST(NULL AS NVARCHAR(512)) AS [Value] WHERE 1 = 0;
        """;

    internal const string SelectPlaintextSecretsSql = """
        IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudBackendTerminal]', N'U') IS NOT NULL
            SELECT [Id], [Secret] AS [Value]
            FROM [dbo].[POSM_LinklyCloudBackendTerminal]
            WHERE NULLIF(LTRIM(RTRIM([Secret])), N'') IS NOT NULL
              AND LEFT([Secret], 6) <> N'hbdp1:';
        ELSE
            SELECT CAST(NULL AS BIGINT) AS [Id], CAST(NULL AS NVARCHAR(512)) AS [Value] WHERE 1 = 0;
        """;

    // 带旧值比较（CAS）：迁移期间若有人改写了该行，本次不覆盖，下次启动再处理。
    internal const string UpdatePasswordSql = """
        UPDATE [dbo].[POSM_LinklyCloudCredential]
        SET [Password] = @NewValue
        WHERE [Id] = @Id AND [Password] = @OldValue;
        """;

    internal const string UpdateSecretSql = """
        UPDATE [dbo].[POSM_LinklyCloudBackendTerminal]
        SET [Secret] = @NewValue
        WHERE [Id] = @Id AND [Secret] = @OldValue;
        """;

    public async Task<int> MigrateAsync(CancellationToken cancellationToken = default)
    {
        var migrated = 0;
        migrated += await MigrateTableAsync(
            SelectPlaintextPasswordsSql,
            UpdatePasswordSql,
            value => LinklyCloudLegacyCredentialProtection.ProtectPassword(protector, value),
            "POSM_LinklyCloudCredential.Password");
        migrated += await MigrateTableAsync(
            SelectPlaintextSecretsSql,
            UpdateSecretSql,
            value => LinklyCloudLegacyCredentialProtection.ProtectSecret(protector, value),
            "POSM_LinklyCloudBackendTerminal.Secret");
        return migrated;
    }

    private async Task<int> MigrateTableAsync(
        string selectSql,
        string updateSql,
        Func<string, string> protect,
        string columnName)
    {
        var rows = await dbContext.PosmDb.Ado.SqlQueryAsync<PlaintextRow>(selectSql);
        var migrated = 0;
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Value))
            {
                continue;
            }

            try
            {
                var affected = await dbContext.PosmDb.Ado.ExecuteCommandAsync(
                    updateSql,
                    new SugarParameter("@Id", row.Id),
                    new SugarParameter("@OldValue", row.Value),
                    new SugarParameter("@NewValue", protect(row.Value)));
                migrated += affected;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 单行失败（例如历史超长明文加密后放不进列）不能拖累其余行；该行继续按明文兼容读取。
                logger.LogWarning(
                    ex,
                    "Linkly legacy credential could not be encrypted column={Column} id={Id}",
                    columnName,
                    row.Id);
            }
        }

        if (migrated > 0)
        {
            logger.LogInformation(
                "Linkly legacy credential encrypted column={Column} rows={Rows}",
                columnName,
                migrated);
        }

        return migrated;
    }

    private sealed class PlaintextRow
    {
        public long Id { get; set; }

        public string? Value { get; set; }
    }
}
