using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Common;

namespace Hbpos.Client.Wpf.Services;

public interface ICatalogApiClient
{
    Task<CatalogSyncPageResponse> GetSellableItemsPageAsync(
        string storeCode,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<CatalogCompareResponse> CompareSellableItemsAsync(
        CatalogCompareRequest request,
        CancellationToken cancellationToken = default);

    Task<CatalogPromotionsResponse> GetPromotionRulesAsync(
        string storeCode,
        CancellationToken cancellationToken = default);

    Task<CatalogSpecialProductsPageResponse> GetSpecialProductsPageAsync(
        string storeCode,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<CatalogCodeConflictsResponse> GetCodeConflictsAsync(
        string storeCode,
        CancellationToken cancellationToken = default);

    Task<CatalogLookupResponse?> LookupSellableItemAsync(
        string storeCode,
        string lookupCode,
        CancellationToken cancellationToken = default);

    Task<CatalogSpecialProductMarkResponse> MarkSpecialProductAsync(
        CatalogSpecialProductMarkRequest request,
        CancellationToken cancellationToken = default);

    // 中文注释：以下三个是与 iPad 同一套按版本同步的协议；默认实现按"服务端不支持"处理，
    // 同步服务据此回退旧的逐页核对流程，既有测试替身无需实现。
    Task<CatalogSyncPlanResponse> GetCatalogSyncPlanAsync(
        string storeCode,
        string? baseCatalogVersion,
        CancellationToken cancellationToken = default)
    {
        return Task.FromException<CatalogSyncPlanResponse>(CatalogApiClient.CreateProtocolNotSupportedException());
    }

    /// <summary>
    /// 读取锁定版本的全量页（checksumVersion=2，服务端 gzip），并在返回前校验页摘要、版本与租约回显。
    /// </summary>
    Task<CatalogSyncPageResponse> GetPinnedSellableItemsPageAsync(
        string storeCode,
        string catalogVersion,
        string? downloadLeaseId,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        return Task.FromException<CatalogSyncPageResponse>(CatalogApiClient.CreateProtocolNotSupportedException());
    }

    /// <summary>
    /// 读取两个固定版本之间的增量页，并在返回前校验页摘要与版本回显。
    /// </summary>
    Task<CatalogDeltaPageResponse> GetCatalogDeltaPageAsync(
        string storeCode,
        string baseCatalogVersion,
        string targetCatalogVersion,
        string? downloadLeaseId,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        return Task.FromException<CatalogDeltaPageResponse>(CatalogApiClient.CreateProtocolNotSupportedException());
    }
}

public sealed class CatalogApiClient : ICatalogApiClient
{
    private const string InvalidJsonDataKey = "Hbpos.CatalogApi.InvalidJson";
    private const string ContentTypeDataKey = "Hbpos.CatalogApi.ContentType";
    private const string BodyLengthDataKey = "Hbpos.CatalogApi.BodyLength";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal static readonly TimeSpan TransientRetryDelay = TimeSpan.FromSeconds(3);
    private readonly HttpClient _httpClient;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public CatalogApiClient(HttpClient httpClient)
        : this(httpClient, Task.Delay)
    {
    }

    internal CatalogApiClient(
        HttpClient httpClient,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        _httpClient = httpClient;
        _delayAsync = delayAsync;
    }

    public async Task<CatalogSyncPageResponse> GetSellableItemsPageAsync(
        string storeCode,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var requestUri = BuildUri(
            "api/v1/catalog/sellable-items/page",
            ("storeCode", storeCode),
            ("cursor", cursor),
            ("pageSize", pageSize.ToString(CultureInfo.InvariantCulture)));

        var stopwatch = Stopwatch.StartNew();
        Log($"GET {requestUri} start base={_httpClient.BaseAddress}");
        try
        {
            var responseResult = await ExecuteWithTransientRetryAsync(
                $"GET {requestUri}",
                async token =>
                {
                    using var response = await _httpClient.GetAsync(requestUri, token);
                    var result = await ReadApiResultAsync<CatalogSyncPageResponse>(response, token);
                    return (Result: result, response.StatusCode);
                },
                cancellationToken);
            stopwatch.Stop();
            Log($"GET {requestUri} completed status={(int)responseResult.StatusCode} items={responseResult.Result.Items.Count} total={responseResult.Result.TotalCount} deletedLookups={responseResult.Result.DeletedLookups.Count} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return responseResult.Result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log($"GET {requestUri} failed elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
            throw;
        }
    }

    public async Task<CatalogCompareResponse> CompareSellableItemsAsync(
        CatalogCompareRequest request,
        CancellationToken cancellationToken = default)
    {
        const string requestUri = "api/v1/catalog/sellable-items/compare";
        var stopwatch = Stopwatch.StartNew();
        Log($"POST {requestUri} start base={_httpClient.BaseAddress} store={request.StoreCode} localLookups={request.LocalLookups.Count}");
        try
        {
            var responseResult = await ExecuteWithTransientRetryAsync(
                $"POST {requestUri}",
                async token =>
                {
                    using var response = await _httpClient.PostAsJsonAsync(
                        requestUri,
                        request,
                        JsonOptions,
                        token);
                    var result = await ReadApiResultAsync<CatalogCompareResponse>(response, token);
                    return (Result: result, response.StatusCode);
                },
                cancellationToken);
            stopwatch.Stop();
            Log($"POST {requestUri} completed status={(int)responseResult.StatusCode} upsertedLookups={responseResult.Result.UpsertedLookups.Count} deletedLookups={responseResult.Result.DeletedLookups.Count} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return responseResult.Result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log($"POST {requestUri} failed elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
            throw;
        }
    }

    public async Task<CatalogPromotionsResponse> GetPromotionRulesAsync(
        string storeCode,
        CancellationToken cancellationToken = default)
    {
        var requestUri = BuildUri(
            "api/v1/catalog/promotions",
            ("storeCode", storeCode));

        var stopwatch = Stopwatch.StartNew();
        Log($"GET {requestUri} start base={_httpClient.BaseAddress}");
        try
        {
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            var result = await ReadApiResultAsync<CatalogPromotionsResponse>(response, cancellationToken);
            stopwatch.Stop();
            Log($"GET {requestUri} completed status={(int)response.StatusCode} promotions={result.Promotions.Count} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log($"GET {requestUri} failed elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
            throw;
        }
    }

    public async Task<CatalogSpecialProductsPageResponse> GetSpecialProductsPageAsync(
        string storeCode,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var requestUri = BuildUri(
            "api/v1/catalog/special-products/page",
            ("storeCode", storeCode),
            ("cursor", cursor),
            ("pageSize", pageSize.ToString(CultureInfo.InvariantCulture)));

        var stopwatch = Stopwatch.StartNew();
        Log($"GET {requestUri} start base={_httpClient.BaseAddress}");
        try
        {
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            var result = await ReadApiResultAsync<CatalogSpecialProductsPageResponse>(response, cancellationToken);
            stopwatch.Stop();
            Log($"GET {requestUri} completed status={(int)response.StatusCode} items={result.Items.Count} total={result.TotalCount} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log($"GET {requestUri} failed elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
            throw;
        }
    }

    public async Task<CatalogCodeConflictsResponse> GetCodeConflictsAsync(
        string storeCode,
        CancellationToken cancellationToken = default)
    {
        var requestUri = BuildUri(
            "api/v1/catalog/sellable-items/code-conflicts",
            ("storeCode", storeCode));

        var stopwatch = Stopwatch.StartNew();
        Log($"GET {requestUri} start base={_httpClient.BaseAddress}");
        try
        {
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            var result = await ReadApiResultAsync<CatalogCodeConflictsResponse>(response, cancellationToken);
            stopwatch.Stop();
            Log($"GET {requestUri} completed status={(int)response.StatusCode} available={result.Available} items={result.Items.Count} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log($"GET {requestUri} failed elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
            throw;
        }
    }

    public async Task<CatalogLookupResponse?> LookupSellableItemAsync(
        string storeCode,
        string lookupCode,
        CancellationToken cancellationToken = default)
    {
        var requestUri = BuildUri(
            "api/v1/catalog/sellable-items/lookup",
            ("storeCode", storeCode),
            ("lookupCode", lookupCode));

        var stopwatch = Stopwatch.StartNew();
        Log($"GET {requestUri} start base={_httpClient.BaseAddress} storeCode={storeCode} lookupCode={lookupCode}");
        try
        {
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            CatalogLookupResponse? result;
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                result = await ReadLookupNotFoundAsync(response, cancellationToken);
            }
            else
            {
                result = await ReadApiResultAsync<CatalogLookupResponse>(response, cancellationToken);
            }

            stopwatch.Stop();
            Log($"GET {requestUri} completed storeCode={storeCode} lookupCode={lookupCode} status={(int)response.StatusCode} found={result?.Found.ToString() ?? "<null>"} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return result;
        }
        catch (OperationCanceledException ex)
        {
            stopwatch.Stop();
            Log($"GET {requestUri} canceled storeCode={storeCode} lookupCode={lookupCode} elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log($"GET {requestUri} failed storeCode={storeCode} lookupCode={lookupCode} elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
            throw;
        }
    }

    public async Task<CatalogSpecialProductMarkResponse> MarkSpecialProductAsync(
        CatalogSpecialProductMarkRequest request,
        CancellationToken cancellationToken = default)
    {
        const string requestUri = "api/v1/catalog/special-products/mark";
        var stopwatch = Stopwatch.StartNew();
        Log($"POST {requestUri} start base={_httpClient.BaseAddress} store={request.StoreCode} product={request.ProductCode} isSpecial={request.IsSpecialProduct}");
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                requestUri,
                request,
                JsonOptions,
                cancellationToken);

            var result = await ReadApiResultAsync<CatalogSpecialProductMarkResponse>(response, cancellationToken);
            stopwatch.Stop();
            Log($"POST {requestUri} completed status={(int)response.StatusCode} store={result.StoreCode} product={result.ProductCode} items={result.Items.Count} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log($"POST {requestUri} failed store={request.StoreCode} product={request.ProductCode} elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
            throw;
        }
    }

    public async Task<CatalogSyncPlanResponse> GetCatalogSyncPlanAsync(
        string storeCode,
        string? baseCatalogVersion,
        CancellationToken cancellationToken = default)
    {
        var requestUri = BuildUri(
            "api/v1/catalog/sync-plan",
            ("storeCode", storeCode),
            ("baseCatalogVersion", baseCatalogVersion));

        var stopwatch = Stopwatch.StartNew();
        Log($"GET {requestUri} start base={_httpClient.BaseAddress}");
        try
        {
            var responseResult = await ExecuteWithTransientRetryAsync(
                $"GET {requestUri}",
                async token =>
                {
                    using var response = await _httpClient.GetAsync(requestUri, token);
                    var result = await ReadApiResultAsync<CatalogSyncPlanResponse>(response, token);
                    return (Result: result, response.StatusCode);
                },
                cancellationToken);
            stopwatch.Stop();
            var plan = responseResult.Result;
            Log($"GET {requestUri} completed status={(int)responseResult.StatusCode} mode={plan.Mode} target={plan.TargetCatalogVersion} total={plan.TargetTotal} deltaOperations={plan.DeltaOperationCount?.ToString(CultureInfo.InvariantCulture) ?? "<none>"} lease={!string.IsNullOrWhiteSpace(plan.DownloadLeaseId)} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return plan;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log($"GET {requestUri} failed elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
            throw;
        }
    }

    public async Task<CatalogSyncPageResponse> GetPinnedSellableItemsPageAsync(
        string storeCode,
        string catalogVersion,
        string? downloadLeaseId,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogVersion);
        var requestUri = BuildUri(
            "api/v1/catalog/sellable-items/page",
            ("storeCode", storeCode),
            ("cursor", cursor),
            ("pageSize", pageSize.ToString(CultureInfo.InvariantCulture)),
            ("catalogVersion", catalogVersion),
            ("downloadLeaseId", downloadLeaseId),
            ("checksumVersion", "2"));

        var stopwatch = Stopwatch.StartNew();
        Log($"GET {requestUri} start base={_httpClient.BaseAddress}");
        try
        {
            var responseResult = await ExecuteWithTransientRetryAsync(
                $"GET {requestUri}",
                async token =>
                {
                    using var response = await _httpClient.GetAsync(requestUri, token);
                    var result = await ReadApiResultAsync<CatalogSyncPageResponse>(response, token);
                    return (Result: result, response.StatusCode);
                },
                cancellationToken);
            var page = responseResult.Result;
            VerifyPinnedPage(page, storeCode, catalogVersion, downloadLeaseId);
            stopwatch.Stop();
            Log($"GET {requestUri} completed status={(int)responseResult.StatusCode} items={page.Items.Count} total={page.TotalCount} hasMore={page.HasMore} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return page;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log($"GET {requestUri} failed elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
            throw;
        }
    }

    public async Task<CatalogDeltaPageResponse> GetCatalogDeltaPageAsync(
        string storeCode,
        string baseCatalogVersion,
        string targetCatalogVersion,
        string? downloadLeaseId,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseCatalogVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetCatalogVersion);
        var requestUri = BuildUri(
            "api/v1/catalog/delta/page",
            ("storeCode", storeCode),
            ("baseCatalogVersion", baseCatalogVersion),
            ("targetCatalogVersion", targetCatalogVersion),
            ("cursor", cursor),
            ("pageSize", pageSize.ToString(CultureInfo.InvariantCulture)),
            ("downloadLeaseId", downloadLeaseId));

        var stopwatch = Stopwatch.StartNew();
        Log($"GET {requestUri} start base={_httpClient.BaseAddress}");
        try
        {
            var responseResult = await ExecuteWithTransientRetryAsync(
                $"GET {requestUri}",
                async token =>
                {
                    using var response = await _httpClient.GetAsync(requestUri, token);
                    var result = await ReadApiResultAsync<CatalogDeltaPageResponse>(response, token);
                    return (Result: result, response.StatusCode);
                },
                cancellationToken);
            var page = responseResult.Result;
            VerifyDeltaPage(page, storeCode, baseCatalogVersion, targetCatalogVersion, downloadLeaseId);
            stopwatch.Stop();
            Log($"GET {requestUri} completed status={(int)responseResult.StatusCode} upserts={page.Items.Count} deletes={page.DeletedLookups.Count} hasMore={page.HasMore} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return page;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log($"GET {requestUri} failed elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
            throw;
        }
    }

    internal static CatalogApiException CreateProtocolNotSupportedException()
    {
        return new CatalogApiException(
            "Catalog sync plan protocol is not supported by this catalog client.",
            HttpStatusCode.NotImplemented);
    }

    private static void VerifyPinnedPage(
        CatalogSyncPageResponse page,
        string storeCode,
        string catalogVersion,
        string? downloadLeaseId)
    {
        if (!string.Equals(page.StoreCode?.Trim(), storeCode.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw CreateVerificationException("Catalog page store code does not match the request.", "CATALOG_PAGE_STORE_MISMATCH");
        }

        if (!string.Equals(page.CatalogVersion, catalogVersion, StringComparison.Ordinal))
        {
            throw CreateVerificationException("Catalog page version does not match the pinned catalog version.", "CATALOG_PAGE_VERSION_MISMATCH");
        }

        if (!string.IsNullOrWhiteSpace(downloadLeaseId) &&
            !string.Equals(page.DownloadLeaseId, downloadLeaseId, StringComparison.Ordinal))
        {
            throw CreateVerificationException("Catalog page download lease does not match the request.", "CATALOG_PAGE_LEASE_MISMATCH");
        }

        var expectedChecksum = CatalogPageChecksums.ComputeSellablePageV2(page.Items);
        if (!string.Equals(page.PageChecksum, expectedChecksum, StringComparison.OrdinalIgnoreCase))
        {
            throw CreateVerificationException("Catalog page checksum verification failed.", "CATALOG_PAGE_CHECKSUM_MISMATCH");
        }
    }

    private static void VerifyDeltaPage(
        CatalogDeltaPageResponse page,
        string storeCode,
        string baseCatalogVersion,
        string targetCatalogVersion,
        string? downloadLeaseId)
    {
        if (!string.Equals(page.StoreCode?.Trim(), storeCode.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw CreateVerificationException("Catalog delta page store code does not match the request.", "CATALOG_PAGE_STORE_MISMATCH");
        }

        if (!string.Equals(page.BaseCatalogVersion, baseCatalogVersion, StringComparison.Ordinal) ||
            !string.Equals(page.TargetCatalogVersion, targetCatalogVersion, StringComparison.Ordinal))
        {
            throw CreateVerificationException("Catalog delta page versions do not match the request.", "CATALOG_PAGE_VERSION_MISMATCH");
        }

        if (!string.IsNullOrWhiteSpace(downloadLeaseId) &&
            !string.Equals(page.DownloadLeaseId, downloadLeaseId, StringComparison.Ordinal))
        {
            throw CreateVerificationException("Catalog delta page download lease does not match the request.", "CATALOG_PAGE_LEASE_MISMATCH");
        }

        var expectedChecksum = CatalogPageChecksums.ComputeDeltaPageV1(
            baseCatalogVersion,
            targetCatalogVersion,
            page.Items,
            page.DeletedLookups);
        if (!string.Equals(page.PageChecksum, expectedChecksum, StringComparison.OrdinalIgnoreCase))
        {
            throw CreateVerificationException("Catalog delta page checksum verification failed.", "CATALOG_PAGE_CHECKSUM_MISMATCH");
        }
    }

    private static CatalogApiException CreateVerificationException(string message, string errorCode)
    {
        return new CatalogApiException(message, HttpStatusCode.OK, errorCode);
    }

    private static async Task<CatalogLookupResponse?> ReadLookupNotFoundAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new CatalogApiException(
                "Catalog lookup returned HTTP 404 without a catalog error body.",
                response.StatusCode);
        }

        ApiResult<CatalogLookupResponse>? result;
        try
        {
            result = JsonSerializer.Deserialize<ApiResult<CatalogLookupResponse>>(content, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new CatalogApiException(
                "Catalog lookup returned HTTP 404 with invalid JSON.",
                response.StatusCode,
                errorCode: null,
                ex);
        }

        if (string.Equals(result?.ErrorCode, "LOOKUP_NOT_FOUND", StringComparison.OrdinalIgnoreCase))
        {
            return result?.Data;
        }

        throw new CatalogApiException(
            result?.Message ?? "Catalog lookup returned HTTP 404.",
            response.StatusCode,
            result?.ErrorCode);
    }

    private static async Task<T> ReadApiResultAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        ApiResult<T>? result = null;

        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                result = JsonSerializer.Deserialize<ApiResult<T>>(content, JsonOptions);
            }
            catch (JsonException ex)
            {
                var formatException = new CatalogApiException(
                    "Catalog API returned invalid JSON.",
                    response.StatusCode,
                    errorCode: null,
                    ex);
                formatException.Data[InvalidJsonDataKey] = true;
                formatException.Data[ContentTypeDataKey] =
                    response.Content.Headers.ContentType?.MediaType ?? "<none>";
                formatException.Data[BodyLengthDataKey] = content.Length;
                throw formatException;
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new CatalogApiException(
                result?.Message ?? $"Catalog API request failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode,
                result?.ErrorCode);
        }

        if (result is null)
        {
            throw new CatalogApiException(
                "Catalog API returned an empty response.",
                response.StatusCode);
        }

        if (!result.Success)
        {
            throw new CatalogApiException(
                result.Message ?? "Catalog API returned a failure response.",
                response.StatusCode,
                result.ErrorCode);
        }

        if (result.Data is null)
        {
            throw new CatalogApiException(
                "Catalog API returned no data.",
                response.StatusCode,
                result.ErrorCode);
        }

        return result.Data;
    }

    private async Task<T> ExecuteWithTransientRetryAsync<T>(
        string operation,
        Func<CancellationToken, Task<T>> executeAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            return await executeAsync(cancellationToken);
        }
        catch (Exception ex) when (ShouldRetry(ex, cancellationToken))
        {
            var details = DescribeRetry(ex);
            Log($"retry operation={operation} attempt=2 maxAttempts=2 reason={details.Reason} status={details.Status} contentType={details.ContentType} bodyLength={details.BodyLength} delayMs={TransientRetryDelay.TotalMilliseconds:0}");
            await _delayAsync(TransientRetryDelay, cancellationToken);
            return await executeAsync(cancellationToken);
        }
    }

    private static bool ShouldRetry(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || exception is OperationCanceledException)
        {
            return false;
        }

        if (exception is CatalogApiException invalidJsonException && IsInvalidJson(invalidJsonException))
        {
            return !IsClientError(invalidJsonException.StatusCode);
        }

        return exception switch
        {
            CatalogApiException apiException => IsGatewayStatus(apiException.StatusCode),
            HttpRequestException httpException => !IsClientError(httpException.StatusCode),
            _ => false
        };
    }

    private static (string Reason, string Status, string ContentType, string BodyLength) DescribeRetry(
        Exception exception)
    {
        if (exception is CatalogApiException invalidJsonException && IsInvalidJson(invalidJsonException))
        {
            return (
                "invalid-json",
                FormatStatus(invalidJsonException.StatusCode),
                invalidJsonException.Data[ContentTypeDataKey]?.ToString() ?? "<none>",
                invalidJsonException.Data[BodyLengthDataKey]?.ToString() ?? "<unknown>");
        }

        return exception switch
        {
            CatalogApiException apiException => (
                "gateway-status",
                FormatStatus(apiException.StatusCode),
                "<unknown>",
                "<unknown>"),
            HttpRequestException httpException => (
                "http-request",
                FormatStatus(httpException.StatusCode),
                "<unknown>",
                "<unknown>"),
            _ => ("unknown", "<unknown>", "<unknown>", "<unknown>")
        };
    }

    private static bool IsInvalidJson(CatalogApiException exception)
    {
        return exception.Data[InvalidJsonDataKey] is true;
    }

    private static bool IsGatewayStatus(HttpStatusCode? statusCode)
    {
        return statusCode is
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;
    }

    private static bool IsClientError(HttpStatusCode? statusCode)
    {
        return statusCode is not null && (int)statusCode is >= 400 and < 500;
    }

    private static string FormatStatus(HttpStatusCode? statusCode)
    {
        return statusCode is null
            ? "<none>"
            : ((int)statusCode).ToString(CultureInfo.InvariantCulture);
    }

    private static string BuildUri(string path, params (string Name, string? Value)[] query)
    {
        var queryString = string.Join(
            "&",
            query
                .Where(x => !string.IsNullOrWhiteSpace(x.Value))
                .Select(x => $"{Uri.EscapeDataString(x.Name)}={Uri.EscapeDataString(x.Value!)}"));

        return string.IsNullOrEmpty(queryString)
            ? path
            : $"{path}?{queryString}";
    }

    private static void Log(string message)
    {
        ConsoleLog.Write("CatalogApi", message);
    }
}

public sealed class CatalogApiException : Exception
{
    public CatalogApiException(
        string message,
        HttpStatusCode? statusCode = null,
        string? errorCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public HttpStatusCode? StatusCode { get; }

    public string? ErrorCode { get; }
}
