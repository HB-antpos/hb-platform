using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BlazorApp.Api.Services.Attendance;

namespace BlazorApp.Api.Services.Myob;

/// <summary>我方员工当前（现行）时薪记录；还没有记录时 HasRecord = false，其余为 null。</summary>
internal sealed record PayRateSyncOur(
    bool HasRecord,
    string? PayMode,
    decimal? Weekday,
    decimal? Saturday,
    decimal? Sunday,
    decimal? PublicHoliday);

/// <summary>MYOB 员工卡与时薪同步有关的值：PayBasis（Hourly / Salary）、基础时薪原值（最多 4 位小数）、离职日期。年薪永远不读。</summary>
internal sealed record PayRateSyncMyob(string? PayBasis, decimal? HourlyRate, DateTime? TerminationDate);

/// <summary>
/// 一档倍数（周六 / 周日 / 公假）推算要用的输入：映射里选的类别 UID、该 UID 在 MYOB 里读到的类别记录（找不到为 null）、
/// 该员工的 MYOB 卡有没有挂这个类别。
/// </summary>
internal sealed record PayRateSyncMultipleInput(string? MappedUid, MyobWageCategoryRecord? Category, bool OnCard);

internal sealed record PayRateSyncInput(
    PayRateSyncOur Our,
    PayRateSyncMyob Myob,
    PayRateSyncMultipleInput Saturday,
    PayRateSyncMultipleInput Sunday,
    PayRateSyncMultipleInput PublicHoliday,
    DateTime Today);

internal sealed record PayRateSyncCell(
    decimal? OurRate,
    decimal? Suggested,
    decimal? Multiplier,
    bool CanApply,
    string? UnavailableReason,
    bool DefaultSelected);

internal sealed record PayRateSyncResult(
    string Kind,
    string? SkipReason,
    decimal? MyobHourlyRate,
    bool Rounded,
    PayRateSyncCell Weekday,
    PayRateSyncCell Saturday,
    PayRateSyncCell Sunday,
    PayRateSyncCell PublicHoliday,
    string Hash);

/// <summary>
/// 「MYOB → 我方」转账时薪同步的判定规则（纯函数，无 IO；方案 docs/plans/2026-11-16-myob-hourly-rate-sync.md 第 3 节）。
/// 平日价 = MYOB 基础时薪；周六 / 周日 / 公假价 = 基础时薪 × 映射类别的倍数（取自 HourlyDetails.RegularRateMultiplier，不解析类别名称）。
/// 所有价格一律先取 2 位小数（与 <see cref="PayrollCalculator.Round2"/> 同一种取整）再比较、再写入。
/// </summary>
internal static class MyobPayRateSyncRules
{
    private const string PayModeCash = "Cash";
    private const string PayBasisHourly = "Hourly";
    private const string PayRateRegular = "RegularRate";

    internal static PayRateSyncResult Compute(PayRateSyncInput input)
    {
        var our = input.Our;
        var ourWeekday = new PayRateSyncCell(our.Weekday, null, null, false, null, false);
        var ourSaturday = new PayRateSyncCell(our.Saturday, null, null, false, null, false);
        var ourSunday = new PayRateSyncCell(our.Sunday, null, null, false, null, false);
        var ourHoliday = new PayRateSyncCell(our.PublicHoliday, null, null, false, null, false);

        // 跳过判定：先看 MYOB 一侧的问题（离职、非时薪制），再看我方一侧（全现金不用转账价）。
        string? skip = null;
        if (input.Myob.TerminationDate is { } terminated && terminated.Date <= input.Today.Date)
        {
            skip = MyobPayRateSyncReasons.Terminated;
        }
        else if (!IsHourly(input.Myob))
        {
            skip = MyobPayRateSyncReasons.NotHourly;
        }
        else if (our.HasRecord && string.Equals(our.PayMode, PayModeCash, StringComparison.OrdinalIgnoreCase))
        {
            skip = MyobPayRateSyncReasons.PayModeCash;
        }

        if (skip is not null)
        {
            // 年薪制 / 没填时薪不带 MYOB 数（折算时薪不是真实时薪，不让人误当成可用值）。
            var carry = skip == MyobPayRateSyncReasons.NotHourly ? null : input.Myob.HourlyRate;
            return new PayRateSyncResult(
                MyobPayRateSyncKinds.Skip, skip, carry, false,
                ourWeekday, ourSaturday, ourSunday, ourHoliday,
                Hash(our, null, null, null, null));
        }

        var raw = input.Myob.HourlyRate!.Value;
        var baseRate = PayrollCalculator.Round2(raw);

        var weekdayDiffers = our.Weekday != baseRate;
        var kind = !our.HasRecord
            ? MyobPayRateSyncKinds.Create
            : our.Weekday is null
                ? MyobPayRateSyncKinds.Fill
                : weekdayDiffers ? MyobPayRateSyncKinds.Differ : MyobPayRateSyncKinds.Same;

        // 平日价：新建 / 补空默认勾上；与现值不同的覆盖默认不勾（逐项勾才覆盖）。
        var weekday = new PayRateSyncCell(
            our.Weekday, baseRate, null, weekdayDiffers, null,
            kind is MyobPayRateSyncKinds.Create or MyobPayRateSyncKinds.Fill);

        var saturday = Multiple(our.Saturday, baseRate, input.Saturday, defaultSelected: false);
        var sunday = Multiple(our.Sunday, baseRate, input.Sunday, defaultSelected: false);

        // 公假价默认勾上的条件：能写、平日价这一格默认也会写入（Create / Fill）或已经一致（Same），
        // 且我方公假价为空或还是旧规则（总方案 11.4）自动带出的「等于平日价」。
        // Differ 时平日价默认不勾，公假价跟着也不默认勾，避免只改了公假、平日价还是旧值。
        var holidayDefault = kind != MyobPayRateSyncKinds.Differ
            && (our.PublicHoliday is null || our.PublicHoliday == our.Weekday);
        var holiday = Multiple(our.PublicHoliday, baseRate, input.PublicHoliday, holidayDefault);

        return new PayRateSyncResult(
            kind, null, raw, baseRate != raw,
            weekday, saturday, sunday, holiday,
            Hash(our, raw, saturday.Suggested, sunday.Suggested, holiday.Suggested));
    }

