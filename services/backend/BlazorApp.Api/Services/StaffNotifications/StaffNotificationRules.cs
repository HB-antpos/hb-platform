namespace BlazorApp.Api.Services.StaffNotifications;

/// <summary>员工通知的小规则：纯函数，便于单测。</summary>
internal static class StaffNotificationRules
{
    /// <summary>短信单条估价（澳元）。10-30 用 ClickSend 实测每条 0.0792。只用于界面估算与对账，不参与任何扣费。</summary>
    public const decimal SmsUnitCostAud = 0.0792m;

    /// <summary>一次最多发给多少人，与后台用户列表的批量勾选上限一致。</summary>
    public const int MaxRecipientsPerBatch = 100;

    /// <summary>系统最多保留多少个模板（含自建），防止误操作无限新建。</summary>
    public const int MaxTemplates = 50;

    // 这些域名带数字或很短，但是真实邮箱服务，不能当成占位邮箱。
    private static readonly HashSet<string> KnownShortProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "qq.com", "163.com", "126.com", "139.com", "188.com", "263.net", "me.com",
    };

    // 二级后缀：域名最后两段才是后缀（如 example.com.au），取主名要多跳一段。
    private static readonly string[] CompoundSuffixes =
    {
        "com.au", "net.au", "org.au", "edu.au", "gov.au", "co.uk", "com.cn", "co.nz",
    };

    /// <summary>
    /// 邮箱域名像占位（如 123.com、11.com、ww.com）：主域名是纯数字或不超过 2 个字符，且不在常见邮箱服务白名单里。
    /// 这只是启发式提示，不是判定：2026-11 查生产在用账号：15 个 123.com、另有零星 11.com / ww.com 这类手填的假邮箱，
    /// 邮件发出去也没人收到。命中只会让收件人变成「需确认」（默认不勾选），不会阻止发送。
    /// </summary>
    public static bool IsSuspiciousEmail(string? email)
    {
        var at = email?.LastIndexOf('@') ?? -1;
        if (email is null || at < 0 || at == email.Length - 1) return false;

        var domain = email[(at + 1)..].Trim().ToLowerInvariant();
        if (KnownShortProviders.Contains(domain)) return false;

        var parts = domain.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return true; // 没有点的域名收不到信

        var suffixLength = CompoundSuffixes.Any(suffix => domain.EndsWith("." + suffix, StringComparison.Ordinal)) ? 2 : 1;
        var labelIndex = parts.Length - 1 - suffixLength;
        if (labelIndex < 0) return false;

        var label = parts[labelIndex];
        return label.Length <= 2 || label.All(char.IsAsciiDigit);
    }

    /// <summary>手机号打码（非澳洲手机号也用）：只保留最后 3 位数字。</summary>
    public static string MaskRawPhone(string? raw)
    {
        var digits = new string((raw ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        return digits.Length <= 3 ? "***" : "***" + digits[^3..];
    }
}
