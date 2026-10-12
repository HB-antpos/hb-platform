using System.Buffers;
using System.Net;
using Microsoft.AspNetCore.Http.Features;

namespace BlazorApp.FaceIngest;

/// <summary>
/// 海康摄像机推送接收端点的处理逻辑（任意 HTTP 方法、任意 Content-Type）。
/// 业务口径：
/// - 鉴权失败（无 token / 不匹配 / 摄像机停用）→ 不读请求体，401 空体，按来源节流记 Warning；
/// - 鉴权通过：CaptureEnabled=false 或存储护栏触发 → 读掉请求体丢弃后照样 200（避免断网续传反复重推）；
///   正常 → 原样落盘后 200 空体；请求体超限 413；写盘失败 503（让摄像机缓存后重推）。
/// - 只做本地文件写入，不做任何同步网络调用，保证 1 秒内响应。
/// - 日志与落盘内容都不得出现 token 原文。
/// </summary>
internal sealed class FaceIngestRequestHandler
{
    private const int DrainBufferSize = 81920;
    private const int MaxLoggedSourceLength = 64;
    private static readonly TimeSpan LogThrottleWindow = TimeSpan.FromSeconds(60);

    private readonly FaceIngestSettingsMonitor _settings;
    private readonly RawCaptureStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FaceIngestRequestHandler> _logger;
    private readonly ThrottledLogGate _rejectionLogGate;
    private readonly ThrottledLogGate _discardLogGate;
    // 中文注释：因存储护栏累计丢弃的报文数（进程启动以来），写进节流 Warning 日志。
    private long _droppedTotal;

    public FaceIngestRequestHandler(
        FaceIngestSettingsMonitor settings,
        RawCaptureStore store,
        TimeProvider timeProvider,
        ILogger<FaceIngestRequestHandler> logger)
    {
        _settings = settings;
        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;
        _rejectionLogGate = new ThrottledLogGate(timeProvider, LogThrottleWindow);
        _discardLogGate = new ThrottledLogGate(timeProvider, LogThrottleWindow);
    }

    public async Task HandleAsync(HttpContext context)
    {
        var startTimestamp = _timeProvider.GetTimestamp();
        var receivedAtUtc = _timeProvider.GetUtcNow();
        // 中文注释：整个请求只取一次快照，热更新不会在请求中途改变判定口径。
        var settings = _settings.Current;
        var request = context.Request;
        var response = context.Response;

        var token = IngestTokenResolver.Resolve(request);
        if (token.Value is null)
        {
            Reject(context, token, "missing-token", cameraId: null);
            return;
        }

        var camera = settings.FindCameraByToken(token.Value);
        if (camera is null)
        {
            Reject(context, token, "token-mismatch", cameraId: null);
            return;
        }

        if (!camera.Enabled)
        {
            Reject(context, token, "camera-disabled", camera.CameraId);
            return;
        }

        // 中文注释：鉴权通过后才把单请求上限对齐到当前快照（Kestrel 全局上限取启动时的同一配置值）。
        var maxBodyBytes = settings.MaxRequestBodyBytes;
        var bodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is { IsReadOnly: false })
        {
            bodySizeFeature.MaxRequestBodySize = maxBodyBytes;
        }

