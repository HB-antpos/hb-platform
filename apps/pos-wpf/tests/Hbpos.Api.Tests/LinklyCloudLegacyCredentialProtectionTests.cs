using System.Security.Cryptography;
using BlazorApp.Shared.Security;
using Hbpos.Api.Data;
using Hbpos.Api.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SecurityProtection = Hbpos.Api.Security.LinklyCloudTerminalCredentialDataProtection;

namespace Hbpos.Api.Tests;

// H15：旧版门店 Linkly 密码 / 终端 secret 原先明文落库。这里锁定“写入必加密、读取透明解密、历史明文兼容、密钥不一致可诊断”。
public sealed class LinklyCloudLegacyCredentialProtectionTests : IDisposable
{
    private const string Password = "store-portal-password-!@#";
    private const string Secret = "terminal-secret-0123456789";
    private readonly string keysPath = Path.Combine(
        Path.GetTempPath(),
        "hbpos-legacy-credential-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(keysPath))
        {
            Directory.Delete(keysPath, recursive: true);
        }
    }

    [Fact]
    public async Task Credential_repository_decorator_writes_ciphertext_and_returns_plaintext()
    {
        var protector = CreateProtector(keysPath);
        var inner = new InMemoryCredentialRepository();
        var repository = new ProtectingLinklyCloudCredentialRepository(inner, protector);

        var saved = await repository.UpsertAsync(
            "S01", "Production", "merchant", Password, DateTime.UtcNow, "device:POS-01", CancellationToken.None);

        Assert.Equal(Password, saved.Password);
        var stored = Assert.IsType<string>(inner.Stored!.Password);
        Assert.StartsWith(LinklyCloudLegacyCredentialProtection.ProtectedPrefix, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, stored, StringComparison.Ordinal);
        Assert.Equal("merchant", inner.Stored.Username);

        var read = await repository.GetByStoreCodeAsync("S01", "Production", CancellationToken.None);
        Assert.Equal(Password, read!.Password);
    }

    [Fact]
    public async Task Credential_repository_decorator_still_reads_unmigrated_plaintext_rows()
    {
        var inner = new InMemoryCredentialRepository
        {
            Stored = new LinklyCloudCredentialRecord
            {
                StoreCode = "S01",
                Environment = "Production",
                Username = "merchant",
                Password = "legacy-plaintext-password"
            }
        };
        var repository = new ProtectingLinklyCloudCredentialRepository(inner, CreateProtector(keysPath));

        var read = await repository.GetByStoreCodeAsync("S01", "Production", CancellationToken.None);

        Assert.Equal("legacy-plaintext-password", read!.Password);
    }

