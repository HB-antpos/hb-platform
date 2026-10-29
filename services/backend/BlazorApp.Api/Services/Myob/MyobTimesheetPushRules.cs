using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BlazorApp.Api.Services.Myob;

/// <summary>MYOB Timesheet 里的一个单元格：某工资类别、某天、多少小时。快照（BeforeJson / AfterJson）只存这三样，不含姓名、金额。</summary>
internal sealed record MyobTimesheetCell(string CategoryUid, string Date, decimal Hours);

/// <summary>规划出的一个单元格（分钟）：同一 MYOB 类别、同一天的所有规划行已合并。</summary>
internal sealed record MyobPlannedCell(string CategoryUid, string Date, int Minutes);

/// <summary>
/// Timesheet 推送的纯函数（方案第 4.2、4.3、4.5 节）：规划 → MYOB 类别单元格、请求哈希、MYOB 现状归一化、比较、PUT 请求体、快照序列化。
/// 全部不碰数据库和网络，便于单测。
/// 取整依据实测：MYOB 的 Hours 保留 3 位小数（7.333 读回 7.333000）；规划是分钟，换算后按 3 位小数取整，
/// 因此回读比较的容差取 0.0005 小时，同一天同一类别的多条规划行先合并分钟再换算，避免逐条取整累积误差。
/// </summary>
internal static class MyobTimesheetPushRules
{
    /// <summary>小时保留的小数位（实测 MYOB 保留 3 位）。</summary>
    public const int HoursDecimals = 3;

    /// <summary>回读比较容差（小时）：3 位小数取整的最大误差。</summary>
    public const decimal HoursTolerance = 0.0005m;

    /// <summary>一次推送最多处理的员工数（一块）。</summary>
    public const int MaxUsersPerChunk = 10;

    public static decimal ToHours(int minutes) =>
        Math.Round(minutes / 60m, HoursDecimals, MidpointRounding.AwayFromZero);

    /// <summary>
    /// 把本公司的规划行（MyobCompanyPlan.Pushed）按「MYOB 类别 UID × 日期」合并分钟。
    /// 映射缺失（null / 空白）的类别键收集到 <c>UnmappedKeys</c>，对应的规划行不进结果——调用方必须把它当拦截项处理。
    /// </summary>
    public static (List<MyobPlannedCell> Cells, List<string> UnmappedKeys) BuildPlannedCells(
        MyobCompanyPlan plan, IReadOnlyDictionary<string, string?> mappingByKey)
    {
        // 类别 UID 比较忽略大小写（同一个 GUID 写法不同也要合并）；结果保留第一次见到的写法。
        var merged = new Dictionary<(string Uid, string Date), int>();
        var spelling = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unmapped = new List<string>();
        foreach (var line in plan.Pushed.Where(item => item.Minutes > 0))
        {
            var uid = mappingByKey.GetValueOrDefault(line.CategoryKey);
            if (string.IsNullOrWhiteSpace(uid))
            {
                if (!unmapped.Contains(line.CategoryKey))
                {
                    unmapped.Add(line.CategoryKey);
                }

                continue;
            }

            var canonical = spelling.TryAdd(uid.Trim(), uid.Trim()) ? uid.Trim() : spelling[uid.Trim()];
            var key = (canonical, FormatDate(line.Date));
            merged[key] = merged.GetValueOrDefault(key) + line.Minutes;
        }

        var cells = merged
            .OrderBy(pair => pair.Key.Uid, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.Key.Date, StringComparer.Ordinal)
            .Select(pair => new MyobPlannedCell(pair.Key.Uid, pair.Key.Date, pair.Value))
            .ToList();
        return (cells, unmapped);
    }

    public static List<MyobTimesheetCell> ToTarget(IEnumerable<MyobPlannedCell> planned) =>
        planned.Select(cell => new MyobTimesheetCell(cell.CategoryUid, cell.Date, ToHours(cell.Minutes))).ToList();

