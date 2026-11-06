using System.Security.Cryptography;
using System.Text;
using BlazorApp.Api.Data;
using BlazorApp.Api.Utils;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services;

/// <summary>生成随机密码后交给邮件渲染的信息。密码只存在于这个对象和邮件里。</summary>
public sealed record GeneratedPasswordMail(string ToEmail, string DisplayName, string LoginName, string Password)
{
    /// <summary>record 自动生成的 ToString 会带出所有属性；以后有人用 {@mail} 打日志会泄露随机密码，所以显式去掉。</summary>
    public override string ToString() => "GeneratedPasswordMail { Password = *** }";
}

/// <summary>按模板渲染好的邮件（主题 / 纯文本 / HTML）。公开接口不能暴露 internal 的 EmailContent，所以单独定义。</summary>
public sealed record RenderedEmailContent(string Subject, string Text, string Html);

public interface IPasswordResetService
{
    /// <summary>登录页自助找回：无论邮箱是否存在都返回同一结果，防止枚举账号。</summary>
    Task<ApiResponse<bool>> RequestSelfServiceAsync(string email, string? requestIp);

    /// <summary>店长新建或重置后给员工发邀请验证码；调用方负责权限与目标范围校验。</summary>
    Task<ApiResponse<PasswordSetupEmailResultDto>> SendInviteAsync(string userGuid, string requestedBy, string? requestIp);

    /// <summary>
    /// 店长新建员工后给员工邮箱发系统生成的初始密码：店长全程不经手密码，员工首次登录必须改密。
    /// 发信失败会恢复原密码哈希，不会把已有密码改成谁都不知道的值。
    /// </summary>
    Task<ApiResponse<PasswordSetupEmailResultDto>> SendInitialPasswordAsync(string userGuid, string requestedBy);

    /// <summary>
    /// 店长重置员工密码：与初始密码同一条通道（系统生成随机密码发到员工邮箱，店长不经手，员工首次登录必须改密），
    /// 区别只在邮件文案写「密码已被重置、旧密码已失效」，且「须改密」原因记为 reset。
    /// 新密码一送达，旧密码与旧设备上的登录立即失效；发信失败会恢复原密码哈希。
    /// </summary>
    Task<ApiResponse<PasswordSetupEmailResultDto>> SendResetPasswordAsync(string userGuid, string requestedBy);

    /// <summary>
    /// 员工通知「账号已开通」用：与初始密码同一条通道（写入随机密码 → 发信 → 失败恢复原密码 → 送达后吊销旧登录），
    /// 区别只在邮件内容由调用方按模板渲染。「须改密」原因记为 created-emailed，临时密码 72 小时失效。
    /// </summary>
    Task<ApiResponse<PasswordSetupEmailResultDto>> SendTemplatedPasswordAsync(
        string userGuid,
        string requestedBy,
        Func<GeneratedPasswordMail, RenderedEmailContent> buildContent
    );

    /// <summary>用邮箱 + 验证码设置新密码。成功后清除须改密标记并吊销该账号全部登录会话，并回传登录名。</summary>
    Task<ApiResponse<PasswordResetConfirmResultDto>> ConfirmAsync(string email, string code, string newPassword, string? requestIp);
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

    public Task<ApiResponse<PasswordSetupEmailResultDto>> SendInitialPasswordAsync(string userGuid, string requestedBy) =>
        SendGeneratedPasswordAsync(userGuid, requestedBy, isReset: false);

    public Task<ApiResponse<PasswordSetupEmailResultDto>> SendResetPasswordAsync(string userGuid, string requestedBy) =>
        SendGeneratedPasswordAsync(userGuid, requestedBy, isReset: true);

    public Task<ApiResponse<PasswordSetupEmailResultDto>> SendTemplatedPasswordAsync(
        string userGuid,
        string requestedBy,
        Func<GeneratedPasswordMail, RenderedEmailContent> buildContent
    ) =>
        SendGeneratedPasswordAsync(
            userGuid,
            requestedBy,
            isReset: false,
            sendMail: mail =>
            {
                var content = buildContent(mail);
                return _emailSender.SendRenderedAsync(mail.ToEmail, content.Subject, content.Text, content.Html, "staff-notification");
            }
        );

