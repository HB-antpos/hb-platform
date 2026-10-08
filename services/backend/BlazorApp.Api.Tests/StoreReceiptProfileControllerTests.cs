using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlazorApp.Api.Controllers;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.StoreReceiptProfiles;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 门店小票资料下发控制器：路由与权限特性、错误码到 HTTP 状态映射、JSON 形状，以及架构边界。
/// </summary>
public sealed class StoreReceiptProfileControllerTests
{
    // ───────────────────────── 路由与权限 ─────────────────────────

    [Fact]
    public void 控制器路由前缀是api_stores_receipt_profile且要求登录()
    {
        var type = typeof(StoreReceiptProfilesController);

        Assert.NotNull(type.GetCustomAttribute<ApiControllerAttribute>());
        Assert.Equal("api/stores/receipt-profile", type.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());
    }

    [Theory]
    [InlineData(nameof(StoreReceiptProfilesController.GetStatus), typeof(HttpPostAttribute), "status", Permissions.Stores.View)]
    [InlineData(nameof(StoreReceiptProfilesController.GetDevices), typeof(HttpGetAttribute), "{storeGuid}/devices", Permissions.Stores.View)]
    [InlineData(nameof(StoreReceiptProfilesController.Publish), typeof(HttpPostAttribute), "publish", Permissions.Stores.Edit)]
    public void 三个接口的HTTP方法_模板_权限与契约一致(
        string methodName,
        Type httpAttributeType,
        string template,
        string policy)
    {
        var method = typeof(StoreReceiptProfilesController).GetMethod(methodName)!;

        var http = (HttpMethodAttribute)method.GetCustomAttribute(httpAttributeType)!;
        Assert.Equal(template, http.Template);
        // 复用现有权限码，不新增：View 管只读，Edit 管下发。
        var authorize = Assert.Single(method.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(policy, authorize.Policy);
    }

    [Fact]
    public void 只暴露三个接口且没有匿名放行()
    {
        var actions = typeof(StoreReceiptProfilesController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any())
            .ToList();

        Assert.Equal(3, actions.Count);
        Assert.DoesNotContain(actions, method => method.GetCustomAttribute<AllowAnonymousAttribute>() is not null);
        Assert.Null(typeof(StoreReceiptProfilesController).GetCustomAttribute<AllowAnonymousAttribute>());
    }

    [Fact]
    public void 与现有StoresController的路由不冲突()
    {
        // StoresController 的路由前缀是 api/[controller]；它的所有动作模板都以字面量段开头（all-by-name / guid/... / sync ...），
        // 没有以 {参数} 开头的模板，因此 api/stores/receipt-profile/... 不会与它产生歧义匹配。
        var templates = typeof(StoresController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>())
            .Select(attribute => attribute.Template)
            .Where(template => !string.IsNullOrEmpty(template))
            .ToList();

        Assert.NotEmpty(templates);
        Assert.All(templates, template => Assert.False(template!.StartsWith('{'), template));
        Assert.DoesNotContain(templates, template => template!.StartsWith("receipt-profile", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 本功能未新增权限码()
    {
        var storePermissions = typeof(Permissions.Stores)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet();

        Assert.Equal(
            new[] { "Stores.View", "Stores.Create", "Stores.Edit", "Stores.Delete", "Stores.Sync" }.ToHashSet(),
            storePermissions
        );
    }

    // ───────────────────────── 错误码到 HTTP 状态 ─────────────────────────

    [Theory]
    [InlineData(StoreReceiptProfileErrorCodes.InvalidRequest, 400)]
    [InlineData(StoreReceiptProfileErrorCodes.NotPublishable, 400)]
    [InlineData(StoreReceiptProfileErrorCodes.StoreNotFound, 404)]
    [InlineData(StoreReceiptProfileErrorCodes.PublishConflict, 409)]
    [InlineData("SOMETHING_ELSE", 400)]
    [InlineData(null, 400)]
    public void 错误码映射到HTTP状态(string? errorCode, int expectedStatus)
    {
        Assert.Equal(expectedStatus, StoreReceiptProfilesController.MapStatusCode(errorCode));
    }

    [Fact]
    public async Task Status_成功返回200并把storeGuids原样交给服务()
    {
        var service = new Mock<IStoreReceiptProfileService>();
        var data = new List<StoreReceiptProfileStatusItemDto> { new() { StoreGuid = "g-1" } };
        service
            .Setup(item => item.GetStatusAsync(It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResponse<List<StoreReceiptProfileStatusItemDto>>.OK(data));
        var controller = CreateController(service);

        var result = await controller.GetStatus(
            new StoreReceiptProfileRequestDto { StoreGuids = new List<string> { "g-1" } },
            CancellationToken.None
        );

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(data, Assert.IsType<ApiResponse<List<StoreReceiptProfileStatusItemDto>>>(ok.Value).Data);
        service.Verify(item => item.GetStatusAsync(
            It.Is<IReadOnlyList<string>?>(guids => guids != null && guids.SequenceEqual(new[] { "g-1" })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Status与Publish_请求体缺失时交给服务按INVALID_RECEIPT_PROFILE_REQUEST处理()
    {
        var service = new Mock<IStoreReceiptProfileService>();
        service
            .Setup(item => item.GetStatusAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResponse<List<StoreReceiptProfileStatusItemDto>>.Error("bad", StoreReceiptProfileErrorCodes.InvalidRequest));
        service
            .Setup(item => item.PublishAsync(null, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResponse<StoreReceiptProfilePublishResultDto>.Error("bad", StoreReceiptProfileErrorCodes.InvalidRequest));
        var controller = CreateController(service);

        var status = await controller.GetStatus(null, CancellationToken.None);
        var publish = await controller.Publish(new StoreReceiptProfileRequestDto(), CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(status).StatusCode);
        Assert.Equal(400, Assert.IsType<ObjectResult>(publish).StatusCode);
    }

    [Fact]
    public async Task Devices_门店不存在返回404且响应体保留STORE_NOT_FOUND()
    {
        var service = new Mock<IStoreReceiptProfileService>();
        service
            .Setup(item => item.GetDevicesAsync("g-x", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResponse<StoreReceiptProfileDevicesDto>.Error("门店不存在或已删除", StoreReceiptProfileErrorCodes.StoreNotFound));
        var controller = CreateController(service);

        var result = await controller.GetDevices("g-x", CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, response.StatusCode);
        var body = Assert.IsType<ApiResponse<StoreReceiptProfileDevicesDto>>(response.Value);
        Assert.False(body.Success);
        Assert.Equal("STORE_NOT_FOUND", body.ErrorCode);
    }

    [Fact]
    public async Task Publish_把当前登录用户名作为PublishedBy传给服务()
    {
        var service = new Mock<IStoreReceiptProfileService>();
        service
            .Setup(item => item.PublishAsync(It.IsAny<IReadOnlyList<string>?>(), "alice", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResponse<StoreReceiptProfilePublishResultDto>.OK(new StoreReceiptProfilePublishResultDto()));
        var controller = CreateController(service, username: "alice");

        var result = await controller.Publish(
            new StoreReceiptProfileRequestDto { StoreGuids = new List<string> { "g-1", "g-2" } },
            CancellationToken.None
        );

        Assert.IsType<OkObjectResult>(result);
        service.Verify(item => item.PublishAsync(
            It.Is<IReadOnlyList<string>?>(guids => guids != null && guids.SequenceEqual(new[] { "g-1", "g-2" })),
            "alice",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(StoreReceiptProfileErrorCodes.NotPublishable, 400)]
    [InlineData(StoreReceiptProfileErrorCodes.PublishConflict, 409)]
    [InlineData(StoreReceiptProfileErrorCodes.InvalidRequest, 400)]
    public async Task Publish_整批失败时状态码与响应体Details一并返回(string errorCode, int expectedStatus)
    {
        var details = new List<StoreReceiptProfilePublishErrorDetailDto>
        {
            new() { StoreGuid = "g-1", StoreCode = "S001", ErrorCode = "STORE_INACTIVE", Message = "门店已停用" },
        };
        var service = new Mock<IStoreReceiptProfileService>();
        service
            .Setup(item => item.PublishAsync(It.IsAny<IReadOnlyList<string>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiResponse<StoreReceiptProfilePublishResultDto>.Error("失败", errorCode, details));
        var controller = CreateController(service);

        var result = await controller.Publish(
            new StoreReceiptProfileRequestDto { StoreGuids = new List<string> { "g-1" } },
            CancellationToken.None
        );

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(expectedStatus, response.StatusCode);
        var body = Assert.IsType<ApiResponse<StoreReceiptProfilePublishResultDto>>(response.Value);
        Assert.False(body.Success);
        Assert.Equal(errorCode, body.ErrorCode);
        Assert.Same(details, body.Details);
    }

    // ───────────────────────── JSON 形状（与契约逐字一致） ─────────────────────────

    // 与 Program.cs 的 AddJsonOptions 一致：camelCase + WhenWritingNull（null 属性默认被整个省略）。
    private static readonly JsonSerializerOptions WebJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void StatusItem_JSON字段名与契约逐字一致_可空字段显式输出null()
    {
        var item = new StoreReceiptProfileStatusItemDto
        {
            StoreGuid = "g-1",
            StoreCode = "S001",
            StoreName = "门店",
            Status = StoreReceiptProfileStatuses.Never,
            Current = new StoreReceiptProfileFieldsDto { StoreName = "门店" },
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(item, WebJson));
        var root = document.RootElement;

        Assert.Equal(
            new[]
            {
                "storeGuid", "storeCode", "storeName", "status", "latestVersion", "publishedAtUtc", "publishedBy",
                "current", "latest", "deviceTotal", "deviceApplied",
            },
            root.EnumerateObject().Select(property => property.Name).ToArray()
        );
        Assert.Equal(JsonValueKind.Null, root.GetProperty("latest").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("publishedAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("publishedBy").ValueKind);
        Assert.Equal(
            new[] { "brandName", "storeName", "address", "phone", "abn", "returnPolicy", "voucherTerms", "installmentTerms" },
            root.GetProperty("current").EnumerateObject().Select(property => property.Name).ToArray()
        );
        Assert.Equal(JsonValueKind.Null, root.GetProperty("current").GetProperty("brandName").ValueKind);
        // 未定制的代金券使用说明 / 分期条款显式输出 null（前端靠 === null 判断「使用收银端默认文案」）。
        Assert.Equal(JsonValueKind.Null, root.GetProperty("current").GetProperty("voucherTerms").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("current").GetProperty("installmentTerms").ValueKind);
    }

    [Fact]
    public void StatusItem_发布时间序列化为带Z的ISO字符串()
    {
        var item = new StoreReceiptProfileStatusItemDto
        {
            PublishedAtUtc = DateTime.SpecifyKind(new DateTime(2026, 10, 7, 3, 0, 0), DateTimeKind.Utc),
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(item, WebJson));

        Assert.Equal("2026-10-07T03:00:00Z", document.RootElement.GetProperty("publishedAtUtc").GetString());
    }

    [Fact]
    public void DeviceDto与DevicesDto_JSON字段名与契约逐字一致()
    {
        var devices = new StoreReceiptProfileDevicesDto
        {
            StoreGuid = "g-1",
            StoreCode = "S001",
            LatestVersion = 2,
            Devices = { new StoreReceiptProfileDeviceDto { DeviceCode = "d", DeviceSystem = "Windows", ClientKind = "wpf", DeviceStatus = 1 } },
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(devices, WebJson));
        var root = document.RootElement;

        Assert.Equal(
            new[] { "storeGuid", "storeCode", "latestVersion", "devices" },
            root.EnumerateObject().Select(property => property.Name).ToArray()
        );
        var device = root.GetProperty("devices")[0];
        Assert.Equal(
            new[]
            {
                "deviceCode", "deviceSystem", "clientKind", "deviceStatus", "isOnline", "lastHeartbeatAt",
                "appliedVersion", "appliedAtUtc", "upToDate",
            },
            device.EnumerateObject().Select(property => property.Name).ToArray()
        );
        Assert.Equal(JsonValueKind.Null, device.GetProperty("lastHeartbeatAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, device.GetProperty("appliedVersion").ValueKind);
        Assert.Equal(JsonValueKind.Null, device.GetProperty("appliedAtUtc").ValueKind);
    }

    [Fact]
    public void PublishResult与错误Details_JSON字段名与契约逐字一致()
    {
        var result = new StoreReceiptProfilePublishResultDto
        {
            RequestedCount = 1,
            PublishedCount = 1,
            Items = { new StoreReceiptProfilePublishItemDto { StoreGuid = "g", StoreCode = "S", Outcome = "published", Version = 1 } },
        };
        var details = new List<StoreReceiptProfilePublishErrorDetailDto>
        {
            new() { StoreGuid = "g", ErrorCode = "STORE_NOT_FOUND", Message = "m" },
            new() { StoreGuid = "g2", StoreCode = "S2", ErrorCode = "STORE_INACTIVE", Message = "m" },
        };

        using var resultDocument = JsonDocument.Parse(JsonSerializer.Serialize(result, WebJson));
        using var detailsDocument = JsonDocument.Parse(JsonSerializer.Serialize(details, WebJson));

        Assert.Equal(
            new[] { "requestedCount", "publishedCount", "unchangedCount", "items" },
            resultDocument.RootElement.EnumerateObject().Select(property => property.Name).ToArray()
        );
        Assert.Equal(
            new[] { "storeGuid", "storeCode", "outcome", "version" },
            resultDocument.RootElement.GetProperty("items")[0].EnumerateObject().Select(property => property.Name).ToArray()
        );
        // storeCode 在 Details 里是可选的（契约写作 storeCode?）：找不到的门店没有代码，整个省略。
        Assert.Equal(
            new[] { "storeGuid", "errorCode", "message" },
            detailsDocument.RootElement[0].EnumerateObject().Select(property => property.Name).ToArray()
        );
        Assert.Equal(
            new[] { "storeGuid", "storeCode", "errorCode", "message" },
            detailsDocument.RootElement[1].EnumerateObject().Select(property => property.Name).ToArray()
        );
    }

    [Fact]
    public void 请求体JSON用storeGuids绑定()
    {
        var request = JsonSerializer.Deserialize<StoreReceiptProfileRequestDto>(
            """{"storeGuids":["a","b"]}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        );

        Assert.Equal(new[] { "a", "b" }, request!.StoreGuids);
    }

    [Fact]
    public void 状态与结果枚举值与契约一致()
    {
        Assert.Equal("never", StoreReceiptProfileStatuses.Never);
        Assert.Equal("synced", StoreReceiptProfileStatuses.Synced);
        Assert.Equal("pending", StoreReceiptProfileStatuses.Pending);
        Assert.Equal("published", StoreReceiptProfileOutcomes.Published);
        Assert.Equal("unchanged", StoreReceiptProfileOutcomes.Unchanged);
        Assert.Equal("INVALID_RECEIPT_PROFILE_REQUEST", StoreReceiptProfileErrorCodes.InvalidRequest);
        Assert.Equal("STORE_NOT_FOUND", StoreReceiptProfileErrorCodes.StoreNotFound);
        Assert.Equal("RECEIPT_PROFILE_NOT_PUBLISHABLE", StoreReceiptProfileErrorCodes.NotPublishable);
        Assert.Equal("RECEIPT_PROFILE_PUBLISH_CONFLICT", StoreReceiptProfileErrorCodes.PublishConflict);
        Assert.Equal("STORE_PROFILE_INVALID_CHARACTERS", StoreReceiptProfileErrorCodes.InvalidCharacters);
        Assert.Equal("STORE_PROFILE_TOO_LONG", StoreReceiptProfileErrorCodes.TooLong);
    }

    // ───────────────────────── 架构边界 ─────────────────────────

    [Fact]
    public void 领域服务与控制器不依赖Services_React与Hbpos且不放进ProductWarehouse()
    {
        var root = FindRepoRoot();
        var files = Directory
            .EnumerateFiles(
                Path.Combine(root, "services/backend/BlazorApp.Api/Services/StoreReceiptProfiles"),
                "*.cs",
                SearchOption.AllDirectories
            )
            .Append(Path.Combine(root, "services/backend/BlazorApp.Api/Controllers/React/StoreReceiptProfilesController.cs"))
            .ToList();

        Assert.True(files.Count >= 4);
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("BlazorApp.Api.Services.React", source, StringComparison.Ordinal);
            Assert.DoesNotContain("using Hbpos.", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Features.ProductWarehouse", source, StringComparison.Ordinal);
        }

        Assert.False(
            Directory.Exists(Path.Combine(root, "services/backend/BlazorApp.Api/Features/ProductWarehouse/StoreReceiptProfiles"))
        );
    }

    [Fact]
    public void 新表实体不进SqlSugarContext自动建表清单()
    {
        // 建表只由版本号迁移负责：进了 tableTypes 会在启动时绕过「须显式 --schema=migrate」的门禁。
        var source = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "services/backend/BlazorApp.Api/Data/SqlSugarContext.cs")
        );

        Assert.DoesNotContain("StoreReceiptProfileRelease", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PosReceiptProfileAck", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Store实体未被修改_不含小票下发相关新列()
    {
        var storeProperties = typeof(BlazorApp.Shared.Models.Store)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(property => property.Name)
            .ToHashSet();

        Assert.DoesNotContain(storeProperties, name => name.Contains("Release", StringComparison.OrdinalIgnoreCase) || name.Contains("ProfileVersion", StringComparison.OrdinalIgnoreCase));
    }

    // ───────────────────────── 辅助 ─────────────────────────

    private static StoreReceiptProfilesController CreateController(
        Mock<IStoreReceiptProfileService> service,
        string username = "tester")
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(item => item.GetCurrentUsername()).Returns(username);
        return new StoreReceiptProfilesController(service.Object, currentUser.Object);
    }

    private static string FindRepoRoot([CallerFilePath] string sourcePath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourcePath)!);
        while (directory != null)
        {
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("无法定位仓库根目录。");
    }
}
