using Hbpos.Api.Data;
using SqlSugar;

namespace Hbpos.Api.Services;

/// <summary>
/// 员工个人界面偏好的读写：主库 dbo.UserPreference（迁移 20261029.004-user-preference 由主 API 的 --schema=migrate 建表）。
/// 本服务启动时不代建表；表缺失或数据库异常时抛出原始异常，由控制器按「暂不可用」应答，收银端沿用本机主题。
/// </summary>
public interface ICashierPreferenceStore
{
    Task<string?> GetAsync(string userGuid, string preferenceKey, CancellationToken cancellationToken);

    Task SetAsync(
        string userGuid,
        string preferenceKey,
        string preferenceValue,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken);
}

public sealed class SqlSugarCashierPreferenceStore(HbposSqlSugarContext dbContext) : ICashierPreferenceStore
{
    internal const string SelectSql = """
        SELECT TOP (1) [PreferenceValue]
        FROM [dbo].[UserPreference]
        WHERE [UserGUID] = @UserGuid AND [PreferenceKey] = @PreferenceKey;
        """;

    // UPDLOCK + SERIALIZABLE 锁住 (员工, 偏好键) 的键范围：同一员工在两台收银机上同时改配色也不会双插主键冲突。
    internal const string UpsertSql = """
        SET NOCOUNT ON;
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;
        UPDATE [dbo].[UserPreference] WITH (UPDLOCK, SERIALIZABLE)
        SET [PreferenceValue] = @PreferenceValue,
            [UpdatedAtUtc] = @UpdatedAtUtc
        WHERE [UserGUID] = @UserGuid AND [PreferenceKey] = @PreferenceKey;
        IF @@ROWCOUNT = 0
            INSERT INTO [dbo].[UserPreference] ([UserGUID], [PreferenceKey], [PreferenceValue], [UpdatedAtUtc])
            VALUES (@UserGuid, @PreferenceKey, @PreferenceValue, @UpdatedAtUtc);
        COMMIT TRANSACTION;
        """;

    public async Task<string?> GetAsync(string userGuid, string preferenceKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await dbContext.MainDb.Ado.GetScalarAsync(
            SelectSql,
            new SugarParameter("@UserGuid", userGuid, System.Data.DbType.String),
            new SugarParameter("@PreferenceKey", preferenceKey, System.Data.DbType.String));
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    public async Task SetAsync(
        string userGuid,
        string preferenceKey,
        string preferenceValue,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await dbContext.MainDb.Ado.ExecuteCommandAsync(
            UpsertSql,
            new SugarParameter("@UserGuid", userGuid, System.Data.DbType.String),
            new SugarParameter("@PreferenceKey", preferenceKey, System.Data.DbType.String),
            new SugarParameter("@PreferenceValue", preferenceValue, System.Data.DbType.String),
            new SugarParameter("@UpdatedAtUtc", updatedAtUtc) { DbType = System.Data.DbType.DateTime2 });
    }
}
