using System.Globalization;
using System.Text;

namespace BlazorApp.Api.Services.Myob;

/// <summary>参与匹配的我方员工资料（已从 User / EmployeeProfile 整理好）。</summary>
internal sealed record MyobMatchSubject(
    string UserGuid,
    string? GivenName,
    string? FamilyName,
    string? FullName,
    string? Email,
    string? Phone,
    DateTime? Birthday);

/// <summary>一张 MYOB 员工卡参与匹配所需的字段（Contact/Employee + EmployeePayrollDetails 合并）。</summary>
public sealed record MyobMatchCard(
    string Uid,
    string FirstName,
    string LastName,
    string? CompanyName,
    bool IsIndividual,
    IReadOnlyList<string> Emails,
    IReadOnlyList<string> Phones,
    DateTime? DateOfBirth,
    bool IsActive);

/// <summary>单个（员工, 员工卡）组合的评估结果。</summary>
internal sealed record MyobMatchEvaluation(
    int Score,
    string Email,
    string Birthday,
    string Name,
    string Phone,
    bool NamePartial,
    bool HasPositiveEvidence,
    bool IsHigh,
    IReadOnlyList<string> Notes);

/// <summary>
/// 员工 ↔ MYOB 员工卡匹配打分（方案第 6 节）。信号：邮箱 > 生日 > 姓名 > 手机后 8 位。
/// 只负责「给建议」：评估结果永远由人确认后才生效。纯函数，无 IO，便于单元测试。
///
/// 分值（0–100）：邮箱一致 +45、生日一致 +30、姓名一致 +15（只是写法相近 +6）、手机一致 +10；
/// 惩罚：生日两边都有却不同 −10、邮箱两边都有却不同 −5（邮箱常换，惩罚轻）。手机不一致不扣分。
/// 高分（可批量确认）＝ 分数 ≥ 85 且邮箱、生日都一致，且卡片是启用的。
/// 候选资格＝至少有一项「正向证据」（邮箱 / 生日 / 姓名 / 手机之一一致）；全是不一致 / 未知的组合不会被推荐。
/// </summary>
internal static class MyobEmployeeMatcher
{
    internal const int HighScoreThreshold = 85;
    private const int PhoneSignificantDigits = 8;

    public static MyobMatchEvaluation Evaluate(MyobMatchSubject subject, MyobMatchCard card)
    {
        var email = EmailSignal(subject.Email, card.Emails);
        var birthday = BirthdaySignal(subject.Birthday, card.DateOfBirth);
        var (name, namePartial) = NameSignal(subject, card);
        var phone = PhoneSignal(subject.Phone, card.Phones);

        var score = 0;
        if (email == MyobSignalResults.Match) score += 45;
        else if (email == MyobSignalResults.Mismatch) score -= 5;
        if (birthday == MyobSignalResults.Match) score += 30;
        else if (birthday == MyobSignalResults.Mismatch) score -= 10;
        if (name == MyobSignalResults.Match) score += 15;
        else if (namePartial) score += 6;
        if (phone == MyobSignalResults.Match) score += 10;
        score = Math.Clamp(score, 0, 100);

        var positive = email == MyobSignalResults.Match
            || birthday == MyobSignalResults.Match
            || name == MyobSignalResults.Match
            || namePartial
            || phone == MyobSignalResults.Match;
        var high = positive
            && card.IsActive
            && score >= HighScoreThreshold
            && email == MyobSignalResults.Match
            && birthday == MyobSignalResults.Match;

        return new MyobMatchEvaluation(
            score, email, birthday, name, phone, namePartial, positive, high,
            BuildNotes(subject, card, email, birthday, name, namePartial, phone));
    }

    // ───────────────────────── 单项信号 ─────────────────────────

    internal static string EmailSignal(string? ours, IReadOnlyList<string> theirs)
    {
        var mine = NormalizeEmail(ours);
        var others = theirs.Select(NormalizeEmail).Where(item => item is not null).Select(item => item!).ToList();
        if (mine is null || others.Count == 0)
        {
            return MyobSignalResults.Unknown;
        }

        return others.Contains(mine, StringComparer.Ordinal) ? MyobSignalResults.Match : MyobSignalResults.Mismatch;
    }

    internal static string BirthdaySignal(DateTime? ours, DateTime? theirs)
    {
        if (ours is null || theirs is null)
        {
            return MyobSignalResults.Unknown;
        }

        return ours.Value.Date == theirs.Value.Date ? MyobSignalResults.Match : MyobSignalResults.Mismatch;
    }

    /// <summary>手机只比较末 8 位数字：兼容 0412 345 678 与 +61 412 345 678 两种写法。</summary>
    internal static string PhoneSignal(string? ours, IReadOnlyList<string> theirs)
    {
        var mine = LastDigits(ours);
        var others = theirs.Select(LastDigits).Where(item => item is not null).Select(item => item!).ToList();
        if (mine is null || others.Count == 0)
        {
            return MyobSignalResults.Unknown;
        }

        return others.Contains(mine, StringComparer.Ordinal) ? MyobSignalResults.Match : MyobSignalResults.Mismatch;
    }

