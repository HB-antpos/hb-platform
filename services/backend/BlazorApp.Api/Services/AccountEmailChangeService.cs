using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using BlazorApp.Api.Data;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services;

public interface IAccountEmailChangeService
{
    /// <summary>给待绑定的新邮箱发 6 位验证码；当前邮箱在验证通过前保持不变。</summary>
    Task<ApiResponse<PasswordSetupEmailResultDto>> RequestAsync(string userGuid, string newEmail, string? requestIp);

    /// <summary>新邮箱 + 验证码通过后替换账号邮箱，并作废该账号其余未用验证码。</summary>
    Task<ApiResponse<AccountEmailChangeResultDto>> ConfirmAsync(string userGuid, string newEmail, string code);
}

/// <summary>
/// 员工绑定 / 更换自己的账号邮箱。邮箱是找回密码的唯一渠道，所以必须先证明新邮箱属于本人：
/// 验证码只发到新邮箱，30 分钟有效、60 秒冷却、错 5 次作废；与密码类验证码共用一张表但按用途隔离。
/// 店长替员工补邮箱不走这里：店长写入后发的设置密码验证码只有邮箱本人收得到，等价于验证。
/// </summary>
public sealed class AccountEmailChangeService : IAccountEmailChangeService
{
    public const string EmailExistsCode = "EMAIL_EXISTS";
    public const string EmailUnchangedCode = "EMAIL_UNCHANGED";
    internal const int CodeTtlMinutes = 30;

    private readonly SqlSugarContext _context;
    private readonly IAccountPasswordEmailSender _emailSender;
    private readonly ILogger<AccountEmailChangeService> _logger;

    public AccountEmailChangeService(
        SqlSugarContext context,
        IAccountPasswordEmailSender emailSender,
        ILogger<AccountEmailChangeService> logger
    )
    {
        _context = context;
        _emailSender = emailSender;
        _logger = logger;
    }

    public async Task<ApiResponse<PasswordSetupEmailResultDto>> RequestAsync(
        string userGuid,
        string newEmail,
        string? requestIp
    )
    {
        var normalized = NormalizeTargetEmail(newEmail);
        if (normalized is null)
        {
            return ApiResponse<PasswordSetupEmailResultDto>.Error("请输入有效的邮箱", "INVALID_EMAIL");
        }

        var db = _context.Db;
        var user = await db.Queryable<User>()
            .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted && item.IsActive);
        if (user is null)
        {
            return ApiResponse<PasswordSetupEmailResultDto>.Error("用户不存在", "USER_NOT_FOUND");
        }
        if (string.Equals(user.Email?.Trim(), normalized, StringComparison.OrdinalIgnoreCase))
        {
            return ApiResponse<PasswordSetupEmailResultDto>.Error("新邮箱与当前邮箱相同", EmailUnchangedCode);
        }
        if (await IsEmailTakenAsync(normalized, userGuid))
        {
            return ApiResponse<PasswordSetupEmailResultDto>.Error("该邮箱已被其他账号使用", EmailExistsCode);
        }

        var now = DateTime.UtcNow;
        var latest = await db.Queryable<UserPasswordResetCode>()
            .Where(item => item.UserGUID == userGuid && item.Purpose == UserPasswordResetCode.PurposeEmailChange)
            .OrderBy(item => item.CreatedAtUtc, SqlSugar.OrderByType.Desc)
            .FirstAsync();
        if (latest is not null && latest.CreatedAtUtc.AddSeconds(PasswordResetService.ResendCooldownSeconds) > now)
        {
            return ApiResponse<PasswordSetupEmailResultDto>.Error(
                "验证码刚刚发送，请一分钟后再试",
                PasswordResetService.ResendCooldownCode
            );
        }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var row = new UserPasswordResetCode
        {
            UserGUID = userGuid,
            Purpose = UserPasswordResetCode.PurposeEmailChange,
            TargetEmail = normalized,
            ExpiresAtUtc = now.AddMinutes(CodeTtlMinutes),
            CreatedAtUtc = now,
            RequestedBy = "self",
            RequestIp = requestIp,
        };
        row.CodeHash = PasswordResetService.HashCode(row.Id, code);

        await db.Ado.BeginTranAsync();
        try
        {
            // 同一账号只保留一个有效的换邮箱验证码；不动密码类验证码。
            await db.Updateable<UserPasswordResetCode>()
                .SetColumns(item => item.ConsumedAtUtc == now)
                .Where(item => item.UserGUID == userGuid
                    && item.ConsumedAtUtc == null
                    && item.Purpose == UserPasswordResetCode.PurposeEmailChange)
                .ExecuteCommandAsync();
            await db.Insertable(row).ExecuteCommandAsync();
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }

        var send = await _emailSender.SendEmailChangeCodeAsync(
            normalized,
            string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName!,
            user.Username,
            code,
            row.ExpiresAtUtc
        );
        if (!send.Success)
        {
            // 没送达的验证码立即作废。
            await db.Updateable<UserPasswordResetCode>()
                .SetColumns(item => item.ConsumedAtUtc == DateTime.UtcNow)
                .Where(item => item.Id == row.Id && item.ConsumedAtUtc == null)
                .ExecuteCommandAsync();
            return ApiResponse<PasswordSetupEmailResultDto>.Error(
                send.Message,
                send.ErrorCode ?? AccountPasswordEmailSender.AccountEmailSendFailedCode
            );
        }

