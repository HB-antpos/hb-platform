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
}

/// <summary>
/// MYOB Business API v2 的最小客户端（阶段 0 只需 OAuth 与 Company）。
/// 所有请求经同一个最小间隔限流（MYOB 每个 API Key 每秒 8 次）。
/// </summary>
public sealed class MyobApiClient : IMyobApiClient
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