    /// <summary>PayBasis 是 Hourly 且基础时薪 &gt; 0 才算有效的时薪制（年薪制卡可能带非零的折算时薪，不能只看时薪）。</summary>
    internal static bool IsHourly(PayRateSyncMyob myob) =>
        string.Equals(myob.PayBasis, PayBasisHourly, StringComparison.OrdinalIgnoreCase)
        && myob.HourlyRate is > 0m;

    private static PayRateSyncCell Multiple(
        decimal? ourRate, decimal baseRate, PayRateSyncMultipleInput input, bool defaultSelected)
    {
        var (multiplier, reason) = ResolveMultiplier(input);
        if (multiplier is null)
        {
            return new PayRateSyncCell(ourRate, null, null, false, reason, false);
        }

        var suggested = PayrollCalculator.Round2(baseRate * multiplier.Value);
        var canApply = ourRate != suggested;
        return new PayRateSyncCell(ourRate, suggested, multiplier, canApply, null, canApply && defaultSelected);
    }

    /// <summary>
    /// 一档的倍数：映射没配 → 无法推算；类别在 MYOB 里找不到；员工卡没挂这个类别；
    /// 类别不是「基础时薪 × 倍数」（PayRate 不是 RegularRate，或倍数缺失 / ≤ 0）。
    /// </summary>
    internal static (decimal? Multiplier, string? Reason) ResolveMultiplier(PayRateSyncMultipleInput input)
    {
        if (string.IsNullOrWhiteSpace(input.MappedUid))
        {
            return (null, MyobPayRateSyncReasons.CategoryNotMapped);
        }

        if (input.Category is null)
        {
            return (null, MyobPayRateSyncReasons.CategoryNotFound);
        }

        if (!input.OnCard)
        {
            return (null, MyobPayRateSyncReasons.CategoryNotOnCard);
        }

        if (!string.Equals(input.Category.PayRate, PayRateRegular, StringComparison.OrdinalIgnoreCase)
            || input.Category.RegularRateMultiplier is not > 0m)
        {
            return (null, MyobPayRateSyncReasons.CategoryNotRateMultiple);
        }

        return (input.Category.RegularRateMultiplier, null);
    }

    /// <summary>
    /// 覆盖「我方四个现值 + MYOB 基础时薪原值 + 三个建议值」的短哈希：预览之后任何一个变了，apply 就拒写那个员工。
    /// 只用于变更检测，不是安全凭据；不含任何 MYOB 标识。
    /// </summary>
    internal static string Hash(
        PayRateSyncOur our, decimal? myobRaw, decimal? saturday, decimal? sunday, decimal? holiday)
    {
        var text = string.Join(
            "|",
            our.HasRecord ? "1" : "0",
            our.PayMode ?? "-",
            Norm(our.Weekday), Norm(our.Saturday), Norm(our.Sunday), Norm(our.PublicHoliday),
            Norm(myobRaw), Norm(saturday), Norm(sunday), Norm(holiday));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }

    private static string Norm(decimal? value) =>
        value is null ? "-" : value.Value.ToString("0.0000", CultureInfo.InvariantCulture);
}
