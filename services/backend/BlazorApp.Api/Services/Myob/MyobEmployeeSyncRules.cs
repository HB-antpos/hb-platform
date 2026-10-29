using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.Myob;

/// <summary>我方员工当前资料（已从 User / EmployeeProfile / 未成年合规记录整理好）。</summary>
internal sealed record SyncOurValues(
    string UserGuid,
    string DisplayName,
    string? FullName,
    string? GivenName,
    string? MiddleName,
    string? FamilyName,
    string? Email,
    string? Phone,
    DateTime? Birthday,
    EmployeeGender? Gender,
    string? Address,
    EmployeeType? EmployeeType,
    string? BankBsb,
    string? BankAccount,
    string? SuperName,
    string? SuperAccount,
    int SensitiveRevision,
    bool HasPendingSensitiveRequest);

/// <summary>MYOB 员工卡里与资料同步有关的值（名册 + 付款详情合并后的原值）。</summary>
internal sealed record SyncMyobValues(
    bool IsIndividual,
    string? FirstName,
    string? LastName,
    IReadOnlyList<string> Emails,
    IReadOnlyList<string> Phones,
    DateTime? Birthday,
    string? Gender,
    MyobAddressRecord? Address,
    string? EmploymentStatus,
    bool HasLeaveEntitlement,
    string? Bsb,
    string? AccountNumber,
    string? SuperFundName,
    string? SuperMembershipNumber);

/// <summary>
/// 一项差异。Norm 是规范化后的值（比较与哈希用）；After 是要写入我方的目标值（键因字段而异，见 <see cref="MyobEmployeeSyncRules.CurrentValues"/>）。
/// </summary>
internal sealed record SyncDiff(
    string Field,
    string? OurDisplay,
    string? MyobDisplay,
    string Kind,
    string OurNorm,
    string MyobNorm,
    string? Note,
    IReadOnlyDictionary<string, string?> After);

/// <summary>
/// 「MYOB → 我方」资料同步的差异规则（纯函数，无 IO）。
/// 总原则：MYOB 没有值就不比（绝不用空值清掉我方资料）；我方为空 → 补空（Fill）；两边都有且规范化后不同 → 覆盖（Overwrite）；相同 → 无差异。
/// </summary>
internal static class MyobEmployeeSyncRules
{
    /// <summary>计算全部字段的差异，按 <see cref="MyobSyncFields.All"/> 顺序。</summary>
    internal static List<SyncDiff> Compute(SyncOurValues our, SyncMyobValues myob)
    {
        var result = new List<SyncDiff>();
        foreach (var field in MyobSyncFields.All)
        {
            var diff = ComputeField(field, our, myob);
            if (diff is not null)
            {
                result.Add(diff);
            }
        }

        return result;
    }

    internal static SyncDiff? ComputeField(string field, SyncOurValues our, SyncMyobValues myob) => field switch
    {
        MyobSyncFields.Name => NameDiff(our, myob),
        MyobSyncFields.Phone => PhoneDiff(our, myob),
        MyobSyncFields.Email => EmailDiff(our, myob),
        MyobSyncFields.Birthday => BirthdayDiff(our, myob),
        MyobSyncFields.Gender => GenderDiff(our, myob),
        MyobSyncFields.Address => AddressDiff(our, myob),
        MyobSyncFields.EmploymentType => EmploymentTypeDiff(our, myob),
        MyobSyncFields.Bank => BankDiff(our, myob),
        MyobSyncFields.Superannuation => SuperDiff(our, myob),
        _ => null,
    };

    // ───────────────────────── 各字段 ─────────────────────────

    private static SyncDiff? NameDiff(SyncOurValues our, SyncMyobValues myob)
    {
        // 公司型卡片（非个人）的「姓名」是公司名，不能当员工姓名写进来。
        if (!myob.IsIndividual)
        {
            return null;
        }

        var first = TidyName(myob.FirstName);
        var last = TidyName(myob.LastName);
        var full = string.Join(' ', new[] { first, last }.Where(part => !string.IsNullOrWhiteSpace(part)));
        if (full.Length == 0)
        {
            return null;
        }

        var ours = Blank(our.FullName);
        var ourNorm = NormTokens(ours);
        var myobNorm = NormTokens(full);
        if (ourNorm == myobNorm)
        {
            return null;
        }

        // 中间名在 MYOB 名里时拆不开，统一写「名 = MYOB 名，姓 = MYOB 姓，中间名清空」，FullName 由三段合成。
        return Build(MyobSyncFields.Name, ours, full, ourNorm, myobNorm, null,
            ("given", first), ("middle", null), ("family", last), ("full", full));
    }

