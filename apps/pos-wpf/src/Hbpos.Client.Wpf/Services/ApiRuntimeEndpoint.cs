using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Hbpos.Client.Wpf.Services;

public sealed class ApiRuntimeEndpointState
{
    private readonly object _gate = new();
    private readonly Uri _startupAddress;
    private Uri _currentAddress;
    private CancellationTokenSource _generationCancellation = new();
    private TaskCompletionSource? _drainedRequests;
    private long _transitionSequence;
    private long _activeRequestCount;
    private ApiEndpointTransition? _transition;
    private long _version;

    public ApiRuntimeEndpointState(string initialAddress)
    {
        _startupAddress = new Uri(ApiServerSettingsService.NormalizeAddress(initialAddress), UriKind.Absolute);
        _currentAddress = _startupAddress;
    }

    public Uri StartupAddress => _startupAddress;

    public Uri CurrentAddress => Volatile.Read(ref _currentAddress);

    public long Version => Interlocked.Read(ref _version);

    public bool Switch(string address)
    {
        var target = new Uri(ApiServerSettingsService.NormalizeAddress(address), UriKind.Absolute);
        if (string.Equals(CurrentAddress.AbsoluteUri, target.AbsoluteUri, StringComparison.Ordinal))
        {
            return false;
        }

        var transition = BeginTransitionAsync(target.AbsoluteUri, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        Commit(transition);
        return true;
    }

    public async Task<ApiEndpointTransition> BeginTransitionAsync(
        string targetAddress,
        CancellationToken cancellationToken)
    {
        var target = new Uri(ApiServerSettingsService.NormalizeAddress(targetAddress), UriKind.Absolute);
        ApiEndpointTransition transition;
        Task drained;
        CancellationTokenSource previousGeneration;
        lock (_gate)
        {
            if (_transition is not null)
            {
                throw new ApiEndpointTransitionException("API 服务器正在切换。");
            }

            if (string.Equals(_currentAddress.AbsoluteUri, target.AbsoluteUri, StringComparison.Ordinal))
            {
                throw new ArgumentException("目标 API 服务器与当前服务器相同。", nameof(targetAddress));
            }

            transition = new ApiEndpointTransition(++_transitionSequence, _currentAddress, target);
            _transition = transition;
            previousGeneration = _generationCancellation;
            _drainedRequests = _activeRequestCount == 0
                ? null
                : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            drained = _drainedRequests?.Task ?? Task.CompletedTask;
        }

        // Begin 阶段先封闭新请求并取消旧代，再等待所有 handler 租约真正退出。
        previousGeneration.Cancel();
        try
        {
            await drained.WaitAsync(cancellationToken);
            return transition;
        }
        catch
        {
            Abort(transition);
            throw;
        }
    }

    public void Publish(ApiEndpointTransition transition)
    {
        lock (_gate)
        {
            ValidateTransition(transition);
            Volatile.Write(ref _currentAddress, transition.TargetAddress);
            Interlocked.Increment(ref _version);
        }
    }

    public void Complete(ApiEndpointTransition transition)
    {
        lock (_gate)
        {
            ValidateTransition(transition);
            _generationCancellation.Dispose();
            _generationCancellation = new CancellationTokenSource();
            _transition = null;
            _drainedRequests = null;
        }
    }

    public void Commit(ApiEndpointTransition transition)
    {
        Publish(transition);
        Complete(transition);
    }

    public void Abort(ApiEndpointTransition transition)
    {
        lock (_gate)
        {
            ValidateTransition(transition);
            Volatile.Write(ref _currentAddress, transition.PreviousAddress);
            _generationCancellation.Dispose();
            _generationCancellation = new CancellationTokenSource();
            _transition = null;
            _drainedRequests = null;
        }
    }

    internal ApiRuntimeEndpointLease? AcquireRequestLease()
    {
        lock (_gate)
        {
            if (_transition is not null)
            {
                return null;
            }

            _activeRequestCount++;
            return new ApiRuntimeEndpointLease(
                new ApiRuntimeEndpointSnapshot(_currentAddress, _generationCancellation.Token),
                ReleaseRequestLease);
        }
    }

    private void ReleaseRequestLease()
    {
        TaskCompletionSource? drained = null;
        lock (_gate)
        {
            _activeRequestCount--;
            if (_activeRequestCount == 0 && _transition is not null)
            {
                drained = _drainedRequests;
            }
        }

        drained?.TrySetResult();
    }

    private void ValidateTransition(ApiEndpointTransition transition)
    {
        if (_transition is null || _transition.Id != transition.Id)
        {
            throw new ApiEndpointTransitionException("API 服务器切换令牌无效或已结束。");
        }
    }
}

public sealed record ApiEndpointTransition(long Id, Uri PreviousAddress, Uri TargetAddress);

public sealed class ApiEndpointTransitionException(string message) : InvalidOperationException(message);

internal sealed record ApiRuntimeEndpointSnapshot(Uri Address, CancellationToken CancellationToken);

internal sealed class ApiRuntimeEndpointLease(
    ApiRuntimeEndpointSnapshot snapshot,
    Action release) : IDisposable
{
    private int _disposed;

    public ApiRuntimeEndpointSnapshot Snapshot { get; } = snapshot;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            release();
        }
    }
}

