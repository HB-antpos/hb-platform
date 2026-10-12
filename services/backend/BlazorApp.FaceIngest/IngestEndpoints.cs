namespace BlazorApp.FaceIngest;

/// <summary>
/// 路由登记：
/// - <c>GET /health</c>：匿名健康检查，只给出开关、存储统计与摄像机来源状态，不暴露摄像机 / 门店信息；
/// - 接收端点（任意 HTTP 方法）：<c>/hik/v1/{token}</c>、<c>/hik/v1/{token}/{**rest}</c>（token 在路径，首选），
///   <c>/hik/v1</c> 与 <c>/hik/v1/</c> 带 <c>?token=</c>，以及以上任一路径带 <c>Authorization: Basic</c>。
/// 公网地址经 Nginx 去掉前缀：https://hotbargain.vip/face-ingest/hik/v1/&lt;token&gt; → 本服务 /hik/v1/&lt;token&gt;。
/// </summary>
internal static class IngestEndpoints
{
    public static IEndpointRouteBuilder MapFaceIngestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health", WriteHealthAsync);

        // 中文注释：用 Map 而非 MapPost —— 摄像机 Test 按钮可能发 GET / PUT（未证实），一律接收并记录。
        RequestDelegate ingest = static context =>
            context.RequestServices.GetRequiredService<FaceIngestRequestHandler>().HandleAsync(context);
        endpoints.Map("/hik/v1/{" + IngestTokenResolver.RouteValueName + "}", ingest);
        endpoints.Map("/hik/v1/{" + IngestTokenResolver.RouteValueName + "}/{**rest}", ingest);
        // 中文注释：路由匹配忽略末尾斜杠，这一条同时覆盖 /hik/v1 与 /hik/v1/。
        endpoints.Map("/hik/v1", ingest);
        return endpoints;
    }

    private static Task WriteHealthAsync(HttpContext context)
    {
        var settings = context.RequestServices.GetRequiredService<FaceIngestSettingsMonitor>().Current;
        var store = context.RequestServices.GetRequiredService<RawCaptureStore>();
        var cameraSource = context.RequestServices.GetRequiredService<ICameraRegistrySource>();
        // 中文注释：来源未就绪时 status 仍是 healthy（进程本身健康，容器不该被反复重启），由 cameraSourceReady 单独反映。
        return context.Response.WriteAsJsonAsync(new HealthResponse(
            "healthy",
            settings.CaptureEnabled,
            store.StoredFileCount,
            store.StoredBytes,
            cameraSource.SourceName,
            cameraSource.Current is not null));
    }

    /// <summary>
    /// 健康检查响应（camelCase）：status / captureEnabled / storedFileCount / storedBytes / cameraSource（json | database）/ cameraSourceReady。
    /// </summary>
    internal sealed record HealthResponse(
        string Status,
        bool CaptureEnabled,
        long StoredFileCount,
        long StoredBytes,
        string CameraSource,
        bool CameraSourceReady);
}
