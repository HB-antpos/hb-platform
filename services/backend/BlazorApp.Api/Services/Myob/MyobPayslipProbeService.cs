using BlazorApp.Api.Data;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Shared.DTOs;
using SqlSugar;

namespace BlazorApp.Api.Services.Myob;

/// <summary>探针里的一次请求：用的查询串模板（员工 UID 用占位符）、是否跳过、失败原因与结构摘要。</summary>
public sealed class MyobPayslipProbeStepDto
{
    public string Key { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>实际发给 MYOB 的查询串（空格还原为可读形式；员工 UID 以占位符显示，不暴露任何取值）。</summary>
    public string Query { get; set; } = string.Empty;

    public bool Skipped { get; set; }
    public string? SkipReason { get; set; }

    /// <summary>网络层失败（连不上 / 超时）。MYOB 返回的非 2xx 不算失败，状态码在 Result.StatusCode。</summary>
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public MyobAdviceProbeResult? Result { get; set; }
}

/// <summary>
/// 由探针结果推出的结论：true = 已证实可用，false = 已证实不可用，null = 数据不足无法判断（例如该公司没有足够历史工资单）。
/// </summary>
public sealed class MyobPayslipProbeVerdictsDto
{
    /// <summary>$filter=PaymentDate ge … and PaymentDate lt … 是否在服务端生效。</summary>
    public bool? DateFilterWorks { get; set; }

    /// <summary>$filter=Employee/UID eq guid'…' 是否在服务端生效。</summary>
    public bool? EmployeeFilterWorks { get; set; }

    public bool? TopWorks { get; set; }
    public bool? SkipWorks { get; set; }
    public bool? OrderByWorks { get; set; }
}

public sealed class MyobPayslipProbeDto
{
    public string ConnectionGuid { get; set; } = string.Empty;
    public string ConnectionName { get; set; } = string.Empty;

    /// <summary>探针运行日期（yyyy-MM-dd，澳洲东部时间），筛选日期窗口以它为准。</summary>
    public string RunDate { get; set; } = string.Empty;

    public List<MyobPayslipProbeStepDto> Steps { get; set; } = [];
    public MyobPayslipProbeVerdictsDto Verdicts { get; set; } = new();

    public string Note { get; set; } =
        "只读探针：只返回字段结构、取值种类和计数，不含任何姓名、金额、员工 UID 取值；MYOB 错误体只取错误类型名。";
}

public interface IMyobPayslipProbeService
{
    Task<ApiResponse<MyobPayslipProbeDto>> RunAsync(string connectionGuid, CancellationToken cancellationToken);
}

/// <summary>
/// 工资单报表探针（方案第 1.3 节）：官方文档没有写 Report/Payroll/EmployeePayrollAdvice 的查询参数，
/// 在已授权公司文件上用固定的几组只读 GET 核对：真实字段、分页、日期 / 员工筛选、$skip、$orderby。
/// 所有请求都是 GET，经同一个限流闸；响应体在 <see cref="IMyobApiClient.ProbePayrollAdviceAsync"/> 内部消化，只出结构与计数。
/// 核对完成、结论写回方案后，本服务与接口应删除。
/// </summary>
internal sealed class MyobPayslipProbeService : IMyobPayslipProbeService
{
    private const int MaxPages = 12;
    private const string EmployeePlaceholder = "<首条记录的员工 UID>";

    private readonly ISqlSugarClient _db;
    private readonly IMyobApiClient _client;
    private readonly IMyobTokenProvider _tokens;
    private readonly TimeProvider _time;

    public MyobPayslipProbeService(
        SqlSugarContext context,
        IMyobApiClient client,
        IMyobTokenProvider tokens,
        TimeProvider? timeProvider = null)
        : this(context.Db, client, tokens, timeProvider)
    {
    }

