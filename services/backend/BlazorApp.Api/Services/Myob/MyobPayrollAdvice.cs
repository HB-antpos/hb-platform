using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlazorApp.Api.Services.Myob;

// Report/Payroll/EmployeePayrollAdvice（MYOB 已发薪工资单）：只读。
// 方案 docs/plans/2026-10-29-myob-payslip-history.md。官方文档只给出字段，没有写查询参数与 scope；
// 筛选写法（$filter / $orderby / $skip）要靠只读探针在已授权公司文件上核对后才能当作事实。

/// <summary>工资单明细行。Type 取值以真实响应为准（文档只列 Wage / Deduction / Expense / Tax，示例里还有 Entitlement / Superannuation）。</summary>
public sealed record MyobPayrollAdviceLine(
    string? CategoryName,
    string? CategoryType,
    decimal? Hours,
    decimal? CalculationRate,
    decimal? Amount,
    decimal? YearToDate);

/// <summary>
/// 一张已发薪工资单（展示用精简对象）。
/// 隐私：反序列化时不读取 AnnualSalary / DateOfBirth / EmploymentClassification，这些字段不会进入内存缓存、DTO 或日志。
/// 2026-10-29 起为了还原 MYOB 默认 Pay Slip 版式，放开 HourlyRate 与 ChequeNumber（MYOB 原版页头的 Hourly Rate / Reference Number）：
/// 它们只用于明细页与 PDF，不进列表 DTO、不写日志；员工本来就能在 MYOB 发出的工资单里看到这两项。
/// </summary>
public sealed record MyobPayrollAdviceRecord(
    string? EmployerName,
    string? EmployerAbn,
    string EmployeeUid,
    string? EmployeeName,
    string? EmployeeDisplayId,
    DateTime? PeriodStart,
    DateTime? PeriodEnd,
    DateTime? PaymentDate,
    string? PayFrequency,
    decimal? GrossPay,
    decimal? NetPay,
    string? SuperFundName,
    decimal? HourlyRate,
    string? ChequeNumber,
    IReadOnlyList<MyobPayrollAdviceLine> Lines);

/// <summary>
/// 工资单查询条件。支付日期区间为 [PaymentFrom, PaymentToExclusive)；<see cref="IMyobApiClient.GetPayrollAdvicesAsync"/> 要求两端都有
/// （按 ≤90 天窗口取数）；EmployeeUid 可选，用于只取某一名员工。
/// </summary>
public sealed record MyobPayrollAdviceQuery(
    DateTime? PaymentFrom = null,
    DateTime? PaymentToExclusive = null,
    string? EmployeeUid = null);

/// <summary>
/// 拼接工资单报表的查询串。只接受强类型输入（日期、Guid），不接受外部原样字符串，避免把用户输入拼进 OData 表达式。
/// 日期字面量写法 datetime'yyyy-MM-dd' 来自官方「Retrieving data」页的示例；Guid 字面量 guid'…' 是 OData v3 通用写法，
/// 官方页面没有说明，由探针核对。
/// </summary>
internal static class MyobAdviceQueries
{
    internal const string ReportPath = "Report/Payroll/EmployeePayrollAdvice";

    /// <summary>一页查询串（$top=1000，skip 为 0 时不带 $skip）。</summary>
    internal static string Build(MyobPayrollAdviceQuery query, int skip = 0) =>
        Compose(PageSize, skip > 0 ? skip : null, Filter(query), null);

    /// <summary>服务端单页上限；返回满一页意味着这个窗口里可能还有更多。</summary>
    internal const int PageSize = 1000;

