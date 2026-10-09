using System.Text.RegularExpressions;
using BlazorApp.Shared.Security;
using Hbpos.Api.Services;
using Microsoft.Extensions.Logging;
using PosCredentialDataProtection = Hbpos.Api.Security.LinklyCloudTerminalCredentialDataProtection;

namespace Hbpos.Api.Tests;

// M8：POS 与 Admin 的 Linkly 凭据密钥目录不一致时，旧代码把一切解密失败都吞成“请重新录入密码”，
// 管理员重录后密文仍由 Admin 那套密钥加密，POS 依旧解不开，形成死循环且日志里没有任何线索。
public sealed class LinklyCloudCredentialKeyRingMismatchTests : IDisposable
{
    private readonly string adminKeysPath = NewKeysPath();
    private readonly string posKeysPath = NewKeysPath();

    public void Dispose()
    {
        foreach (var path in new[] { adminKeysPath, posKeysPath })
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    [Fact]
    public void Payload_key_id_matches_the_key_file_that_encrypted_it()
    {
        var admin = CreateProtector(adminKeysPath);

        var protectedPassword = admin.ProtectPassword("lane-password");

        Assert.True(LinklyCloudProtectedPayload.TryReadKeyId(protectedPassword, out var keyId));
        // 解析器读到的 key id 必须和密钥目录里真实的 key 文件一致，否则日志里的“缺失 key id”会误导排障。
        var keyFile = Assert.Single(Directory.GetFiles(adminKeysPath, "key-*.xml"));
        Assert.Equal(
            Guid.Parse(Regex.Match(Path.GetFileName(keyFile), "key-(?<id>[0-9a-fA-F-]{36})\\.xml").Groups["id"].Value),
            keyId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("corrupted-secret")]
    [InlineData("AAAA")]
    public void Payload_key_id_is_not_reported_for_values_that_are_not_data_protection_payloads(string? value)
    {
        Assert.False(LinklyCloudProtectedPayload.TryReadKeyId(value, out var keyId));
        Assert.Equal(Guid.Empty, keyId);
    }

    [Fact]
    public void Runtime_materialization_reports_the_missing_key_for_a_foreign_key_ring()
    {
        var admin = CreateProtector(adminKeysPath);
        var pos = CreateProtector(posKeysPath);
        var stored = CreateStored(admin.ProtectPassword("lane-password"), secret: null);
        LinklyCloudProtectedPayload.TryReadKeyId(stored.Password, out var adminKeyId);

        var exception = Assert.Throws<LinklyCloudTerminalCredentialKeyRingMismatchException>(() =>
            SqlSugarLinklyCloudTerminalRepository.MaterializeRuntimeTerminal(stored, pos));

        Assert.Equal(adminKeyId, exception.MissingKeyId);
        Assert.Contains(adminKeyId.ToString("D"), exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lane-password", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(stored.Password, exception.Message, StringComparison.Ordinal);
        // 仍是 Unavailable 的子类：既有 catch 不会漏接。
        Assert.IsAssignableFrom<LinklyCloudTerminalCredentialUnavailableException>(exception);
    }

    [Fact]
    public void Runtime_materialization_reports_the_secret_key_when_only_the_secret_cannot_be_decrypted()
    {
        var pos = CreateProtector(posKeysPath);
        var admin = CreateProtector(adminKeysPath);
        // 密码由 POS 自己的环加密（可解），secret 由另一套环加密（不可解）。
        var stored = CreateStored(pos.ProtectPassword("lane-password"), admin.ProtectSecret("lane-secret"));
        LinklyCloudProtectedPayload.TryReadKeyId(stored.Secret, out var secretKeyId);

        var exception = Assert.Throws<LinklyCloudTerminalCredentialKeyRingMismatchException>(() =>
            SqlSugarLinklyCloudTerminalRepository.MaterializeRuntimeTerminal(stored, pos));

        Assert.Equal(secretKeyId, exception.MissingKeyId);
    }

    [Fact]
    public void Corrupted_ciphertext_keeps_the_generic_unavailable_failure()
    {
        var pos = CreateProtector(posKeysPath);
        var stored = CreateStored(pos.ProtectPassword("lane-password"), secret: "corrupted-secret");

        var exception = Assert.Throws<LinklyCloudTerminalCredentialUnavailableException>(() =>
            SqlSugarLinklyCloudTerminalRepository.MaterializeRuntimeTerminal(stored, pos));

        // 不是合法的 Data Protection 载荷，不能谎称“密钥环不一致”。
        Assert.IsNotType<LinklyCloudTerminalCredentialKeyRingMismatchException>(exception);
    }

    [Fact]
    public void List_materialization_surfaces_the_failure_instead_of_silently_marking_needs_repair()
    {
        var admin = CreateProtector(adminKeysPath);
        var pos = CreateProtector(posKeysPath);
        var stored = CreateStored(admin.ProtectPassword("lane-password"), secret: null) with { PairingState = "Ready" };
        Exception? reported = null;

        var materialized = SqlSugarLinklyCloudTerminalRepository.MaterializeListTerminal(
            stored,
            pos,
            ex => reported = ex);

        Assert.Equal("NeedsRepair", materialized.PairingState);
        Assert.IsType<LinklyCloudTerminalCredentialKeyRingMismatchException>(reported);
    }

    [Fact]
    public void Runtime_materialization_logs_the_missing_key_id_at_error_level_with_the_exception()
    {
        var admin = CreateProtector(adminKeysPath);
        var pos = CreateProtector(posKeysPath);
        var stored = CreateStored(admin.ProtectPassword("lane-password"), secret: null);
        LinklyCloudProtectedPayload.TryReadKeyId(stored.Password, out var adminKeyId);
        var logger = new CapturingLogger<SqlSugarLinklyCloudTerminalRepository>();
        var repository = new SqlSugarLinklyCloudTerminalRepository(dbContext: null!, pos, logger);

        Assert.Throws<LinklyCloudTerminalCredentialKeyRingMismatchException>(() =>
            repository.MaterializeRuntimeTerminalWithLogging(stored));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.IsType<LinklyCloudTerminalCredentialKeyRingMismatchException>(entry.Exception);
        Assert.Contains(adminKeyId.ToString("D"), entry.Message, StringComparison.OrdinalIgnoreCase);
        // 中心日志只上传 EventId 名称，缺失的 key id 必须能从这里读到。
        Assert.Equal($"linkly-keyring-missing-key:{adminKeyId:D}", entry.EventId.Name);
        Assert.DoesNotContain("lane-password", entry.Message, StringComparison.Ordinal);
    }

    private static string NewKeysPath() => Path.Combine(
        Path.GetTempPath(),
        "hbpos-linkly-keyring-tests",
        Guid.NewGuid().ToString("N"));

    private static ILinklyCloudTerminalCredentialProtector CreateProtector(string path) =>
        PosCredentialDataProtection.CreateProtector(PosCredentialDataProtection.CreateProvider(path));

    private static LinklyCloudTerminalRecord CreateStored(string protectedPassword, string? secret) => new()
    {
        TerminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
        Environment = "Sandbox",
        StoreCode = "S01",
        LaneNo = 1,
        DisplayName = "Front",
        Username = "lane-user",
        Password = protectedPassword,
        Secret = secret,
        PairingState = "Unpaired",
        CredentialProtectionVersion = BlazorApp.Shared.Security.LinklyCloudTerminalCredentialDataProtection.CurrentVersion,
        UpdatedAt = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc)
    };

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, EventId EventId, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, eventId, formatter(state, exception), exception));
    }
}
