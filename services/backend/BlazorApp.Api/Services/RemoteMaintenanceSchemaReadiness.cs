using BlazorApp.Api.Data;

namespace BlazorApp.Api.Services;

/// <summary>远程维护 schema 只读门禁；迁移由运维显式执行。</summary>
public sealed class RemoteMaintenanceSchemaReadiness(SqlSugarContext context, ILogger<RemoteMaintenanceSchemaReadiness> logger)
{
    private const string ReadinessSql = """
        SELECT CASE WHEN OBJECT_ID(N'dbo.HBweb_RemoteMaintenanceDevice', N'U') IS NOT NULL
          AND COL_LENGTH(N'dbo.HBweb_RemoteMaintenanceDevice', N'HardwareId') IS NOT NULL
          AND COL_LENGTH(N'dbo.HBweb_RemoteMaintenanceDevice', N'MonitorTokenHash') IS NOT NULL
          AND COL_LENGTH(N'dbo.HBweb_RemoteMaintenanceDevice', N'CommitResponseCiphertext') IS NOT NULL
          AND COL_LENGTH(N'dbo.HBweb_RemoteMaintenanceDevice', N'LastAcceptedSequence') IS NOT NULL
          AND COL_LENGTH(N'dbo.HBweb_RemoteMaintenanceDevice', N'LastAcceptedAtUtc') IS NOT NULL
          AND COL_LENGTH(N'dbo.HBweb_RemoteMaintenanceDevice', N'LastOperationId') IS NOT NULL
          AND COL_LENGTH(N'dbo.HBweb_RemoteMaintenanceDevice', N'ObservedRustDeskVersion') IS NOT NULL
          AND EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.HBweb_RemoteMaintenanceDevice') AND name = N'UX_HBweb_RemoteMaintenanceDevice_DeviceRegistrationId')
          THEN 1 ELSE 0 END;
        """;

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 此重载第二参数是 SQL 参数，不接受 CancellationToken；显式传空参数避免错误绑定。
            var ready = await context.Db.Ado.GetIntAsync(ReadinessSql, Array.Empty<SqlSugar.SugarParameter>());
            cancellationToken.ThrowIfCancellationRequested();
            return ready == 1;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "远程维护 schema 只读门禁失败");
            return false;
        }
    }
}