    [Fact]
    public async Task Credential_repository_decorator_reports_key_ring_mismatch_without_leaking_values()
    {
        var inner = new InMemoryCredentialRepository();
        await new ProtectingLinklyCloudCredentialRepository(inner, CreateProtector(keysPath)).UpsertAsync(
            "S01", "Production", "merchant", Password, DateTime.UtcNow, null, CancellationToken.None);
        var otherRing = new ProtectingLinklyCloudCredentialRepository(
            inner,
            CreateProtector(keysPath + "-other"));

        try
        {
            var exception = await Assert.ThrowsAsync<LinklyCloudLegacyCredentialUnprotectException>(() =>
                otherRing.GetByStoreCodeAsync("S01", "Production", CancellationToken.None));

            Assert.IsAssignableFrom<CryptographicException>(exception.InnerException);
            Assert.Contains("LinklyCloudCredentialDataProtection:KeysPath", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Password, exception.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(inner.Stored!.Password!, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(keysPath + "-other"))
            {
                Directory.Delete(keysPath + "-other", recursive: true);
            }
        }
    }

    [Fact]
    public async Task Terminal_repository_decorator_encrypts_secret_for_upsert_and_pairing_completion()
    {
        var protector = CreateProtector(keysPath);
        var inner = new InMemoryTerminalRepository();
        var repository = new ProtectingLinklyCloudBackendTerminalCredentialRepository(inner, protector);

        var upserted = await repository.UpsertAsync(
            "Production", "S01", "POS-01", Secret, "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            DateTime.UtcNow, null, CancellationToken.None);
        Assert.Equal(Secret, upserted.Secret);
        Assert.StartsWith(LinklyCloudLegacyCredentialProtection.ProtectedPrefix, inner.Stored!.Secret, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, inner.Stored.Secret, StringComparison.Ordinal);

        var completed = await repository.CompleteLegacyPairingAsync(
            "Production", "S01", "POS-01", Guid.NewGuid(), DateTime.UtcNow, "paired-" + Secret,
            "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", null, CancellationToken.None);
        Assert.Equal("paired-" + Secret, completed.Secret);
        Assert.StartsWith(LinklyCloudLegacyCredentialProtection.ProtectedPrefix, inner.Stored.Secret, StringComparison.Ordinal);
        Assert.DoesNotContain("paired-" + Secret, inner.Stored.Secret, StringComparison.Ordinal);

        var read = await repository.GetByDeviceAsync("Production", "S01", "POS-01", CancellationToken.None);
        Assert.Equal("paired-" + Secret, read!.Secret);
    }

    [Fact]
    public async Task Terminal_repository_decorator_still_reads_unmigrated_plaintext_secret()
    {
        var inner = new InMemoryTerminalRepository
        {
            Stored = new LinklyCloudBackendTerminalCredentialRecord
            {
                Environment = "Production",
                StoreCode = "S01",
                DeviceCode = "POS-01",
                Secret = "legacy-plaintext-secret",
                PosId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
            }
        };
        var repository = new ProtectingLinklyCloudBackendTerminalCredentialRepository(inner, CreateProtector(keysPath));

        var read = await repository.GetByDeviceAsync("Production", "S01", "POS-01", CancellationToken.None);

        Assert.Equal("legacy-plaintext-secret", read!.Secret);
    }

    [Fact]
    public async Task Credential_service_rejects_password_that_would_not_fit_the_ciphertext_column()
    {
        var service = new LinklyCloudCredentialService(new InMemoryCredentialRepository());

        await Assert.ThrowsAsync<LinklyCloudCredentialValidationException>(() => service.UpsertAsync(
            "S01",
            new Hbpos.Contracts.Linkly.LinklyCloudCredentialUpsertRequest(
                "Production", "merchant", new string('p', LinklyCloudCredentialService.MaxPasswordBytes + 1)),
            null,
            CancellationToken.None));

        // 上限本身加密后必须仍小于 NVARCHAR(256)。
        var protectedMax = LinklyCloudLegacyCredentialProtection.ProtectPassword(
            CreateProtector(keysPath),
            new string('p', LinklyCloudCredentialService.MaxPasswordBytes));
        Assert.True(protectedMax.Length <= 256, $"protected length {protectedMax.Length}");
    }

    private static ILinklyCloudTerminalCredentialProtector CreateProtector(string path) =>
        SecurityProtection.CreateProtector(SecurityProtection.CreateProvider(path));

    private sealed class InMemoryCredentialRepository : ILinklyCloudCredentialRepository
    {
        public LinklyCloudCredentialRecord? Stored { get; set; }

        public Task<LinklyCloudCredentialRecord?> GetByStoreCodeAsync(
            string storeCode,
            string environment,
            CancellationToken cancellationToken) =>
            Task.FromResult(Stored is null ? null : Clone(Stored));

        public Task<LinklyCloudCredentialRecord> UpsertAsync(
            string storeCode,
            string environment,
            string username,
            string password,
            DateTime updatedAt,
            string? updatedBy,
            CancellationToken cancellationToken)
        {
            Stored = new LinklyCloudCredentialRecord
            {
                StoreCode = storeCode,
                Environment = environment,
                Username = username,
                Password = password,
                UpdatedAt = updatedAt,
                UpdatedBy = updatedBy
            };
            return Task.FromResult(Clone(Stored));
        }

        private static LinklyCloudCredentialRecord Clone(LinklyCloudCredentialRecord source) => new()
        {
            Id = source.Id,
            StoreCode = source.StoreCode,
            Environment = source.Environment,
            Username = source.Username,
            Password = source.Password,
            UpdatedAt = source.UpdatedAt,
            UpdatedBy = source.UpdatedBy
        };
    }

    private sealed class InMemoryTerminalRepository : ILinklyCloudBackendTerminalCredentialRepository
    {
        public LinklyCloudBackendTerminalCredentialRecord? Stored { get; set; }

        public Task<LinklyCloudBackendTerminalCredentialRecord?> GetByDeviceAsync(
            string environment,
            string storeCode,
            string deviceCode,
            CancellationToken cancellationToken) =>
            Task.FromResult(Stored is null ? null : Clone(Stored));

        public Task<LinklyCloudBackendTerminalCredentialRecord> UpsertAsync(
            string environment,
            string storeCode,
            string deviceCode,
            string secret,
            string posId,
            DateTime updatedAt,
            string? updatedBy,
            CancellationToken cancellationToken) =>
            Task.FromResult(Save(environment, storeCode, deviceCode, secret, posId, updatedAt, updatedBy));

        public Task AcquireLegacyPairingLeaseAsync(
            string environment,
            string storeCode,
            Guid attemptId,
            DateTime leaseExpiresAt,
            DateTime now,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReleaseLegacyPairingLeaseAsync(
            string environment,
            string storeCode,
            Guid attemptId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<LinklyCloudBackendTerminalCredentialRecord> CompleteLegacyPairingAsync(
            string environment,
            string storeCode,
            string deviceCode,
            Guid attemptId,
            DateTime now,
            string secret,
            string posId,
            string? updatedBy,
            CancellationToken cancellationToken) =>
            Task.FromResult(Save(environment, storeCode, deviceCode, secret, posId, now, updatedBy));

        private LinklyCloudBackendTerminalCredentialRecord Save(
            string environment,
            string storeCode,
            string deviceCode,
            string secret,
            string posId,
            DateTime updatedAt,
            string? updatedBy)
        {
            Stored = new LinklyCloudBackendTerminalCredentialRecord
            {
                Environment = environment,
                StoreCode = storeCode,
                DeviceCode = deviceCode,
                Secret = secret,
                PosId = posId,
                UpdatedAt = updatedAt,
                UpdatedBy = updatedBy
            };
            return Clone(Stored);
        }

        private static LinklyCloudBackendTerminalCredentialRecord Clone(LinklyCloudBackendTerminalCredentialRecord source) => new()
        {
            Id = source.Id,
            Environment = source.Environment,
            StoreCode = source.StoreCode,
            DeviceCode = source.DeviceCode,
            Secret = source.Secret,
            PosId = source.PosId,
            UpdatedAt = source.UpdatedAt,
            UpdatedBy = source.UpdatedBy
        };
    }
}

public sealed class LegacyCredentialSqlServerFactAttribute : FactAttribute
{
    public const string ConnectionVariable = "HB_TEST_SQLSERVER_CONNECTION";

    public LegacyCredentialSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
        {
            Skip = "未配置隔离 SQL Server，跳过旧版 Linkly 凭据迁移真实 SQL 测试。";
        }
    }
}

/// <summary>在真实 SQL Server 上验证启动迁移：明文补加密、已加密不重复处理、可重复执行、CAS 不覆盖并发改写。</summary>
[Trait("Category", "SQL")]
public sealed class LinklyCloudLegacyCredentialMigrationSqlServerTests : IAsyncLifetime
{
    private readonly string keysPath = Path.Combine(
        Path.GetTempPath(),
        "hbpos-legacy-credential-sql-tests",
        Guid.NewGuid().ToString("N"));
    private string? masterConnection;
    private string connection = string.Empty;
    private string database = string.Empty;

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(LegacyCredentialSqlServerFactAttribute.ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return;
        }

        database = $"HbLinklyLegacyCred_{Guid.NewGuid():N}";
        var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" };
        masterConnection = builder.ConnectionString;
        await ExecuteAtAsync(masterConnection, $"CREATE DATABASE [{database}]");
        builder.InitialCatalog = database;
        connection = builder.ConnectionString;
        await ExecuteAtAsync(connection, SqlSugarLinklyCloudCredentialSchemaInitializer.EnsureTableSql);
        await ExecuteAtAsync(connection, SqlSugarLinklyCloudBackendAsyncSchemaInitializer.EnsureTableSql);
    }

    public async Task DisposeAsync()
    {
        if (Directory.Exists(keysPath))
        {
            Directory.Delete(keysPath, recursive: true);
        }

        if (masterConnection is null)
        {
            return;
        }

        await ExecuteAtAsync(
            masterConnection,
            $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
    }

    [LegacyCredentialSqlServerFact]
    public async Task Migrator_encrypts_plaintext_rows_once_and_repositories_still_read_plaintext()
    {
        await ExecuteAtAsync(connection, """
            INSERT INTO [dbo].[POSM_LinklyCloudCredential] ([StoreCode], [Environment], [Username], [Password])
            VALUES (N'S01', N'Production', N'merchant', N'plain-password');
            INSERT INTO [dbo].[POSM_LinklyCloudBackendTerminal] ([Environment], [StoreCode], [DeviceCode], [Secret], [PosId])
            VALUES (N'Production', N'S01', N'POS-01', N'plain-secret', N'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');
            """);
        var context = CreateContext();
        var protector = SecurityProtection.CreateProtector(SecurityProtection.CreateProvider(keysPath));
        var migrator = new SqlSugarLinklyCloudLegacyCredentialProtectionMigrator(
            context,
            protector,
            NullLogger<SqlSugarLinklyCloudLegacyCredentialProtectionMigrator>.Instance);

        Assert.Equal(2, await migrator.MigrateAsync());
        Assert.Equal(0, await migrator.MigrateAsync());

        var storedPassword = await ScalarAsync("SELECT [Password] FROM [dbo].[POSM_LinklyCloudCredential]");
        var storedSecret = await ScalarAsync("SELECT [Secret] FROM [dbo].[POSM_LinklyCloudBackendTerminal]");
        Assert.StartsWith(LinklyCloudLegacyCredentialProtection.ProtectedPrefix, storedPassword, StringComparison.Ordinal);
        Assert.StartsWith(LinklyCloudLegacyCredentialProtection.ProtectedPrefix, storedSecret, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-password", storedPassword, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-secret", storedSecret, StringComparison.Ordinal);

        var credential = await new ProtectingLinklyCloudCredentialRepository(
                new SqlSugarLinklyCloudCredentialRepository(context), protector)
            .GetByStoreCodeAsync("S01", "Production", CancellationToken.None);
        var terminal = await new ProtectingLinklyCloudBackendTerminalCredentialRepository(
                new SqlSugarLinklyCloudBackendTerminalCredentialRepository(context), protector)
            .GetByDeviceAsync("Production", "S01", "POS-01", CancellationToken.None);
        Assert.Equal("plain-password", credential!.Password);
        Assert.Equal("plain-secret", terminal!.Secret);
    }

    [LegacyCredentialSqlServerFact]
    public async Task Repository_write_through_decorator_never_stores_plaintext()
    {
        var context = CreateContext();
        var protector = SecurityProtection.CreateProtector(SecurityProtection.CreateProvider(keysPath));
        var repository = new ProtectingLinklyCloudCredentialRepository(
            new SqlSugarLinklyCloudCredentialRepository(context), protector);

        // 模式表不存在时 UpsertSql 会失败，所以先建模式表（与生产初始化一致）。
        await ExecuteAtAsync(connection, """
            IF OBJECT_ID(N'[dbo].[POSM_LinklyCloudConfigurationMode]', N'U') IS NULL
                CREATE TABLE [dbo].[POSM_LinklyCloudConfigurationMode] (
                    [Environment] NVARCHAR(32) NOT NULL,
                    [StoreCode] NVARCHAR(32) NOT NULL,
                    [Mode] NVARCHAR(16) NOT NULL,
                    [LegacyPairingAttemptId] UNIQUEIDENTIFIER NULL,
                    [LegacyPairingLeaseExpiresAt] DATETIME2(7) NULL,
                    [UpdatedAt] DATETIME2(7) NOT NULL,
                    [UpdatedBy] NVARCHAR(128) NULL);
            """);
        var saved = await repository.UpsertAsync(
            "S01", "Production", "merchant", "fresh-password", DateTime.UtcNow, "device:POS-01", CancellationToken.None);

        Assert.Equal("fresh-password", saved.Password);
        var stored = await ScalarAsync("SELECT [Password] FROM [dbo].[POSM_LinklyCloudCredential]");
        Assert.StartsWith(LinklyCloudLegacyCredentialProtection.ProtectedPrefix, stored, StringComparison.Ordinal);
        Assert.DoesNotContain("fresh-password", stored, StringComparison.Ordinal);
    }

    private HbposSqlSugarContext CreateContext()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:MainConnection"] = connection,
            ["ConnectionStrings:PosmConnection"] = connection,
            ["Database:CommandTimeoutSeconds"] = "30",
        }).Build();
        return new HbposSqlSugarContext(config, NullLogger<HbposSqlSugarContext>.Instance);
    }

    private async Task<string> ScalarAsync(string sql)
    {
        await using var sqlConnection = new SqlConnection(connection);
        await sqlConnection.OpenAsync();
        await using var command = new SqlCommand(sql, sqlConnection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAtAsync(string connectionString, string sql)
    {
        await using var sqlConnection = new SqlConnection(connectionString);
        await sqlConnection.OpenAsync();
        // 初始化脚本可能含多个批次分隔，这里整体作为单批执行即可（脚本本身不含 GO）。
        await using var command = new SqlCommand(sql, sqlConnection);
        await command.ExecuteNonQueryAsync();
    }
}