    private static SyncDiff? PhoneDiff(SyncOurValues our, SyncMyobValues myob)
    {
        var myobPhones = myob.Phones.Select(CanonPhone).Where(item => item is not null).Select(item => item!).ToList();
        if (myobPhones.Count == 0)
        {
            return null;
        }

        // MYOB 常同时填座机和手机：优先 04 开头的手机；我方号码只要和 MYOB 任何一个号码一致就算一致。
        var ours = Blank(our.Phone);
        var ourCanon = CanonPhone(ours);
        if (ourCanon is not null && myobPhones.Contains(ourCanon))
        {
            return null;
        }

        var chosen = myobPhones.FirstOrDefault(item => item.StartsWith("04", StringComparison.Ordinal)) ?? myobPhones[0];
        var display = FormatPhone(chosen);
        return Build(MyobSyncFields.Phone, ours, display, ourCanon ?? string.Empty, chosen, null, ("phone", display));
    }

    private static SyncDiff? EmailDiff(SyncOurValues our, SyncMyobValues myob)
    {
        var myobEmails = myob.Emails.Select(MyobEmployeeMatcher.NormalizeEmail).Where(item => item is not null).Select(item => item!).ToList();
        if (myobEmails.Count == 0)
        {
            return null;
        }

        var ours = Blank(our.Email);
        var ourNorm = MyobEmployeeMatcher.NormalizeEmail(ours);
        if (ourNorm is not null && myobEmails.Contains(ourNorm))
        {
            return null;
        }

        var chosen = myob.Emails.First(item => MyobEmployeeMatcher.NormalizeEmail(item) == myobEmails[0]).Trim();
        return Build(MyobSyncFields.Email, ours, chosen, ourNorm ?? string.Empty, myobEmails[0], null, ("email", chosen));
    }