    /// <summary>
    /// 生成随机密码并发到员工邮箱的共用实现。isReset 只影响邮件文案、「须改密」原因和提示语，
    /// 写库、发信、失败回滚、吊销旧登录的流程完全一致。
    /// </summary>
    private async Task<ApiResponse<PasswordSetupEmailResultDto>> SendGeneratedPasswordAsync(
        string userGuid,
        string requestedBy,
        bool isReset,
        Func<GeneratedPasswordMail, Task<ApiResponse<bool>>>? sendMail = null
    )
    {
        var db = _context.Db;
        var user = await db.Queryable<User>()
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

        var previousHash = user.PasswordHash;
        // 记下写入前的「须先改密」标记整行（可能没有）：发信失败回滚时按原样还原（见下方失败分支）。
        var previousRequirement = await UserPasswordChangeRequirements.GetAsync(db, userGuid);
        var initialPassword = GenerateInitialPassword();
        var newHash = PasswordHasher.HashSubmittedPassword(initialPassword, PasswordHasher.PasswordFormatRaw);
        var now = DateTime.UtcNow;

        // 关键逻辑：先写入新密码并打上「首次登录必须改密」标记，再发信。
        // 邮件里是明文初始密码，员工用它登录后必须立刻改成自己的密码。
        await db.Ado.BeginTranAsync();
        try
        {
            await db.Updateable<User>()
                .SetColumns(item => item.PasswordHash == newHash)
                .SetColumns(item => item.UpdatedAt == now)
                .Where(item => item.UserGUID == userGuid && !item.IsDeleted)
                .ExecuteCommandAsync();
            await UserPasswordChangeRequirements.RequireAsync(
                db,
                userGuid,
                // 4 位随机临时密码强度低：用 -emailed 原因标记，登录时超过 72 小时即失效（见 AuthService.LoginAsync）。
                isReset ? UserPasswordChangeRequirement.ReasonResetEmailed : UserPasswordChangeRequirement.ReasonCreatedEmailed,
                requestedBy,
                now
            );
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }

        var displayName = string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName!;
        // 员工通知传入自己的发信委托（按模板渲染邮件）；其余调用沿用固定文案的初始密码 / 重置密码邮件。
        ApiResponse<bool> send;
        try
        {
            send = sendMail != null
                ? await sendMail(new GeneratedPasswordMail(user.Email, displayName, user.Username, initialPassword))
                : await _emailSender.SendInitialPasswordAsync(
                    user.Email,
                    displayName,
                    user.Username,
                    initialPassword,
                    isReset: isReset
                );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 此时新密码已经落库：渲染 / 发信一旦抛异常，必须和「发送失败」走同一条回滚路径，
            // 否则员工的旧密码已被换掉、新密码却谁也没收到。日志只记异常类型，不记收件人与密码。
            _logger.LogError("发送随机密码邮件时出现意外异常，将恢复原密码，异常类型：{ExceptionType}", ex.GetType().Name);
            send = ApiResponse<bool>.Error("邮件发送失败，请稍后重试", AccountPasswordEmailSender.AccountEmailSendFailedCode);
        }

        if (!send.Success)
        {
            // 没送达的初始密码立即作废：以新哈希为条件恢复，避免覆盖期间别处发生的改密。
            var restored = await db.Updateable<User>()
                .SetColumns(item => item.PasswordHash == previousHash)
                .SetColumns(item => item.UpdatedAt == DateTime.UtcNow)
                .Where(item => item.UserGUID == userGuid && item.PasswordHash == newHash)
                .ExecuteCommandAsync();
            // 密码已恢复成原样，「须先改密」标记也要还原成写入前的样子：重置对象是在用的老账号，
            // 原本没有标记就删掉（否则员工用旧密码登录会被无故强制改密）；原本有标记就按原行写回
            // （原因、时间都不变，否则手输密码的标记会被换成会过期的临时密码标记）。
            // 仅当哈希确实是我们恢复的才动标记，期间别处发生的改密不被覆盖。
            if (restored > 0)
            {
                await UserPasswordChangeRequirements.RestoreAsync(db, userGuid, previousRequirement);
            }
            return ApiResponse<PasswordSetupEmailResultDto>.Error(
                send.Message,
                send.ErrorCode ?? AccountPasswordEmailSender.AccountEmailSendFailedCode
            );
        }

        // 初始密码已送达：未用的验证码不能再覆盖它，旧设备上的登录一并失效。
        await db.Updateable<RefreshToken>()
            .SetColumns(token => token.IsRevoked == true)
            .SetColumns(token => token.UpdatedAt == now)
            .Where(token => token.UserGUID == userGuid && !token.IsRevoked)
            .ExecuteCommandAsync();
        await db.Updateable<UserPasswordResetCode>()
            .SetColumns(item => item.ConsumedAtUtc == now)
            .Where(item => item.UserGUID == userGuid && item.ConsumedAtUtc == null)
            .ExecuteCommandAsync();

        var label = isReset ? "重置后的新密码" : "初始密码";
        _logger.LogInformation("已向员工邮箱发送{Label}，操作人：{RequestedBy}", label, requestedBy);
        return ApiResponse<PasswordSetupEmailResultDto>.OK(
            new PasswordSetupEmailResultDto { MaskedEmail = MaskEmail(user.Email) },
            $"{label}已发送到 {MaskEmail(user.Email)}"
        );
    }