    /// <summary>
    /// 姓名：我方 名 + 姓（或全名）与 MYOB 名 + 姓，不分先后、不分大小写 / 变音符号；一致＝match。
    /// 姓相同且名一方是另一方的前缀（Tom / Thomas）＝写法相近（partial，信号仍记 mismatch，分数给少量）。
    /// 任一边没有姓名＝unknown。
    /// </summary>
    internal static (string Signal, bool Partial) NameSignal(MyobMatchSubject subject, MyobMatchCard card)
    {
        var cardTokens = card.IsIndividual || string.IsNullOrWhiteSpace(card.CompanyName)
            ? Tokens($"{card.FirstName} {card.LastName}")
            : Tokens(card.CompanyName);
        var ours = new List<List<string>>();
        var given = Tokens(subject.GivenName);
        var family = Tokens(subject.FamilyName);
        if (given.Count + family.Count > 0)
        {
            ours.Add(given.Concat(family).ToList());
        }

        var full = Tokens(subject.FullName);
        if (full.Count > 0)
        {
            ours.Add(full);
        }

        if (cardTokens.Count == 0 || ours.Count == 0)
        {
            return (MyobSignalResults.Unknown, false);
        }

        var cardKey = string.Join(' ', cardTokens.OrderBy(item => item, StringComparer.Ordinal));
        if (ours.Any(tokens => string.Join(' ', tokens.OrderBy(item => item, StringComparer.Ordinal)) == cardKey))
        {
            return (MyobSignalResults.Match, false);
        }

        // 写法相近：姓一致（我方姓 / 全名最后一词 = MYOB 姓），名互为前缀（至少 2 个字母）。
        var cardLast = Tokens(card.LastName).LastOrDefault();
        var cardFirst = Tokens(card.FirstName).FirstOrDefault();
        if (cardLast is not null && cardFirst is not null)
        {
            var ourFamily = family.LastOrDefault() ?? full.LastOrDefault();
            var ourGiven = given.FirstOrDefault() ?? full.FirstOrDefault();
            if (ourFamily == cardLast && ourGiven is not null && ourGiven != cardFirst && IsNicknameOf(ourGiven, cardFirst))
            {
                return (MyobSignalResults.Mismatch, true);
            }
        }

        return (MyobSignalResults.Mismatch, false);
    }

    /// <summary>
    /// 名字的常见简称关系：首字母相同、较短的名字（至少 2 个字母）是较长名字的子序列，
    /// 如 Tom / Thomas、Ben / Benjamin、Jo / Jonathan。只在姓一致时才参与判断，且只给少量分，误判代价很小。
    /// </summary>
    internal static bool IsNicknameOf(string a, string b)
    {
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        if (shorter.Length < 2 || shorter[0] != longer[0])
        {
            return false;
        }

        var index = 0;
        foreach (var ch in longer)
        {
            if (ch == shorter[index] && ++index == shorter.Length)
            {
                return true;
            }
        }

        return false;
    }

    // ───────────────────────── 说明文字 ─────────────────────────

    private static IReadOnlyList<string> BuildNotes(
        MyobMatchSubject subject,
        MyobMatchCard card,
        string email,
        string birthday,
        string name,
        bool namePartial,
        string phone)
    {
        var notes = new List<string>();
        if (!card.IsActive)
        {
            notes.Add("MYOB 里这张卡已停用");
        }

        if (namePartial)
        {
            var ours = subject.GivenName?.Trim();
            var theirs = card.FirstName.Trim();
            notes.Add(string.IsNullOrWhiteSpace(ours)
                ? "姓名写法不同"
                : $"姓名写法不同（{ours} / {theirs}）");
        }
        else if (name == MyobSignalResults.Mismatch)
        {
            notes.Add("姓名不同");
        }

        if (birthday == MyobSignalResults.Mismatch)
        {
            notes.Add("生日与我方不一致");
        }

        if (email == MyobSignalResults.Mismatch)
        {
            notes.Add("邮箱不一致");
        }

        if (phone == MyobSignalResults.Mismatch)
        {
            var othersOk = email == MyobSignalResults.Match && birthday == MyobSignalResults.Match
                && name == MyobSignalResults.Match;
            notes.Add(othersOk ? "手机不一致，其余都对上" : "手机不一致");
        }

        return notes;
    }

    // ───────────────────────── 规范化 ─────────────────────────

    internal static string? NormalizeEmail(string? value)
    {
        var text = value?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>取最后 8 位数字；不足 6 位的号码当作没有（太短没有区分度）。</summary>
    internal static string? LastDigits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length < 6)
        {
            return null;
        }

        return digits.Length <= PhoneSignificantDigits ? digits : digits[^PhoneSignificantDigits..];
    }

    /// <summary>姓名切词：去变音符号、转小写，只保留字母 / 数字（中文等非拉丁字符原样保留）。</summary>
    internal static List<string> Tokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }

        return builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    /// <summary>手机掩码：「•••• 4821」；位数不足 4 位返回 null。</summary>
    internal static string? MaskPhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length < 4 ? null : $"•••• {digits[^4..]}";
    }
}
