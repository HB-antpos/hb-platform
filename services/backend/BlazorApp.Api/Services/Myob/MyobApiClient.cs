using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace BlazorApp.Api.Services.Myob;

/// <summary>MYOB token 端点返回；refresh 响应没有 user 字段，且 refresh_token 可能轮换，调用方必须落库新值。</summary>
public sealed record MyobTokenResponse(string AccessToken, string? RefreshToken, int ExpiresInSeconds);

/// <summary>GET /{cf_uri}/Company 的摘要：公司名与 ABN / TFN（只有 11 位数字才是 ABN，由调用方判断）。</summary>
public sealed record MyobCompanyInfo(string CompanyName, string? AbnOrTfn);

/// <summary>Contact/Employee.Addresses 的一条地址（资料同步用：拼成一行写入我方住址）。</summary>
public sealed record MyobAddressRecord(string? Street, string? City, string? State, string? PostCode, string? Country);

/// <summary>Contact/Employee 的一条（只取绑定与资料同步需要的字段）。邮箱 / 手机来自所有 Addresses 的合集；Addresses 保留原始地址行。</summary>
public sealed record MyobEmployeeRecord(
    string Uid,
    string? DisplayId,
    string FirstName,
    string LastName,
    string? CompanyName,
    bool IsIndividual,
    bool IsActive,
    IReadOnlyList<string> Emails,
    IReadOnlyList<string> Phones,
    IReadOnlyList<MyobAddressRecord>? Addresses = null);

/// <summary>EmployeePayrollDetails.Entitlements 的一项：假期累计类别（只要名字和是否已分配）。</summary>
public sealed record MyobEntitlementRecord(string? CategoryName, bool IsAssigned);

/// <summary>
/// Contact/EmployeePayrollDetails 的一条（绑定、类型检查与资料同步需要的字段）。
/// Gender 为 MYOB 原值（Male / Female / Other）；SuperFundName / SuperMembershipNumber 取自 Superannuation 节。
/// </summary>
public sealed record MyobEmployeePayrollRecord(
    string EmployeeUid,
    DateTime? DateOfBirth,
    string? EmploymentStatus,
    IReadOnlyList<MyobEntitlementRecord> Entitlements,
    string? Gender = null,
    string? SuperFundName = null,
    string? SuperMembershipNumber = null);

/// <summary>EmployeePaymentDetails.BankAccounts 的一条。</summary>
public sealed record MyobBankAccountRecord(string? Bsb, string? AccountNumber);

/// <summary>Contact/EmployeePaymentDetails 的一条（资料同步只取银行账户）。</summary>
public sealed record MyobEmployeePaymentRecord(string EmployeeUid, IReadOnlyList<MyobBankAccountRecord> BankAccounts);

/// <summary>Payroll/PayrollCategory/Wage 的一条（只取映射需要的字段）。</summary>
public sealed record MyobWageCategoryRecord(string Uid, string Name, string? WageType);

/// <summary>某员工在某周期的 MYOB Timesheet 现状（只读判断用）。</summary>
public sealed record MyobTimesheetStatus(bool HasEntries, bool HasProcessedEntries);

/// <summary>Timesheet 里的一天：Date 保留 MYOB 返回的原文（用来核对日期格式），Hours 保留原有小数位。</summary>
public sealed record MyobTimesheetEntryRecord(string? Date, decimal? Hours, bool Processed);

/// <summary>Timesheet 的一行（一个工资类别）：只取类别 UID 与每天的工时，不取 Job / Notes。</summary>
public sealed record MyobTimesheetLineRecord(string? CategoryUid, IReadOnlyList<MyobTimesheetEntryRecord> Entries);

/// <summary>PUT Timesheet 的一天。Date 原样写进请求体，由调用方决定格式（yyyy-MM-dd 或带时间）。</summary>
public sealed record MyobTimesheetPutEntry(string Date, decimal Hours);

public sealed record MyobTimesheetPutLine(string CategoryUid, IReadOnlyList<MyobTimesheetPutEntry> Entries);

/// <summary>
/// PUT / DELETE Timesheet 的结果：HTTP 状态码与 MYOB 错误类型名 / 错误码（只取 Errors[0].Name 与 Errors[0].ErrorCode，不回显响应体）。
/// 非 2xx 不抛异常，由调用方判断。
/// </summary>
public sealed record MyobTimesheetPutResult(int StatusCode, string? ErrorName, string? ErrorCode = null)
{
    public bool Success => StatusCode is >= 200 and < 300;
}

