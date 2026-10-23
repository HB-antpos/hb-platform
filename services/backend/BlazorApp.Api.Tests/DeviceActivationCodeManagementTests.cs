using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Models.POSM;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using System.Text.Json;
using BlazorApp.Shared.Security;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class DeviceActivationCodeManagementTests
{
    [Fact]
    public void Permission_IsSeededButNotGrantedToStoreOrWarehouseManagerTemplates()
    {
        Assert.Contains(
            PermissionSeedData.AllPermissions,
            definition => definition.Code == Permissions.DeviceRegistration.ActivationCodes.Manage);

        foreach (var roleName in new[] { "StoreManager", "WarehouseManager" })
        {
            var template = Assert.Single(
                PermissionSeedData.RolePermissionTemplates,
                item => item.RoleName == roleName);
            Assert.DoesNotContain(
                Permissions.DeviceRegistration.ActivationCodes.Manage,
                template.PermissionCodes);
        }
    }

    [Fact]
    public void Navigation_ActivationPermissionAloneShowsDeviceRegistrationPage()
    {
        var principal = TestClaimsPrincipal.WithPermission(
            Permissions.DeviceRegistration.ActivationCodes.Manage);

        var menu = new NavigationService().BuildMenu(principal);

        var system = Assert.Single(menu, item => item.Path == "/system");
        // 设备管理已并入「设备与远程」合并页。
        Assert.Contains(system.Children!, item => item.Path == "/system/devices");
    }

    [Fact]
    public void Controller_UsesApprovedRouteAndExactPermissionOnly()
    {
        var route = Assert.Single(
            typeof(DeviceActivationCodesController)
                .GetCustomAttributes(typeof(RouteAttribute), inherit: false)
                .Cast<RouteAttribute>());
        Assert.Equal("api/react/v1/device-activation-codes", route.Template);

        var authorize = Assert.Single(
            typeof(DeviceActivationCodesController)
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
                .Cast<AuthorizeAttribute>(),
            item => item.Policy != null);
        Assert.Equal(Permissions.DeviceRegistration.ActivationCodes.Manage, authorize.Policy);
    }

    [Fact]
    public void Schema_HasNoPlaintextColumnAndRecordsIdempotentRebindAudit()
    {
        var sql = DeviceActivationCodeSchema.EnsureSql;

        Assert.Contains("POSM_DeviceActivationGrant", sql, StringComparison.Ordinal);
        Assert.Contains("SecretHash", sql, StringComparison.Ordinal);
        Assert.Contains("ConsumedDeviceRegistrationId", sql, StringComparison.Ordinal);
        Assert.Contains("ConsumedAuthorizationHash", sql, StringComparison.Ordinal);
        Assert.Contains("BINARY(32)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(
            typeof(DeviceActivationCodeGrantDto).GetProperties(),
            property => property.Name.Contains("Authorization", StringComparison.Ordinal));
        Assert.Contains("ROWVERSION", sql, StringComparison.Ordinal);
        Assert.Contains("PreviousStoreCode", sql, StringComparison.Ordinal);
        Assert.Contains("PreviousDeviceCode", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("[ActivationCode]", sql, StringComparison.Ordinal);
        Assert.Contains("sp_getapplock", sql, StringComparison.Ordinal);
        Assert.Contains("RevokedConsumedExclusive", sql, StringComparison.Ordinal);
        Assert.Contains("sys.columns", sql, StringComparison.Ordinal);
        Assert.Contains("is_not_trusted", sql, StringComparison.Ordinal);
        Assert.Contains("PATINDEX", sql, StringComparison.Ordinal);
        Assert.Contains("[^0-9A-Za-z_]", sql, StringComparison.Ordinal);
        Assert.Contains("expected.[ColumnName]", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("sys.sql_expression_dependencies", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("NormalizedDefinition", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("REPLACE(checkInfo.[definition]", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("checkInfo.[definition] =", sql, StringComparison.Ordinal);
        Assert.Contains("@DeviceActivationTableWasCreated", sql, StringComparison.Ordinal);
        Assert.Contains("IX_POSM_DeviceActivationGrant_StoreCreated", sql, StringComparison.Ordinal);
        Assert.Contains("[key_ordinal] = 2", sql, StringComparison.Ordinal);
        Assert.Contains("[is_descending_key] = 1", sql, StringComparison.Ordinal);
        Assert.Contains("IX_POSM_DeviceActivationGrant_Usable", sql, StringComparison.Ordinal);
        Assert.Contains("[filter_definition]", sql, StringComparison.Ordinal);
        Assert.Contains("THROW 51010", sql, StringComparison.Ordinal);
        Assert.Contains("THROW 51011", sql, StringComparison.Ordinal);
        Assert.Contains("THROW 51012", sql, StringComparison.Ordinal);
        Assert.Contains("OBJECT_ID", sql, StringComparison.Ordinal);
        Assert.Contains(sql, DeviceRuntimeStatusSchemaMigrator.SqlScriptsForTests);
    }

    [Fact]
    public void Schema_VerifySqlIsReadOnlyAndChecksTheExactSecurityShape()
    {
        var sql = DeviceActivationCodeSchema.VerifySql;

        Assert.Contains("OBJECT_ID(N'[dbo].[POSM_DeviceActivationGrant]', N'U')", sql, StringComparison.Ordinal);
        Assert.Contains("THROW 51100", sql, StringComparison.Ordinal);
        Assert.Contains(
            "keyConstraint.[name] = N'PK_POSM_DeviceActivationGrant'",
            sql,
            StringComparison.Ordinal
        );
        Assert.Contains("expected.[ColumnName]", sql, StringComparison.Ordinal);
        foreach (var columnName in new[]
                 {
                     "GrantId",
                     "SecretHash",
                     "StoreCode",
                     "DeviceSystem",
                     "CreatedAtUtc",
                     "CreatedBy",
                     "Reason",
                     "ExpiresAtUtc",
                     "RevokedAtUtc",
                     "RevokedBy",
                     "RevokeReason",
                     "ConsumedAtUtc",
                     "ConsumedHardwareId",
                     "ConsumedDeviceCode",
                     "ConsumedDeviceRegistrationId",
                     "ConsumedAuthorizationHash",
                     "ConsumedDeviceSystem",
                     "ConsumptionKind",
                     "PreviousStoreCode",
                     "PreviousDeviceCode",
                     "RowVersion",
                 })
        {
            Assert.Contains($"N'{columnName}'", sql, StringComparison.Ordinal);
        }

        Assert.Contains("COL_LENGTH(N'dbo.POSM_DeviceActivationGrant', N'ActivationCode')", sql, StringComparison.Ordinal);
        Assert.Contains("sys.key_constraints", sql, StringComparison.Ordinal);
        Assert.Contains("[key_ordinal] = 1", sql, StringComparison.Ordinal);
        Assert.Contains("UX_POSM_DeviceActivationGrant_SecretHash", sql, StringComparison.Ordinal);
        Assert.Contains("IX_POSM_DeviceActivationGrant_StoreCreated", sql, StringComparison.Ordinal);
        Assert.Contains("IX_POSM_DeviceActivationGrant_Usable", sql, StringComparison.Ordinal);
        Assert.Contains("indexInfo.[is_unique]", sql, StringComparison.Ordinal);
        Assert.Contains("indexInfo.[is_disabled]", sql, StringComparison.Ordinal);
        Assert.Contains("indexInfo.[is_hypothetical]", sql, StringComparison.Ordinal);
        Assert.Contains("indexInfo.[has_filter]", sql, StringComparison.Ordinal);
        Assert.Contains("indexInfo.[filter_definition]", sql, StringComparison.Ordinal);
        Assert.Contains("[is_included_column]", sql, StringComparison.Ordinal);
        Assert.Contains("sys.check_constraints", sql, StringComparison.Ordinal);
        Assert.Contains("checkInfo.[is_not_trusted] = 0", sql, StringComparison.Ordinal);
        Assert.Contains("CK_POSM_DeviceActivationGrant_Expiry", sql, StringComparison.Ordinal);
        Assert.Contains("CK_POSM_DeviceActivationGrant_Revocation", sql, StringComparison.Ordinal);
        Assert.Contains("CK_POSM_DeviceActivationGrant_Consumption", sql, StringComparison.Ordinal);
        Assert.Contains("CK_POSM_DeviceActivationGrant_RevokedConsumedExclusive", sql, StringComparison.Ordinal);
        Assert.Contains(
            "AS expectedDefinition([ConstraintName], [NormalizedDefinition])",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "expectedDefinition.[NormalizedDefinition] = normalized.[NormalizedDefinition]",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("N'(EXPIRESATUTC>CREATEDATUTC)'", sql, StringComparison.Ordinal);
        Assert.Contains(
            "N'(REVOKEDATUTCISNULLANDREVOKEDBYISNULLANDREVOKEREASONISNULLORREVOKEDATUTCISNOTNULLANDREVOKEDBYISNOTNULLANDREVOKEREASONISNOTNULL)'",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("CONSUMPTIONKINDISNOTNULL", sql, StringComparison.Ordinal);
        Assert.Contains("CONSUMPTIONKIND=''INITIAL''", sql, StringComparison.Ordinal);
        Assert.Contains("CONSUMPTIONKIND=''REBIND''", sql, StringComparison.Ordinal);
        Assert.Contains(
            "N'(REVOKEDATUTCISNULLORCONSUMEDATUTCISNULL)'",
            sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PATINDEX", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OR1=1", sql, StringComparison.OrdinalIgnoreCase);

        foreach (var forbidden in new[]
                 {
                     "CREATE ",
                     "ALTER ",
                     "DROP ",
                     "TRUNCATE ",
                     "INSERT ",
                     "UPDATE ",
                     "DELETE ",
                     "MERGE ",
                     "BEGIN TRAN",
                     "COMMIT",
                     "ROLLBACK",
                     "SAVE TRAN",
                     "sp_getapplock",
                     "SCHEMA_PROBE",
                 })
        {
            Assert.DoesNotContain(forbidden, sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Schema_UsesRolledBackSemanticProbesToRejectWeakTrustedChecks()
    {
        var sql = DeviceActivationCodeSchema.EnsureSql;

        Assert.Contains("SET XACT_ABORT OFF;", sql, StringComparison.Ordinal);
        Assert.Contains("SAVE TRANSACTION DeviceActivationCheckProbe;", sql, StringComparison.Ordinal);
        Assert.Contains("ROLLBACK TRANSACTION DeviceActivationCheckProbe;", sql, StringComparison.Ordinal);
        Assert.Contains("@DeviceActivationProbeCases TABLE", sql, StringComparison.Ordinal);
        Assert.Contains("@DeviceActivationProbeCase <= 29", sql, StringComparison.Ordinal);
        Assert.Contains("AND [ConsumptionKind] IS NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("@DeviceActivationExpectedConstraint", sql, StringComparison.Ordinal);
        Assert.Contains("(1,  N'CK_POSM_DeviceActivationGrant_Expiry'", sql, StringComparison.Ordinal);
        Assert.Contains("(7,  N'CK_POSM_DeviceActivationGrant_Revocation'", sql, StringComparison.Ordinal);
        Assert.Contains("(8,  N'CK_POSM_DeviceActivationGrant_Consumption'", sql, StringComparison.Ordinal);
        Assert.Contains("(14, N'CK_POSM_DeviceActivationGrant_Consumption'", sql, StringComparison.Ordinal);
        Assert.Contains("(21, N'CK_POSM_DeviceActivationGrant_Consumption'", sql, StringComparison.Ordinal);
        Assert.Contains("(29, N'CK_POSM_DeviceActivationGrant_RevokedConsumedExclusive'", sql, StringComparison.Ordinal);
        Assert.Contains("ERROR_MESSAGE()", sql, StringComparison.Ordinal);
        Assert.Contains("@DeviceActivationExpectedConstraintPattern", sql, StringComparison.Ordinal);
        Assert.Contains("REPLACE(@DeviceActivationExpectedConstraintPattern, N'[', N'[[]')", sql, StringComparison.Ordinal);
        Assert.Contains("REPLACE(@DeviceActivationExpectedConstraintPattern, N'%', N'[%]')", sql, StringComparison.Ordinal);
        Assert.Contains("REPLACE(@DeviceActivationExpectedConstraintPattern, N'_', N'[_]')", sql, StringComparison.Ordinal);
        Assert.Contains("N'%[^0-9A-Za-z_]' + @DeviceActivationExpectedConstraintPattern + N'[^0-9A-Za-z_]%'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("N'%[^0-9A-Za-z_]' + @DeviceActivationExpectedConstraint + N'[^0-9A-Za-z_]%'", sql, StringComparison.Ordinal);
        Assert.Contains("N' ' + @DeviceActivationProbeCaughtErrorMessage + N' '", sql, StringComparison.Ordinal);
        Assert.Contains("@DeviceActivationProbeUnexpectedErrorNumber", sql, StringComparison.Ordinal);
        Assert.Contains("HBPOS_SCHEMA_PROBE", sql, StringComparison.Ordinal);
        Assert.Contains("'Other'", sql, StringComparison.Ordinal);
        Assert.Contains("THROW 51013", sql, StringComparison.Ordinal);
        Assert.Contains("@DeviceActivationPositiveProbeCases TABLE", sql, StringComparison.Ordinal);
        Assert.Contains("@DeviceActivationPositiveProbeCase <= 4", sql, StringComparison.Ordinal);
        Assert.Contains("(1, 0, NULL,      0)", sql, StringComparison.Ordinal);
        Assert.Contains("(2, 1, NULL,      0)", sql, StringComparison.Ordinal);
        Assert.Contains("(3, 0, 'Initial', 0)", sql, StringComparison.Ordinal);
        Assert.Contains("(4, 0, 'Rebind',  1)", sql, StringComparison.Ordinal);
        Assert.Contains("SAVE TRANSACTION DeviceActivationPositiveProbe", sql, StringComparison.Ordinal);
        Assert.Contains("ROLLBACK TRANSACTION DeviceActivationPositiveProbe", sql, StringComparison.Ordinal);
        Assert.Contains("THROW 51014", sql, StringComparison.Ordinal);
        Assert.Contains("SET XACT_ABORT ON;", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ConsumptionConstraintMigrationScript_IsGuardedAndOnlyReplacesExactConstraint()
    {
        var sql = File.ReadAllText(FindMigrationScript());

        Assert.Contains("HBPOS:Schema:DeviceActivationGrant", sql, StringComparison.Ordinal);
        Assert.Contains("@LockOwner = N'Transaction'", sql, StringComparison.Ordinal);
        Assert.Contains("CASE WHEN", sql, StringComparison.Ordinal);
        Assert.Contains("ELSE 1 END", sql, StringComparison.Ordinal);
        Assert.Contains("AND [ConsumptionKind] IS NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains(
            "DROP CONSTRAINT [CK_POSM_DeviceActivationGrant_Consumption]",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "WITH CHECK ADD CONSTRAINT [CK_POSM_DeviceActivationGrant_Consumption]",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("SAVE TRANSACTION DeviceActivationConstraintProbe", sql, StringComparison.Ordinal);
        Assert.Contains("ERROR_NUMBER()", sql, StringComparison.Ordinal);
        Assert.Contains("= 547", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DELETE FROM [dbo].[POSM_DeviceActivationGrant]",
            sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "UPDATE [dbo].[POSM_DeviceActivationGrant]",
            sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAction_SetsNoStoreBeforeReturningTheOneTimeSecret()
    {
        var controller = new DeviceActivationCodesController(null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        await Assert.ThrowsAsync<NullReferenceException>(() => controller.Create(
            new DeviceActivationCodeCreateRequestDto("S001", "Windows", 1440, "New counter")));

        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task CreateAndList_UseWhitelistedTtlAndNeverReadBackTheOneTimeSecret()
    {
        using var fixture = new ManagementFixture();
        fixture.SeedStore("S001", "Store 1");

        var created = await fixture.Service.CreateAsync(
            new DeviceActivationCodeCreateRequestDto("S001", "Windows", 30, "New counter"),
            "ADMIN");

        Assert.True(created.Success);
        Assert.NotNull(created.Data);
        Assert.True(DeviceActivationCodeCodec.TryParse(created.Data.ActivationCode, out _));
        Assert.Equal(fixture.UtcNow.AddMinutes(30), created.Data.Grant.ExpiresAtUtc);
        Assert.Equal(DateTimeKind.Utc, created.Data.Grant.ExpiresAtUtc.Kind);
        Assert.Contains(
            "\"expiresAtUtc\":\"2026-08-27T01:32:03Z\"",
            JsonSerializer.Serialize(created, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            StringComparison.Ordinal);

        fixture.ExecutedPosmSql.Clear();
        var listed = await fixture.Service.ListAsync(1, 30, "S001", "Windows", "Available");
        var page = Assert.IsType<PagedResult<DeviceActivationCodeGrantDto>>(listed.Data);
        var item = Assert.Single(Assert.IsType<List<DeviceActivationCodeGrantDto>>(page.Items));
        var listJson = JsonSerializer.Serialize(listed, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain(created.Data.ActivationCode, listJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secretHash", listJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DateTimeKind.Utc, item.CreatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, item.ExpiresAtUtc.Kind);
        Assert.Contains("Z\"", listJson, StringComparison.Ordinal);
        AssertSecretColumnsWereNotSelected(fixture.ExecutedPosmSql);
    }

    [Fact]
    public async Task ListAndManageableStores_EnforceServerSideStoreScope()
    {
        using var fixture = new ManagementFixture();
        fixture.SeedStore("S001", "Store 1");
        fixture.SeedStore("S002", "Store 2");
        await fixture.Service.CreateAsync(
            new DeviceActivationCodeCreateRequestDto("S001", "Windows", 120, "Counter 1"),
            "ADMIN");
        await fixture.Service.CreateAsync(
            new DeviceActivationCodeCreateRequestDto("S002", "Android", 120, "Counter 2"),
            "ADMIN");
        fixture.Scope.Scope = AllowedScope(isAdmin: false, "S001");

        var list = await fixture.Service.ListAsync(1, 30, null, null, null);
        var denied = await fixture.Service.ListAsync(1, 30, "S002", null, null);
        var stores = await fixture.Service.GetManageableStoresAsync();

        Assert.Equal(
            "S001",
            Assert.Single(Assert.IsType<List<DeviceActivationCodeGrantDto>>(list.Data!.Items)).StoreCode);
        Assert.False(denied.Success);
        Assert.Equal("DEVICE_ACTIVATION_STORE_FORBIDDEN", denied.ErrorCode);
        Assert.Equal("S001", Assert.Single(stores.Data!).StoreCode);
    }

    [Fact]
    public async Task CreateAndRevoke_EnforceServerSideStoreScopeWithoutMutatingGrant()
    {
        using var fixture = new ManagementFixture();
        fixture.SeedStore("S001", "Store 1");
        fixture.SeedStore("S002", "Store 2");
        var target = await fixture.Service.CreateAsync(
            new DeviceActivationCodeCreateRequestDto("S002", "Windows", 120, "Target counter"),
            "ADMIN");
        fixture.Scope.Scope = AllowedScope(isAdmin: false, "S001");

        var createDenied = await fixture.Service.CreateAsync(
            new DeviceActivationCodeCreateRequestDto("S002", "Android", 30, "Out of scope"),
            "DELEGATED");
        var revokeDenied = await fixture.Service.RevokeAsync(
            target.Data!.Grant.GrantId,
            new DeviceActivationCodeRevokeRequestDto("Out of scope"),
            "DELEGATED");

        Assert.False(createDenied.Success);
        Assert.Equal("DEVICE_ACTIVATION_STORE_FORBIDDEN", createDenied.ErrorCode);
        Assert.False(revokeDenied.Success);
        Assert.Equal("DEVICE_ACTIVATION_STORE_FORBIDDEN", revokeDenied.ErrorCode);

        fixture.Scope.Scope = AllowedScope();
        var unchanged = await fixture.Service.ListAsync(1, 30, "S002", null, "Available");
        Assert.Equal(
            target.Data.Grant.GrantId,
            Assert.Single(Assert.IsType<List<DeviceActivationCodeGrantDto>>(unchanged.Data!.Items)).GrantId);
    }

    [Fact]
    public async Task Revoke_IsAtomicAndNeverReturnsOrReconstructsTheSecret()
    {
        using var fixture = new ManagementFixture();
        fixture.SeedStore("S001", "Store 1");
        var created = await fixture.Service.CreateAsync(
            new DeviceActivationCodeCreateRequestDto("S001", "iPadOS", 1440, "Review counter"),
            "ADMIN");

        fixture.ExecutedPosmSql.Clear();
        var revoked = await fixture.Service.RevokeAsync(
            created.Data!.Grant.GrantId,
            new DeviceActivationCodeRevokeRequestDto("Device retired"),
            "ADMIN");
        var repeated = await fixture.Service.RevokeAsync(
            created.Data.Grant.GrantId,
            new DeviceActivationCodeRevokeRequestDto("Repeat"),
            "ADMIN");
        var revokedJson = JsonSerializer.Serialize(revoked, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.True(revoked.Success);
        Assert.Equal("Revoked", revoked.Data!.Status);
        Assert.Equal(DateTimeKind.Utc, revoked.Data.RevokedAtUtc!.Value.Kind);
        Assert.False(repeated.Success);
        Assert.Equal("DEVICE_ACTIVATION_NOT_REVOCABLE", repeated.ErrorCode);
        Assert.DoesNotContain(created.Data.ActivationCode, revokedJson, StringComparison.Ordinal);
        Assert.Contains(
            "\"revokedAtUtc\":\"2026-08-27T01:02:03Z\"",
            revokedJson,
            StringComparison.Ordinal);
        AssertSecretColumnsWereNotSelected(fixture.ExecutedPosmSql);
    }

    [Theory]
    [InlineData(29)]
    [InlineData(31)]
    [InlineData(119)]
    [InlineData(121)]
    [InlineData(1439)]
    [InlineData(1441)]
    public async Task Create_RejectsNonWhitelistedTtlBeforeStoreOrDatabaseAccess(int minutes)
    {
        using var fixture = new ManagementFixture(createTables: false);

        var response = await fixture.Service.CreateAsync(
            new DeviceActivationCodeCreateRequestDto("S001", "Windows", minutes, "Counter"),
            "ADMIN");

        Assert.False(response.Success);
        Assert.Equal("DEVICE_ACTIVATION_VALIDITY_INVALID", response.ErrorCode);
        Assert.Equal(0, fixture.Scope.CanAccessCalls);
    }

    [Fact]
    public async Task CreateAndRevoke_RejectReservedActivationCodeInReasonBeforeDatabaseAccess()
    {
        using var fixture = new ManagementFixture(createTables: false);
        var activationCode = DeviceActivationCodeCodec.Create().ActivationCode;

        var create = await fixture.Service.CreateAsync(
            new DeviceActivationCodeCreateRequestDto(
                "S001",
                "Windows",
                30,
                $"Counter {activationCode}"),
            "ADMIN");
        var revoke = await fixture.Service.RevokeAsync(
            Guid.NewGuid(),
            new DeviceActivationCodeRevokeRequestDto($"Retire {activationCode}"),
            "ADMIN");

        Assert.False(create.Success);
        Assert.Equal("DEVICE_ACTIVATION_REASON_INVALID", create.ErrorCode);
        Assert.False(revoke.Success);
        Assert.Equal("DEVICE_ACTIVATION_REASON_INVALID", revoke.ErrorCode);
        Assert.Equal(0, fixture.Scope.CanAccessCalls);
    }

    [Fact]
    public async Task List_DefensivelyRedactsReservedCodesFromCorruptHistoricalMetadata()
    {
        using var fixture = new ManagementFixture();
        fixture.SeedStore("S001", "Store 1");
        var activationCode = DeviceActivationCodeCodec.Create().ActivationCode;
        fixture.SeedGrantWithMetadata(activationCode);

        var listed = await fixture.Service.ListAsync(1, 30, null, null, null);
        var json = JsonSerializer.Serialize(listed, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.True(listed.Success);
        Assert.DoesNotContain(activationCode, json, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManageableStores_DefensivelyRedactsReservedCodesFromStoreNames()
    {
        using var fixture = new ManagementFixture();
        var activationCode = DeviceActivationCodeCodec.Create().ActivationCode;
        fixture.SeedStore("S001", $"Store {activationCode}");

        var stores = await fixture.Service.GetManageableStoresAsync();

        var store = Assert.Single(stores.Data!);
        Assert.Equal("[REDACTED]", store.StoreName);
    }

    private static CurrentUserManageableStoreScope AllowedScope(
        bool isAdmin = true,
        params string[] storeCodes) =>
        new()
        {
            IsAllowed = true,
            IsAuthenticated = true,
            IsAdmin = isAdmin,
            StoreCodes = storeCodes,
        };

    private static string FindMigrationScript(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourceFilePath)!,
            "..",
            "SqlScripts",
            "MigrateDeviceActivationGrantConsumptionConstraint.sql"));

    private static void AssertSecretColumnsWereNotSelected(IEnumerable<string> statements)
    {
        var selects = statements.Where(statement =>
            statement.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase));
        foreach (var select in selects)
        {
            Assert.DoesNotContain("SecretHash", select, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ConsumedAuthorizationHash", select, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class ManagementFixture : IDisposable
    {
        private readonly string _posmPath = Path.Combine(
            Path.GetTempPath(), $"hb-activation-posm-{Guid.NewGuid():N}.db");
        private readonly string _mainPath = Path.Combine(
            Path.GetTempPath(), $"hb-activation-main-{Guid.NewGuid():N}.db");

        public DateTime UtcNow { get; } =
            new(2026, 8, 27, 1, 2, 3, DateTimeKind.Utc);

        public SqlSugarClient PosmDb { get; }
        public SqlSugarClient MainDb { get; }
        public List<string> ExecutedPosmSql { get; } = [];
        public FakeStoreScope Scope { get; } = new() { Scope = AllowedScope() };
        public DeviceActivationCodeManagementService Service { get; }

        public ManagementFixture(bool createTables = true)
        {
            PosmDb = CreateSqlite(_posmPath);
            MainDb = CreateSqlite(_mainPath);
            PosmDb.Aop.OnLogExecuting = (sql, _) => ExecutedPosmSql.Add(sql);
            if (createTables)
            {
                CreateTables();
            }
            Service = new DeviceActivationCodeManagementService(
                PosmDb,
                MainDb,
                Scope,
                NullLogger<DeviceActivationCodeManagementService>.Instance,
                new FixedTimeProvider(UtcNow));
        }

        public void SeedStore(string code, string name) =>
            MainDb.Ado.ExecuteCommand(
                "INSERT INTO [Store] ([StoreCode], [StoreName], [IsActive], [IsDeleted]) VALUES (@Code, @Name, 1, 0);",
                new SugarParameter("@Code", code),
                new SugarParameter("@Name", name));

        public void SeedGrantWithMetadata(string activationCode)
        {
            var material = DeviceActivationCodeCodec.Create();
            PosmDb.Ado.ExecuteCommand(
                """
                INSERT INTO [POSM_DeviceActivationGrant]
                    ([GrantId], [SecretHash], [StoreCode], [DeviceSystem], [CreatedAtUtc], [CreatedBy],
                     [Reason], [ExpiresAtUtc], [ConsumedHardwareId], [RowVersion])
                VALUES
                    (@GrantId, @SecretHash, 'S001', 'Windows', @CreatedAtUtc, @CreatedBy,
                     @Reason, @ExpiresAtUtc, @HardwareId, X'');
                """,
                new SugarParameter("@GrantId", material.GrantId),
                new SugarParameter("@SecretHash", material.SecretHash),
                new SugarParameter("@CreatedAtUtc", UtcNow),
                new SugarParameter("@CreatedBy", $"Actor {activationCode}"),
                new SugarParameter("@Reason", $"Reason {activationCode}"),
                new SugarParameter("@ExpiresAtUtc", UtcNow.AddMinutes(30)),
                new SugarParameter("@HardwareId", $"Hardware {activationCode}"));
        }

        public void Dispose()
        {
            PosmDb.Dispose();
            MainDb.Dispose();
            SqliteTempFileCleanup.DeleteIfExists(_posmPath);
            SqliteTempFileCleanup.DeleteIfExists(_mainPath);
        }

        private void CreateTables()
        {
            MainDb.Ado.ExecuteCommand(
                """
                CREATE TABLE [Store]
                (
                    [StoreCode] TEXT NOT NULL PRIMARY KEY,
                    [StoreName] TEXT NOT NULL,
                    [IsActive] INTEGER NOT NULL,
                    [IsDeleted] INTEGER NOT NULL
                );
                """);
            PosmDb.Ado.ExecuteCommand(
                """
                CREATE TABLE [POSM_DeviceActivationGrant]
                (
                    [GrantId] TEXT NOT NULL PRIMARY KEY,
                    [SecretHash] BLOB NOT NULL,
                    [StoreCode] TEXT NOT NULL,
                    [DeviceSystem] TEXT NOT NULL,
                    [CreatedAtUtc] TEXT NOT NULL,
                    [CreatedBy] TEXT NOT NULL,
                    [Reason] TEXT NOT NULL,
                    [ExpiresAtUtc] TEXT NOT NULL,
                    [RevokedAtUtc] TEXT NULL,
                    [RevokedBy] TEXT NULL,
                    [RevokeReason] TEXT NULL,
                    [ConsumedAtUtc] TEXT NULL,
                    [ConsumedHardwareId] TEXT NULL,
                    [ConsumedDeviceCode] TEXT NULL,
                    [ConsumedDeviceRegistrationId] INTEGER NULL,
                    [ConsumedAuthorizationHash] BLOB NULL,
                    [ConsumedDeviceSystem] TEXT NULL,
                    [ConsumptionKind] TEXT NULL,
                    [PreviousStoreCode] TEXT NULL,
                    [PreviousDeviceCode] TEXT NULL,
                    [RowVersion] BLOB NOT NULL DEFAULT X''
                );
                """);
        }

        private static SqlSugarClient CreateSqlite(string path) =>
            new(new ConnectionConfig
            {
                ConnectionString = $"Data Source={path}",
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            });
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed class FakeStoreScope : ICurrentUserManageableStoreScopeService
    {
        public required CurrentUserManageableStoreScope Scope { get; set; }
        public int CanAccessCalls { get; private set; }

        public Task<CurrentUserManageableStoreScope> GetScopeAsync() => Task.FromResult(Scope);

        public Task<IReadOnlyList<string>> GetAccessibleStoreCodesAsync() =>
            Task.FromResult(Scope.StoreCodes);

        public Task<bool> CanAccessStoreCodeAsync(string storeCode)
        {
            CanAccessCalls++;
            return Task.FromResult(Scope.CanAccessStoreCode(storeCode));
        }

        public Task<bool> CanAccessOrderAsync(string orderGuid) => Task.FromResult(false);
        public Task<bool> CanManageStoreAsync(string storeGuid) => Task.FromResult(false);
        public Task<bool> CanManageUserAsync(string userGuid) => Task.FromResult(false);
    }
}

internal static class TestClaimsPrincipal
{
    internal static System.Security.Claims.ClaimsPrincipal WithPermission(string permission) =>
        new(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim("permission", permission)],
            "Test"));
}
