using Hbpos.Api;
using Hbpos.Api.Auth;
using Hbpos.Api.Logging;
using Hbpos.Api.Services;
using Hbpos.Contracts.Devices;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

// 中文注释：慢请求/5xx 结构化日志阈值（毫秒），目录热路径默认只记录慢与错，避免全量诊断日志。
var slowRequestThresholdMs = builder.Configuration.GetValue("Logging:SlowRequestThresholdMs", 2_000);

builder.Logging.AddHbposFileLogging(builder.Configuration, builder.Environment);
builder.Services.AddHbposCentralLogging(builder.Configuration);
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    foreach (var proxyValue in builder.Configuration
                 .GetSection("ReverseProxy:KnownProxies")
                 .GetChildren()
                 .Select(static child => child.Value)
                 .Where(static value => !string.IsNullOrWhiteSpace(value)))
    {
        if (!IPAddress.TryParse(proxyValue, out var proxyAddress))
        {
            throw new InvalidOperationException("ReverseProxy:KnownProxies contains an invalid IP address.");
        }

        options.KnownProxies.Add(proxyAddress);
    }
});
builder.Services.AddRateLimiter(DeviceActivationRateLimitOptions.Configure);
builder.Services.AddSwaggerGen(options =>
{
    options.SchemaFilter<SharedSaleCartPayloadSchemaFilter>();
    options.SchemaFilter<DeviceActivationRequestSchemaFilter>();
});
// 中文注释：响应压缩仅由 CatalogResponseCompressionProvider 按白名单放行（商品分页 + checksumVersion=2、
// 码冲突候选），其他端点与 v1/WPF 分页一律保持未压缩，行为与启用前一致。
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/json"]);
});
builder.Services.AddSingleton<IResponseCompressionProvider, CatalogResponseCompressionProvider>();
builder.Services
    .AddAuthentication(DeviceAuthConstants.Scheme)
    .AddScheme<AuthenticationSchemeOptions, DeviceAuthenticationHandler>(
        DeviceAuthConstants.Scheme,
        options => { });
builder.Services.AddAuthorization(CashierAuthorizationPolicies.AddPolicies);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAuthorizationHandler, CashierPermissionAuthorizationHandler>();
builder.Services.AddHbposApiServices(builder.Configuration);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    // 启动时只检查 Square 后端注册存在，避免提前实例化 SQL 仓储并要求数据库连接串。
    var serviceRegistration = scope.ServiceProvider.GetRequiredService<IServiceProviderIsService>();
    if (!serviceRegistration.IsService(typeof(ISquareTerminalBackendService)))
    {
        throw new InvalidOperationException($"{nameof(ISquareTerminalBackendService)} is not registered.");
    }

    // REST client 不依赖本地数据库，真实解析可提前验证 Square:ApiVersion 配置。
    _ = scope.ServiceProvider.GetRequiredService<ISquareTerminalRestClient>();

    var storeSchemaInitializer = scope.ServiceProvider.GetRequiredService<IStoreSchemaInitializer>();
    await storeSchemaInitializer.InitializeAsync();

    // 关键逻辑：考勤签名服务依赖 MainDb 密钥表，初始化失败时直接阻止 API 启动。
    var attendanceQrKeySchemaInitializer = scope.ServiceProvider.GetRequiredService<IAttendanceQrKeySchemaInitializer>();
    await attendanceQrKeySchemaInitializer.InitializeAsync();

    var advertisementSchemaInitializer = scope.ServiceProvider.GetRequiredService<IAdvertisementSchemaInitializer>();
    await advertisementSchemaInitializer.InitializeAsync();

    var linklyCloudCredentialSchemaInitializer = scope.ServiceProvider.GetRequiredService<ILinklyCloudCredentialSchemaInitializer>();
    await linklyCloudCredentialSchemaInitializer.InitializeAsync();

    if (HasConnectionString(app.Configuration, "PosmConnection", "HBPOSMConnection"))
    {
        var orderSyncSchemaInitializer = scope.ServiceProvider.GetRequiredService<IOrderSyncSchemaInitializer>();
        await orderSyncSchemaInitializer.InitializeAsync();

        var operationAuditSchemaInitializer = scope.ServiceProvider.GetRequiredService<IOperationAuditSchemaInitializer>();
        await operationAuditSchemaInitializer.InitializeAsync();

        var deviceRuntimeStatusSchemaInitializer = scope.ServiceProvider.GetRequiredService<IDeviceRuntimeStatusSchemaInitializer>();
        await deviceRuntimeStatusSchemaInitializer.InitializeAsync();

        var linklyCloudBackendAsyncSchemaInitializer = scope.ServiceProvider.GetRequiredService<ILinklyCloudBackendAsyncSchemaInitializer>();
        await linklyCloudBackendAsyncSchemaInitializer.InitializeAsync();

        var linklySettlementSchemaInitializer = scope.ServiceProvider.GetRequiredService<ILinklySettlementSchemaInitializer>();
        await linklySettlementSchemaInitializer.InitializeAsync();

        var installmentRepaymentClaimSchemaInitializer = scope.ServiceProvider.GetRequiredService<IInstallmentRepaymentClaimSchemaInitializer>();
        await installmentRepaymentClaimSchemaInitializer.InitializeAsync();

        var installmentCancelClaimSchemaInitializer = scope.ServiceProvider.GetRequiredService<IInstallmentCancelClaimSchemaInitializer>();
        await installmentCancelClaimSchemaInitializer.InitializeAsync();

        var sharedHeldOrderSchemaInitializer = scope.ServiceProvider.GetRequiredService<ISharedHeldOrderSchemaInitializer>();
        await sharedHeldOrderSchemaInitializer.InitializeAsync();

        var squareWebhookSchemaInitializer = scope.ServiceProvider.GetRequiredService<ISquareWebhookSchemaInitializer>();
        await squareWebhookSchemaInitializer.InitializeAsync();
    }

    var squareTokenSchemaInitializer = scope.ServiceProvider.GetRequiredService<ISquareTokenSchemaInitializer>();
    await squareTokenSchemaInitializer.InitializeAsync();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

// 关键逻辑：仅信任显式代理的一跳 X-Forwarded-For，使审核端点按真实公网 IP 限流。
app.UseForwardedHeaders();
app.UseHttpsRedirection();

// 目录热路径结构化日志：仅慢请求与 5xx 错误记录 Method/Path 与耗时，
// 不记录完整查询串、游标、lease ID、商品与凭据，避免大目录下载刷爆诊断日志。
app.Use(async (context, next) =>
{
    var diagnoseStopwatch = Stopwatch.StartNew();
    try
    {
        await next();
    }
    finally
    {
        diagnoseStopwatch.Stop();
        if (context.Response.StatusCode >= 500 ||
            diagnoseStopwatch.ElapsedMilliseconds >= slowRequestThresholdMs)
        {
            var diagnoseLogger = context.RequestServices
                .GetRequiredService<ILogger<Program>>();
            diagnoseLogger.LogInformation(
                "[RequestSummary] {Method} {Path} => {StatusCode} {ElapsedMs}ms",
                context.Request.Method,
                context.Request.Path,
                context.Response.StatusCode,
                diagnoseStopwatch.ElapsedMilliseconds);
        }
    }
});

app.UseResponseCompression();
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

static bool HasConnectionString(IConfiguration configuration, string primaryName, string fallbackName)
{
    return !string.IsNullOrWhiteSpace(configuration.GetConnectionString(primaryName)) ||
        !string.IsNullOrWhiteSpace(configuration.GetConnectionString(fallbackName));
}
