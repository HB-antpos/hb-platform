using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class InvoiceEmailSettingsServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _sqliteConnection;
    private readonly SqlSugarClient _db;

    public InvoiceEmailSettingsServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _sqliteConnection = new SqliteConnection($"Data Source={_dbPath}");
        _sqliteConnection.Open();

        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _sqliteConnection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });

        _db.CodeFirst.InitTables(typeof(InvoiceEmailConfiguration));
    }

    [Fact]
    public async Task GetSettingsAsync_WhenDatabaseEmpty_ReturnsAppsettingsFallbackWithoutPassword()
    {
        var service = CreateService(new InvoiceEmailOptions
        {
            Host = "smtp.example.com",
            Port = 465,
            UseSsl = true,
            CheckCertificateRevocation = false,
            Username = "smtp-user",
            Password = "secret",
            FromEmail = "warehouse@example.com",
            FromName = "HOT BARGAIN",
            MaxAttachmentBytes = 12345,
        });

        var result = await service.GetSettingsAsync();
        var account = Assert.Single(result.Data!.Accounts);

        Assert.True(result.Success);
        Assert.Equal(InvoiceEmailConfiguration.DefaultId, account.Id);
        Assert.True(account.IsDefault);
        Assert.Equal("smtp.example.com", account.Host);
        Assert.Equal(465, account.Port);
        Assert.False(account.CheckCertificateRevocation);
        Assert.Equal("smtp-user", account.Username);
        Assert.True(account.HasPassword);
        Assert.Equal("warehouse@example.com", account.FromEmail);
        Assert.Equal("HOT BARGAIN", account.FromName);
        Assert.Equal(12345, account.MaxAttachmentBytes);
        Assert.Null(account.UpdatedBy);
    }

    [Fact]
    public async Task UpdateSettingsAsync_WhenDatabaseEmptyAndFallbackPasswordExists_PreservesFallbackPassword()
    {
        var service = CreateService(new InvoiceEmailOptions
        {
            Password = "fallback-secret",
        });

        var result = await service.UpdateSettingsAsync(
            CreateRequest(CreateAccount(host: "smtp-updated.example.com", password: "")),
            "admin"
        );
        var row = await _db.Queryable<InvoiceEmailConfiguration>().SingleAsync();
        var runtime = await service.GetEffectiveOptionsAsync();

        Assert.True(result.Success);
        Assert.True(result.Data!.Accounts.Single().HasPassword);
        Assert.Equal("fallback-secret", row.EncryptedPassword);
        Assert.Equal("smtp-updated.example.com", runtime.Host);
        Assert.Equal("fallback-secret", runtime.Password);
    }

    [Fact]
    public async Task UpdateSettingsAsync_SavesMultiplePlainTextPasswordsAndUsesDefaultAccount()
    {
        var service = CreateService();

        var result = await service.UpdateSettingsAsync(
            CreateRequest(
                CreateAccount(
                    id: "primary",
                    name: "Primary",
                    host: "primary.smtp.example.com",
                    password: "primary-secret",
                    fromEmail: "primary@example.com",
                    isDefault: false
                ),
                CreateAccount(
                    id: "default-sender",
                    name: "Default Sender",
                    host: "default.smtp.example.com",
                    password: "default-secret",
                    fromEmail: "default@example.com",
                    isDefault: true
                )
            ),
            "admin"
        );

        var rows = await _db.Queryable<InvoiceEmailConfiguration>().ToListAsync();
        var runtime = await service.GetEffectiveOptionsAsync();

        Assert.True(result.Success);
        Assert.Equal(2, result.Data!.Accounts.Count);
        Assert.All(result.Data.Accounts, account => Assert.True(account.HasPassword));
        Assert.Equal("admin", result.Data.Accounts.Single(account => account.Id == "default-sender").UpdatedBy);
        Assert.Equal("primary-secret", rows.Single(row => row.Id == "primary").EncryptedPassword);
        Assert.Equal("default-secret", rows.Single(row => row.Id == "default-sender").EncryptedPassword);
        Assert.Equal("default.smtp.example.com", runtime.Host);
        Assert.Equal("default-secret", runtime.Password);
        Assert.Equal("default@example.com", runtime.FromEmail);
    }

    [Fact]
    public async Task UpdateSettingsAsync_WhenPasswordEmpty_PreservesSameAccountPassword()
    {
        var service = CreateService();
        await service.UpdateSettingsAsync(
            CreateRequest(CreateAccount(password: "initial-secret")),
            "admin"
        );

        var result = await service.UpdateSettingsAsync(
            CreateRequest(CreateAccount(host: "smtp-updated.example.com", password: "")),
            "admin"
        );
        var runtime = await service.GetEffectiveOptionsAsync();

        Assert.True(result.Success);
        Assert.True(result.Data!.Accounts.Single().HasPassword);
        Assert.Equal("smtp-updated.example.com", runtime.Host);
        Assert.Equal("initial-secret", runtime.Password);
    }

    [Fact]
    public async Task UpdateSettingsAsync_WhenPasswordEmpty_PreservesStoredPlainTextPassword()
    {
        var service = CreateService();
        await SeedPlainTextPasswordAsync("stored-secret");

        var result = await service.UpdateSettingsAsync(
            CreateRequest(CreateAccount(host: "smtp-updated.example.com", password: "")),
            "admin"
        );
        var row = await _db.Queryable<InvoiceEmailConfiguration>().FirstAsync();
        var runtime = await service.GetEffectiveOptionsAsync();

        Assert.True(result.Success);
        Assert.Equal("smtp-updated.example.com", row!.Host);
        Assert.Equal("stored-secret", row.EncryptedPassword);
        Assert.Equal("stored-secret", runtime.Password);
    }

    [Fact]
    public async Task UpdateSettingsAsync_WhenStoredPasswordInvalidAndNewPasswordProvided_ReplacesPassword()
    {
        var service = CreateService();
        await SeedInvalidEncryptedPasswordAsync();

        var result = await service.UpdateSettingsAsync(
            CreateRequest(CreateAccount(host: "smtp-updated.example.com", password: "new-secret")),
            "admin"
        );
        var runtime = await service.GetEffectiveOptionsAsync();

        Assert.True(result.Success);
        Assert.True(result.Data!.Accounts.Single().HasPassword);
        Assert.Equal("smtp-updated.example.com", runtime.Host);
        Assert.Equal("new-secret", runtime.Password);
    }

    [Fact]
    public async Task UpdateSettingsAsync_WhenClearPassword_RemovesStoredPassword()
    {
        var service = CreateService();
        await service.UpdateSettingsAsync(
            CreateRequest(CreateAccount(password: "initial-secret")),
            "admin"
        );

        var result = await service.UpdateSettingsAsync(
            CreateRequest(CreateAccount(password: "", clearPassword: true)),
            "admin"
        );
        var runtime = await service.GetEffectiveOptionsAsync();

        Assert.True(result.Success);
        Assert.False(result.Data!.Accounts.Single().HasPassword);
        Assert.True(string.IsNullOrWhiteSpace(runtime.Password));
    }

    [Fact]
    public async Task UpdateSettingsAsync_RejectsMissingOrDuplicatedDefaultAccount()
    {
        var service = CreateService();

        var noDefault = await service.UpdateSettingsAsync(
            CreateRequest(CreateAccount(id: "a", isDefault: false)),
            "admin"
        );
        var twoDefaults = await service.UpdateSettingsAsync(
            CreateRequest(
                CreateAccount(id: "a", isDefault: true),
                CreateAccount(id: "b", isDefault: true)
            ),
            "admin"
        );

        Assert.False(noDefault.Success);
        Assert.Equal("INVOICE_EMAIL_DEFAULT_ACCOUNT_REQUIRED", noDefault.ErrorCode);
        Assert.False(twoDefaults.Success);
        Assert.Equal("INVOICE_EMAIL_DEFAULT_ACCOUNT_REQUIRED", twoDefaults.ErrorCode);
    }

    [Fact]
    public async Task UpdateSettingsAsync_RemovesAccountsMissingFromSubmittedList()
    {
        var service = CreateService();
        await service.UpdateSettingsAsync(
            CreateRequest(
                CreateAccount(id: "keep", isDefault: true),
                CreateAccount(id: "remove", fromEmail: "remove@example.com", isDefault: false)
            ),
            "admin"
        );

        var result = await service.UpdateSettingsAsync(
            CreateRequest(CreateAccount(id: "keep", password: "", isDefault: true)),
            "admin"
        );
        var rows = await _db.Queryable<InvoiceEmailConfiguration>().ToListAsync();

        Assert.True(result.Success);
        Assert.Single(rows);
        Assert.Equal("keep", rows.Single().Id);
    }

    [Fact]
    public async Task BuildTransientOptions_UsesRequestPasswordWithoutSaving()
    {
        var service = CreateService();
        var request = CreateTestRequest(
            host: "smtp-test.example.com",
            password: "temporary-secret"
        );

        var options = await service.BuildTransientOptionsAsync(request);
        var rowCount = await _db.Queryable<InvoiceEmailConfiguration>().CountAsync();

        Assert.Equal("smtp-test.example.com", options.Host);
        Assert.Equal("temporary-secret", options.Password);
        Assert.Equal(0, rowCount);
    }

    [Fact]
    public async Task BuildTransientOptions_WhenPasswordEmpty_ReusesSavedAccountPassword()
    {
        var service = CreateService();
        await service.UpdateSettingsAsync(
            CreateRequest(CreateAccount(id: "saved", password: "saved-secret")),
            "admin"
        );

        var options = await service.BuildTransientOptionsAsync(
            CreateTestRequest(
                id: "saved",
                host: "smtp-test.example.com",
                password: "",
                clearPassword: false
            )
        );

        Assert.Equal("smtp-test.example.com", options.Host);
        Assert.Equal("saved-secret", options.Password);
    }

    [Fact]
    public async Task BuildTransientOptions_WhenDatabaseEmptyAndFallbackPasswordExists_ReusesFallbackPassword()
    {
        var service = CreateService(new InvoiceEmailOptions
        {
            Password = "fallback-secret",
        });

        var options = await service.BuildTransientOptionsAsync(
            CreateTestRequest(password: "", clearPassword: false)
        );
        var rowCount = await _db.Queryable<InvoiceEmailConfiguration>().CountAsync();

        Assert.Equal("smtp.example.com", options.Host);
        Assert.Equal("fallback-secret", options.Password);
        Assert.Equal(0, rowCount);
    }

    [Fact]
    public async Task BuildTransientOptions_WhenRequestPasswordProvided_DoesNotDecryptStoredPassword()
    {
        var service = CreateService();
        await SeedInvalidEncryptedPasswordAsync();

        var options = await service.BuildTransientOptionsAsync(
            CreateTestRequest(
                id: InvoiceEmailConfiguration.DefaultId,
                host: "smtp-test.example.com",
                password: "temporary-secret",
                clearPassword: false
            )
        );

        Assert.Equal("smtp-test.example.com", options.Host);
        Assert.Equal("temporary-secret", options.Password);
    }

    [Fact]
    public async Task BuildTransientOptions_WhenClearPassword_DoesNotDecryptStoredPassword()
    {
        var service = CreateService();
        await SeedInvalidEncryptedPasswordAsync();

        var options = await service.BuildTransientOptionsAsync(
            CreateTestRequest(
                id: InvoiceEmailConfiguration.DefaultId,
                host: "smtp-test.example.com",
                password: "",
                clearPassword: true
            )
        );

        Assert.Equal("smtp-test.example.com", options.Host);
        Assert.Null(options.Password);
    }

    [Fact]
    public async Task GetEffectiveOptionsAsync_WhenStoredPasswordPlainText_ReturnsPlainTextPassword()
    {
        var service = CreateService();
        await SeedPlainTextPasswordAsync("stored-secret");

        var options = await service.GetEffectiveOptionsAsync();

        Assert.Equal("stored-secret", options.Password);
    }

    [Fact]
    public async Task GetEffectiveOptionsAsync_WhenPlainTextPasswordStartsLikeLegacyPayload_ReturnsPlainTextPassword()
    {
        var service = CreateService();
        await SeedPlainTextPasswordAsync("CfDJ8-plain-secret");

        var options = await service.GetEffectiveOptionsAsync();

        Assert.Equal("CfDJ8-plain-secret", options.Password);
    }

    [Fact]
    public async Task GetEffectiveOptionsAsync_WhenStoredPasswordIsLegacyProtectedPayload_UnprotectsIt()
    {
        var protectedPassword = DataProtectionProvider
            .Create("InvoiceEmailSettingsServiceTests")
            .CreateProtector("Hbweb.InvoiceEmail.SmtpPassword.v1")
            .Protect("legacy-secret");
        var service = CreateService();
        await SeedPlainTextPasswordAsync(protectedPassword);

        var options = await service.GetEffectiveOptionsAsync();

        Assert.Equal("legacy-secret", options.Password);
    }

    [Fact]
    public async Task GetEffectiveOptionsAsync_WhenDefaultAccountMissing_ThrowsDefaultAccountException()
    {
        var service = CreateService();
        await SeedConfigurationAsync("sender-a", isDefault: false);
        await SeedConfigurationAsync("sender-b", isDefault: false);

        await Assert.ThrowsAsync<InvoiceEmailDefaultAccountException>(
            () => service.GetEffectiveOptionsAsync()
        );
    }

    [Fact]
    public async Task GetEffectiveOptionsAsync_WhenDefaultAccountDuplicated_ThrowsDefaultAccountException()
    {
        var service = CreateService();
        await SeedConfigurationAsync("sender-a", isDefault: true);
        await SeedConfigurationAsync("sender-b", isDefault: true);

        await Assert.ThrowsAsync<InvoiceEmailDefaultAccountException>(
            () => service.GetEffectiveOptionsAsync()
        );
    }

    [Fact]
    public async Task GetAccountOptionsAsync_WhenAccountIdGiven_ReturnsThatAccountInsteadOfDefault()
    {
        var service = CreateService();
        await SeedConfigurationAsync("sender-a", isDefault: true);
        await SeedConfigurationAsync("sender-b", isDefault: false);

        var options = await service.GetAccountOptionsAsync(" sender-b ");

        Assert.Equal("sender-b.smtp.example.com", options.Host);
        Assert.Equal("sender-b@example.com", options.FromEmail);
    }

    [Fact]
    public async Task GetAccountOptionsAsync_WhenAccountIdBlank_UsesDefaultAccount()
    {
        var service = CreateService();
        await SeedConfigurationAsync("sender-a", isDefault: false);
        await SeedConfigurationAsync("sender-b", isDefault: true);

        var options = await service.GetAccountOptionsAsync("  ");

        Assert.Equal("sender-b@example.com", options.FromEmail);
    }

    [Fact]
    public async Task GetAccountOptionsAsync_WhenAccountMissing_ThrowsInsteadOfFallingBackToDefault()
    {
        var service = CreateService();
        await SeedConfigurationAsync("sender-a", isDefault: true);

        await Assert.ThrowsAsync<InvoiceEmailAccountNotFoundException>(
            () => service.GetAccountOptionsAsync("deleted-sender")
        );
    }

    [Fact]
    public async Task GetAccountOptionsAsync_WhenDatabaseEmptyAndDefaultIdGiven_UsesAppsettingsFallback()
    {
        var service = CreateService(new InvoiceEmailOptions
        {
            Host = "fallback.smtp.example.com",
            Port = 465,
            FromEmail = "fallback@example.com",
        });

        var options = await service.GetAccountOptionsAsync(InvoiceEmailConfiguration.DefaultId);

        Assert.Equal("fallback.smtp.example.com", options.Host);
        Assert.Equal("fallback@example.com", options.FromEmail);
    }

    [Fact]
    public async Task GetSenderAccountsAsync_ReturnsDefaultFirstWithoutSmtpCredentials()
    {
        var service = CreateService();
        await SeedConfigurationAsync("sender-a", isDefault: false);
        await SeedConfigurationAsync("sender-b", isDefault: true);

        var accounts = await service.GetSenderAccountsAsync();

        Assert.Collection(
            accounts,
            account =>
            {
                Assert.Equal("sender-b", account.Id);
                Assert.True(account.IsDefault);
                Assert.Equal("sender-b@example.com", account.FromEmail);
            },
            account =>
            {
                Assert.Equal("sender-a", account.Id);
                Assert.False(account.IsDefault);
            }
        );
        // 发件账号选项 DTO 只允许名称与发件地址，防止把 SMTP 凭据下发给普通发票操作员。
        Assert.Equal(
            new[] { "FromEmail", "FromName", "Id", "IsDefault", "Name" },
            typeof(InvoiceEmailSenderAccountDto).GetProperties().Select(item => item.Name).Order().ToArray()
        );
    }

    [Fact]
    public async Task GetSenderAccountsAsync_WhenDatabaseEmpty_ReturnsFallbackOnlyWhenFromEmailConfigured()
    {
        var unconfigured = await CreateService().GetSenderAccountsAsync();
        var configured = await CreateService(new InvoiceEmailOptions
        {
            FromEmail = "fallback@example.com",
            FromName = "HOT BARGAIN",
        }).GetSenderAccountsAsync();

        Assert.Empty(unconfigured);
        var account = Assert.Single(configured);
        Assert.Equal(InvoiceEmailConfiguration.DefaultId, account.Id);
        Assert.True(account.IsDefault);
        Assert.Equal("fallback@example.com", account.FromEmail);
    }

    [Fact]
    public void EnsureInvoiceEmailConfigurationMultiAccountSchema_AddsColumnsAndBackfillsDefault()
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db")}");
        connection.Open();
        using var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });

        db.Ado.ExecuteCommand(
            """
            CREATE TABLE InvoiceEmailConfiguration (
                Id varchar(50) PRIMARY KEY,
                Host varchar(200) NOT NULL,
                Port integer NOT NULL,
                UseSsl integer NOT NULL,
                CheckCertificateRevocation integer NOT NULL,
                Username varchar(200) NULL,
                EncryptedPassword varchar(2000) NULL,
                FromEmail varchar(200) NOT NULL,
                FromName varchar(200) NULL,
                MaxAttachmentBytes integer NOT NULL,
                UpdatedAtUtc datetime NOT NULL,
                CreatedAt datetime NOT NULL,
                CreatedBy varchar(200) NULL,
                UpdatedAt datetime NULL,
                UpdatedBy varchar(200) NULL,
                IsDeleted integer NOT NULL
            )
            """
        );
        db.Ado.ExecuteCommand(
            """
            INSERT INTO InvoiceEmailConfiguration (
                Id, Host, Port, UseSsl, CheckCertificateRevocation, FromEmail,
                MaxAttachmentBytes, UpdatedAtUtc, CreatedAt, IsDeleted
            )
            VALUES (
                'default', 'smtp.example.com', 465, 1, 1, 'sender@example.com',
                5242880, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 0
            )
            """
        );

        var context = CreateSqlSugarContext(db);
        typeof(SqlSugarContext)
            .GetMethod(
                "EnsureInvoiceEmailConfigurationMultiAccountSchema",
                BindingFlags.Instance | BindingFlags.NonPublic
            )!
            .Invoke(context, null);

        var columns = db.DbMaintenance.GetColumnInfosByTableName("InvoiceEmailConfiguration", false);
        var row = db.Queryable<InvoiceEmailConfiguration>().Single();

        Assert.Contains(columns, column => column.DbColumnName == "Name");
        Assert.Contains(columns, column => column.DbColumnName == "IsDefault");
        Assert.Equal("sender@example.com", row.Name);
        Assert.True(row.IsDefault);
    }

    public void Dispose()
    {
        _db.Dispose();
        _sqliteConnection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private InvoiceEmailSettingsService CreateService(InvoiceEmailOptions? fallback = null)
    {
        return new InvoiceEmailSettingsService(
            CreateSqlSugarContext(_db),
            Options.Create(fallback ?? new InvoiceEmailOptions()),
            DataProtectionProvider.Create("InvoiceEmailSettingsServiceTests"),
            NullLogger<InvoiceEmailSettingsService>.Instance
        );
    }

    private static UpdateInvoiceEmailSettingsDto CreateRequest(
        params UpdateInvoiceEmailAccountDto[] accounts
    ) =>
        new()
        {
            Accounts = accounts.ToList(),
        };

    private static UpdateInvoiceEmailAccountDto CreateAccount(
        string id = InvoiceEmailConfiguration.DefaultId,
        string name = "Default Sender",
        string host = "smtp.example.com",
        string password = "secret",
        bool clearPassword = false,
        string fromEmail = "warehouse@example.com",
        bool isDefault = true
    ) =>
        new()
        {
            Id = id,
            Name = name,
            Host = host,
            Port = 465,
            UseSsl = true,
            CheckCertificateRevocation = true,
            Username = "smtp-user",
            Password = password,
            ClearPassword = clearPassword,
            FromEmail = fromEmail,
            FromName = "HOT BARGAIN",
            MaxAttachmentBytes = 5_242_880,
            IsDefault = isDefault,
        };

    private static TestInvoiceEmailSettingsDto CreateTestRequest(
        string id = InvoiceEmailConfiguration.DefaultId,
        string name = "Default Sender",
        string host = "smtp.example.com",
        string password = "secret",
        bool clearPassword = false,
        string fromEmail = "warehouse@example.com",
        bool isDefault = true
    ) =>
        new()
        {
            Id = id,
            Name = name,
            Host = host,
            Port = 465,
            UseSsl = true,
            CheckCertificateRevocation = true,
            Username = "smtp-user",
            Password = password,
            ClearPassword = clearPassword,
            FromEmail = fromEmail,
            FromName = "HOT BARGAIN",
            MaxAttachmentBytes = 5_242_880,
            IsDefault = isDefault,
            TestToEmail = "qa@example.com",
        };

    private async Task SeedInvalidEncryptedPasswordAsync()
    {
        await _db.Insertable(
            new InvoiceEmailConfiguration
            {
                Id = InvoiceEmailConfiguration.DefaultId,
                Name = "Default Sender",
                IsDefault = true,
                Host = "smtp.example.com",
                Port = 465,
                UseSsl = true,
                CheckCertificateRevocation = true,
                Username = "smtp-user",
                EncryptedPassword = "invalid-protected-payload",
                FromEmail = "warehouse@example.com",
                MaxAttachmentBytes = 5_242_880,
            }
        ).ExecuteCommandAsync();
    }

    private async Task SeedPlainTextPasswordAsync(string password)
    {
        await _db.Insertable(
            new InvoiceEmailConfiguration
            {
                Id = InvoiceEmailConfiguration.DefaultId,
                Name = "Default Sender",
                IsDefault = true,
                Host = "smtp.example.com",
                Port = 465,
                UseSsl = true,
                CheckCertificateRevocation = true,
                Username = "smtp-user",
                EncryptedPassword = password,
                FromEmail = "warehouse@example.com",
                MaxAttachmentBytes = 5_242_880,
            }
        ).ExecuteCommandAsync();
    }

    private async Task SeedConfigurationAsync(string id, bool isDefault)
    {
        await _db.Insertable(
            new InvoiceEmailConfiguration
            {
                Id = id,
                Name = id,
                IsDefault = isDefault,
                Host = $"{id}.smtp.example.com",
                Port = 465,
                UseSsl = true,
                CheckCertificateRevocation = true,
                Username = "smtp-user",
                FromEmail = $"{id}@example.com",
                MaxAttachmentBytes = 5_242_880,
            }
        ).ExecuteCommandAsync();
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        var dbField = typeof(SqlSugarContext).GetField(
            "_db",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        dbField!.SetValue(context, db);
        return context;
    }
}