    private static SyncDiff? BirthdayDiff(SyncOurValues our, SyncMyobValues myob)
    {
        if (myob.Birthday is null)
        {
            return null;
        }

        var myobDate = myob.Birthday.Value.Date;
        var ourDate = our.Birthday?.Date;
        if (ourDate == myobDate)
        {
            return null;
        }

        return Build(
            MyobSyncFields.Birthday,
            ourDate is null ? null : FormatDate(ourDate.Value),
            FormatDate(myobDate),
            ourDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            myobDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            null,
            ("birthday", myobDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
    }

    private static SyncDiff? GenderDiff(SyncOurValues our, SyncMyobValues myob)
    {
        var myobGender = myob.Gender?.Trim().ToLowerInvariant() switch
        {
            "male" => EmployeeGender.Male,
            "female" => EmployeeGender.Female,
            "other" => EmployeeGender.Other,
            _ => (EmployeeGender?)null,
        };
        if (myobGender is null)
        {
            return null;
        }

        // 我方「未知」等同没填。
        var ourGender = our.Gender is null or EmployeeGender.Unknown ? null : our.Gender;
        if (ourGender == myobGender)
        {
            return null;
        }

        return Build(
            MyobSyncFields.Gender,
            ourGender is null ? null : GenderLabel(ourGender.Value),
            GenderLabel(myobGender.Value),
            ourGender?.ToString() ?? string.Empty,
            myobGender.Value.ToString(),
            null,
            ("gender", myobGender.Value.ToString().ToLowerInvariant()));
    }

    private static SyncDiff? AddressDiff(SyncOurValues our, SyncMyobValues myob)
    {
        var composed = ComposeAddress(myob.Address);
        if (composed is null)
        {
            return null;
        }

        var ours = Blank(our.Address);
        var ourNorm = NormTokens(ours);
        var myobNorm = NormTokens(composed);
        if (ourNorm == myobNorm)
        {
            return null;
        }

        return Build(MyobSyncFields.Address, ours, composed, ourNorm, myobNorm, null, ("address", composed));
    }

    private static SyncDiff? EmploymentTypeDiff(SyncOurValues our, SyncMyobValues myob)
    {
        // Casual → 临时工；Other 等无法对应的不同步。
        var myobType = myob.EmploymentStatus?.Trim().ToLowerInvariant() switch
        {
            "fulltime" or "full time" or "full-time" => EmployeeType.FullTime,
            "parttime" or "part time" or "part-time" => EmployeeType.PartTime,
            "casual" => EmployeeType.Temporary,
            _ => (EmployeeType?)null,
        };
        if (myobType is null || our.EmployeeType == myobType)
        {
            return null;
        }

        string? note = null;
        if (our.EmployeeType == EmployeeType.Temporary && myobType != EmployeeType.Temporary && myob.HasLeaveEntitlement)
        {
            note = "MYOB 挂了年假 / 病假累计";
        }
        else if (our.EmployeeType != EmployeeType.Temporary && myobType == EmployeeType.Temporary && !myob.HasLeaveEntitlement)
        {
            note = "MYOB 没挂年假 / 病假累计";
        }

        return Build(
            MyobSyncFields.EmploymentType,
            our.EmployeeType is null ? null : EmployeeTypeLabel(our.EmployeeType.Value),
            EmployeeTypeLabel(myobType.Value),
            our.EmployeeType?.ToString() ?? string.Empty,
            myobType.Value.ToString(),
            note,
            ("employmentType", EmployeeTypeKey(myobType.Value)));
    }

    private static SyncDiff? BankDiff(SyncOurValues our, SyncMyobValues myob)
    {
        var bsb = Digits(myob.Bsb);
        var account = Digits(myob.AccountNumber);
        if (bsb is null && account is null)
        {
            return null;
        }

        var ourBsb = Digits(our.BankBsb);
        var ourAccount = Digits(our.BankAccount);
        var ourNorm = $"{ourBsb}|{ourAccount}";
        var myobNorm = $"{bsb}|{account}";
        if (ourNorm == myobNorm)
        {
            return null;
        }

        var ourEmpty = ourBsb is null && ourAccount is null;
        return Build(
            MyobSyncFields.Bank,
            ourEmpty ? null : BankDisplay(ourBsb, ourAccount),
            BankDisplay(bsb, account),
            ourEmpty ? string.Empty : ourNorm,
            myobNorm,
            null,
            ("bsb", FormatBsb(bsb)), ("account", account));
    }

    private static SyncDiff? SuperDiff(SyncOurValues our, SyncMyobValues myob)
    {
        var fund = Blank(myob.SuperFundName);
        var member = Blank(myob.SuperMembershipNumber);
        if (fund is null && member is null)
        {
            return null;
        }

        var ourFund = Blank(our.SuperName);
        var ourMember = Blank(our.SuperAccount);
        var ourNorm = $"{NormTokens(ourFund)}|{AlphaNum(ourMember)}";
        var myobNorm = $"{NormTokens(fund)}|{AlphaNum(member)}";
        if (ourNorm == myobNorm)
        {
            return null;
        }

        var ourEmpty = ourFund is null && ourMember is null;
        return Build(
            MyobSyncFields.Superannuation,
            ourEmpty ? null : SuperDisplay(ourFund, ourMember),
            SuperDisplay(fund, member),
            ourEmpty ? string.Empty : ourNorm,
            myobNorm,
            null,
            ("name", fund), ("account", member));
    }

    // ───────────────────────── 当前值 / 哈希 ─────────────────────────

    /// <summary>某字段在我方的当前值（与 <see cref="SyncDiff.After"/> 同键）：撤销快照的 Before，也用于撤销前比对「写入之后有没有被人改过」。</summary>
    internal static Dictionary<string, string?> CurrentValues(string field, SyncOurValues our) => field switch
    {
        MyobSyncFields.Name => new()
        {
            ["given"] = Blank(our.GivenName),
            ["middle"] = Blank(our.MiddleName),
            ["family"] = Blank(our.FamilyName),
            ["full"] = Blank(our.FullName),
        },
        MyobSyncFields.Phone => new() { ["phone"] = Blank(our.Phone) },
        MyobSyncFields.Email => new() { ["email"] = Blank(our.Email) },
        MyobSyncFields.Birthday => new() { ["birthday"] = our.Birthday?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
        MyobSyncFields.Gender => new() { ["gender"] = our.Gender is null or EmployeeGender.Unknown ? null : our.Gender.Value.ToString().ToLowerInvariant() },
        MyobSyncFields.Address => new() { ["address"] = Blank(our.Address) },
        MyobSyncFields.EmploymentType => new() { ["employmentType"] = our.EmployeeType is null ? null : EmployeeTypeKey(our.EmployeeType.Value) },
        MyobSyncFields.Bank => new() { ["bsb"] = Blank(our.BankBsb), ["account"] = Blank(our.BankAccount) },
        MyobSyncFields.Superannuation => new() { ["name"] = Blank(our.SuperName), ["account"] = Blank(our.SuperAccount) },
        _ => new(),
    };

    /// <summary>两份「字段值字典」是否一致（键值逐一比较，null 与空白同义）。</summary>
    internal static bool SameValues(IReadOnlyDictionary<string, string?> left, IReadOnlyDictionary<string, string?> right) =>
        left.Count == right.Count
        && left.All(pair => right.TryGetValue(pair.Key, out var other)
            && string.Equals(Blank(pair.Value), Blank(other), StringComparison.Ordinal));

    /// <summary>规范化值的短哈希：预览时给前端，写入时服务端重算比对，防止「预览之后值变了」。</summary>
    internal static string Hash(string field, string side, string norm)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{field}|{side}|{norm}"));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    // ───────────────────────── 规范化与展示 ─────────────────────────

    private static SyncDiff Build(
        string field,
        string? ourDisplay,
        string myobDisplay,
        string ourNorm,
        string myobNorm,
        string? note,
        params (string Key, string? Value)[] after) =>
        new(
            field,
            ourDisplay,
            myobDisplay,
            string.IsNullOrEmpty(ourNorm) ? MyobSyncChangeKinds.Fill : MyobSyncChangeKinds.Overwrite,
            ourNorm,
            myobNorm,
            note,
            after.ToDictionary(item => item.Key, item => item.Value));

    internal static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>名字切词后用空格拼接；比较时忽略大小写、标点和多余空格。</summary>
    internal static string NormTokens(string? value) => string.Join(' ', MyobEmployeeMatcher.Tokens(value));

    private static string AlphaNum(string? value) =>
        value is null ? string.Empty : new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string? Digits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? null : digits;
    }

    /// <summary>MYOB 里的名字常是全大写（AIDAN FORDE）：全大写时改成首字母大写再写入，其余保持原样。</summary>
    internal static string? TidyName(string? value)
    {
        var text = Blank(value);
        if (text is null)
        {
            return null;
        }

        var hasLetter = text.Any(char.IsLetter);
        return hasLetter && text == text.ToUpperInvariant()
            ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant())
            : text;
    }

    /// <summary>澳洲号码规范成本地 10 位：+61 4xx → 04xx；其余只保留数字。</summary>
    internal static string? CanonPhone(string? value)
    {
        var digits = Digits(value);
        if (digits is null)
        {
            return null;
        }

        if (digits.StartsWith("61", StringComparison.Ordinal) && digits.Length == 11)
        {
            return "0" + digits[2..];
        }

        if (digits.Length == 9 && digits[0] == '4')
        {
            return "0" + digits;
        }

        return digits;
    }

    internal static string FormatPhone(string canon) =>
        canon.Length == 10 && canon.StartsWith("04", StringComparison.Ordinal)
            ? $"{canon[..4]} {canon[4..7]} {canon[7..]}"
            : canon;

    /// <summary>街道、城市、州、邮编拼成一行：「14 Bay St, Robina QLD 4226」；街道里的换行换成逗号。</summary>
    internal static string? ComposeAddress(MyobAddressRecord? address)
    {
        if (address is null)
        {
            return null;
        }

        // 只有州 / 邮编 / 国家、没有街道和城市的地址没有意义（MYOB 常把国家默认填成 Australia），不同步。
        if (Blank(address.Street) is null && Blank(address.City) is null)
        {
            return null;
        }

        var street = Blank(address.Street) is { } text
            ? string.Join(", ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            : null;
        var locality = string.Join(' ', new[] { Blank(address.City), Blank(address.State), Blank(address.PostCode) }
            .Where(part => part is not null));
        var line = string.Join(", ", new[] { street, locality.Length == 0 ? null : locality }.Where(part => part is not null));
        return line.Length == 0 ? null : line;
    }

    private static string FormatDate(DateTime date) => date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    private static string GenderLabel(EmployeeGender gender) => gender switch
    {
        EmployeeGender.Male => "男",
        EmployeeGender.Female => "女",
        _ => "其他",
    };

    private static string EmployeeTypeLabel(EmployeeType type) => type switch
    {
        EmployeeType.FullTime => "全职",
        EmployeeType.PartTime => "兼职",
        _ => "临时工",
    };

    /// <summary>与 EmployeeProfileService.ParseEmployeeType 能解析的写法一致。</summary>
    internal static string EmployeeTypeKey(EmployeeType type) => type switch
    {
        EmployeeType.FullTime => "fullTime",
        EmployeeType.PartTime => "partTime",
        _ => "casual",
    };

    /// <summary>BSB 与我方现有数据同一写法：6 位数字写成 123-456，其余原样（写入与展示共用）。</summary>
    internal static string? FormatBsb(string? digits) =>
        digits is { Length: 6 } ? $"{digits[..3]}-{digits[3..]}" : digits;

    /// <summary>银行展示：BSB 满 6 位写成 063-010；账号只留末 4 位。</summary>
    private static string BankDisplay(string? bsb, string? account)
    {
        var parts = new List<string>();
        if (bsb is not null)
        {
            parts.Add(FormatBsb(bsb)!);
        }

        if (account is not null)
        {
            parts.Add($"•••• {(account.Length <= 4 ? account : account[^4..])}");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>退休金展示：基金名称；会员号只留末 4 位。</summary>
    private static string SuperDisplay(string? fund, string? member)
    {
        var parts = new List<string>();
        if (fund is not null)
        {
            parts.Add(fund);
        }

        if (member is not null)
        {
            parts.Add($"•••• {(member.Length <= 4 ? member : member[^4..])}");
        }

        return string.Join(" · ", parts);
    }
}
