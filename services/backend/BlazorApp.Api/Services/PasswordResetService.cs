using System.Security.Cryptography;
using System.Text;
using BlazorApp.Api.Data;
using BlazorApp.Api.Utils;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services;

public interface IPasswordResetService
{
    /// <summary>登录页自助找回：无论邮箱是否存在都返回同一结果，防止枚举账号。</summary>
    Task<ApiResponse<bool>> RequestSelfServiceAsync(string email, string? requestIp);

    /// <summary>店长新建或重置后给员工发邀请验证码；调用方负责权限与目标范围校验。</summary>
    Task<ApiResponse<PasswordSetupEmailResultDto>> SendInviteAsync(string userGuid, string requestedBy, string? requestIp);

    /// <summary>用邮箱 + 验证码设置新密码。成功后清除须改密标记并吊销该账号全部登录会话。</summary>
    Task<ApiResponse<bool>> ConfirmAsync(string email, string code, string newPassword, string? requestIp);
}

/// <summary>
/// 邮箱验证码设置 / 找回密码。验证码 6 位、只存哈希；同一账号同时只有一个有效验证码。
/// 邀请（店长新建 / 重置）72 小时有效，自助找回 30 分钟有效；60 秒内不重复发送；错 5 次作废。
/// </summary>
public sealed class PasswordResetService : IPasswordResetService
{
    public const string ResetCodeInvalidCode = "RESET_CODE_INVALID";
    public const string NoDeliverableEmailCode = "ACCOUNT_EMAIL_MISSING";
    public const string ResendCooldownCode = "ACCOUNT_EMAIL_COOLDOWN";

    internal const int InviteTtlHours = 72;
    internal const int ResetTtlMinutes = 30;
    internal const int ResendCooldownSeconds = 60;
    internal const int MaxFailedAttempts = 5;
    private const string SelfRequestedBy = "self";
    private const string GenericRequestMessage = "如果该邮箱已开通账号，验证码已发送，请查收邮件";
    /// <summary>未填邮箱时系统生成的内部占位邮箱，收不到信。</summary>
    private const string InternalEmailSuffix = ".store.local";

    private readonly SqlSugarContext _context;
    private readonly IAccountPasswordEmailSender _emailSender;
    private readonly ILogger<PasswordResetService> _logger;

    public PasswordResetService(
        SqlSugarContext context,
        IAccountPasswordEmailSender emailSender,
        ILogger<PasswordResetService> logger
    )
    {
        _context = context;
        _emailSender = emailSender;
        _logger = logger;
    }

