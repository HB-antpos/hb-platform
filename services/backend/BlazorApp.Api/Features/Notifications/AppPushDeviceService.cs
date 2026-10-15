using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BlazorApp.Api.Data;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.Notifications;

/// <summary>注册（或刷新）当前账号这台设备的 Expo 推送令牌。</summary>
public sealed class PushDeviceRegisterRequestDto
{
    /// <summary>形如 <c>ExponentPushToken[xxxxxxxxxxxxxxxxxxxxxx]</c> 或 <c>ExpoPushToken[...]</c>，整串不超过 200 个字符。</summary>
    public string? ExpoPushToken { get; set; }

    /// <summary><c>ios</c> 或 <c>android</c>。</summary>
    public string? Platform { get; set; }

    public string? AppVersion { get; set; }

    public string? RuntimeVersion { get; set; }
}

public sealed class PushDeviceRegisterResultDto
{
    public string PushDeviceId { get; set; } = string.Empty;
}

/// <summary>撤销当前账号名下的某个推送令牌（退出登录时调用）。令牌只放请求体，不放 URL。</summary>
public sealed class PushDeviceRevokeRequestDto
{
    public string? ExpoPushToken { get; set; }
}

public sealed class PushDeviceRevokeResultDto
{
    /// <summary>本次实际撤销的活动令牌数；令牌不存在、已撤销或属于别的账号时为 0（接口仍返回成功，幂等）。</summary>
    public int Revoked { get; set; }
}

public interface IAppPushDeviceService
{
    /// <summary>
    /// 注册 / 刷新推送令牌（upsert）。同一令牌已被别的账号占用时转给当前账号（旧关联软撤销 <c>reassigned</c>）——
    /// 门店手持会换人登录，上一个人的通知不能再发到下一个人手里。输入不合法抛 <see cref="ArgumentException"/>（控制器转 400）。
    /// </summary>
    Task<PushDeviceRegisterResultDto> RegisterAsync(
        string userGuid,
        PushDeviceRegisterRequestDto? request,
        CancellationToken cancellationToken = default
    );

    /// <summary>撤销属于当前账号的活动令牌（<c>logout</c>）；幂等，别人的令牌不动。</summary>
    Task<PushDeviceRevokeResultDto> RevokeAsync(
        string userGuid,
        PushDeviceRevokeRequestDto? request,
        CancellationToken cancellationToken = default
    );
}

