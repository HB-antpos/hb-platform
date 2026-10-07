using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Hbpos.Contracts.Linkly;

namespace Hbpos.Client.Wpf.Services;

public sealed class LinklyBackendReceiptPrintedNotifier(IHttpClientFactory httpClientFactory) : ICardReceiptPrintedNotifier
{
    public const string HttpClientName = nameof(LinklyBackendReceiptPrintedNotifier);

    public async Task MarkReceiptPrintedAsync(
        string environment,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var httpClient = httpClientFactory.CreateClient(HttpClientName);
        var request = new LinklyCloudBackendMarkReceiptPrintedRequest(environment);
        const string operation = "receipt/printed";
        var relativeUrl = $"api/v1/linkly/cloud-backend/transactions/{Uri.EscapeDataString(sessionId)}/receipt/printed";
        var absoluteUrl = httpClient.BaseAddress is null
            ? relativeUrl
            : new Uri(httpClient.BaseAddress, relativeUrl).ToString();
        var requestJson = JsonSerializer.Serialize(request);
        var evidenceEnvironment = Enum.TryParse<CardTerminalEnvironment>(environment, ignoreCase: true, out var parsedEnvironment)
            ? parsedEnvironment
            : (CardTerminalEnvironment?)null;
        LinklyJsonLog.Write(
            "LinklyBackend",
            "backend-terminal",
            operation,
            "request",
            direction: "request",
            environment: evidenceEnvironment,
            sessionId: sessionId,
            request: new
            {
                method = HttpMethod.Post.Method,
                url = absoluteUrl,
                body = request
            },
            details: new
            {
                timestamp = DateTimeOffset.Now,
                certCase = "4.1.3",
                method = HttpMethod.Post.Method,
                url = absoluteUrl,
                transactionReference = sessionId,
                requestJson,
                responseJson = (string?)null
            });

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage sentResponse;
        try
        {
            sentResponse = await httpClient.PostAsJsonAsync(
                relativeUrl,
                request,
                cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // 调用方（小票打印）会对异常再记一条 Warning；这里只补发送阶段的耗时与原因，记 Information 避免重复告警。
            LinklyJsonLog.Write(
                "LinklyBackend",
                "backend-terminal",
                operation,
                "failed",
                direction: "response",
                environment: evidenceEnvironment,
                sessionId: sessionId,
                success: false,
                reason: ex is HttpRequestException
                    ? "network-error"
                    : cancellationToken.IsCancellationRequested ? "cancelled" : "timeout",
                elapsedMs: stopwatch.ElapsedMilliseconds,
                details: new { method = HttpMethod.Post.Method, url = absoluteUrl },
                exception: ex,
                level: LinklyLogLevel.Information);
            throw;
        }

        using var response = sentResponse;
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        stopwatch.Stop();
        LinklyJsonLog.Write(
            "LinklyBackend",
            "backend-terminal",
            operation,
            "response",
            direction: "response",
            environment: evidenceEnvironment,
            sessionId: sessionId,
            httpStatus: response.StatusCode,
            success: response.IsSuccessStatusCode,
            reason: response.IsSuccessStatusCode ? null : "receipt-printed-marker-failed",
            elapsedMs: stopwatch.ElapsedMilliseconds,
            response: new
            {
                method = HttpMethod.Post.Method,
                url = absoluteUrl,
                body = string.IsNullOrWhiteSpace(responseJson) ? null : responseJson
            },
            details: new
            {
                timestamp = DateTimeOffset.Now,
                certCase = "4.1.3",
                method = HttpMethod.Post.Method,
                url = absoluteUrl,
                transactionReference = sessionId,
                requestJson = (string?)null,
                responseJson
            });
        response.EnsureSuccessStatusCode();
    }
}
