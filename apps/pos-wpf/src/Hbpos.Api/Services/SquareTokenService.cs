using System.Security.Claims;
using Hbpos.Api.Data;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.Square;
using SqlSugar;

namespace Hbpos.Api.Services;

public interface ISquareTokenService
{
    Task<SquareTokenResponse?> GetActiveTokenAsync(
        string environment,
        CancellationToken cancellationToken);
}

/// <summary>
/// 当前请求所属门店。设备令牌自带门店声明，Square 接口据此挑选门店专用 token；
/// 没有门店上下文（如 webhook）时返回 null，只会命中共用 token。
/// </summary>
public interface ISquareStoreScope
{
    string? StoreCode { get; }
}

public sealed class HttpContextSquareStoreScope(IHttpContextAccessor httpContextAccessor) : ISquareStoreScope
{
    public string? StoreCode =>
        SquareTokenService.NormalizeStoreCode(
            httpContextAccessor.HttpContext?.User.FindFirstValue(DeviceAuthConstants.StoreCodeClaim));
}

public sealed class SquareTokenService(
    ISquareTokenRepository repository,
    ISquareStoreScope storeScope) : ISquareTokenService
{
    public async Task<SquareTokenResponse?> GetActiveTokenAsync(
        string environment,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironment = NormalizeEnvironment(environment);
        if (normalizedEnvironment is null)
        {
            return null;
        }

        // 门店专用 token 优先，没有时回落到全部门店共用的 token（由仓储一次查询完成）。
        var token = await repository.GetActiveTokenAsync(
            normalizedEnvironment,
            storeScope.StoreCode,
            cancellationToken);
        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            // 空 token 等同于未配置，不把 AccessToken="" 的内部对象继续向后传。
            return null;
        }

        return new SquareTokenResponse(
            normalizedEnvironment,
            token.AccessToken,
            new DateTimeOffset(DateTime.SpecifyKind(token.UpdatedAt ?? DateTime.UtcNow, DateTimeKind.Utc)));
    }

    internal static string? NormalizeStoreCode(string? storeCode)
    {
        var trimmed = storeCode?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    internal static string? NormalizeEnvironment(string? environment)
    {
        return (environment ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "PRODUCTION" => "Production",
            "SANDBOX" or "TEST" => "Sandbox",
            _ => null
        };
    }
}

public interface ISquareTokenRepository
{
    /// <summary>
    /// 读取生效 token：storeCode 有值时优先返回该门店专用行，没有则返回共用行（StoreCode 为 NULL）。
    /// </summary>
    Task<SquareTokenRecord?> GetActiveTokenAsync(
        string environment,
        string? storeCode,
        CancellationToken cancellationToken);
}

public sealed class SqlSugarSquareTokenRepository(HbposSqlSugarContext dbContext) : ISquareTokenRepository
{
    public async Task<SquareTokenRecord?> GetActiveTokenAsync(
        string environment,
        string? storeCode,
        CancellationToken cancellationToken)
    {
        // @StoreCode 为 NULL 时 [StoreCode] = NULL 恒不成立，只会匹配共用行；
        // 门店行排在共用行之前，所以有专用 token 就一定用专用的。
        const string sql = """
            SELECT TOP 1
                [Id],
                [Environment],
                [StoreCode],
                [AccessToken],
                [IsEnabled],
                [UpdatedAt],
                [UpdatedBy]
            FROM [dbo].[POSM_SquareToken]
            WHERE [Environment] = @Environment
              AND [IsEnabled] = 1
              AND NULLIF(LTRIM(RTRIM([AccessToken])), '') IS NOT NULL
              AND ([StoreCode] = @StoreCode OR [StoreCode] IS NULL)
            ORDER BY CASE WHEN [StoreCode] IS NULL THEN 1 ELSE 0 END, [UpdatedAt] DESC, [Id] DESC;
            """;

        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<SquareTokenRecord>(
            sql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode, System.Data.DbType.String));
    }
}

public sealed class SquareTokenRecord
{
    public long Id { get; set; }

    public string? Environment { get; set; }

    // NULL = 全部门店共用。
    public string? StoreCode { get; set; }

    public string? AccessToken { get; set; }

    public bool IsEnabled { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public string? UpdatedBy { get; set; }
}
