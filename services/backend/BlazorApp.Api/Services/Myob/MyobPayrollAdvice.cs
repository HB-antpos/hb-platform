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
/// 隐私：反序列化时就不读取 AnnualSalary / HourlyRate / DateOfBirth / ChequeNumber / EmploymentClassification，
/// 这些字段不会进入内存缓存、DTO 或日志。
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
    IReadOnlyList<MyobPayrollAdviceLine> Lines);

/// <summary>工资单查询条件。支付日期区间为 [PaymentFrom, PaymentToExclusive)；全部为空 = 不加筛选。</summary>
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

    /// <summary>完整分页查询串（含 $top=1000）。</summary>
    internal static string Build(MyobPayrollAdviceQuery query) => Compose(1000, null, Filter(query), null);

    /// <summary>探针用：自定义 $top / $skip / $orderby / 筛选的组合（全部由我方固定模板生成）。</summary>
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

// ───────────────────────── 探针（只读，只产出结构与计数） ─────────────────────────

/// <summary>响应里出现过的一个字段路径与取值种类（String / Number / Boolean / Null / Object / Array），不含任何取值。</summary>
public sealed record MyobProbeField(string Path, IReadOnlyList<string> Kinds);

/// <summary>
/// 一次探针请求（可能翻多页）的结构摘要。没有姓名、金额、UID 的取值：只有字段路径、取值种类、计数、日期范围和类别名称。
/// FirstEmployeeUid 仅供后续「按员工筛选」一步使用，不会出现在对外 DTO 里。
/// </summary>
public sealed record MyobAdviceProbeResult(
    int? StatusCode,
    long ElapsedMs,
    int Pages,
    bool Truncated,
    string RootShape,
    IReadOnlyList<string> RootFields,
    int ItemCount,
    int DistinctEmployees,
    string? MinPaymentDate,
    string? MaxPaymentDate,
    string? FirstItemPaymentDate,
    string? LastItemPaymentDate,
    IReadOnlyList<MyobProbeField> Fields,
    IReadOnlyList<string> LineTypes,
    IReadOnlyList<string> CategoryNames,
    IReadOnlyList<string> ErrorNames,
    bool BodyUnparseable)
{
    internal string? FirstEmployeeUid { get; init; }
}

/// <summary>把若干页响应体累计成 <see cref="MyobAdviceProbeResult"/>；纯逻辑，不访问网络，便于单测。</summary>
internal sealed class MyobAdviceProbeAccumulator
{
    private const int MaxPaths = 300;
    private const int MaxCategoryNames = 60;
    private const int MaxErrorNames = 10;
    private const int MaxErrorNameLength = 100;

    private readonly Dictionary<string, SortedSet<string>> _fields = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _rootFields = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _lineTypes = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _categoryNames = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _errorNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _employees = new(StringComparer.OrdinalIgnoreCase);
    private string _rootShape = "None";
    private int _items;
    private DateTime? _minDate;
    private DateTime? _maxDate;
    private DateTime? _firstDate;
    private DateTime? _lastDate;
    private string? _firstEmployeeUid;
    private bool _unparseable;

    /// <summary>累计一页响应，返回下一页链接（没有则 null）。</summary>
    internal string? AddPage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            string? next = null;
            IEnumerable<JsonElement> items;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Items", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                _rootShape = "Object{Items}";
                foreach (var property in root.EnumerateObject())
                {
                    _rootFields.Add(property.Name);
                }

                if (root.TryGetProperty("NextPageLink", out var link) && link.ValueKind == JsonValueKind.String)
                {
                    next = link.GetString();
                }

