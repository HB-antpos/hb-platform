using BlazorApp.Api.Services.React;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class PaymentTerminalSettingsSchemaMigratorTests
{
    [Fact]
    public void Schema_lock_matches_pos_api_shared_linkly_lock()
    {
        var sql = PaymentTerminalSettingsSchemaMigrator.SchemaLockSql;

        Assert.Contains("sys.sp_getapplock", sql);
        Assert.Contains("Hbpos.LinklyCloud.Schema.v2", sql);
        Assert.Contains("@LockOwner = N'Transaction'", sql);
        Assert.Contains("@LockTimeout = 60000", sql);
    }

    [Fact]
    public void Scripts_ContainPaymentTerminalTablesAndEnvironmentConstraints()
    {
        var sql = string.Join("\n", PaymentTerminalSettingsSchemaMigrator.SqlScriptsForTests);

        Assert.Contains("POSM_SquareToken", sql);
        Assert.Contains("POSM_LinklyCloudCredential", sql);
        Assert.Contains("CK_POSM_SquareToken_Environment", sql);
        Assert.Contains("CK_POSM_LinklyCloudCredential_Environment", sql);
        Assert.Contains("N'Production'", sql);
        Assert.Contains("N'Sandbox'", sql);
    }

    [Fact]
    public void Scripts_KeepOneEnabledSquareTokenAndOneLinklyCredentialPerStoreEnvironment()
    {
        var sql = string.Join("\n", PaymentTerminalSettingsSchemaMigrator.SqlScriptsForTests);

        Assert.Contains("UX_POSM_SquareToken_Environment_Enabled", sql);
        Assert.Contains("WHERE [IsEnabled] = 1", sql);
        Assert.Contains("DF_POSM_LinklyCloudCredential_Environment", sql);
        Assert.Contains("DEFAULT (N'Production') FOR [Environment]", sql);
        Assert.Contains("UX_POSM_LinklyCloudCredential_StoreCode_Environment", sql);
        Assert.Contains("UNIQUE ([StoreCode], [Environment])", sql);
    }

    [Fact]
    public void Scripts_BackfillExistingLinklyEnvironmentBeforeAddingConstraints()
    {
        var sql = string.Join("\n", PaymentTerminalSettingsSchemaMigrator.SqlScriptsForTests);

        Assert.Contains("SET [Environment] = N'Production'", sql);
        Assert.Contains("ALTER COLUMN [Environment] NVARCHAR(32) NOT NULL", sql);
    }

    [Fact]
    public void Scripts_CreateIdempotentLinklyMultiTerminalSchemaWithoutCopyingLegacyCredentials()
    {
        var sql = string.Join("\n", PaymentTerminalSettingsSchemaMigrator.SqlScriptsForTests);

        Assert.Contains("POSM_LinklyCloudTerminal", sql);
        Assert.Contains("POSM_LinklyCloudDeviceSelection", sql);
        Assert.Contains("POSM_LinklyCloudConfigurationMode", sql);
        Assert.Contains("POSM_LinklyCloudBackendSession", sql);
        Assert.Contains("[TerminalId] UNIQUEIDENTIFIER NULL", sql);
        Assert.Contains("[ClientAcknowledgedAt] DATETIME2(7) NULL", sql);
        Assert.Contains("IX_POSM_LinklyCloudBackendSession_TerminalRecovery", sql);
        Assert.Contains("UX_POSM_LinklyCloudBackendSession_ActiveCloudTerminal", sql);
        Assert.Contains("IX_POSM_LinklyCloudBackendSession_DeviceRecovery", sql);
        // M35：回调按 (Environment, SessionId) 查会话；与 Hbpos.Api 初始化脚本同名，按名称幂等创建。
        Assert.Contains("[name] = N'IX_POSM_LinklyCloudBackendSession_EnvSession'", sql);
        Assert.Contains("ON [dbo].[POSM_LinklyCloudBackendSession] ([Environment], [SessionId])", sql);
        Assert.Contains("[PairingAttemptId] UNIQUEIDENTIFIER NULL", sql);
        Assert.Contains("[PairingLeaseExpiresAt] DATETIME2(7) NULL", sql);
        Assert.Contains("UX_POSM_LinklyCloudTerminal_Scope_LaneNo", sql);
        Assert.Contains("UX_POSM_LinklyCloudTerminal_Scope_Username", sql);
        Assert.Contains("UX_POSM_LinklyCloudTerminal_Scope_DisplayName", sql);
        Assert.Contains("UX_POSM_LinklyCloudDeviceSelection_Scope_Terminal", sql);
        Assert.Contains("GROUP BY [Environment], [StoreCode], [TerminalId]", sql);
        Assert.Contains("HAVING COUNT(*) > 1", sql);
        Assert.Contains("THROW 51004", sql);
        Assert.Contains("CK_POSM_LinklyCloudTerminal_PairingState", sql);
        Assert.Contains("CK_POSM_LinklyCloudConfigurationMode_Mode", sql);
        Assert.Contains("IF OBJECT_ID", sql);
        Assert.DoesNotContain("INSERT INTO [dbo].[POSM_LinklyCloudTerminal]", sql);
        Assert.DoesNotContain("FROM [dbo].[POSM_LinklyCloudCredential]", sql);
        Assert.DoesNotContain("DELETE FROM [dbo].[POSM_LinklyCloudDeviceSelection]", sql);
    }

    [Fact]
    public void Scripts_add_idempotent_legacy_pairing_lease_to_configuration_scope()
    {
        var sql = string.Join("\n", PaymentTerminalSettingsSchemaMigrator.SqlScriptsForTests);

        Assert.Contains("[LegacyPairingAttemptId] UNIQUEIDENTIFIER NULL", sql);
        Assert.Contains("[LegacyPairingLeaseExpiresAt] DATETIME2(7) NULL", sql);
        Assert.Contains(
            "COL_LENGTH(N'dbo.POSM_LinklyCloudConfigurationMode', N'LegacyPairingAttemptId') IS NULL",
            sql);
        Assert.Contains(
            "COL_LENGTH(N'dbo.POSM_LinklyCloudConfigurationMode', N'LegacyPairingLeaseExpiresAt') IS NULL",
            sql);
    }

    [Fact]
    public void Scripts_protect_new_terminal_credentials_and_require_reentry_for_legacy_rows()
    {
        var sql = string.Join("\n", PaymentTerminalSettingsSchemaMigrator.SqlScriptsForTests);

        Assert.Contains("[Password] NVARCHAR(2048) NOT NULL", sql);
        Assert.Contains("[Secret] NVARCHAR(2048) NULL", sql);
        Assert.Contains("[CredentialProtectionVersion] TINYINT NOT NULL", sql);
        Assert.Contains("DEFAULT (1)", sql);
        Assert.Contains("DEFAULT (0) WITH VALUES", sql);
        Assert.Contains("ALTER COLUMN [Password] NVARCHAR(2048) NOT NULL", sql);
        Assert.Contains("ALTER COLUMN [Secret] NVARCHAR(2048) NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudTerminal', N'Password') < 4096", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_LinklyCloudTerminal', N'Secret') < 4096", sql);
        Assert.Contains("CK_POSM_LinklyCloudTerminal_CredentialProtectionVersion", sql);
        Assert.DoesNotContain("UPDATE [dbo].[POSM_LinklyCloudTerminal] SET [Password]", sql);
        Assert.DoesNotContain("UPDATE [dbo].[POSM_LinklyCloudTerminal] SET [Secret]", sql);
    }

    [Fact]
    public void LinklyMultiTerminalMigration_only_contains_v2_objects()
    {
        var scripts = PaymentTerminalSettingsSchemaMigrator.LinklyMultiTerminalSqlScriptsForTests;
        var sql = string.Join("\n", scripts);
        var backendColumns = scripts
            .Select((script, index) => (script, index))
            .Single(item => item.script.Contains(
                "COL_LENGTH(N'dbo.POSM_LinklyCloudBackendSession', N'TerminalId')",
                StringComparison.Ordinal
            ));
        var backendIndexes = scripts
            .Select((script, index) => (script, index))
            .Single(item => item.script.Contains(
                "CREATE UNIQUE INDEX [UX_POSM_LinklyCloudBackendSession_ActiveCloudTerminal]",
                StringComparison.Ordinal
            ));
        var credentialProtectionColumn = scripts
            .Select((script, index) => (script, index))
            .Single(item => item.script.Contains(
                "ADD [CredentialProtectionVersion] TINYINT NOT NULL",
                StringComparison.Ordinal
            ));
        var credentialProtectionConstraint = scripts
            .Select((script, index) => (script, index))
            .Single(item => item.script.Contains(
                "ADD CONSTRAINT [CK_POSM_LinklyCloudTerminal_CredentialProtectionVersion]",
                StringComparison.Ordinal
            ));

        Assert.Contains("POSM_LinklyCloudBackendSession", sql);
        Assert.Contains("POSM_LinklyCloudTerminal", sql);
        Assert.Contains("POSM_LinklyCloudDeviceSelection", sql);
        Assert.Contains("POSM_LinklyCloudConfigurationMode", sql);
        Assert.DoesNotContain("POSM_SquareToken", sql);
        Assert.DoesNotContain("POSM_LinklyCloudCredential", sql);
        Assert.True(
            backendColumns.index < backendIndexes.index,
            "补列和引用新列的索引必须分成前后两个 SQL batch。"
        );
        Assert.DoesNotContain(
            "CREATE INDEX [IX_POSM_LinklyCloudBackendSession_TerminalRecovery]",
            backendColumns.script,
            StringComparison.Ordinal
        );
        Assert.True(
            credentialProtectionColumn.index < credentialProtectionConstraint.index,
            "凭据保护版本列和引用它的约束必须分成前后两个 SQL batch。"
        );
        Assert.DoesNotContain(
            "ADD CONSTRAINT [CK_POSM_LinklyCloudTerminal_CredentialProtectionVersion]",
            credentialProtectionColumn.script,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void LinklyMultiTerminalVerifySql_is_read_only_and_covers_required_signatures()
    {
        var sql = PaymentTerminalSettingsSchemaMigrator.LinklyMultiTerminalVerifySql;

        Assert.Contains("POSM_LinklyCloudBackendSession", sql);
        Assert.Contains("POSM_LinklyCloudTerminal", sql);
        Assert.Contains("POSM_LinklyCloudDeviceSelection", sql);
        Assert.Contains("POSM_LinklyCloudConfigurationMode", sql);
        Assert.Contains("TerminalId", sql);
        Assert.Contains("ClientAcknowledgedAt", sql);
        Assert.Contains("CredentialProtectionVersion", sql);
        Assert.Contains("PairingAttemptId", sql);
        Assert.Contains("LegacyPairingAttemptId", sql);
        Assert.Contains("UX_POSM_LinklyCloudTerminal_Scope_LaneNo", sql);
        Assert.Contains("UX_POSM_LinklyCloudDeviceSelection_Scope_Terminal", sql);
        Assert.Contains("IX_POSM_LinklyCloudBackendSession_TerminalRecovery", sql);
        Assert.Contains("UX_POSM_LinklyCloudBackendSession_ActiveCloudTerminal", sql);
        Assert.Contains("sys.check_constraints", sql);
        Assert.Contains("ck.definition", sql);
        Assert.Contains("in_form.remainder = REPLICATE(N',', required.value_count - 1)", sql);
        Assert.Contains("or_form.remainder = REPLICATE(N'or', required.value_count - 1)", sql);
        Assert.DoesNotContain("cleaned.definition", sql);
        Assert.Contains("fk.delete_referential_action = 0", sql);
        Assert.Contains("fk.update_referential_action = 0", sql);
        Assert.Contains("THROW 51600", sql);
        Assert.Contains("THROW 51616", sql);
        Assert.DoesNotContain("CREATE TABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT INTO", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE [", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", sql, StringComparison.OrdinalIgnoreCase);
    }
}