public sealed class ApiRuntimeEndpointHandler(
    ApiRuntimeEndpointState endpointState,
    ApiTransportSelector? transportSelector = null) : DelegatingHandler
{
    // 测试直接 new 处理器时不传选择器：别名组之外的地址只有一个候选，行为与未启用故障转移完全一致。
    private readonly ApiTransportSelector _transportSelector = transportSelector ?? new ApiTransportSelector();

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var lease = endpointState.AcquireRequestLease();
        if (lease is null)
        {
            // 切换窗口内封闭新请求，并统一为端点代际取消，避免正常协调流程抛出业务异常。
            return Task.FromCanceled<HttpResponseMessage>(new CancellationToken(canceled: true));
        }

        string? relativePath = null;
        var requestUri = request.RequestUri;
        if (requestUri is not null &&
            requestUri.IsAbsoluteUri &&
            TryGetStartupRelativePath(requestUri, endpointState.StartupAddress, out var startupRelativePath))
        {
            relativePath = startupRelativePath;
        }

        return SendForGenerationAsync(request, relativePath, lease.Snapshot, cancellationToken, lease);
    }

    private async Task<HttpResponseMessage> SendForGenerationAsync(
        HttpRequestMessage request,
        string? relativePath,
        ApiRuntimeEndpointSnapshot endpoint,
        CancellationToken callerCancellationToken,
        ApiRuntimeEndpointLease lease)
    {
        using (lease)
        using (var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                   endpoint.CancellationToken,
                   callerCancellationToken))
        {
            if (relativePath is null)
            {
                // 不是发往 API 服务器的请求（例如外部 CDN 图片），原样转发，不参与入口选择。
                return await base.SendAsync(request, linkedCancellation.Token);
            }

            var configured = endpoint.Address;
            // 写请求（POST/PUT 等）不当探路的：冷却结束后由读请求先确认首选入口恢复，见 ApiTransportSelector.Resolve。
            var transport = _transportSelector.Resolve(configured, allowProbe: IsRetryableRequest(request));
            request.RequestUri = new Uri(transport, relativePath);

            // 关键逻辑：只有无请求体的 GET/HEAD 才允许在「连接阶段失败」后自动换入口重试。
            // 连接都没建立，请求不可能送达服务器，重试是安全的；而 POST 等写请求即使连接失败也不自动重试，
            // 避免任何重复提交的可能，由调用方既有的离线队列/重试机制负责，但失败会让后续请求改用备用入口。
            // 必须在发送前克隆：内层处理器（设备授权头等）会修改原请求，发送后再克隆会带上重复的头。
            var retryRequest = _transportSelector.GetFallback(configured, transport) is not null
                ? CreateRetryRequest(request)
                : null;
            try
            {
                var response = await base.SendAsync(request, linkedCancellation.Token);
                if (IsEdgeOriginFailure(response.StatusCode))
                {
                    // EdgeOne 自己报告「边缘到源站」失败（522 连不上源站、523 不可达、524 回源超时、525/526 TLS 等）。
                    // 这些非标准状态码源站从不返回，是干净的「边缘出问题」信号：本次起改走备用入口，
                    // 读请求立即换备用入口重试一次；写请求不重试（源站可能已在处理），直接把响应交给调用方。
                    _transportSelector.ReportConnectFailure(configured, transport);
                    var statusFallback = _transportSelector.GetFallback(configured, transport);
                    if (retryRequest is null || statusFallback is null)
                    {
                        return response;
                    }

                    ConsoleLog.WriteWarning(
                        "ApiEndpoint",
                        $"api edge reported origin failure host={transport.Host} status={(int)response.StatusCode}, retry on host={statusFallback.Host}",
                        new ApplicationLogContext(
                            RequestMethod: request.Method.Method,
                            StatusCode: (int)response.StatusCode));
                    response.Dispose();
                    retryRequest.RequestUri = new Uri(statusFallback, relativePath);
                    var statusRetryResponse = await base.SendAsync(retryRequest, linkedCancellation.Token);
                    _transportSelector.ReportSuccess(configured, statusFallback);
                    return statusRetryResponse;
                }

                _transportSelector.ReportSuccess(configured, transport);
                return response;
            }
            catch (HttpRequestException ex) when (IsConnectFailure(ex))
            {
                _transportSelector.ReportConnectFailure(configured, transport);
                var fallback = _transportSelector.GetFallback(configured, transport);
                if (retryRequest is null || fallback is null)
                {
                    throw;
                }

                ConsoleLog.WriteWarning(
                    "ApiEndpoint",
                    $"api transport connect failed host={transport.Host} reason={ex.HttpRequestError}, retry on host={fallback.Host}",
                    new ApplicationLogContext(
                        RequestMethod: request.Method.Method,
                        Properties: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["failedHost"] = transport.Host,
                            ["fallbackHost"] = fallback.Host,
                            ["reason"] = ex.HttpRequestError.ToString()
                        }),
                    ex);
                retryRequest.RequestUri = new Uri(fallback, relativePath);
                var retryResponse = await base.SendAsync(retryRequest, linkedCancellation.Token);
                _transportSelector.ReportSuccess(configured, fallback);
                return retryResponse;
            }
            finally
            {
                retryRequest?.Dispose();
            }
        }
    }

    /// <summary>EdgeOne 回源失败专用的 520~530 状态码（源站和 ASP.NET 都不会产生）。</summary>
    private static bool IsEdgeOriginFailure(HttpStatusCode statusCode)
    {
        return (int)statusCode is >= 520 and <= 530;
    }

    /// <summary>
    /// 连接阶段的失败：域名解析失败、TCP 建连失败/超时、TLS 握手失败、代理隧道失败。
    /// 不包含请求已发出后的超时或断开（那种情况服务器可能已经处理了请求）。
    /// </summary>
    private static bool IsConnectFailure(HttpRequestException exception)
    {
        if (exception.HttpRequestError is HttpRequestError.NameResolutionError
            or HttpRequestError.ConnectionError
            or HttpRequestError.SecureConnectionError
            or HttpRequestError.ProxyTunnelError)
        {
            return true;
        }

        return exception.InnerException is SocketException
        {
            SocketErrorCode: SocketError.ConnectionRefused
                or SocketError.HostUnreachable
                or SocketError.NetworkUnreachable
                or SocketError.TimedOut
                or SocketError.HostNotFound
                or SocketError.TryAgain
        };
    }

    /// <summary>只有无请求体的 GET/HEAD 才允许自动换入口重试，也只有它们可以去试探首选入口。</summary>
    private static bool IsRetryableRequest(HttpRequestMessage request)
    {
        return request.Content is null
            && (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head);
    }

    private static HttpRequestMessage? CreateRetryRequest(HttpRequestMessage original)
    {
        if (!IsRetryableRequest(original))
        {
            return null;
        }

        var clone = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
            VersionPolicy = original.VersionPolicy
        };
        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in original.Options)
        {
            ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;
        }

        return clone;
    }

    private static bool TryGetStartupRelativePath(Uri requestUri, Uri startupAddress, out string relativePath)
    {
        relativePath = string.Empty;
        if (!string.Equals(requestUri.Scheme, startupAddress.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(requestUri.Host, startupAddress.Host, StringComparison.OrdinalIgnoreCase) ||
            requestUri.Port != startupAddress.Port ||
            !requestUri.AbsolutePath.StartsWith(startupAddress.AbsolutePath, StringComparison.Ordinal))
        {
            return false;
        }

        relativePath = requestUri.AbsoluteUri[startupAddress.AbsoluteUri.Length..];
        return true;
    }
}