        return ApiResponse<PasswordSetupEmailResultDto>.OK(new PasswordSetupEmailResultDto
        {
            MaskedEmail = PasswordResetService.MaskEmail(normalized),
            ExpiresAtUtc = row.ExpiresAtUtc,
        }, $"验证码已发送到 {PasswordResetService.MaskEmail(normalized)}");
    }

    public async Task<ApiResponse<AccountEmailChangeResultDto>> ConfirmAsync(
        string userGuid,
        string newEmail,
        string code
    )
    {
        var normalized = NormalizeTargetEmail(newEmail);
        var normalizedCode = new string((code ?? string.Empty).Where(char.IsDigit).ToArray());
        if (normalized is null || normalizedCode.Length != 6)
        {
            return InvalidCode();
        }

        var db = _context.Db;
        var now = DateTime.UtcNow;
        // 关键逻辑：只认换邮箱验证码，且目标邮箱必须与发码时一致，防止拿 A 邮箱的码绑定 B 邮箱。
        var row = await db.Queryable<UserPasswordResetCode>()
            .Where(item => item.UserGUID == userGuid
                && item.ConsumedAtUtc == null
                && item.Purpose == UserPasswordResetCode.PurposeEmailChange)
            .OrderBy(item => item.CreatedAtUtc, SqlSugar.OrderByType.Desc)
            .FirstAsync();
        if (row is null
            || row.ExpiresAtUtc < now
            || row.FailedAttempts >= PasswordResetService.MaxFailedAttempts
            || !string.Equals(row.TargetEmail, normalized, StringComparison.Ordinal))
        {
            return InvalidCode();
        }

        var matches = CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(PasswordResetService.HashCode(row.Id, normalizedCode)),
            Encoding.ASCII.GetBytes(row.CodeHash)
        );
        if (!matches)
        {
            var failed = row.FailedAttempts;
            await db.Updateable<UserPasswordResetCode>()
                .SetColumns(item => item.FailedAttempts == failed + 1)
                .Where(item => item.Id == row.Id && item.FailedAttempts == failed && item.ConsumedAtUtc == null)
                .ExecuteCommandAsync();
            return PasswordResetService.WrongCode<AccountEmailChangeResultDto>(failed);
        }

        await db.Ado.BeginTranAsync();
        try
        {
            var consumed = await db.Updateable<UserPasswordResetCode>()
                .SetColumns(item => item.ConsumedAtUtc == now)
                .Where(item => item.Id == row.Id && item.ConsumedAtUtc == null)
                .ExecuteCommandAsync();
            if (consumed != 1)
            {
                await db.Ado.RollbackTranAsync();
                return InvalidCode();
            }
            // 发码到验证之间邮箱可能被别的账号占用，事务内复查唯一性。
            if (await IsEmailTakenAsync(normalized, userGuid))
            {
                await db.Ado.RollbackTranAsync();
                return ApiResponse<AccountEmailChangeResultDto>.Error("该邮箱已被其他账号使用", EmailExistsCode);
            }
            var updated = await db.Updateable<User>()
                .SetColumns(item => item.Email == normalized)
                .SetColumns(item => item.UpdatedAt == now)
                .SetColumns(item => item.UpdatedBy == "self")
                .Where(item => item.UserGUID == userGuid && !item.IsDeleted && item.IsActive)
                .ExecuteCommandAsync();
            if (updated != 1)
            {
                await db.Ado.RollbackTranAsync();
                return ApiResponse<AccountEmailChangeResultDto>.Error("用户不存在", "USER_NOT_FOUND");
            }
            // 发往旧邮箱的设置密码验证码随之作废。
            await PasswordResetService.InvalidateOutstandingCodesAsync(db, userGuid, now);
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }

        _logger.LogInformation("员工已验证并更换账号邮箱，UserGuid: {UserGuid}", userGuid);
        return ApiResponse<AccountEmailChangeResultDto>.OK(
            new AccountEmailChangeResultDto { Email = normalized },
            "邮箱已绑定"
        );
    }

    /// <summary>新邮箱必须是可投递的真实邮箱：格式合法、不超长、不是分店占位邮箱。</summary>
    internal static string? NormalizeTargetEmail(string? email)
    {
        var normalized = PasswordResetService.NormalizeEmail(email);
        if (normalized is null || normalized.Length > 254 || !PasswordResetService.IsDeliverableEmail(normalized))
        {
            return null;
        }
        try
        {
            return new MailAddress(normalized).Address == normalized ? normalized : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private Task<bool> IsEmailTakenAsync(string normalizedEmail, string userGuid) =>
        _context.Db.Queryable<User>().AnyAsync(item =>
            item.UserGUID != userGuid && !item.IsDeleted && item.Email.ToLower() == normalizedEmail);

    private static ApiResponse<AccountEmailChangeResultDto> InvalidCode() =>
        ApiResponse<AccountEmailChangeResultDto>.Error(
            "验证码无效或已过期，请重新获取",
            PasswordResetService.ResetCodeInvalidCode
        );
}