internal sealed partial class AppPushDeviceService(
    SqlSugarContext context,
    TimeProvider timeProvider,
    ILogger<AppPushDeviceService> logger
) : IAppPushDeviceService
{
    /// <summary>令牌整串（含 <c>ExponentPushToken[</c> 前缀与 <c>]</c>）的长度上限，与列宽 nvarchar(200) 一致。</summary>
    internal const int MaxTokenLength = 200;

    internal const int MaxVersionLength = 50;

    /// <summary>每个账号最多保留的活动设备数；超出时撤销最久未活跃的。</summary>
    internal const int MaxActiveDevicesPerUser = 10;

    // 内层字符集 [A-Za-z0-9_-]{10,200} 加上前缀与括号整串可到约 218，超过列宽，所以另有「整串 ≤ 200」的检查。
    [GeneratedRegex(@"^(Exponent|Expo)PushToken\[[A-Za-z0-9_-]{10,200}\]\z", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    private readonly ISqlSugarClient _db = context.Db;

    public async Task<PushDeviceRegisterResultDto> RegisterAsync(
        string userGuid,
        PushDeviceRegisterRequestDto? request,
        CancellationToken cancellationToken = default
    )
    {
        var input = ValidateRegister(request);

        // 并发注册同一令牌时，过滤唯一索引兜底；输家重新读取（此时赢家的行已存在，走刷新或转移分支）。
        // 同一令牌就是同一台物理设备，真正的并发争用者至多两三个，最多重试 2 次（共 3 次尝试）。
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await RegisterOnceAsync(userGuid, input, cancellationToken);
            }
            catch (Exception ex) when (attempt < 2 && (ex is RegisterRaceException || SqlErrorClassifier.IsUniqueViolation(ex)))
            {
                logger.LogDebug("推送令牌并发注册冲突，重试: Attempt={Attempt}, Token={Token}", attempt + 1, TokenFingerprint.Of(input.Token));
            }
        }
    }

    public async Task<PushDeviceRevokeResultDto> RevokeAsync(
        string userGuid,
        PushDeviceRevokeRequestDto? request,
        CancellationToken cancellationToken = default
    )
    {
        var token = ValidateToken(request?.ExpoPushToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var candidates = await _db.Queryable<AppPushDevice>()
            .Where(device => device.ExpoPushToken == token && device.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);
        // 只撤销属于当前账号的行：别人的令牌（含手持上一位登录者之外的任何人）原样不动。
        var ownIds = candidates
            .Where(device => string.Equals(device.UserGUID, userGuid, StringComparison.OrdinalIgnoreCase))
            .Select(device => device.PushDeviceId)
            .ToList();
        if (ownIds.Count == 0)
        {
            return new PushDeviceRevokeResultDto { Revoked = 0 };
        }

        var revoked = await RevokeAsync(_db, ownIds, AppPushRevokeReasons.Logout, now, cancellationToken);
        logger.LogInformation(
            "推送令牌已撤销: User={UserGuid}, Reason={Reason}, Count={Count}, Token={Token}",
            userGuid,
            AppPushRevokeReasons.Logout,
            revoked,
            TokenFingerprint.Of(token)
        );
        return new PushDeviceRevokeResultDto { Revoked = revoked };
    }

    /// <summary>按设备 Id 软撤销（仅对尚未撤销的行生效，已撤销的不覆盖原因）。返回实际撤销行数。</summary>
    internal static async Task<int> RevokeAsync(
        ISqlSugarClient db,
        IReadOnlyCollection<string> pushDeviceIds,
        string reason,
        DateTime nowUtc,
        CancellationToken cancellationToken
    )
    {
        if (pushDeviceIds.Count == 0)
        {
            return 0;
        }

        var ids = pushDeviceIds.ToList();
        return await db.Updateable<AppPushDevice>()
            .SetColumns(device => device.RevokedAtUtc == nowUtc)
            .SetColumns(device => device.RevokeReason == reason)
            .Where(device => ids.Contains(device.PushDeviceId) && device.RevokedAtUtc == null)
            .ExecuteCommandAsync(cancellationToken);
    }

    private async Task<PushDeviceRegisterResultDto> RegisterOnceAsync(
        string userGuid,
        RegisterInput input,
        CancellationToken cancellationToken
    )
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var existing = await _db.Queryable<AppPushDevice>()
            .Where(device => device.ExpoPushToken == input.Token && device.RevokedAtUtc == null)
            .FirstAsync(cancellationToken);

        string pushDeviceId;
        if (existing != null && string.Equals(existing.UserGUID, userGuid, StringComparison.OrdinalIgnoreCase))
        {
            // 同账号同令牌：只刷新活跃时间与版本，不新建行。
            var existingId = existing.PushDeviceId;
            var refreshed = await _db.Updateable<AppPushDevice>()
                .SetColumns(device => device.LastSeenAtUtc == now)
                .SetColumns(device => device.Platform == input.Platform)
                .SetColumns(device => device.AppVersion == input.AppVersion)
                .SetColumns(device => device.RuntimeVersion == input.RuntimeVersion)
                .Where(device => device.PushDeviceId == existingId && device.RevokedAtUtc == null)
                .ExecuteCommandAsync(cancellationToken);
            if (refreshed == 0)
            {
                // 刚好被并发撤销（退出登录 / 转移）：让外层重读一次，走新建分支。
                throw new RegisterRaceException();
            }

            pushDeviceId = existingId;
        }
        else
        {
            pushDeviceId = Guid.NewGuid().ToString("N");
            var created = new AppPushDevice
            {
                PushDeviceId = pushDeviceId,
                UserGUID = userGuid,
                Platform = input.Platform,
                ExpoPushToken = input.Token,
                AppVersion = input.AppVersion,
                RuntimeVersion = input.RuntimeVersion,
                CreatedAtUtc = now,
                LastSeenAtUtc = now,
            };

            if (existing == null)
            {
                await _db.Insertable(created).ExecuteCommandAsync(cancellationToken);
            }
            else
            {
                // 令牌换人：旧关联撤销 + 为当前账号新建一行必须在同一事务里，否则中途失败会留下「令牌无主」或双主的窗口。
                // 这里不在别的事务内调用（控制器直接调用），所以可以安全使用 UseTranAsync。
                var previousId = existing.PushDeviceId;
                var transaction = await _db.Ado.UseTranAsync(async () =>
                {
                    await _db.Updateable<AppPushDevice>()
                        .SetColumns(device => device.RevokedAtUtc == now)
                        .SetColumns(device => device.RevokeReason == AppPushRevokeReasons.Reassigned)
                        .Where(device => device.PushDeviceId == previousId && device.RevokedAtUtc == null)
                        .ExecuteCommandAsync(cancellationToken);
                    await _db.Insertable(created).ExecuteCommandAsync(cancellationToken);
                });
                if (!transaction.IsSuccess)
                {
                    throw transaction.ErrorException ?? new InvalidOperationException("转移推送令牌失败");
                }

                logger.LogInformation(
                    "推送令牌已转给新账号: User={UserGuid}, Platform={Platform}, Token={Token}",
                    userGuid,
                    input.Platform,
                    TokenFingerprint.Of(input.Token)
                );
            }
        }

        await EnforceDeviceLimitAsync(userGuid, pushDeviceId, now, cancellationToken);
        return new PushDeviceRegisterResultDto { PushDeviceId = pushDeviceId };
    }

    /// <summary>每个账号最多保留 <see cref="MaxActiveDevicesPerUser"/> 个活动设备：超出的按最久未活跃撤销（当前这台一定保留）。</summary>
    private async Task EnforceDeviceLimitAsync(
        string userGuid,
        string currentDeviceId,
        DateTime now,
        CancellationToken cancellationToken
    )
    {
        var others = await _db.Queryable<AppPushDevice>()
            .Where(device => device.UserGUID == userGuid && device.RevokedAtUtc == null && device.PushDeviceId != currentDeviceId)
            .OrderBy(device => device.LastSeenAtUtc, OrderByType.Desc)
            .Select(device => device.PushDeviceId)
            .ToListAsync(cancellationToken);
        var surplus = others.Skip(MaxActiveDevicesPerUser - 1).ToList();
        if (surplus.Count == 0)
        {
            return;
        }

        var revoked = await RevokeAsync(_db, surplus, AppPushRevokeReasons.Limit, now, cancellationToken);
        logger.LogInformation(
            "账号推送设备超过上限，已撤销最久未活跃的设备: User={UserGuid}, Revoked={Revoked}, Limit={Limit}",
            userGuid,
            revoked,
            MaxActiveDevicesPerUser
        );
    }

    private static RegisterInput ValidateRegister(PushDeviceRegisterRequestDto? request)
    {
        if (request == null)
        {
            throw new ArgumentException("请求体不能为空。");
        }

        var token = ValidateToken(request.ExpoPushToken);
        var platform = request.Platform?.Trim().ToLowerInvariant();
        if (!AppPushPlatforms.IsValid(platform))
        {
            throw new ArgumentException("platform 只能是 ios 或 android。");
        }

        return new RegisterInput(
            token,
            platform!,
            NormalizeVersion(request.AppVersion, "appVersion"),
            NormalizeVersion(request.RuntimeVersion, "runtimeVersion")
        );
    }

    /// <summary>令牌格式校验：<c>^(Exponent|Expo)PushToken[...]$</c> 且整串 ≤ 200。错误消息不回显令牌内容。</summary>
    internal static string ValidateToken(string? value)
    {
        var token = value?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            throw new ArgumentException("expoPushToken 不能为空。");
        }

        if (token.Length > MaxTokenLength || !TokenPattern().IsMatch(token))
        {
            throw new ArgumentException("expoPushToken 格式不正确。");
        }

        return token;
    }

    private static string? NormalizeVersion(string? value, string name)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > MaxVersionLength)
        {
            throw new ArgumentException($"{name} 超过 {MaxVersionLength} 个字符。");
        }

        return trimmed;
    }

    private sealed record RegisterInput(string Token, string Platform, string? AppVersion, string? RuntimeVersion);

    /// <summary>刷新时发现行刚被并发撤销（退出登录 / 被转移）：与唯一冲突同类处理，让外层重读后重试一次。</summary>
    private sealed class RegisterRaceException : Exception;
}

/// <summary>
/// 推送令牌在日志 / 审计里的安全写法：只记长度与 SHA-256 前 8 位十六进制，足够排障时对号入座，但无法还原令牌。
/// 令牌持有者可向该设备发推送，明文不得进入任何日志。
/// </summary>
internal static class TokenFingerprint
{
    internal static string Of(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return "len=0";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return $"len={token.Length} sha256={Convert.ToHexString(hash, 0, 4).ToLowerInvariant()}";
    }
}
