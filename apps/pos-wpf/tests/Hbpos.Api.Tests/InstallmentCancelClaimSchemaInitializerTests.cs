using Hbpos.Api.Services;

namespace Hbpos.Api.Tests;

public sealed class InstallmentCancelClaimSchemaInitializerTests
{
    [Fact]
    public async Task Initializer_creates_an_independent_transactional_cancel_claim_schema()
    {
        var executor = new CapturingExecutor();
        var initializer = new SqlSugarInstallmentCancelClaimSchemaInitializer(executor);

        await initializer.InitializeAsync(CancellationToken.None);

        var sql = Assert.Single(executor.Commands);
        Assert.Contains("SET XACT_ABORT ON", sql);
        Assert.Contains("sys.sp_getapplock", sql);
        Assert.Contains("[dbo].[POSM_InstallmentCancelClaim]", sql);
        Assert.Contains("N'Prepared', N'RefundPending', N'Committed', N'Released', N'Declined', N'Unknown'", sql);
        Assert.Contains("WHERE [IsBlocking] = 1", sql);
        Assert.Contains("[CommitResponseJson] NVARCHAR(MAX) NULL", sql);
        Assert.Contains("[LastRecoveryCashierId] NVARCHAR(50) NULL", sql);
        Assert.Contains("[LastRecoveryCashierUserGuid] NVARCHAR(50) NULL", sql);
        Assert.Contains("[RecoveredAtUtc] DATETIME2(7) NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_InstallmentCancelClaim', N'CommitResponseJson') IS NULL", sql);
        Assert.Contains("EXEC(N'UPDATE [dbo].[POSM_InstallmentCancelClaim]", sql);
        Assert.Contains("EXEC(N'ALTER TABLE [dbo].[POSM_InstallmentCancelClaim]", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_InstallmentCancelClaim', N'LastRecoveryCashierId') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_InstallmentCancelClaim', N'LastRecoveryCashierName') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_InstallmentCancelClaim', N'LastRecoveryCashierUserGuid') IS NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_InstallmentCancelClaim', N'RecoveredAtUtc') IS NULL", sql);
        Assert.Contains("ADD [CommitResponseJson] NVARCHAR(MAX) NULL", sql);
        Assert.Contains("ADD [LastRecoveryCashierId] NVARCHAR(50) NULL", sql);
        Assert.Contains("ADD [LastRecoveryCashierName] NVARCHAR(100) NULL", sql);
        Assert.Contains("ADD [LastRecoveryCashierUserGuid] NVARCHAR(50) NULL", sql);
        Assert.Contains("ADD [RecoveredAtUtc] DATETIME2(7) NULL", sql);
        Assert.Contains("[RefundMode] NVARCHAR(20) NULL", sql);
        Assert.Contains("COL_LENGTH(N'dbo.POSM_InstallmentCancelClaim', N'RefundMode') IS NULL", sql);
        Assert.Contains("ADD [RefundMode] NVARCHAR(20) NULL", sql);
        Assert.Contains("CK_POSM_InstallmentCancelClaim_RefundMode", sql);
        Assert.Contains("COMMIT TRANSACTION", sql);
    }

    private sealed class CapturingExecutor : IInstallmentCancelClaimSchemaSqlExecutor
    {
        public List<string> Commands { get; } = [];

        public Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
        {
            Commands.Add(sql);
            return Task.CompletedTask;
        }
    }
}

internal sealed class TestNoOpInstallmentCancelClaimSchemaInitializer
    : IInstallmentCancelClaimSchemaInitializer
{
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