                items = list.EnumerateArray();
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                _rootShape = "Array";
                items = root.EnumerateArray();
            }
            else
            {
                // 单个对象，或错误体：错误体里只取 Errors[].Name。
                _rootShape = root.ValueKind == JsonValueKind.Object ? "Object" : root.ValueKind.ToString();
                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in root.EnumerateObject())
                    {
                        _rootFields.Add(property.Name);
                    }
                }

                items = root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("Errors", out _)
                    ? [root]
                    : [];
            }

            foreach (var item in items)
            {
                AddItem(item);
            }

            return next;
        }
        catch (JsonException)
        {
            _unparseable = true;
            return null;
        }
    }

    /// <summary>非 2xx 响应：只取 MYOB 错误体里的 Errors[].Name（错误类型名），不记 Message（可能回显筛选表达式）。</summary>
    internal void AddError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("Errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array)
            {
                foreach (var error in errors.EnumerateArray())
                {
                    if (_errorNames.Count < MaxErrorNames
                        && error.ValueKind == JsonValueKind.Object
                        && error.TryGetProperty("Name", out var name)
                        && name.ValueKind == JsonValueKind.String)
                    {
                        var text = name.GetString() ?? string.Empty;
                        _errorNames.Add(text.Length <= MaxErrorNameLength ? text : text[..MaxErrorNameLength]);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // 非 JSON 错误体（网关错误页等）：只按状态码处理。
        }
    }

    internal MyobAdviceProbeResult Build(int? statusCode, long elapsedMs, int pages, bool truncated) =>
        new(
            statusCode, elapsedMs, pages, truncated, _rootShape, _rootFields.ToList(), _items, _employees.Count,
            Format(_minDate), Format(_maxDate), Format(_firstDate), Format(_lastDate),
            _fields.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new MyobProbeField(pair.Key, pair.Value.ToList()))
                .ToList(),
            _lineTypes.ToList(), _categoryNames.ToList(), _errorNames.ToList(), _unparseable)
        {
            FirstEmployeeUid = _firstEmployeeUid,
        };

    private void AddItem(JsonElement item)
    {
        _items++;
        Walk(item, string.Empty);
        if (item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (item.TryGetProperty("Employee", out var employee) && employee.ValueKind == JsonValueKind.Object
            && employee.TryGetProperty("UID", out var uid) && uid.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(uid.GetString()))
        {
            _employees.Add(uid.GetString()!);
            _firstEmployeeUid ??= uid.GetString();
        }

        if (item.TryGetProperty("PaymentDate", out var paymentDate) && paymentDate.ValueKind == JsonValueKind.String
            && DateTime.TryParse(paymentDate.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            _minDate = _minDate is null || date < _minDate ? date : _minDate;
            _maxDate = _maxDate is null || date > _maxDate ? date : _maxDate;
            _firstDate ??= date;
            _lastDate = date;
        }

        if (item.TryGetProperty("Lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
        {
            foreach (var line in lines.EnumerateArray())
            {
                if (line.ValueKind == JsonValueKind.Object
                    && line.TryGetProperty("PayrollCategory", out var category) && category.ValueKind == JsonValueKind.Object)
                {
                    if (category.TryGetProperty("Type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() is { Length: > 0 } typeText)
                    {
                        _lineTypes.Add(typeText);
                    }

                    if (_categoryNames.Count < MaxCategoryNames
                        && category.TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } nameText)
                    {
                        _categoryNames.Add(nameText);
                    }
                }
            }
        }
    }

    // 记录字段路径（数组元素用 [] 表示）与取值种类，不记录任何取值。
    private void Walk(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                Note(path, "Object");
                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}");
                }

                break;
            case JsonValueKind.Array:
                Note(path, "Array");
                foreach (var child in element.EnumerateArray())
                {
                    Walk(child, $"{path}[]");
                }

                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                Note(path, "Boolean");
                break;
            default:
                Note(path, element.ValueKind.ToString());
                break;
        }
    }

    private void Note(string path, string kind)
    {
        if (path.Length == 0)
        {
            return;
        }

        if (!_fields.TryGetValue(path, out var kinds))
        {
            if (_fields.Count >= MaxPaths)
            {
                return;
            }

            kinds = new SortedSet<string>(StringComparer.Ordinal);
            _fields[path] = kinds;
        }

        kinds.Add(kind);
    }

    private static string? Format(DateTime? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