    /// <summary>
    /// 生成 4 位字母 + 数字初始密码（无特殊符号）：去掉 0/O、1/l/I 这类易混字符，
    /// 保证至少含一个字母和一个数字。员工要从邮件里手输，所以不用易看错的字符。
    /// 登录接口与首次改密页都不限制「当前密码」的最小长度（只限制新密码 ≥ 6 位），4 位初始密码可正常登录并改密。
    /// 使用系统加密随机数，只通过邮件送达员工，首次登录即被员工自设密码替换。
    /// </summary>
    internal static string GenerateInitialPassword()
    {
        const string letters = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string all = letters + digits;
        const int length = 4;

        var chars = new List<char>(length)
        {
            letters[RandomNumberGenerator.GetInt32(letters.Length)],
            digits[RandomNumberGenerator.GetInt32(digits.Length)],
        };
        while (chars.Count < length)
        {
            chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);
        }
        // Fisher-Yates 洗牌，避免「字母、数字」固定出现在开头两位。
        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars.ToArray());
    }

    public async Task<ApiResponse<PasswordResetConfirmResultDto>> ConfirmAsync(
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
            return ApiResponse<PasswordResetConfirmResultDto>.Error("密码长度必须在6-100个字符之间", "VALIDATION_ERROR");
        }

        var db = _context.Db;
        var user = await FindUserByEmailAsync(normalizedEmail);
        if (user is null)
        {
            return InvalidCode();
        }

        var now = DateTime.UtcNow;
        // 关键逻辑：只认密码类验证码；换邮箱验证码发到的是待验证的新邮箱，绝不能拿来设置密码。
        var row = await db.Queryable<UserPasswordResetCode>()
            .Where(item => item.UserGUID == user.UserGUID
                && item.ConsumedAtUtc == null
                && item.Purpose != UserPasswordResetCode.PurposeEmailChange)
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
            return WrongCode<PasswordResetConfirmResultDto>(failed);
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
        // 老账号的登录名不是邮箱，回传登录名供完成页展示并预填登录框。
        return ApiResponse<PasswordResetConfirmResultDto>.OK(
            new PasswordResetConfirmResultDto { LoginName = user.Username },
            "密码已设置，请用新密码登录"
        );
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
        // 冷却与作废只在密码类验证码之间生效，不影响进行中的换邮箱验证。
        var latest = await db.Queryable<UserPasswordResetCode>()
            .Where(item => item.UserGUID == user.UserGUID && item.Purpose != UserPasswordResetCode.PurposeEmailChange)
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
            // 发新码即作废同类旧码，保证同一账号只有一个有效的密码类验证码。
            await db.Updateable<UserPasswordResetCode>()
                .SetColumns(item => item.ConsumedAtUtc == now)
                .Where(item => item.UserGUID == user.UserGUID
                    && item.ConsumedAtUtc == null
                    && item.Purpose != UserPasswordResetCode.PurposeEmailChange)
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

    /// <summary>
    /// 账号邮箱被改掉后调用（店长改、员工换绑、管理员改），作废该账号所有未用验证码：
    /// 发往旧邮箱的设置密码码、进行中的换邮箱码都不能再用。须在改邮箱的同一事务内调用。
    /// </summary>
    internal static Task<int> InvalidateOutstandingCodesAsync(SqlSugar.ISqlSugarClient db, string userGuid, DateTime now) =>
        db.Updateable<UserPasswordResetCode>()
            .SetColumns(item => item.ConsumedAtUtc == now)
            .Where(item => item.UserGUID == userGuid && item.ConsumedAtUtc == null)
            .ExecuteCommandAsync();

    /// <summary>验证码不匹配时的提示：失败次数由调用方以乐观锁累加后传入累加前的值。</summary>
    internal static ApiResponse<T> WrongCode<T>(int failedBefore)
    {
        var remaining = Math.Max(0, MaxFailedAttempts - failedBefore - 1);
        return remaining > 0
            ? ApiResponse<T>.Error($"验证码不正确，还可尝试 {remaining} 次", ResetCodeInvalidCode)
            : ApiResponse<T>.Error("验证码错误次数过多，请重新获取", ResetCodeInvalidCode);
    }

    internal static string? NormalizeEmail(string? email)
    {
        var normalized = email?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) || !normalized.Contains('@') ? null : normalized;
    }

    internal static string HashCode(string id, string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{id}:{code}"))).ToLowerInvariant();

    private static ApiResponse<PasswordResetConfirmResultDto> InvalidCode() =>
        ApiResponse<PasswordResetConfirmResultDto>.Error("验证码无效或已过期，请重新获取", ResetCodeInvalidCode);

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