    /// <summary>组合 $top / $skip / $orderby / $filter（全部由我方强类型输入生成，不接受外部原样字符串）。</summary>
    internal static string Compose(int top, int? skip, string? filter, string? orderBy)
    {
        var parts = new List<string> { $"$top={top.ToString(CultureInfo.InvariantCulture)}" };
        if (skip is not null)
        {
            parts.Add($"$skip={skip.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        // 官方页面建议的参数顺序：filter、orderby、top、skip；这里保持 $top 在前，探针会验证这种写法是否可用。
        if (!string.IsNullOrEmpty(filter))
        {
            parts.Add($"$filter={Encode(filter)}");
        }

        if (!string.IsNullOrEmpty(orderBy))
        {
            parts.Add($"$orderby={Encode(orderBy)}");
        }

        return string.Join('&', parts);
    }

    internal static string? Filter(MyobPayrollAdviceQuery query)
    {
        var clauses = new List<string>();
        if (query.PaymentFrom is { } from)
        {
            clauses.Add($"PaymentDate ge {DateLiteral(from)}");
        }

        if (query.PaymentToExclusive is { } to)
        {
            clauses.Add($"PaymentDate lt {DateLiteral(to)}");
        }

        if (!string.IsNullOrWhiteSpace(query.EmployeeUid))
        {
            clauses.Add($"Employee/UID eq {GuidLiteral(query.EmployeeUid)}");
        }

        return clauses.Count == 0 ? null : string.Join(" and ", clauses);
    }

    internal static string DateLiteral(DateTime date) =>
        $"datetime'{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}'";

    /// <exception cref="ArgumentException">不是合法 Guid（防止把任意字符串拼进筛选表达式）。</exception>
    internal static string GuidLiteral(string value) =>
        Guid.TryParse(value, out var guid)
            ? $"guid'{guid:D}'"
            : throw new ArgumentException("员工 UID 不是合法的 Guid", nameof(value));

    // 筛选表达式只含字母、数字、空格、斜杠、短横线和单引号；空格必须写成 %20，其余字符保持原样（官方示例里单引号不转义）。
    private static string Encode(string value) => value.Replace(" ", "%20", StringComparison.Ordinal);
}

/// <summary>Report/Payroll/EmployeePayrollAdvice 的 JSON 载荷；只声明展示需要的字段。属性名大小写不敏感。</summary>
internal sealed class PayrollAdvicePayload
{
    public EmployerPayload? Employer { get; set; }
    public EmployeePayload? Employee { get; set; }
    public DateTime? PayPeriodStartDate { get; set; }
    public DateTime? PayPeriodEndDate { get; set; }
    public DateTime? PaymentDate { get; set; }
    public string? PayFrequency { get; set; }
    public decimal? GrossPay { get; set; }
    public decimal? NetPay { get; set; }
    public NamedPayload? SuperannuationFund { get; set; }
    public decimal? HourlyRate { get; set; }
    public string? ChequeNumber { get; set; }
    public List<LinePayload>? Lines { get; set; }

    internal sealed class EmployerPayload
    {
        public string? CompanyName { get; set; }

        [JsonPropertyName("ABNOrTFN")]
        public string? AbnOrTfn { get; set; }
    }

    internal sealed class EmployeePayload
    {
        public string? Uid { get; set; }
        public string? Name { get; set; }
        public string? DisplayId { get; set; }
    }

    internal sealed class NamedPayload
    {
        public string? Name { get; set; }
    }

    internal sealed class CategoryPayload
    {
        public string? Name { get; set; }
        public string? Type { get; set; }
    }

    internal sealed class LinePayload
    {
        public CategoryPayload? PayrollCategory { get; set; }
        // 文档把 Hours 标成 Integer，但示例里出现了 12.308，所以按 decimal 读。
        public decimal? Hours { get; set; }
        public decimal? CalculationRate { get; set; }
        public decimal? Amount { get; set; }
        public decimal? YearToDate { get; set; }
    }

    /// <summary>没有员工 UID 的记录无法匹配绑定，直接丢弃。</summary>
    internal static MyobPayrollAdviceRecord? ToRecord(PayrollAdvicePayload item)
    {
        if (string.IsNullOrWhiteSpace(item.Employee?.Uid))
        {
            return null;
        }

        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        return new MyobPayrollAdviceRecord(
            Clean(item.Employer?.CompanyName),
            Clean(item.Employer?.AbnOrTfn),
            item.Employee!.Uid!.Trim(),
            Clean(item.Employee.Name),
            Clean(item.Employee.DisplayId),
            item.PayPeriodStartDate,
            item.PayPeriodEndDate,
            item.PaymentDate,
            Clean(item.PayFrequency),
            item.GrossPay,
            item.NetPay,
            Clean(item.SuperannuationFund?.Name),
            item.HourlyRate,
            Clean(item.ChequeNumber),
            (item.Lines ?? [])
                .Select(line => new MyobPayrollAdviceLine(
                    Clean(line.PayrollCategory?.Name),
                    Clean(line.PayrollCategory?.Type),
                    line.Hours,
                    line.CalculationRate,
                    line.Amount,
                    line.YearToDate))
                .ToList());
    }
}

/// <summary>
/// 把 [from, toExclusive) 切成若干个最长 maxDays 天的窗口。
/// 为什么要切：服务端单页最多 1000 条，满页时 NextPageLink 为空（不能靠它翻页）；窗口取小后绝大多数一次就取完，
/// 万一某窗口仍然满页，再在窗口内用 $skip 继续翻。
/// </summary>
internal static class MyobAdviceWindows
{
    internal const int DefaultMaxDays = 90;

    internal static IReadOnlyList<(DateTime From, DateTime ToExclusive)> Split(DateTime from, DateTime toExclusive, int maxDays = DefaultMaxDays)
    {
        if (maxDays <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDays));
        }

        var windows = new List<(DateTime, DateTime)>();
        var cursor = from.Date;
        var end = toExclusive.Date;
        while (cursor < end)
        {
            var next = cursor.AddDays(maxDays);
            if (next > end)
            {
                next = end;
            }

            windows.Add((cursor, next));
            cursor = next;
        }

        return windows;
    }
}