    /// <summary>
    /// 请求哈希：SHA256(公司 | 员工卡 UID | 周期起止 | 按 类别 UID、日期 排序后的 (类别 UID, 日期, 分钟))。
    /// 顺序无关，改一分钟就变；用来判断「已推送」还是「需重新推送」。
    /// </summary>
    public static string ComputeRequestHash(
        string connectionGuid, string employeeUid, DateTime start, DateTime end, IEnumerable<MyobPlannedCell> planned)
    {
        var builder = new StringBuilder()
            .Append(connectionGuid.Trim().ToLowerInvariant()).Append('|')
            .Append(employeeUid.Trim().ToLowerInvariant()).Append('|')
            .Append(FormatDate(start)).Append('|')
            .Append(FormatDate(end)).Append('|');
        foreach (var cell in planned
                     .OrderBy(item => item.CategoryUid.Trim().ToLowerInvariant(), StringComparer.Ordinal)
                     .ThenBy(item => item.Date, StringComparer.Ordinal))
        {
            builder.Append(cell.CategoryUid.Trim().ToLowerInvariant()).Append(',')
                .Append(cell.Date).Append(',')
                .Append(cell.Minutes.ToString(CultureInfo.InvariantCulture)).Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    /// <summary>
    /// MYOB 读回的内容 → 单元格：只留小时 ≠ 0 的天（0 小时的记录是清零残留，对工资没有影响；负工时是更正，必须保留）；
    /// 日期取前 10 位（读回为 2026-11-09T00:00:00）；同一类别同一天重复出现时累加。
    /// </summary>
    public static List<MyobTimesheetCell> Normalize(IEnumerable<MyobTimesheetLineRecord> lines)
    {
        var merged = new Dictionary<(string Uid, string Date), decimal>();
        foreach (var line in lines)
        {
            var uid = line.CategoryUid?.Trim() ?? string.Empty;
            foreach (var entry in line.Entries.Where(item => item.Hours is not null and not 0m))
            {
                var raw = entry.Date?.Trim() ?? string.Empty;
                var date = raw.Length >= 10 ? raw[..10] : raw;
                var key = (uid, date);
                merged[key] = merged.GetValueOrDefault(key) + entry.Hours!.Value;
            }
        }

        return merged
            .OrderBy(pair => pair.Key.Uid, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.Key.Date, StringComparer.Ordinal)
            .Select(pair => new MyobTimesheetCell(pair.Key.Uid, pair.Key.Date, pair.Value))
            .ToList();
    }

    /// <summary>
    /// 现有行里带 Job / Notes（我们的快照不保存这两项，覆盖后撤销也还原不了）：当作「有内容」，必须勾选覆盖才写。
    /// </summary>
    public static bool HasExtras(IEnumerable<MyobTimesheetLineRecord> lines) => lines.Any(line => line.HasExtras);

    /// <summary>MYOB 里是否有已发薪（Processed）的记录——哪怕小时为 0 也算，API 不能改它们。</summary>
    public static bool HasProcessed(IEnumerable<MyobTimesheetLineRecord> lines) =>
        lines.Any(line => line.Entries.Any(entry => entry.Processed));

    /// <summary>两组单元格是否一致：类别（忽略大小写）+ 日期 + 小时（容差内）逐项对应，数量相同。</summary>
    public static bool SameCells(IReadOnlyList<MyobTimesheetCell> left, IReadOnlyList<MyobTimesheetCell> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var a = Order(left);
        var b = Order(right);
        for (var index = 0; index < a.Count; index++)
        {
            if (!string.Equals(a[index].CategoryUid, b[index].CategoryUid, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(a[index].Date, b[index].Date, StringComparison.Ordinal)
                || Math.Abs(a[index].Hours - b[index].Hours) > HoursTolerance)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>目标 / 快照 → PUT 请求体的行：按类别分组，日期 yyyy-MM-dd（实测被接受）。空集合 = 空 Lines（实测可清空该区间）。</summary>
    public static List<MyobTimesheetPutLine> ToPutLines(IEnumerable<MyobTimesheetCell> cells) =>
        cells
            .GroupBy(cell => cell.CategoryUid, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new MyobTimesheetPutLine(
                group.Key,
                group.OrderBy(cell => cell.Date, StringComparer.Ordinal)
                    .Select(cell => new MyobTimesheetPutEntry(cell.Date, cell.Hours))
                    .ToList()))
            .ToList();

    public static string ToJson(IReadOnlyList<MyobTimesheetCell> cells) => JsonSerializer.Serialize(cells);

    /// <summary>读快照；空 / 损坏返回 null（调用方当作「快照不可用」，拒绝撤销而不是猜）。</summary>
    public static List<MyobTimesheetCell>? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<MyobTimesheetCell>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static decimal TotalHours(IEnumerable<MyobTimesheetCell> cells) => cells.Sum(cell => cell.Hours);

    public static string FormatDate(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static List<MyobTimesheetCell> Order(IEnumerable<MyobTimesheetCell> cells) =>
        cells
            .OrderBy(cell => cell.CategoryUid, StringComparer.OrdinalIgnoreCase)
            .ThenBy(cell => cell.Date, StringComparer.Ordinal)
            .ToList();
}

/// <summary>
/// 推送总闸与灰度白名单（方案 4.6）：总闸 Myob__TimesheetPushEnabled 默认关；白名单 Myob__TimesheetPushConnectionGuids
/// **空 = 全部禁止**（只开总闸忘了配白名单也不会写任何公司文件），写 <c>*</c> 才表示全部公司文件。
/// 检查放在推送服务入口与撤销入口、MyobApiClient.PutTimesheetAsync；客户端层拿不到连接 GUID，只看总闸。
/// </summary>
internal static class MyobTimesheetPushGate
{
    public static bool IsAllowed(MyobOptions options, string connectionGuid)
    {
        if (!options.TimesheetPushEnabled)
        {
            return false;
        }

        var allowed = options.TimesheetPushConnectionGuids
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return allowed.Contains("*") || allowed.Contains(connectionGuid, StringComparer.OrdinalIgnoreCase);
    }
}