        if (request.ContentLength is long declaredLength && declaredLength > maxBodyBytes)
        {
            LogBodyTooLarge(camera, declaredLength, maxBodyBytes);
            response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        if (!settings.CaptureEnabled)
        {
            var drained = await DrainAsync(context, camera, maxBodyBytes);
            if (drained == DrainOutcome.Completed)
            {
                if (_discardLogGate.TryAcquire("capture-disabled:" + camera.CameraId, out var suppressed))
                {
                    _logger.LogInformation(
                        "Face ingest capture disabled; request body discarded camera={CameraId} store={StoreCode} suppressedSinceLastLog={SuppressedCount}",
                        camera.CameraId,
                        camera.StoreCode,
                        suppressed);
                }

                response.StatusCode = StatusCodes.Status200OK;
            }

            return;
        }

        var rejectReason = _store.CheckCapacity(settings);
        if (rejectReason != StorageRejectReason.None)
        {
            var drained = await DrainAsync(context, camera, maxBodyBytes);
            if (drained == DrainOutcome.Completed)
            {
                // 关键逻辑：故意回 200 丢弃，不回 5xx——否则摄像机会把报文缓存到 SD 卡，恢复后成批重推，越积越多。
                var droppedTotal = Interlocked.Increment(ref _droppedTotal);
                if (_discardLogGate.TryAcquire("storage-full:" + rejectReason, out var suppressed))
                {
                    _logger.LogWarning(
                        "Face ingest storage guard tripped; request body discarded reason={Reason} camera={CameraId} storedFiles={StoredFileCount} storedBytes={StoredBytes} droppedTotal={DroppedTotal} suppressedSinceLastLog={SuppressedCount}",
                        rejectReason,
                        camera.CameraId,
                        _store.StoredFileCount,
                        _store.StoredBytes,
                        droppedTotal,
                        suppressed);
                }

                response.StatusCode = StatusCodes.Status200OK;
            }

            return;
        }

        var requestInfo = BuildRequestInfo(context, token);
        var result = await _store.SaveAsync(
            new CaptureWriteRequest(camera, receivedAtUtc, startTimestamp, maxBodyBytes, requestInfo),
            request.Body,
            context.RequestAborted);

        switch (result.Status)
        {
            case CaptureSaveStatus.Stored:
                _logger.LogInformation(
                    "Face ingest capture stored camera={CameraId} store={StoreCode} bytes={BodyBytes} sha256={BodySha256Prefix} contentType={ContentType} durationMs={DurationMs}",
                    camera.CameraId,
                    camera.StoreCode,
                    result.BodyBytes,
                    result.BodySha256![..12],
                    requestInfo.ContentType,
                    result.DurationMs);
                response.StatusCode = StatusCodes.Status200OK;
                break;
            case CaptureSaveStatus.BodyTooLarge:
                LogBodyTooLarge(camera, result.BodyBytes, maxBodyBytes);
                response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                break;
            case CaptureSaveStatus.ClientAborted:
                _logger.LogWarning(
                    "Face ingest client aborted while sending body; partial data discarded camera={CameraId} receivedBytes={BodyBytes} error={ErrorType}",
                    camera.CameraId,
                    result.BodyBytes,
                    result.Error?.GetType().Name);
                response.StatusCode = StatusCodes.Status400BadRequest;
                break;
            case CaptureSaveStatus.StorageFailed:
                _logger.LogError(
                    result.Error,
                    "Face ingest storage write failed; responding 503 so the camera retries later camera={CameraId} receivedBytes={BodyBytes}",
                    camera.CameraId,
                    result.BodyBytes);
                response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                break;
            default:
                throw new InvalidOperationException($"Unexpected capture save status {result.Status}.");
        }
    }

