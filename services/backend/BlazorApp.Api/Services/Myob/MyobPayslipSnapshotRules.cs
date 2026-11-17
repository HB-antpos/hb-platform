using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlazorApp.Api.Models.Myob;

namespace BlazorApp.Api.Services.Myob;

/// <summary>一次对账的结果：要新增的行、要改写的行（含恢复）、要标记「MYOB 里已找不到」的行。</summary>
internal sealed record MyobPayslipReconcilePlan(
    IReadOnlyList<MyobPayslipSnapshot> Inserts,
    IReadOnlyList<MyobPayslipSnapshot> Updates,
    IReadOnlyList<MyobPayslipSnapshot> Removals)
{
    internal bool HasChanges => Inserts.Count + Updates.Count + Removals.Count > 0;
}

/// <summary>
/// 工资单本地快照的纯规则（不碰数据库，便于单测）：识别同一张单、内容哈希、记录 ↔ 行互转、窗口对账。
/// 方案 docs/plans/2026-10-29-myob-payslip-history.md 第 12 节。
/// </summary>
internal static class MyobPayslipSnapshotRules
{
    // 与 MyobPayslipSnapshot 的列长一致；MYOB 返回更长的文本时截断，免得整批保存被「字符串被截断」拒掉。
    private const int NameMax = 200;
    private const int ShortMax = 50;
    private const int FrequencyMax = 30;
    private const int ChequeMax = 100;