/// <summary>
/// 调用 MYOB 失败。Code 取自 <see cref="MyobErrorCodes"/>；IsAuthorizationRevoked＝refresh token 已失效（invalid_grant），
/// 需要公司文件 Administrator 重新授权，调用方据此把连接标成 NeedsReauth。
/// 消息里绝不包含 token / secret / 授权码。
/// </summary>
public sealed class MyobApiException(
    string code,
    string message,
    HttpStatusCode? statusCode = null,
    bool isAuthorizationRevoked = false) : Exception(message)
{
    public string Code { get; } = code;
    public HttpStatusCode? StatusCode { get; } = statusCode;
    public bool IsAuthorizationRevoked { get; } = isAuthorizationRevoked;
}

public interface IMyobApiClient
{
    /// <summary>授权码换 token（授权码 2~5 分钟过期，回调里必须立即调用）。</summary>
    Task<MyobTokenResponse> ExchangeCodeAsync(string code, CancellationToken cancellationToken);

    Task<MyobTokenResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken);

    Task<MyobCompanyInfo> GetCompanyAsync(string businessId, string accessToken, CancellationToken cancellationToken);

    /// <summary>GET /{cf_uri}/Contact/Employee，自动翻页拉全量（含已停用）。需要 scope sme-contacts-employee。</summary>
    Task<IReadOnlyList<MyobEmployeeRecord>> GetEmployeesAsync(string businessId, string accessToken, CancellationToken cancellationToken);

    /// <summary>GET /{cf_uri}/Contact/EmployeePayrollDetails，自动翻页拉全量。需要 scope sme-payroll。</summary>
    Task<IReadOnlyList<MyobEmployeePayrollRecord>> GetEmployeePayrollDetailsAsync(string businessId, string accessToken, CancellationToken cancellationToken);

    /// <summary>GET /{cf_uri}/Contact/EmployeePaymentDetails，自动翻页拉全量（银行账户）。需要 scope sme-payroll；资料同步按需调用，失败不影响绑定页。</summary>
    Task<IReadOnlyList<MyobEmployeePaymentRecord>> GetEmployeePaymentDetailsAsync(string businessId, string accessToken, CancellationToken cancellationToken);

    /// <summary>GET /{cf_uri}/Payroll/PayrollCategory/Wage：公司文件里的工资类别（用于 Timesheet 类别映射）。需要 scope sme-payroll。</summary>
    Task<IReadOnlyList<MyobWageCategoryRecord>> GetWageCategoriesAsync(string businessId, string accessToken, CancellationToken cancellationToken);

    /// <summary>
    /// GET /{cf_uri}/Payroll/Timesheet/{employeeUid}?StartDate=&EndDate=：只读，判断该员工该周期在 MYOB 里是否已有记录 / 已发薪（Processed）。
    /// MYOB 对没有记录的员工可能返回空体或 404，都按「没有记录」处理。
    /// </summary>
    Task<MyobTimesheetStatus> GetTimesheetStatusAsync(
        string businessId, string accessToken, string employeeUid, DateTime startDate, DateTime endDate, CancellationToken cancellationToken);

    /// <summary>
    /// GET /{cf_uri}/Report/Payroll/EmployeePayrollAdvice：已发薪工资单（只读）。需要 scope sme-payroll。
    /// query 的支付日期区间 [PaymentFrom, PaymentToExclusive) 必须两端都有：按 ≤90 天窗口并发取数，窗口满页（1000 条）时在窗口内用 $skip 继续翻页，
    /// 不跟 NextPageLink（服务端到顶后它是空的，会静默漏数据）；单次请求遇 5xx 重试 1 次；结果按「员工 + 支付日期 + 周期 + 金额」去重，
    /// 支付日期倒序返回。只返回展示用精简对象（不含年薪 / 时薪 / 生日 / 支票号）。
    /// 默认实现抛 NotSupportedException：既有测试替身无需改动，真实客户端覆盖。
    /// </summary>
    Task<IReadOnlyList<MyobPayrollAdviceRecord>> GetPayrollAdvicesAsync(
        string businessId, string accessToken, MyobPayrollAdviceQuery query, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <summary>
    /// GET /{cf_uri}/Payroll/Timesheet/{employeeUid}?StartDate=&EndDate=：返回每行的类别 UID 与每天的工时 / Processed。
    /// 404 或空体 = 没有记录（空列表）；其他非 2xx 抛 <see cref="MyobApiException"/>（只含状态码）。默认实现抛 NotSupportedException。
    /// </summary>
    Task<IReadOnlyList<MyobTimesheetLineRecord>> GetTimesheetLinesAsync(
        string businessId, string accessToken, string employeeUid, DateTime startDate, DateTime endDate, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <summary>
    /// PUT /{cf_uri}/Payroll/Timesheet/{employeeUid}?StartDate=&EndDate=：**写 MYOB**。
    /// 目前只有临时的写入探针（Myob__WriteProbe*，只对一个连接、一张测试员工卡）调用；正式推送在探针实测、方案定稿之后另批实现。
    /// 默认实现抛 NotSupportedException。
    /// </summary>
    Task<MyobTimesheetPutResult> PutTimesheetAsync(
        string businessId,
        string accessToken,
        string employeeUid,
        DateTime startDate,
        DateTime endDate,
        IReadOnlyList<MyobTimesheetPutLine> lines,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <summary>
    /// DELETE /{cf_uri}/Payroll/Timesheet/{employeeUid}?StartDate=&EndDate=：**写 MYOB**。官方文档只列了 DELETE，没说删什么、已发薪记录能不能删，
    /// 所以目前只有临时的写入探针调用，实测后才决定正式推送 / 撤销用不用它。默认实现抛 NotSupportedException。
    /// </summary>
    Task<MyobTimesheetPutResult> DeleteTimesheetAsync(
        string businessId, string accessToken, string employeeUid, DateTime startDate, DateTime endDate, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>
/// MYOB Business API v2 的最小客户端（阶段 0 只需 OAuth 与 Company）。
/// 所有请求经同一个最小间隔限流（MYOB 每个 API Key 每秒 8 次）。
/// </summary>
public sealed class MyobApiClient : IMyobApiClient, IMyobRawReadClient
{
    private const int MaxErrorTextLength = 200;

    // 进程内共用：限流针对 API Key，而不是针对某个连接。
    private static readonly SemaphoreSlim RateGate = new(1, 1);
    private static long _lastRequestTimestamp;

    // MYOB 文档示例里 expires_in 是带引号的字符串，所以允许从字符串读数字；属性名大小写不敏感。
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _http;
    private readonly MyobOptions _options;

    public MyobApiClient(HttpClient http, IOptions<MyobOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public Task<MyobTokenResponse> ExchangeCodeAsync(string code, CancellationToken cancellationToken) =>
        PostTokenAsync(
            new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["scope"] = _options.Scopes,
                ["code"] = code,
                ["redirect_uri"] = _options.RedirectUri,
                ["grant_type"] = "authorization_code",
            },
            MyobErrorCodes.TokenExchangeFailed,
            cancellationToken);

    public Task<MyobTokenResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        PostTokenAsync(
            new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token",
            },
            MyobErrorCodes.TokenExchangeFailed,
            cancellationToken);

    public async Task<MyobCompanyInfo> GetCompanyAsync(
        string businessId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var url = $"{_options.ApiBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(businessId)}/Company";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("x-myobapi-key", _options.ClientId);
        request.Headers.TryAddWithoutValidation("x-myobapi-version", "v2");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new MyobApiException(
                MyobErrorCodes.ApiCallFailed,
                $"MYOB 公司信息接口返回 {(int)response.StatusCode}",
                response.StatusCode);
        }

        CompanyPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CompanyPayload>(body, JsonOptions);
        }
        catch (JsonException)
        {
            throw new MyobApiException(MyobErrorCodes.ApiCallFailed, "MYOB 公司信息接口返回了无法解析的内容", response.StatusCode);
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.CompanyName))
        {
            throw new MyobApiException(MyobErrorCodes.ApiCallFailed, "MYOB 公司信息接口没有返回公司名称", response.StatusCode);
        }

        return new MyobCompanyInfo(payload.CompanyName.Trim(), payload.AbnOrTfn);
    }

    public async Task<IReadOnlyList<MyobEmployeeRecord>> GetEmployeesAsync(
        string businessId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var items = await GetAllPagesAsync<EmployeePayload>(businessId, accessToken, "Contact/Employee", cancellationToken);
        return items
            .Where(item => !string.IsNullOrWhiteSpace(item.Uid))
            .Select(item => new MyobEmployeeRecord(
                item.Uid!,
                string.IsNullOrWhiteSpace(item.DisplayId) ? null : item.DisplayId.Trim(),
                item.FirstName?.Trim() ?? string.Empty,
                item.LastName?.Trim() ?? string.Empty,
                string.IsNullOrWhiteSpace(item.CompanyName) ? null : item.CompanyName.Trim(),
                item.IsIndividual ?? true,
                item.IsActive ?? true,
                (item.Addresses ?? [])
                    .Select(address => address.Email?.Trim())
                    .Where(email => !string.IsNullOrWhiteSpace(email))
                    .Select(email => email!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                (item.Addresses ?? [])
                    .SelectMany(address => new[] { address.Phone1, address.Phone2, address.Phone3 })
                    .Where(phone => !string.IsNullOrWhiteSpace(phone))
                    .Select(phone => phone!.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
                (item.Addresses ?? [])
                    .Where(address => !string.IsNullOrWhiteSpace(address.Street)
                        || !string.IsNullOrWhiteSpace(address.City)
                        || !string.IsNullOrWhiteSpace(address.PostCode))
                    .Select(address => new MyobAddressRecord(
                        address.Street?.Trim(), address.City?.Trim(), address.State?.Trim(),
                        address.PostCode?.Trim(), address.Country?.Trim()))
                    .ToList()))
            .ToList();
    }

    // 临时探针专用：原样返回 EmployeePayrollDetails 的 JSON，供核对 Wage 节真实字段名（见 MyobWageProbeService）。
    public async Task<IReadOnlyList<JsonElement>> GetEmployeePayrollDetailsRawAsync(
        string businessId,
        string accessToken,
        CancellationToken cancellationToken) =>
        await GetAllPagesAsync<JsonElement>(businessId, accessToken, "Contact/EmployeePayrollDetails", cancellationToken);

    public async Task<IReadOnlyList<MyobEmployeePayrollRecord>> GetEmployeePayrollDetailsAsync(
        string businessId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var items = await GetAllPagesAsync<EmployeePayrollPayload>(
            businessId, accessToken, "Contact/EmployeePayrollDetails", cancellationToken);
        return items
            .Where(item => !string.IsNullOrWhiteSpace(item.Employee?.Uid))
            .Select(item => new MyobEmployeePayrollRecord(
                item.Employee!.Uid!,
                item.DateOfBirth,
                string.IsNullOrWhiteSpace(item.EmploymentStatus) ? null : item.EmploymentStatus.Trim(),
                (item.Entitlements ?? [])
                    .Select(entitlement => new MyobEntitlementRecord(
                        entitlement.EntitlementCategory?.Name?.Trim(),
                        entitlement.IsAssigned ?? false))
                    .ToList(),
                string.IsNullOrWhiteSpace(item.Gender) ? null : item.Gender.Trim(),
                string.IsNullOrWhiteSpace(item.Superannuation?.SuperannuationFund?.Name)
                    ? null
                    : item.Superannuation!.SuperannuationFund!.Name!.Trim(),
                string.IsNullOrWhiteSpace(item.Superannuation?.EmployeeMembershipNumber)
                    ? null
                    : item.Superannuation!.EmployeeMembershipNumber!.Trim()))
            .ToList();
    }

    public async Task<IReadOnlyList<MyobEmployeePaymentRecord>> GetEmployeePaymentDetailsAsync(
        string businessId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var items = await GetAllPagesAsync<EmployeePaymentPayload>(
            businessId, accessToken, "Contact/EmployeePaymentDetails", cancellationToken);
        return items
            .Where(item => !string.IsNullOrWhiteSpace(item.Employee?.Uid))
            .Select(item => new MyobEmployeePaymentRecord(
                item.Employee!.Uid!,
                (item.BankAccounts ?? [])
                    .Select(account => new MyobBankAccountRecord(
                        string.IsNullOrWhiteSpace(account.BsbNumber) ? null : account.BsbNumber.Trim(),
                        string.IsNullOrWhiteSpace(account.BankAccountNumber) ? null : account.BankAccountNumber.Trim()))
                    .Where(account => account.Bsb is not null || account.AccountNumber is not null)
                    .ToList()))
            .ToList();
    }

    /// <summary>
    /// 拉某个列表接口的全部页。MYOB 列表响应形如 { Items, NextPageLink, Count }：
    /// 先请求 $top=1000，再沿 NextPageLink 翻页；只跟随与 ApiBaseUrl 同主机的链接（防止被带到别处），最多翻 50 页兜底。
    /// 任何非 2xx 都按 ApiCallFailed 处理，消息只含状态码（401 多半是 scope 不够，403 是没有权限）。
    /// </summary>
    private async Task<List<T>> GetAllPagesAsync<T>(
        string businessId,
        string accessToken,
        string path,
        CancellationToken cancellationToken)
    {
        const int maxPages = 50;
        var baseUrl = _options.ApiBaseUrl.TrimEnd('/');
        var baseHost = new Uri(baseUrl).Host;
        string? url = $"{baseUrl}/{Uri.EscapeDataString(businessId)}/{path}?$top=1000";
        var result = new List<T>();

        for (var page = 0; page < maxPages && url is not null; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("x-myobapi-key", _options.ClientId);
            request.Headers.TryAddWithoutValidation("x-myobapi-version", "v2");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new MyobApiException(
                    MyobErrorCodes.ApiCallFailed,
                    $"MYOB {path} 接口返回 {(int)response.StatusCode}",
                    response.StatusCode);
            }

            ListPayload<T>? payload;
            try
            {
                payload = JsonSerializer.Deserialize<ListPayload<T>>(body, JsonOptions);
            }
            catch (JsonException)
            {
                throw new MyobApiException(MyobErrorCodes.ApiCallFailed, $"MYOB {path} 接口返回了无法解析的内容", response.StatusCode);
            }

            result.AddRange(payload?.Items ?? []);

            url = null;
            if (!string.IsNullOrWhiteSpace(payload?.NextPageLink)
                && Uri.TryCreate(payload.NextPageLink, UriKind.Absolute, out var next)
                && string.Equals(next.Host, baseHost, StringComparison.OrdinalIgnoreCase))
            {
                url = next.ToString();
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<MyobWageCategoryRecord>> GetWageCategoriesAsync(
        string businessId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var items = await GetAllPagesAsync<WageCategoryPayload>(
            businessId, accessToken, "Payroll/PayrollCategory/Wage", cancellationToken);
        return items
            .Where(item => !string.IsNullOrWhiteSpace(item.Uid) && !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => new MyobWageCategoryRecord(
                item.Uid!, item.Name!.Trim(), string.IsNullOrWhiteSpace(item.WageType) ? null : item.WageType.Trim()))
            .ToList();
    }

    // 窗口并发数：RateGate 只约束相邻请求的发起间隔，不串行化整个请求；这里再限并发，避免一次查询占满 MYOB 配额。
    private const int AdviceWindowConcurrency = 4;

    // 单个窗口最多翻的页数：满页后继续 $skip；超过说明窗口里的数据量异常，宁可报错也不静默截断。
    private const int AdviceMaxPagesPerWindow = 20;

    public async Task<IReadOnlyList<MyobPayrollAdviceRecord>> GetPayrollAdvicesAsync(
        string businessId,
        string accessToken,
        MyobPayrollAdviceQuery query,
        CancellationToken cancellationToken)
    {
        if (query.PaymentFrom is null || query.PaymentToExclusive is null)
        {
            throw new ArgumentException("工资单查询必须指定支付日期区间", nameof(query));
        }

        var windows = MyobAdviceWindows.Split(query.PaymentFrom.Value, query.PaymentToExclusive.Value);
        using var gate = new SemaphoreSlim(AdviceWindowConcurrency);
        var tasks = windows.Select(async window =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await GetAdviceWindowAsync(
                    businessId, accessToken, query with { PaymentFrom = window.From, PaymentToExclusive = window.ToExclusive }, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        var pages = await Task.WhenAll(tasks);

        // 窗口边界与 $skip 翻页期间有新工资单插入时可能出现重复；金额也进键，避免把「同一周期同一天的更正单」误当重复吞掉。
        return pages
            .SelectMany(items => items)
            .GroupBy(item => (item.EmployeeUid.ToLowerInvariant(), item.PaymentDate, item.PeriodStart, item.PeriodEnd, item.GrossPay, item.NetPay))
            .Select(group => group.First())
            .OrderByDescending(item => item.PaymentDate)
            .ThenBy(item => item.EmployeeName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>取一个窗口：满页就继续 $skip，直到某页不足一页。</summary>
    private async Task<List<MyobPayrollAdviceRecord>> GetAdviceWindowAsync(
        string businessId,
        string accessToken,
        MyobPayrollAdviceQuery windowQuery,
        CancellationToken cancellationToken)
    {
        var records = new List<MyobPayrollAdviceRecord>();
        for (var page = 0; page < AdviceMaxPagesPerWindow; page++)
        {
            var items = await GetAdvicePageAsync(
                businessId, accessToken, MyobAdviceQueries.Build(windowQuery, page * MyobAdviceQueries.PageSize), cancellationToken);
            records.AddRange(items.Select(PayrollAdvicePayload.ToRecord).OfType<MyobPayrollAdviceRecord>());
            if (items.Count < MyobAdviceQueries.PageSize)
            {
                return records;
            }
        }

        throw new MyobApiException(
            MyobErrorCodes.ApiCallFailed,
            $"MYOB 工资单报表在一个查询窗口内超过 {AdviceMaxPagesPerWindow * MyobAdviceQueries.PageSize} 条，请缩小日期范围");
    }

    /// <summary>取一页（原始条数用于判断是否满页）；5xx 重试 1 次，4xx 与再次失败只报状态码，不回显响应体（含薪资）。</summary>
    private async Task<List<PayrollAdvicePayload>> GetAdvicePageAsync(
        string businessId,
        string accessToken,
        string query,
        CancellationToken cancellationToken)
    {
        var url = $"{_options.ApiBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(businessId)}/{MyobAdviceQueries.ReportPath}?{query}";
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("x-myobapi-key", _options.ClientId);
            request.Headers.TryAddWithoutValidation("x-myobapi-version", "v2");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await SendAsync(request, cancellationToken);
            if ((int)response.StatusCode >= 500 && attempt == 0)
            {
                // 探针里见过 MYOB 偶发 500（重跑正常）。
                continue;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new MyobApiException(
                    MyobErrorCodes.ApiCallFailed,
                    $"MYOB 工资单报表接口返回 {(int)response.StatusCode}",
                    response.StatusCode);
            }

            try
            {
                return JsonSerializer.Deserialize<ListPayload<PayrollAdvicePayload>>(body, JsonOptions)?.Items ?? [];
            }
            catch (JsonException)
            {
                throw new MyobApiException(MyobErrorCodes.ApiCallFailed, "MYOB 工资单报表接口返回了无法解析的内容", response.StatusCode);
            }
        }
    }

    public async Task<MyobTimesheetStatus> GetTimesheetStatusAsync(
        string businessId,
        string accessToken,
        string employeeUid,
        DateTime startDate,
        DateTime endDate,
        CancellationToken cancellationToken)
    {
        var url = $"{_options.ApiBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(businessId)}/Payroll/Timesheet/{Uri.EscapeDataString(employeeUid)}"
            + $"?StartDate={startDate:yyyy-MM-dd}&EndDate={endDate:yyyy-MM-dd}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("x-myobapi-key", _options.ClientId);
        request.Headers.TryAddWithoutValidation("x-myobapi-version", "v2");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new MyobTimesheetStatus(false, false);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new MyobApiException(
                MyobErrorCodes.ApiCallFailed,
                $"MYOB Timesheet 接口返回 {(int)response.StatusCode}",
                response.StatusCode);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return new MyobTimesheetStatus(false, false);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var hasEntries = false;
            var processed = false;
            // 响应可能是单个 Timesheet（根上有 Lines），也可能是 { Items: [...] } 列表：两种都兼容。
            foreach (var timesheet in EnumerateTimesheets(document.RootElement))
            {
                if (!timesheet.TryGetProperty("Lines", out var lines) || lines.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var line in lines.EnumerateArray())
                {
                    if (!line.TryGetProperty("Entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var entry in entries.EnumerateArray())
                    {
                        hasEntries = true;
                        if (entry.TryGetProperty("Processed", out var flag) && flag.ValueKind == JsonValueKind.True)
                        {
                            processed = true;
                        }
                    }
                }
            }

            return new MyobTimesheetStatus(hasEntries, processed);
        }
        catch (JsonException)
        {
            throw new MyobApiException(MyobErrorCodes.ApiCallFailed, "MYOB Timesheet 接口返回了无法解析的内容", response.StatusCode);
        }
    }

    public async Task<IReadOnlyList<MyobTimesheetLineRecord>> GetTimesheetLinesAsync(
        string businessId,
        string accessToken,
        string employeeUid,
        DateTime startDate,
        DateTime endDate,
        CancellationToken cancellationToken)
    {
        using var request = CreateTimesheetRequest(HttpMethod.Get, businessId, accessToken, employeeUid, startDate, endDate);
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new MyobApiException(
                MyobErrorCodes.ApiCallFailed,
                $"MYOB Timesheet 接口返回 {(int)response.StatusCode}",
                response.StatusCode);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var result = new List<MyobTimesheetLineRecord>();
            foreach (var timesheet in EnumerateTimesheets(document.RootElement))
            {
                if (!timesheet.TryGetProperty("Lines", out var lines) || lines.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var line in lines.EnumerateArray())
                {
                    string? categoryUid = null;
                    if (line.TryGetProperty("PayrollCategory", out var category)
                        && category.ValueKind == JsonValueKind.Object
                        && category.TryGetProperty("UID", out var uid)
                        && uid.ValueKind == JsonValueKind.String)
                    {
                        categoryUid = uid.GetString();
                    }

                    var entries = new List<MyobTimesheetEntryRecord>();
                    if (line.TryGetProperty("Entries", out var rawEntries) && rawEntries.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in rawEntries.EnumerateArray())
                        {
                            string? date = entry.TryGetProperty("Date", out var dateElement) && dateElement.ValueKind == JsonValueKind.String
                                ? dateElement.GetString()
                                : null;
                            decimal? hours = entry.TryGetProperty("Hours", out var hoursElement)
                                && hoursElement.ValueKind == JsonValueKind.Number
                                && hoursElement.TryGetDecimal(out var value)
                                    ? value
                                    : null;
                            var processed = entry.TryGetProperty("Processed", out var flag) && flag.ValueKind == JsonValueKind.True;
                            entries.Add(new MyobTimesheetEntryRecord(date, hours, processed));
                        }
                    }

                    result.Add(new MyobTimesheetLineRecord(categoryUid, entries));
                }
            }

            return result;
        }
        catch (JsonException)
        {
            throw new MyobApiException(MyobErrorCodes.ApiCallFailed, "MYOB Timesheet 接口返回了无法解析的内容", response.StatusCode);
        }
    }

    public async Task<MyobTimesheetPutResult> PutTimesheetAsync(
        string businessId,
        string accessToken,
        string employeeUid,
        DateTime startDate,
        DateTime endDate,
        IReadOnlyList<MyobTimesheetPutLine> lines,
        CancellationToken cancellationToken)
    {
        // 属性名按 MYOB 的 PascalCase 原样写（不套 Web 默认的 camelCase）。
        var payload = new
        {
            Employee = new { UID = employeeUid },
            StartDate = startDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            EndDate = endDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            Lines = lines.Select(line => new
            {
                PayrollCategory = new { UID = line.CategoryUid },
                Entries = line.Entries.Select(entry => new { entry.Date, entry.Hours }).ToList(),
            }).ToList(),
        };
        using var request = CreateTimesheetRequest(HttpMethod.Put, businessId, accessToken, employeeUid, startDate, endDate);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

        using var response = await SendAsync(request, cancellationToken);
        return await ReadWriteResultAsync(response, cancellationToken);
    }

    public async Task<MyobTimesheetPutResult> DeleteTimesheetAsync(
        string businessId,
        string accessToken,
        string employeeUid,
        DateTime startDate,
        DateTime endDate,
        CancellationToken cancellationToken)
    {
        using var request = CreateTimesheetRequest(HttpMethod.Delete, businessId, accessToken, employeeUid, startDate, endDate);
        using var response = await SendAsync(request, cancellationToken);
        return await ReadWriteResultAsync(response, cancellationToken);
    }

    /// <summary>写请求的结果：2xx 只给状态码；非 2xx 只取 Errors[0].Name 与 Errors[0].ErrorCode，不回显响应体；解析失败就只给状态码。</summary>
    private static async Task<MyobTimesheetPutResult> ReadWriteResultAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return new MyobTimesheetPutResult((int)response.StatusCode, null);
        }

        string? errorName = null;
        string? errorCode = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("Errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array
                && errors.GetArrayLength() > 0
                && errors[0].ValueKind == JsonValueKind.Object)
            {
                if (errors[0].TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    errorName = Truncate(name.GetString() ?? string.Empty);
                }

                if (errors[0].TryGetProperty("ErrorCode", out var code)
                    && code.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                {
                    errorCode = Truncate(code.ValueKind == JsonValueKind.String ? code.GetString() ?? string.Empty : code.GetRawText());
                }
            }
        }
        catch (JsonException)
        {
        }

        return new MyobTimesheetPutResult((int)response.StatusCode, errorName, errorCode);
    }

    private HttpRequestMessage CreateTimesheetRequest(
        HttpMethod method, string businessId, string accessToken, string employeeUid, DateTime startDate, DateTime endDate)
    {
        var url = $"{_options.ApiBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(businessId)}/Payroll/Timesheet/{Uri.EscapeDataString(employeeUid)}"
            + $"?StartDate={startDate:yyyy-MM-dd}&EndDate={endDate:yyyy-MM-dd}";
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("x-myobapi-key", _options.ClientId);
        request.Headers.TryAddWithoutValidation("x-myobapi-version", "v2");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static IEnumerable<JsonElement> EnumerateTimesheets(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                yield return item;
            }

            yield break;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            yield return root;
        }
    }

    private async Task<MyobTokenResponse> PostTokenAsync(
        Dictionary<string, string> form,
        string failureCode,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenUrl)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            TokenErrorPayload? error = null;
            try
            {
                error = JsonSerializer.Deserialize<TokenErrorPayload>(body, JsonOptions);
            }
            catch (JsonException)
            {
                // 非 JSON 的错误体（网关错误页等）：只按状态码处理，不回显原文。
            }

            // invalid_grant＝refresh token 已过期 / 被撤销 / 已用过：只有这种情况才需要重新授权；
            // invalid_client 等是我方配置（Key / Secret）问题，重新授权也解决不了，按普通失败处理。
            var revoked = string.Equals(error?.Error, "invalid_grant", StringComparison.OrdinalIgnoreCase);
            var detail = string.IsNullOrWhiteSpace(error?.Error)
                ? string.Empty
                : $"：{Truncate(error!.Error!)}";
            throw new MyobApiException(
                revoked ? MyobErrorCodes.NeedsReauthorization : failureCode,
                $"MYOB 令牌接口返回 {(int)response.StatusCode}{detail}",
                response.StatusCode,
                revoked);
        }

        TokenPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TokenPayload>(body, JsonOptions);
        }
        catch (JsonException)
        {
            throw new MyobApiException(failureCode, "MYOB 令牌接口返回了无法解析的内容", response.StatusCode);
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken))
        {
            throw new MyobApiException(failureCode, "MYOB 令牌接口没有返回 access_token", response.StatusCode);
        }

        return new MyobTokenResponse(
            payload.AccessToken,
            string.IsNullOrWhiteSpace(payload.RefreshToken) ? null : payload.RefreshToken,
            payload.ExpiresIn > 0 ? payload.ExpiresIn : 1200);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await WaitForRateSlotAsync(cancellationToken);
        try
        {
            return await _http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw new MyobApiException(MyobErrorCodes.ApiCallFailed, "无法连接 MYOB 服务");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MyobApiException(MyobErrorCodes.ApiCallFailed, "连接 MYOB 服务超时");
        }
    }

    /// <summary>串行等待：保证相邻两次请求的发起时间至少相隔 MinRequestIntervalMs。</summary>
    private async Task WaitForRateSlotAsync(CancellationToken cancellationToken)
    {
        if (_options.MinRequestIntervalMs <= 0)
        {
            return;
        }

        await RateGate.WaitAsync(cancellationToken);
        try
        {
            var interval = TimeSpan.FromMilliseconds(_options.MinRequestIntervalMs);
            var last = Interlocked.Read(ref _lastRequestTimestamp);
            if (last != 0)
            {
                var wait = interval - Stopwatch.GetElapsedTime(last);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken);
                }
            }

            Interlocked.Exchange(ref _lastRequestTimestamp, Stopwatch.GetTimestamp());
        }
        finally
        {
            RateGate.Release();
        }
    }

    private static string Truncate(string value) =>
        value.Length <= MaxErrorTextLength ? value : value[..MaxErrorTextLength];

    private sealed class TokenPayload
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }

    private sealed class TokenErrorPayload
    {
        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    private sealed class WageCategoryPayload
    {
        public string? Uid { get; set; }
        public string? Name { get; set; }
        public string? WageType { get; set; }
    }

    private sealed class ListPayload<T>
    {
        public List<T>? Items { get; set; }
        public string? NextPageLink { get; set; }
    }

    private sealed class EmployeePayload
    {
        public string? Uid { get; set; }
        public string? DisplayId { get; set; }
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? CompanyName { get; set; }
        public bool? IsIndividual { get; set; }
        public bool? IsActive { get; set; }
        public List<AddressPayload>? Addresses { get; set; }
    }

    private sealed class AddressPayload
    {
        public string? Email { get; set; }
        public string? Phone1 { get; set; }
        public string? Phone2 { get; set; }
        public string? Phone3 { get; set; }
        public string? Street { get; set; }
        public string? City { get; set; }
        public string? State { get; set; }
        public string? PostCode { get; set; }
        public string? Country { get; set; }
    }

    private sealed class EmployeePayrollPayload
    {
        public EmployeeRefPayload? Employee { get; set; }
        public DateTime? DateOfBirth { get; set; }
        public string? EmploymentStatus { get; set; }
        public string? Gender { get; set; }
        public SuperannuationPayload? Superannuation { get; set; }
        public List<EntitlementPayload>? Entitlements { get; set; }
    }

    private sealed class SuperannuationPayload
    {
        public CategoryPayload? SuperannuationFund { get; set; }
        public string? EmployeeMembershipNumber { get; set; }
    }

    private sealed class EmployeePaymentPayload
    {
        public EmployeeRefPayload? Employee { get; set; }
        public List<BankAccountPayload>? BankAccounts { get; set; }
    }

    private sealed class BankAccountPayload
    {
        // MYOB 字段名是 BSBNumber；属性名大小写不敏感，BsbNumber 即可匹配。
        public string? BsbNumber { get; set; }
        public string? BankAccountNumber { get; set; }
    }

    private sealed class EmployeeRefPayload
    {
        public string? Uid { get; set; }
    }

    private sealed class EntitlementPayload
    {
        public CategoryPayload? EntitlementCategory { get; set; }
        public bool? IsAssigned { get; set; }
    }

    private sealed class CategoryPayload
    {
        public string? Name { get; set; }
    }

    private sealed class CompanyPayload
    {
        public string? CompanyName { get; set; }

        [JsonPropertyName("ABNOrTFN")]
        public string? AbnOrTfn { get; set; }
    }
}