    /// <summary>
    /// 读掉并丢弃请求体（不落盘），让连接可以正常复用。超限按 413、中途断开按 400 处理，返回值告诉调用方是否继续回 200。
    /// </summary>
    private async Task<DrainOutcome> DrainAsync(HttpContext context, CameraRegistration camera, long maxBodyBytes)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(DrainBufferSize);
        long total = 0;
        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = await context.Request.Body.ReadAsync(buffer.AsMemory(0, DrainBufferSize), context.RequestAborted);
                }
                catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    LogBodyTooLarge(camera, total, maxBodyBytes);
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    return DrainOutcome.TooLarge;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        "Face ingest client aborted while sending discarded body camera={CameraId} receivedBytes={BodyBytes} error={ErrorType}",
                        camera.CameraId,
                        total,
                        ex.GetType().Name);
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return DrainOutcome.Aborted;
                }

                if (read == 0)
                {
                    return DrainOutcome.Completed;
                }

                total += read;
                if (total > maxBodyBytes)
                {
                    LogBodyTooLarge(camera, total, maxBodyBytes);
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    return DrainOutcome.TooLarge;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Reject(HttpContext context, ResolvedToken token, string reason, string? cameraId)
    {
        // 关键逻辑：鉴权失败不读请求体，直接 401 空体；日志只记来源、原因与 token 位置，绝不记 token 或含 token 的路径。
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        var source = ResolveSourceAddress(context);
        if (_rejectionLogGate.TryAcquire(source, out var suppressed))
        {
            _logger.LogWarning(
                "Face ingest request rejected status=401 reason={Reason} tokenSource={TokenSource} camera={CameraId} source={SourceAddress} method={Method} suppressedSinceLastLog={SuppressedCount}",
                reason,
                token.SourceName,
                cameraId,
                source,
                context.Request.Method,
                suppressed);
        }
    }

    private void LogBodyTooLarge(CameraRegistration camera, long bytes, long maxBodyBytes)
    {
        _logger.LogWarning(
            "Face ingest request body too large; responding 413 camera={CameraId} bytes={BodyBytes} limit={MaxRequestBodyBytes}",
            camera.CameraId,
            bytes,
            maxBodyBytes);
    }

    /// <summary>
    /// 节流用的「来源」：生产链路是宿主机 Nginx → 127.0.0.1:5004 → docker-proxy / DNAT → 容器，
    /// 容器里看到的连接层地址是 Docker 网桥网关（如 172.18.0.1），不是回环；所以连接层地址为回环、
    /// 私网或链路本地（即「来自本机代理」）时，改用 Nginx 写入的 X-Real-IP 区分门店出口。
    /// 容器端口只绑 127.0.0.1，Nginx 又用 $remote_addr 覆盖该头，外部无法伪造。仅用于日志节流，不用于鉴权。
    /// </summary>
    private static string ResolveSourceAddress(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        // 中文注释：Kestrel 监听双栈 [::]，IPv4 连接以映射地址（::ffff:172.18.0.1）出现；还原成 IPv4，
        // 回退到连接层地址时日志里也是 172.18.0.1 这种形式（IsLocalProxyAddress 自己也会处理映射地址）。
        if (remote is not null && remote.IsIPv4MappedToIPv6)
        {
            remote = remote.MapToIPv4();
        }

        // 中文注释：公网地址直连（理论上只有误把端口开到公网时才会出现）不信任 X-Real-IP，按连接层地址节流。
        if (remote is null || IsLocalProxyAddress(remote))
        {
            var realIp = context.Request.Headers["X-Real-IP"].ToString().Trim();
            if (realIp.Length is > 0 and <= MaxLoggedSourceLength && IPAddress.TryParse(realIp, out var parsed))
            {
                return parsed.ToString();
            }
        }

        return remote?.ToString() ?? "unknown";
    }

    /// <summary>
    /// 是否为本机代理链路上可能出现的对端地址：回环、RFC1918 私网（Docker 网桥网关都在这些网段）、
    /// 链路本地，以及 IPv6 唯一本地 / 链路本地。IPv4 映射地址（生产容器里实际看到的 ::ffff:172.18.0.1）按 IPv4 判定。
    /// </summary>
    internal static bool IsLocalProxyAddress(IPAddress address)
    {
        // 关键逻辑：映射地址的 AddressFamily 是 IPv6，又不属于 ULA / 链路本地，不先还原就会被判成「公网直连」，
        // 401 节流随之退化成全局只有网桥网关一个桶。这里自行还原，不依赖调用方。
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6UniqueLocal || address.IsIPv6LinkLocal;
        }

        Span<byte> bytes = stackalloc byte[4];
        if (!address.TryWriteBytes(bytes, out var written) || written != 4)
        {
            return false;
        }

        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254);
    }

    private static CapturedRequestInfo BuildRequestInfo(HttpContext context, ResolvedToken token)
    {
        var request = context.Request;
        var tokenValue = token.Value;

        var headers = new List<CapturedHeader>();
        foreach (var header in request.Headers)
        {
            var sensitive = RequestRedaction.IsSensitiveHeader(header.Key);
            if (header.Value.Count == 0)
            {
                headers.Add(new CapturedHeader(header.Key, string.Empty));
                continue;
            }

            // 中文注释：同名多值逐条展开以保留重复项。Kestrel 只按头名聚合，不保留不同头名之间的原始到达顺序。
            foreach (var value in header.Value)
            {
                headers.Add(new CapturedHeader(
                    header.Key,
                    sensitive ? RequestRedaction.Mask : RequestRedaction.RedactToken(value, tokenValue) ?? string.Empty));
            }
        }

        var forwardedFor = request.Headers["X-Forwarded-For"];
        var realIp = request.Headers["X-Real-IP"];
        return new CapturedRequestInfo
        {
            Method = request.Method,
            Path = RequestRedaction.RedactToken(request.PathBase.Add(request.Path).Value ?? "/", tokenValue) ?? "/",
            Query = RequestRedaction.RedactToken(RequestRedaction.RedactQuery(request.QueryString.Value), tokenValue) ?? string.Empty,
            Protocol = request.Protocol,
            Headers = headers,
            RemoteIpAddress = context.Connection.RemoteIpAddress?.ToString(),
            ForwardedFor = forwardedFor.Count == 0 ? null : RequestRedaction.RedactToken(forwardedFor.ToString(), tokenValue),
            RealIp = realIp.Count == 0 ? null : RequestRedaction.RedactToken(realIp.ToString(), tokenValue),
            ContentType = RequestRedaction.RedactToken(request.ContentType, tokenValue),
            DeclaredContentLength = request.ContentLength,
            TokenSource = token.SourceName,
        };
    }

    private enum DrainOutcome
    {
        Completed,
        TooLarge,
        Aborted,
    }
}