    /// <summary>
    /// 同一张单的识别键。报表没有工资单 ID：用「员工卡 + 支付日 + 周期起止 + 税前 + 实发」；
    /// 金额进键，是为了让同一周期同一天的更正单（金额不同）保留为另一行，与 MYOB 客户端的去重口径一致。
    /// </summary>
    internal static string ComputeSlipKey(MyobPayrollAdviceRecord record)
    {
        static string Date(DateTime? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-";
        static string Money(decimal? value) => value?.ToString("0.00####", CultureInfo.InvariantCulture) ?? "-";

        return Sha256(string.Join('|',
            record.EmployeeUid.Trim().ToLowerInvariant(),
            Date(record.PaymentDate),
            Date(record.PeriodStart),
            Date(record.PeriodEnd),
            Money(record.GrossPay),
            Money(record.NetPay)));
    }

    /// <summary>整张单（含明细）的内容哈希：同一张单下次同步内容变了（比如更正了明细或姓名），据此改写本地行。</summary>
    internal static string ComputeContentHash(MyobPayrollAdviceRecord record) =>
        Sha256(JsonSerializer.Serialize(record));

    /// <summary>MYOB 记录 → 快照行。没有支付日期的记录无法按区间落库，返回 null（报表按支付日期筛选，正常不会出现）。</summary>
    internal static MyobPayslipSnapshot? ToSnapshot(string connectionGuid, MyobPayrollAdviceRecord record, DateTime nowUtc)
    {
        if (record.PaymentDate is not { } paymentDate)
        {
            return null;
        }

        return new MyobPayslipSnapshot
        {
            ConnectionGuid = connectionGuid,
            SlipKey = ComputeSlipKey(record),
            EmployeeUid = Clamp(record.EmployeeUid, ShortMax)!,
            EmployeeName = Clamp(record.EmployeeName, NameMax),
            EmployeeDisplayId = Clamp(record.EmployeeDisplayId, ShortMax),
            EmployerName = Clamp(record.EmployerName, NameMax),
            EmployerAbn = Clamp(record.EmployerAbn, ShortMax),
            PaymentDate = paymentDate.Date,
            PeriodStart = record.PeriodStart?.Date,
            PeriodEnd = record.PeriodEnd?.Date,
            PayFrequency = Clamp(record.PayFrequency, FrequencyMax),
            GrossPay = record.GrossPay,
            NetPay = record.NetPay,
            SuperFundName = Clamp(record.SuperFundName, NameMax),
            HourlyRate = record.HourlyRate,
            ChequeNumber = Clamp(record.ChequeNumber, ChequeMax),
            LinesJson = JsonSerializer.Serialize(record.Lines),
            ContentHash = ComputeContentHash(record),
            FirstSeenAt = nowUtc,
            UpdatedAt = nowUtc,
        };
    }

    /// <summary>快照行 → 展示用记录（读取路径）。明细 JSON 损坏时按没有明细处理，不让一行坏数据拖垮整张列表。</summary>
    internal static MyobPayrollAdviceRecord ToRecord(MyobPayslipSnapshot row)
    {
        IReadOnlyList<MyobPayrollAdviceLine> lines;
        try
        {
            lines = JsonSerializer.Deserialize<List<MyobPayrollAdviceLine>>(row.LinesJson) ?? [];
        }
        catch (JsonException)
        {
            lines = [];
        }

        return new MyobPayrollAdviceRecord(
            row.EmployerName,
            row.EmployerAbn,
            row.EmployeeUid,
            row.EmployeeName,
            row.EmployeeDisplayId,
            row.PeriodStart,
            row.PeriodEnd,
            row.PaymentDate,
            row.PayFrequency,
            row.GrossPay,
            row.NetPay,
            row.SuperFundName,
            row.HourlyRate,
            row.ChequeNumber,
            lines);
    }

    /// <summary>
    /// 窗口对账：existing 是本地库里该公司、该支付日期窗口内的全部行（含已标记找不到的），incoming 是刚从 MYOB 完整取回的同一窗口。
    /// - 只在 incoming：新增；
    /// - 两边都有但内容变了，或本地标了「找不到」而 MYOB 又出现了：改写并清空 RemovedAt；
    /// - 只在本地且仍有效：MYOB 里撤销 / 重发了，标 RemovedAt（不物理删除，下次又出现会恢复）；
    /// - 内容没变的行不动（不改 UpdatedAt），避免每次同步都写几百行。
    /// 调用方必须保证 incoming 是完整取回的结果：取数失败时不能调用，否则会把没取到的单全部标成找不到。
    /// existing 里的行对象会被就地修改（Updates / Removals 就是被改过的那几个对象）。
    /// </summary>
    internal static MyobPayslipReconcilePlan Reconcile(
        IReadOnlyList<MyobPayslipSnapshot> existing, IReadOnlyList<MyobPayslipSnapshot> incoming, DateTime nowUtc)
    {
        var incomingByKey = new Dictionary<string, MyobPayslipSnapshot>(StringComparer.Ordinal);
        foreach (var row in incoming)
        {
            incomingByKey.TryAdd(row.SlipKey, row);
        }

        var existingByKey = new Dictionary<string, MyobPayslipSnapshot>(StringComparer.Ordinal);
        foreach (var row in existing)
        {
            existingByKey.TryAdd(row.SlipKey, row);
        }

        var inserts = new List<MyobPayslipSnapshot>();
        var updates = new List<MyobPayslipSnapshot>();
        foreach (var (key, fresh) in incomingByKey)
        {
            if (!existingByKey.TryGetValue(key, out var current))
            {
                inserts.Add(fresh);
                continue;
            }

            if (current.RemovedAt is null && string.Equals(current.ContentHash, fresh.ContentHash, StringComparison.Ordinal))
            {
                continue;
            }

            CopyContent(fresh, current);
            current.UpdatedAt = nowUtc;
            current.RemovedAt = null;
            updates.Add(current);
        }

        var removals = new List<MyobPayslipSnapshot>();
        foreach (var (key, current) in existingByKey)
        {
            if (current.RemovedAt is null && !incomingByKey.ContainsKey(key))
            {
                current.RemovedAt = nowUtc;
                removals.Add(current);
            }
        }

        return new MyobPayslipReconcilePlan(inserts, updates, removals);
    }

    private static void CopyContent(MyobPayslipSnapshot from, MyobPayslipSnapshot to)
    {
        to.EmployeeUid = from.EmployeeUid;
        to.EmployeeName = from.EmployeeName;
        to.EmployeeDisplayId = from.EmployeeDisplayId;
        to.EmployerName = from.EmployerName;
        to.EmployerAbn = from.EmployerAbn;
        to.PaymentDate = from.PaymentDate;
        to.PeriodStart = from.PeriodStart;
        to.PeriodEnd = from.PeriodEnd;
        to.PayFrequency = from.PayFrequency;
        to.GrossPay = from.GrossPay;
        to.NetPay = from.NetPay;
        to.SuperFundName = from.SuperFundName;
        to.HourlyRate = from.HourlyRate;
        to.ChequeNumber = from.ChequeNumber;
        to.LinesJson = from.LinesJson;
        to.ContentHash = from.ContentHash;
    }

    private static string? Clamp(string? value, int max) =>
        value is { Length: > 0 } && value.Length > max ? value[..max] : value;

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

/// <summary>
/// 工资单同步的时间规则（纯函数）。自动同步按悉尼本地时间的整点：0、8、12、15、18、21 点（用户确认，「24 点」即次日 0 点）。
/// 不是「到点才跑」而是「最近一个整点之后还没成功同步过就补跑」：容器重启、同步失败、错过整点都会自己补上。
/// </summary>
internal static class MyobPayslipSyncSchedule
{
    internal static readonly int[] SlotHours = [0, 8, 12, 15, 18, 21];

    /// <summary>最近 8 周（56 天）的工资单每次都整体对账：撤销 / 重发只会出现在近期；再早的视为冻结，只有首次回填会碰。</summary>
    internal const int RecentDays = 56;

    /// <summary>首次回填 / 页面可查的最长历史：与页面「最长 24 个月」一致（731 天含首尾，即今天往前 730 天）。</summary>
    internal const int BackfillDays = MyobPayslipService.MaxRangeDays - 1;

    /// <summary>某次同步失败后，隔多久才允许再试（后台与手动都遵守，避免 MYOB 故障时每分钟重打）。</summary>
    internal static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(15);

    /// <summary>手动「同步」的冷却：刚成功同步过就不再打 MYOB（防止连点占满配额）。</summary>
    internal static readonly TimeSpan ManualCooldown = TimeSpan.FromSeconds(60);

    /// <summary>租约时长：同步一家公司的最长占用时间；进程中途死掉，到期后别人可接手。</summary>
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);

    internal static TimeZoneInfo ResolveZone()
    {
        foreach (var id in new[] { "Australia/Sydney", "AUS Eastern Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        // 极端环境没有时区数据库：退化为固定 UTC+10（只影响整点边界，差一小时的夏令时偏差可接受）。
        return TimeZoneInfo.CreateCustomTimeZone("AEST", TimeSpan.FromHours(10), "AEST", "AEST");
    }

    /// <summary>悉尼本地「今天」。</summary>
    internal static DateTime LocalToday(DateTime utcNow, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), zone).Date;

    /// <summary>不晚于 utcNow 的最近一个同步整点（UTC）。</summary>
    internal static DateTime LatestSlotUtc(DateTime utcNow, TimeZoneInfo zone)
    {
        var now = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(now, zone);
        for (var dayOffset = 0; dayOffset >= -1; dayOffset--)
        {
            for (var index = SlotHours.Length - 1; index >= 0; index--)
            {
                var candidate = local.Date.AddDays(dayOffset).AddHours(SlotHours[index]);
                if (candidate > local || zone.IsInvalidTime(candidate))
                {
                    continue;
                }

                return TimeZoneInfo.ConvertTimeToUtc(candidate, zone);
            }
        }

        return now.AddDays(-1);
    }

    /// <summary>历史是否还没回填完整：从没对账过，或已对账的最早日期晚于「今天往前 730 天」。</summary>
    internal static bool NeedsBackfill(MyobPayslipSyncState? state, DateTime localToday) =>
        state?.CoveredFrom is not { } covered || covered.Date > localToday.AddDays(-BackfillDays);

    /// <summary>
    /// 后台任务此刻要不要同步这家公司：
    /// - 租约有效（别人正在同步）→ 不；
    /// - 还没回填完，或最近一个整点之后没有成功同步过 → 要同步；
    /// - 但上次失败且还没过 RetryAfterFailure → 先不重试。
    /// </summary>
    internal static bool IsDue(MyobPayslipSyncState? state, DateTime slotUtc, DateTime nowUtc, DateTime localToday)
    {
        if (state?.LeaseUntil is { } lease && lease > nowUtc)
        {
            return false;
        }

        var stale = state?.LastSuccessAt is not { } success
            || success < slotUtc
            || NeedsBackfill(state, localToday);
        if (!stale)
        {
            return false;
        }

        return !(state is { LastError: not null, LastAttemptAt: { } attempt } && nowUtc - attempt < RetryAfterFailure);
    }
}