    public static bool IsDeliverableEmail(string? email)
    {
        var normalized = email?.Trim();
        return !string.IsNullOrWhiteSpace(normalized)
            && normalized.Contains('@')
            && !normalized.EndsWith(InternalEmailSuffix, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ApiResponse<bool>> RequestSelfServiceAsync(string email, string? requestIp)
    {
        var normalized = NormalizeEmail(email);
        if (normalized is null)
        {
            return ApiResponse<bool>.Error("请输入邮箱", "VALIDATION_ERROR");
        }

        var user = await FindUserByEmailAsync(normalized);
        // 关键逻辑：账号不存在、已停用、冷却中、发信失败都返回同一文案，防止通过此接口探测邮箱是否注册。
        if (user is null || !user.IsActive || !IsDeliverableEmail(user.Email))
        {
            return ApiResponse<bool>.OK(true, GenericRequestMessage);
        }

        var issued = await IssueAndSendAsync(user, UserPasswordResetCode.PurposeReset, SelfRequestedBy, requestIp);
        if (!issued.Success && issued.ErrorCode != ResendCooldownCode)
        {
            _logger.LogWarning("自助找回密码验证码未送达，错误码：{ErrorCode}", issued.ErrorCode);
        }
        return ApiResponse<bool>.OK(true, GenericRequestMessage);
    }

    public async Task<ApiResponse<PasswordSetupEmailResultDto>> SendInviteAsync(
        string userGuid,
        string requestedBy,
        string? requestIp
    )
    {
        var user = await _context.Db.Queryable<User>()
            .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
        if (user is null)
        {
            return ApiResponse<PasswordSetupEmailResultDto>.Error("用户不存在", "USER_NOT_FOUND");
        }
        if (!IsDeliverableEmail(user.Email))
        {
            return ApiResponse<PasswordSetupEmailResultDto>.Error(
                "该员工没有可用邮箱，请先编辑员工补充邮箱",
                NoDeliverableEmailCode
            );
        }

        var issued = await IssueAndSendAsync(user, UserPasswordResetCode.PurposeInvite, requestedBy, requestIp);
        if (!issued.Success)
        {
            return ApiResponse<PasswordSetupEmailResultDto>.Error(issued.Message, issued.ErrorCode ?? "ACCOUNT_EMAIL_SEND_FAILED");
        }
        return ApiResponse<PasswordSetupEmailResultDto>.OK(issued.Data!, $"设置密码邮件已发送到 {MaskEmail(user.Email)}");
    }

    public async Task<ApiResponse<bool>> ConfirmAsync(
        string email,
        string code,
        string newPassword,
        string? requestIp
    )
    {
        var normalizedEmail = NormalizeEmail(email);
        var normalizedCode = new string((code ?? string.Empty).Where(char.IsDigit).ToArray());
        var password = (newPassword ?? string.Empty).Trim();
        if (normalizedEmail is null || normalizedCode.Length != 6)
        {
            return InvalidCode();
        }
        if (password.Length is < 6 or > 100)
        {
            return ApiResponse<bool>.Error("密码长度必须在6-100个字符之间", "VALIDATION_ERROR");
        }

        var db = _context.Db;
        var user = await FindUserByEmailAsync(normalizedEmail);
        if (user is null)
        {
            return InvalidCode();
        }

        var now = DateTime.UtcNow;
        var row = await db.Queryable<UserPasswordResetCode>()
            .Where(item => item.UserGUID == user.UserGUID && item.ConsumedAtUtc == null)
            .OrderBy(item => item.CreatedAtUtc, SqlSugar.OrderByType.Desc)
            .FirstAsync();
        if (row is null || row.ExpiresAtUtc < now || row.FailedAttempts >= MaxFailedAttempts)
        {
            return InvalidCode();
        }

        var matches = CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(HashCode(row.Id, normalizedCode)),
            Encoding.ASCII.GetBytes(row.CodeHash)
        );
        if (!matches)
        {
            var failed = row.FailedAttempts;
            // 以失败次数做乐观锁，并发猜测也只会逐次累加，不会绕过 5 次上限。
            await db.Updateable<UserPasswordResetCode>()
                .SetColumns(item => item.FailedAttempts == failed + 1)
                .Where(item => item.Id == row.Id && item.FailedAttempts == failed && item.ConsumedAtUtc == null)
                .ExecuteCommandAsync();
            var remaining = Math.Max(0, MaxFailedAttempts - failed - 1);
            return remaining > 0
                ? ApiResponse<bool>.Error($"验证码不正确，还可尝试 {remaining} 次", ResetCodeInvalidCode)
                : ApiResponse<bool>.Error("验证码错误次数过多，请重新获取", ResetCodeInvalidCode);
        }

        await db.Ado.BeginTranAsync();
        try
        {
            // 关键逻辑：验证码一次性，先抢占再改密；并发两次提交只有一次成功。
            var consumed = await db.Updateable<UserPasswordResetCode>()
                .SetColumns(item => item.ConsumedAtUtc == now)
                .Where(item => item.Id == row.Id && item.ConsumedAtUtc == null)
                .ExecuteCommandAsync();
            if (consumed != 1)
            {
                await db.Ado.RollbackTranAsync();
                return InvalidCode();
            }
            var passwordHash = PasswordHasher.HashSubmittedPassword(password, PasswordHasher.PasswordFormatRaw);
            await db.Updateable<User>()
                .SetColumns(item => item.PasswordHash == passwordHash)
                .SetColumns(item => item.UpdatedAt == now)
                .Where(item => item.UserGUID == user.UserGUID && !item.IsDeleted)
                .ExecuteCommandAsync();
            // 自己设的密码，不再要求首次改密；旧设备上的登录全部失效。
            await UserPasswordChangeRequirements.ClearAsync(db, user.UserGUID);
            await db.Updateable<RefreshToken>()
                .SetColumns(token => token.IsRevoked == true)
                .SetColumns(token => token.UpdatedAt == now)
                .Where(token => token.UserGUID == user.UserGUID && !token.IsRevoked)
                .ExecuteCommandAsync();
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }

        _logger.LogInformation("邮箱验证码设置密码成功，用途：{Purpose}", row.Purpose);
        return ApiResponse<bool>.OK(true, "密码已设置，请用新密码登录");
    }

    private async Task<ApiResponse<PasswordSetupEmailResultDto>> IssueAndSendAsync(
        User user,
        string purpose,
        string requestedBy,
        string? requestIp
    )
    {
        var db = _context.Db;
        var now = DateTime.UtcNow;
        var latest = await db.Queryable<UserPasswordResetCode>()
            .Where(item => item.UserGUID == user.UserGUID)
            .OrderBy(item => item.CreatedAtUtc, SqlSugar.OrderByType.Desc)
            .FirstAsync();
        if (latest is not null && latest.CreatedAtUtc.AddSeconds(ResendCooldownSeconds) > now)
        {
            return ApiResponse<PasswordSetupEmailResultDto>.Error("验证码刚刚发送，请一分钟后再试", ResendCooldownCode);
        }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var row = new UserPasswordResetCode
        {
            UserGUID = user.UserGUID,
            Purpose = purpose,
            ExpiresAtUtc = purpose == UserPasswordResetCode.PurposeInvite
                ? now.AddHours(InviteTtlHours)
                : now.AddMinutes(ResetTtlMinutes),
            CreatedAtUtc = now,
            RequestedBy = requestedBy,
            RequestIp = requestIp,
        };
        row.CodeHash = HashCode(row.Id, code);

        await db.Ado.BeginTranAsync();
        try
        {
            // 发新码即作废旧码，保证同一账号只有一个有效验证码。
            await db.Updateable<UserPasswordResetCode>()
                .SetColumns(item => item.ConsumedAtUtc == now)
                .Where(item => item.UserGUID == user.UserGUID && item.ConsumedAtUtc == null)
                .ExecuteCommandAsync();
            await db.Insertable(row).ExecuteCommandAsync();
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }

        var send = await _emailSender.SendPasswordCodeAsync(
            user.Email,
            string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName!,
            user.Username,
            code,
            row.ExpiresAtUtc,
            purpose == UserPasswordResetCode.PurposeInvite
        );
        if (!send.Success)
        {
            // 没送达的验证码立即作废，避免留下一个用户收不到却仍有效的码。
            await db.Updateable<UserPasswordResetCode>()
                .SetColumns(item => item.ConsumedAtUtc == DateTime.UtcNow)
                .Where(item => item.Id == row.Id && item.ConsumedAtUtc == null)
                .ExecuteCommandAsync();
            return ApiResponse<PasswordSetupEmailResultDto>.Error(send.Message, send.ErrorCode ?? AccountPasswordEmailSender.AccountEmailSendFailedCode);
        }

        return ApiResponse<PasswordSetupEmailResultDto>.OK(new PasswordSetupEmailResultDto
        {
            MaskedEmail = MaskEmail(user.Email),
            ExpiresAtUtc = row.ExpiresAtUtc,
        });
    }

    private async Task<User?> FindUserByEmailAsync(string normalizedEmail)
    {
        // 新建账号保存时邮箱已转小写；历史数据可能大小写不一，统一按小写比较。
        var candidates = await _context.Db.Queryable<User>()
            .Where(item => !item.IsDeleted && item.Email.ToLower() == normalizedEmail)
            .Take(2)
            .ToListAsync();
        // 同一邮箱对应多个账号时无法确定是谁，宁可不发也不发错人。
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static string? NormalizeEmail(string? email)
    {
        var normalized = email?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) || !normalized.Contains('@') ? null : normalized;
    }

    private static string HashCode(string id, string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{id}:{code}"))).ToLowerInvariant();

    private static ApiResponse<bool> InvalidCode() =>
        ApiResponse<bool>.Error("验证码无效或已过期，请重新获取", ResetCodeInvalidCode);

    internal static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            return string.Empty;
        }
        var parts = email.Split('@', 2);
        var name = parts[0];
        var visible = name.Length <= 2 ? name[..1] : name[..2];
        return $"{visible}***@{parts[1]}";
    }
}