    internal MyobPayslipProbeService(
        ISqlSugarClient db,
        IMyobApiClient client,
        IMyobTokenProvider tokens,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _client = client;
        _tokens = tokens;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<ApiResponse<MyobPayslipProbeDto>> RunAsync(string connectionGuid, CancellationToken cancellationToken)
    {
        var connection = await _db.Queryable<MyobConnection>()
            .FirstAsync(item => item.ConnectionGuid == connectionGuid && item.Status == MyobConnectionStatuses.Active);
        if (connection is null)
        {
            return ApiResponse<MyobPayslipProbeDto>.Error("MYOB 连接不存在或未启用", MyobErrorCodes.ConnectionNotActive);
        }

        string accessToken;
        try
        {
            accessToken = await _tokens.GetAccessTokenAsync(connectionGuid, cancellationToken);
        }
        catch (MyobApiException ex)
        {
            return ApiResponse<MyobPayslipProbeDto>.Error(ex.Message, ex.Code);
        }

        // 澳洲东部标准时间（UTC+10）足够用来取「今天」，与推送预览同口径。
        var today = _time.GetUtcNow().UtcDateTime.AddHours(10).Date;
        var wideFrom = today.AddDays(-90);
        var rangeFrom = today.AddDays(-45);
        var rangeTo = today.AddDays(1);
        var dto = new MyobPayslipProbeDto
        {
            ConnectionGuid = connection.ConnectionGuid,
            ConnectionName = connection.BusinessName,
            RunDate = today.ToString("yyyy-MM-dd"),
        };

        async Task<MyobPayslipProbeStepDto> Run(string key, string description, string query, int maxPages = 1, string? displayQuery = null)
        {
            var step = new MyobPayslipProbeStepDto { Key = key, Description = description, Query = Display(displayQuery ?? query) };
            try
            {
                step.Result = await _client.ProbePayrollAdviceAsync(connection.BusinessId, accessToken, query, maxPages, cancellationToken);
            }
            catch (MyobApiException ex)
            {
                step.ErrorCode = ex.Code;
                step.ErrorMessage = ex.Message;
            }

            dto.Steps.Add(step);
            return step;
        }

        var top5 = await Run("noFilterTop5", "不带筛选取 5 条：看真实字段结构与 $top", MyobAdviceQueries.Compose(5, null, null, null));
        var full = await Run(
            "fullScan", "不带筛选翻页取全部（最多 12 页）：看数据量、日期跨度与分页", MyobAdviceQueries.Compose(1000, null, null, null), MaxPages);
        await Run(
            "dateGe", "按支付日期筛选（仅下界，近 90 天）取 5 条",
            MyobAdviceQueries.Compose(5, null, MyobAdviceQueries.Filter(new MyobPayrollAdviceQuery(PaymentFrom: wideFrom)), null));
        var range = await Run(
            "dateRange", "按支付日期区间筛选（近 45 天，含上下界）翻页取全部",
            MyobAdviceQueries.Build(new MyobPayrollAdviceQuery(rangeFrom, rangeTo)), MaxPages);

        // 员工 UID 取自前面任一步的首条记录，只在这里使用，不写进 DTO。
        var uid = top5.Result?.FirstEmployeeUid ?? full.Result?.FirstEmployeeUid;
        MyobPayslipProbeStepDto employee;
        if (string.IsNullOrWhiteSpace(uid))
        {
            employee = new MyobPayslipProbeStepDto
            {
                Key = "employeeGuid",
                Description = "按员工 UID 筛选取 5 条",
                Query = Display(MyobAdviceQueries.Compose(5, null, $"Employee/UID eq guid'{EmployeePlaceholder}'", null)),
                Skipped = true,
                SkipReason = "前面的请求没有取到任何带员工 UID 的记录，无法构造筛选",
            };
            dto.Steps.Add(employee);
        }
        else
        {
            var filter = MyobAdviceQueries.Filter(new MyobPayrollAdviceQuery(EmployeeUid: uid));
            employee = await Run(
                "employeeGuid", "按员工 UID 筛选取 5 条（UID 取自首条记录，不在结果里显示）",
                MyobAdviceQueries.Compose(5, null, filter, null), 1,
                MyobAdviceQueries.Compose(5, null, $"Employee/UID eq guid'{EmployeePlaceholder}'", null));
        }

        var total = full.Result is { StatusCode: 200, Truncated: false } ? full.Result.ItemCount : (int?)null;
        MyobPayslipProbeStepDto skip;
        if (total is > 0)
        {
            // 跳过全部但最后 2 条：$skip 生效 → 只剩 min(2, total) 条；被忽略 → 回到整页。
            skip = await Run(
                "skip", "$skip 跳到最后 2 条：核对 $skip 是否生效",
                MyobAdviceQueries.Compose(1000, Math.Max(total.Value - 2, 0), null, null));
        }
        else
        {
            skip = new MyobPayslipProbeStepDto
            {
                Key = "skip",
                Description = "$skip 跳到最后 2 条：核对 $skip 是否生效",
                Query = Display(MyobAdviceQueries.Compose(1000, 0, null, null)),
                Skipped = true,
                SkipReason = "没有完整的全量计数（没有数据，或翻页被截断），无法判断",
            };
            dto.Steps.Add(skip);
        }

        var order = await Run(
            "orderByDesc", "$orderby=PaymentDate desc 取 3 条：核对排序是否生效",
            MyobAdviceQueries.Compose(3, null, null, "PaymentDate desc"));

        dto.Verdicts = ComputeVerdicts(top5, full, range, employee, skip, order, rangeFrom, rangeTo);
        return ApiResponse<MyobPayslipProbeDto>.OK(dto);
    }

    /// <summary>纯函数：由各步结构摘要推出结论，便于单测。数据不足一律返回 null，不猜。</summary>
    internal static MyobPayslipProbeVerdictsDto ComputeVerdicts(
        MyobPayslipProbeStepDto top5,
        MyobPayslipProbeStepDto full,
        MyobPayslipProbeStepDto range,
        MyobPayslipProbeStepDto employee,
        MyobPayslipProbeStepDto skip,
        MyobPayslipProbeStepDto order,
        DateTime rangeFrom,
        DateTime rangeToExclusive) =>
        new()
        {
            DateFilterWorks = DateFilter(full.Result, range.Result, rangeFrom, rangeToExclusive),
            EmployeeFilterWorks = EmployeeFilter(employee),
            TopWorks = Top(top5.Result, full.Result),
            SkipWorks = Skip(full.Result, skip),
            OrderByWorks = OrderBy(order.Result),
        };

    private static bool? DateFilter(MyobAdviceProbeResult? full, MyobAdviceProbeResult? range, DateTime from, DateTime toExclusive)
    {
        if (range is null)
        {
            return null;
        }

        if (range.StatusCode != 200)
        {
            return false;
        }

        // 返回了区间外的记录 = 筛选被忽略（或写法被误解）。
        if (range.ItemCount > 0
            && (string.CompareOrdinal(range.MinPaymentDate, from.ToString("yyyy-MM-dd")) < 0
                || string.CompareOrdinal(range.MaxPaymentDate, toExclusive.ToString("yyyy-MM-dd")) >= 0))
        {
            return false;
        }

        // 全量里记录数更多，说明筛选真的把区间外的记录滤掉了。
        return full is { StatusCode: 200 } && full.ItemCount > range.ItemCount ? true : null;
    }

    private static bool? EmployeeFilter(MyobPayslipProbeStepDto employee)
    {
        if (employee.Skipped || employee.Result is null)
        {
            return null;
        }

        var result = employee.Result;
        if (result.StatusCode != 200)
        {
            return false;
        }

        // 用一个确实存在的员工 UID 筛选，应当只剩这一个人的记录。
        return result.ItemCount > 0 && result.DistinctEmployees == 1;
    }

    private static bool? Top(MyobAdviceProbeResult? top5, MyobAdviceProbeResult? full)
    {
        if (top5 is not { StatusCode: 200 })
        {
            return top5 is null ? null : false;
        }

        if (top5.ItemCount > 5)
        {
            return false;
        }

        return full is { StatusCode: 200 } && full.ItemCount > 5 ? true : null;
    }

    private static bool? Skip(MyobAdviceProbeResult? full, MyobPayslipProbeStepDto skip)
    {
        if (skip.Skipped || skip.Result is null || full is not { StatusCode: 200, Truncated: false })
        {
            return null;
        }

        if (skip.Result.StatusCode != 200)
        {
            return false;
        }

        return skip.Result.ItemCount == Math.Min(2, full.ItemCount);
    }

    private static bool? OrderBy(MyobAdviceProbeResult? order)
    {
        if (order is null)
        {
            return null;
        }

        if (order.StatusCode != 200)
        {
            return false;
        }

        if (order.FirstItemPaymentDate is null || order.LastItemPaymentDate is null
            || order.FirstItemPaymentDate == order.LastItemPaymentDate)
        {
            return null;
        }

        // 降序：第一条的支付日期应晚于最后一条。
        return string.CompareOrdinal(order.FirstItemPaymentDate, order.LastItemPaymentDate) > 0;
    }

    private static string Display(string query) => query.Replace("%20", " ", StringComparison.Ordinal);
}
