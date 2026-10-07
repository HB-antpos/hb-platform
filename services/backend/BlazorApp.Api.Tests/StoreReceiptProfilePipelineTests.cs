using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.StoreReceiptProfiles;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 真实 MVC 管线（TestServer）：路由不与 StoresController 冲突、Stores.View / Stores.Edit 策略生效、
/// 响应 JSON 在 Program.cs 的 JSON 选项（camelCase + WhenWritingNull）下仍显式输出契约要求的 null。
/// </summary>
public sealed class StoreReceiptProfilePipelineTests
{
    [Fact]
    public async Task 只读权限可调status与devices_不能调publish_有编辑权限才能下发()
    {
        var service = new Mock<IStoreReceiptProfileService>();
        service
            .Setup(item => item.GetStatusAsync(It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResponse<List<StoreReceiptProfileStatusItemDto>>.OK(new()
            {
                new StoreReceiptProfileStatusItemDto { StoreGuid = "g-1", StoreCode = "S001", StoreName = "门店" },
            }));
        service
            .Setup(item => item.GetDevicesAsync("g-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResponse<StoreReceiptProfileDevicesDto>.OK(new StoreReceiptProfileDevicesDto
            {
                StoreGuid = "g-1",
                Devices = { new StoreReceiptProfileDeviceDto { DeviceCode = "d-1" } },
            }));
        service
            .Setup(item => item.PublishAsync(It.IsAny<IReadOnlyList<string>?>(), "tester", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResponse<StoreReceiptProfilePublishResultDto>.OK(new StoreReceiptProfilePublishResultDto
            {
                RequestedCount = 1,
                PublishedCount = 1,
                Items = { new StoreReceiptProfilePublishItemDto { StoreGuid = "g-1", StoreCode = "S001", Outcome = "published", Version = 1 } },
            }));
        using var server = CreateServer(service);
        using var client = server.CreateClient();

        using var viewStatus = await SendAsync(client, HttpMethod.Post, "/api/stores/receipt-profile/status", "Stores.View", """{"storeGuids":["g-1"]}""");
        using var viewDevices = await SendAsync(client, HttpMethod.Get, "/api/stores/receipt-profile/g-1/devices", "Stores.View");
        using var viewPublish = await SendAsync(client, HttpMethod.Post, "/api/stores/receipt-profile/publish", "Stores.View", """{"storeGuids":["g-1"]}""");
        using var editPublish = await SendAsync(client, HttpMethod.Post, "/api/stores/receipt-profile/publish", "Stores.Edit", """{"storeGuids":["g-1"]}""");
        using var editStatus = await SendAsync(client, HttpMethod.Post, "/api/stores/receipt-profile/status", "Stores.Edit", """{"storeGuids":["g-1"]}""");
        using var anonymous = await SendAsync(client, HttpMethod.Post, "/api/stores/receipt-profile/status", null, """{"storeGuids":["g-1"]}""");

        Assert.Equal(HttpStatusCode.OK, viewStatus.StatusCode);
        Assert.Equal(HttpStatusCode.OK, viewDevices.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, viewPublish.StatusCode);
        Assert.Equal(HttpStatusCode.OK, editPublish.StatusCode);
        // Stores.Edit 不隐含 Stores.View：权限码各管各的，接口不擅自放宽。
        Assert.Equal(HttpStatusCode.Forbidden, editStatus.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        // 被 403 拦住的 publish 不会触达服务。
        service.Verify(item => item.PublishAsync(It.IsAny<IReadOnlyList<string>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task 响应是ApiResponse且camelCase_契约要求的null显式输出()
    {
        var service = new Mock<IStoreReceiptProfileService>();
        service
            .Setup(item => item.GetStatusAsync(It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResponse<List<StoreReceiptProfileStatusItemDto>>.OK(new()
            {
                new StoreReceiptProfileStatusItemDto
                {
                    StoreGuid = "g-1",
                    StoreCode = "S001",
                    StoreName = "门店",
                    Current = new StoreReceiptProfileFieldsDto { StoreName = "门店" },
                },
            }));
        using var server = CreateServer(service);
        using var client = server.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Post, "/api/stores/receipt-profile/status", "Stores.View", """{"storeGuids":["g-1"]}""");
        var json = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        var item = root.GetProperty("data")[0];
        Assert.Equal("never", item.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("latest").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("publishedAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("current").GetProperty("brandName").ValueKind);
        Assert.Equal(0, item.GetProperty("latestVersion").GetInt32());
        Assert.Equal(0, item.GetProperty("deviceTotal").GetInt32());
    }

    [Theory]
    [InlineData(StoreReceiptProfileErrorCodes.PublishConflict, HttpStatusCode.Conflict)]
    [InlineData(StoreReceiptProfileErrorCodes.NotPublishable, HttpStatusCode.BadRequest)]
    [InlineData(StoreReceiptProfileErrorCodes.InvalidRequest, HttpStatusCode.BadRequest)]
    public async Task Publish整批失败_HTTP状态码_ErrorCode与Details在响应体中(string errorCode, HttpStatusCode expectedStatus)
    {
        var service = new Mock<IStoreReceiptProfileService>();
        service
            .Setup(item => item.PublishAsync(It.IsAny<IReadOnlyList<string>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResponse<StoreReceiptProfilePublishResultDto>.Error(
                "失败",
                errorCode,
                new List<StoreReceiptProfilePublishErrorDetailDto>
                {
                    new() { StoreGuid = "g-1", ErrorCode = StoreReceiptProfileErrorCodes.StoreNotFound, Message = "门店不存在或已删除" },
                }));
        using var server = CreateServer(service);
        using var client = server.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Post, "/api/stores/receipt-profile/publish", "Stores.Edit", """{"storeGuids":["g-1"]}""");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(errorCode, document.RootElement.GetProperty("errorCode").GetString());
        var detail = document.RootElement.GetProperty("details")[0];
        Assert.Equal("g-1", detail.GetProperty("storeGuid").GetString());
        Assert.Equal("STORE_NOT_FOUND", detail.GetProperty("errorCode").GetString());
        Assert.False(detail.TryGetProperty("storeCode", out _));
    }

    [Fact]
    public async Task Devices_门店不存在返回404与STORE_NOT_FOUND()
    {
        var service = new Mock<IStoreReceiptProfileService>();
        service
            .Setup(item => item.GetDevicesAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResponse<StoreReceiptProfileDevicesDto>.Error("门店不存在或已删除", StoreReceiptProfileErrorCodes.StoreNotFound));
        using var server = CreateServer(service);
        using var client = server.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Get, "/api/stores/receipt-profile/missing/devices", "Stores.View");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("STORE_NOT_FOUND", document.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task 空对象请求体交给服务按INVALID请求处理而不是500()
    {
        var service = new Mock<IStoreReceiptProfileService>();
        service
            .Setup(item => item.GetStatusAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResponse<List<StoreReceiptProfileStatusItemDto>>.Error("storeGuids 至少包含 1 个门店", StoreReceiptProfileErrorCodes.InvalidRequest));
        using var server = CreateServer(service);
        using var client = server.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Post, "/api/stores/receipt-profile/status", "Stores.View", "{}");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_RECEIPT_PROFILE_REQUEST", document.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task receipt_profile根路径没有动作_只会404或405而不是歧义匹配的500()
    {
        // StoresController 的路由前缀同为 api/stores（大小写不敏感）；它的动作都以字面量段开头，
        // 与 receipt-profile 前缀共存时不得产生 AmbiguousMatchException。
        var service = new Mock<IStoreReceiptProfileService>(MockBehavior.Strict);
        using var server = CreateServer(service);
        using var client = server.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Get, "/api/stores/receipt-profile", "Stores.View");

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, response.StatusCode.ToString());
        service.VerifyNoOtherCalls();
    }

    private static TestServer CreateServer(Mock<IStoreReceiptProfileService> service)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(item => item.GetCurrentUsername()).Returns("tester");
        return new TestServer(
            new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddSingleton(service.Object);
                    services.AddSingleton(currentUser.Object);
                    services.AddAuthentication("test")
                        .AddScheme<AuthenticationSchemeOptions, PermissionHeaderAuthHandler>("test", _ => { });
                    services.AddAuthorization(options =>
                    {
                        foreach (var permission in new[] { Permissions.Stores.View, Permissions.Stores.Edit })
                        {
                            options.AddPolicy(permission, policy => policy.RequireClaim("perm", permission));
                        }
                    });
                    services
                        .AddControllers()
                        .AddApplicationPart(typeof(StoreReceiptProfilesController).Assembly)
                        // 与 Program.cs 的 AddJsonOptions 一致。
                        .AddJsonOptions(options =>
                        {
                            options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                            options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
                            options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                        });
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                })
        );
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        string? permission,
        string? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (permission is not null)
        {
            request.Headers.Add("X-Test-Perm", permission);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return client.SendAsync(request);
    }

    /// <summary>测试认证：带 X-Test-Perm 头即视为已登录并持有该权限，否则未登录。</summary>
    private sealed class PermissionHeaderAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public PermissionHeaderAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Perm", out var permission))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, "test-user"), new Claim("perm", permission.ToString()) },
                "test");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), "test")));
        }
    }
}
