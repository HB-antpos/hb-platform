using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using BlazorApp.Api.Authorization;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Features.FaceCameras;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 真实 MVC + 授权管道下的摄像机管理接口：401 / 403 / 200、no-store 响应头、错误信封、响应体里没有哈希。
/// 数据层用 SQLite 临时库，授权走真实的 PermissionPolicyProvider + PermissionAuthorizationHandler（角色服务用 Mock）。
/// </summary>
public sealed class FaceCameraPipelineTests : IDisposable
{
    private const string AdminUser = "user-with-permission";
    private const string PlainUser = "user-without-permission";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;
    private readonly AuditCapturingLoggerProvider _logs = new();
    private readonly TestServer _server;
    private readonly HttpClient _client;

    public FaceCameraPipelineTests()
    {
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(typeof(Store), typeof(FaceCamera), typeof(FaceCaptureEvent));
        _db.Ado.ExecuteCommand("CREATE UNIQUE INDEX UX_FaceCamera_TokenSha256 ON FaceCamera (TokenSha256)");
        _db.Insertable(new Store { StoreGUID = "s-1013", StoreCode = "1013", StoreName = "Parramatta" }).ExecuteCommand();

        var roleService = new Mock<IRoleService>();
        roleService
            .Setup(service => service.UserHasPermissionAsync(AdminUser, Permissions.FaceCapture.Cameras.Manage))
            .ReturnsAsync(ApiResponse<bool>.OK(true));
        roleService
            .Setup(service => service.UserHasPermissionAsync(PlainUser, It.IsAny<string>()))
            .ReturnsAsync(ApiResponse<bool>.OK(false));

        var service = new FaceCameraAdminService(
            FaceCameraAdminTests.CreateSqlSugarContext(_db),
            new FaceCameraAdminTests.MutableTimeProvider(new DateTime(2026, 10, 13, 2, 0, 0, DateTimeKind.Utc)),
            new ConfigurationBuilder().Build(),
            _logs.CreateLogger<FaceCameraAdminService>());

        _server = new TestServer(
            new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddLogging(builder => builder.AddProvider(_logs));
                    services.AddMemoryCache();
                    services.AddOptions<AppUpdatePolicyOptions>();
                    services.AddSingleton(roleService.Object);
                    services.AddSingleton<IFaceCameraAdminService>(service);
                    // 本类不测图片读取（见 FaceCaptureImagePipelineTests），只需满足控制器的构造依赖。
                    services.AddSingleton(new Mock<IFaceCaptureImageService>().Object);
                    services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", _ => { });
                    services.AddAuthorization();
                    services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
                    services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
                    services.AddControllers().AddApplicationPart(typeof(FaceCamerasController).Assembly);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                }));
        _client = _server.CreateClient();
    }

    [Fact]
    public async Task 未登录返回401_已登录但没有权限返回403_且不触达服务层()
    {
        var calls = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Get, "/api/react/v1/face-cameras", null),
            (HttpMethod.Post, "/api/react/v1/face-cameras", new { cameraId = "cam-1", storeCode = "1013" }),
            (HttpMethod.Put, "/api/react/v1/face-cameras/cam-1", new { storeCode = "1013", enabled = false }),
            (HttpMethod.Post, "/api/react/v1/face-cameras/cam-1/rotate-token", null),
            (HttpMethod.Get, "/api/react/v1/face-cameras/cam-1/captures", null),
            (HttpMethod.Get, "/api/react/v1/face-cameras/cam-1/captures/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/images/11111111111111111111111111111111/content", null),
        };

        foreach (var call in calls)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(call.Method, call.Url, call.Body, user: null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(call.Method, call.Url, call.Body, PlainUser)).StatusCode);
        }

        Assert.Equal(0, await _db.Queryable<FaceCamera>().CountAsync());
        Assert.DoesNotContain(_logs.Entries, entry => entry.Message.Contains("人脸摄像机后台审计"));
    }

    [Fact]
    public async Task 新增_响应一次性带token_且带no_store头_库里只有哈希()
    {
        var response = await Send(HttpMethod.Post, "/api/react/v1/face-cameras",
            new { cameraId = "cam-1", storeCode = "1013", note = "正门", deviceModel = "DS-2CD" }, AdminUser);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoStore(response);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(json.GetProperty("success").GetBoolean());
        var data = json.GetProperty("data");
        var token = data.GetProperty("token").GetString()!;
        Assert.Matches("^[0-9a-f]{48}$", token);
        Assert.Equal($"https://hotbargain.vip/face-ingest/hik/v1/{token}", data.GetProperty("cameraUrl").GetString());
        Assert.Equal("cam-1", data.GetProperty("camera").GetProperty("cameraId").GetString());
        // 摄像机 DTO 里没有任何哈希 / token 字段。
        Assert.DoesNotContain("tokenSha256", data.GetProperty("camera").ToString(), StringComparison.OrdinalIgnoreCase);

        // 之后列表 / 更新 / 抓拍都查不到 token 明文，也不含哈希。
        var list = await (await Send(HttpMethod.Get, "/api/react/v1/face-cameras", null, AdminUser)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(token, list, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tokenSha256", list, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_db.Queryable<FaceCamera>().First().TokenSha256, list, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(token, _logs.Dump(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 业务错误_用信封和错误码返回_同样no_store_且不含token()
    {
        await Send(HttpMethod.Post, "/api/react/v1/face-cameras", new { cameraId = "cam-1", storeCode = "1013" }, AdminUser);

        var duplicate = await Send(HttpMethod.Post, "/api/react/v1/face-cameras", new { cameraId = "cam-1", storeCode = "1013" }, AdminUser);
        var invalid = await Send(HttpMethod.Post, "/api/react/v1/face-cameras", new { cameraId = "Bad Id", storeCode = "1013" }, AdminUser);
        var noStore = await Send(HttpMethod.Post, "/api/react/v1/face-cameras", new { cameraId = "cam-2", storeCode = "9999" }, AdminUser);
        var missing = await Send(HttpMethod.Post, "/api/react/v1/face-cameras/nope/rotate-token", null, AdminUser);
        var emptyBody = await Send(HttpMethod.Post, "/api/react/v1/face-cameras", null, AdminUser);

        await AssertError(duplicate, HttpStatusCode.Conflict, "FACE_CAMERA_ID_EXISTS");
        await AssertError(invalid, HttpStatusCode.BadRequest, "FACE_CAMERA_ID_INVALID");
        await AssertError(noStore, HttpStatusCode.BadRequest, "FACE_CAMERA_STORE_NOT_FOUND");
        await AssertError(missing, HttpStatusCode.NotFound, "FACE_CAMERA_NOT_FOUND");
        await AssertError(emptyBody, HttpStatusCode.BadRequest, "FACE_CAMERA_ID_INVALID");
    }

    [Fact]
    public async Task 轮换_更新_抓拍_走通且都是no_store()
    {
        var created = await Send(HttpMethod.Post, "/api/react/v1/face-cameras", new { cameraId = "cam-1", storeCode = "1013" }, AdminUser);
        var firstToken = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("token").GetString();

        var rotated = await Send(HttpMethod.Post, "/api/react/v1/face-cameras/cam-1/rotate-token", null, AdminUser);
        var updated = await Send(HttpMethod.Put, "/api/react/v1/face-cameras/cam-1",
            new { storeCode = "1013", enabled = false, note = "停用测试" }, AdminUser);
        var captures = await Send(HttpMethod.Get, "/api/react/v1/face-cameras/cam-1/captures?page=1&pageSize=20", null, AdminUser);

        foreach (var response in new[] { rotated, updated, captures })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            AssertNoStore(response);
        }

        var secondToken = JsonDocument.Parse(await rotated.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("token").GetString();
        Assert.NotEqual(firstToken, secondToken);
        var update = JsonDocument.Parse(await updated.Content.ReadAsStringAsync()).RootElement.GetProperty("data");
        Assert.False(update.GetProperty("enabled").GetBoolean());
        Assert.Equal("停用测试", update.GetProperty("note").GetString());
        var page = JsonDocument.Parse(await captures.Content.ReadAsStringAsync()).RootElement.GetProperty("data");
        Assert.Equal(0, page.GetProperty("items").GetArrayLength());
        Assert.False(page.GetProperty("hasMore").GetBoolean());
        var logs = _logs.Dump();
        Assert.DoesNotContain(firstToken!, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secondToken!, logs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 不提供删除_DELETE返回405或404()
    {
        await Send(HttpMethod.Post, "/api/react/v1/face-cameras", new { cameraId = "cam-1", storeCode = "1013" }, AdminUser);

        var response = await Send(HttpMethod.Delete, "/api/react/v1/face-cameras/cam-1", null, AdminUser);

        Assert.True(response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound);
        Assert.Equal(1, await _db.Queryable<FaceCamera>().CountAsync());
    }

    [Fact]
    public void 控制器元数据_整类只认新权限码_无匿名_无删除动作_类上有no_store()
    {
        var type = typeof(FaceCamerasController);

        var authorize = Assert.Single(type.GetCustomAttributes<AuthorizeAttribute>(inherit: false));
        Assert.Equal("FaceCapture.Cameras.Manage", authorize.Policy);
        Assert.Equal(Permissions.FaceCapture.Cameras.Manage, authorize.Policy);
        Assert.Empty(type.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
        Assert.Equal("api/react/v1/face-cameras", Assert.Single(type.GetCustomAttributes<RouteAttribute>(inherit: false)).Template);
        Assert.Single(type.GetCustomAttributes<FaceCameraNoStoreAttribute>(inherit: false));

        var actions = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>(inherit: false).Any())
            .ToList();
        Assert.Equal(6, actions.Count);
        Assert.All(actions, method =>
        {
            Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: false));
            Assert.Empty(method.GetCustomAttributes<AuthorizeAttribute>(inherit: false));
            Assert.DoesNotContain("DELETE", method.GetCustomAttributes<HttpMethodAttribute>().SelectMany(attribute => attribute.HttpMethods));
        });
    }

    [Fact]
    public void NoStore过滤器_设置Cache_Control为no_store()
    {
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var actionContext = new ActionContext(httpContext, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var context = new ResultExecutingContext(actionContext, [], new OkResult(), new object());

        new FaceCameraNoStoreAttribute().OnResultExecuting(context);

        Assert.Equal("no-store", httpContext.Response.Headers.CacheControl.ToString());
    }

    // ---------- 辅助 ----------

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body, string? user)
    {
        var request = new HttpRequestMessage(method, url);
        if (user != null) request.Headers.Add("X-Test-User", user);
        if (body != null) request.Content = JsonContent.Create(body);
        else if (method == HttpMethod.Post || method == HttpMethod.Put) request.Content = JsonContent.Create(new { });
        return await _client.SendAsync(request);
    }

    private static void AssertNoStore(HttpResponseMessage response) =>
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty);

    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        AssertNoStore(response);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Equal(code, json.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("message").GetString()));
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
        _db.Dispose();
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private sealed class HeaderAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out var user))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, user.ToString()),
                    new Claim("userId", user.ToString()),
                    new Claim(ClaimTypes.Name, user + ".name"),
                ],
                "Test");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }
}

/// <summary>新权限码的完整登记：常量、种子（与角色模板脱钩）、入库 SQL、后台菜单与入口。</summary>
public sealed class FaceCameraPermissionRegistrationTests
{
    private const string Code = "FaceCapture.Cameras.Manage";
    private readonly NavigationService _navigation = new();

    [Fact]
    public void 权限常量与种子一致_归入系统管理_不挂任何角色模板()
    {
        Assert.Equal(Code, Permissions.FaceCapture.Cameras.Manage);
        var seed = Assert.Single(PermissionSeedData.AllPermissions, item => item.Code == Code);
        Assert.Equal("系统管理", seed.Category);
        Assert.False(string.IsNullOrWhiteSpace(seed.Name));
        Assert.Contains("/system/face-cameras", seed.Description);
        // 只给管理员：Admin 角色靠角色放行，不写任何角色模板（含店长 / 经理）。
        Assert.All(PermissionSeedData.RolePermissionTemplates, template => Assert.DoesNotContain(Code, template.PermissionCodes));
        // 刻意不带 PosTerminal 前缀（会被当作收银机权限下发）。
        Assert.DoesNotContain("PosTerminal", Code, StringComparison.Ordinal);
    }

    [Fact]
    public void 权限入库脚本_幂等_只登记这一个权限_不授予任何角色或用户()
    {
        var script = File.ReadAllText(ResolveMigrationPath());

        Assert.Contains("N'FaceCapture.Cameras.Manage'", script, StringComparison.Ordinal);
        Assert.Contains("N'系统管理'", script, StringComparison.Ordinal);
        Assert.Contains("@Actor nvarchar(100) = N'Migration_20261013_FaceCameraManage'", script, StringComparison.Ordinal);
        Assert.Contains("DB_NAME() <> N'HBweb'", script, StringComparison.Ordinal);
        Assert.Contains("SET XACT_ABORT ON", script, StringComparison.Ordinal);
        Assert.Contains("BEGIN TRANSACTION", script, StringComparison.Ordinal);
        Assert.Contains("WHERE NOT EXISTS", script, StringComparison.Ordinal);
        Assert.Contains("SYSUTCDATETIME()", script, StringComparison.Ordinal);
        Assert.DoesNotContain("GETDATE()", script, StringComparison.OrdinalIgnoreCase);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(script, @"INSERT INTO dbo\.HbwebSysPermissions\b"));
        Assert.DoesNotContain("HBwebSysRolePermissions", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HbwebSysUserPermissions", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE ", script, StringComparison.OrdinalIgnoreCase);
        // 脚本里的名称 / 描述 / 码与种子完全一致，避免种子与库里文案分叉。
        var seed = PermissionSeedData.AllPermissions.Single(item => item.Code == Code);
        Assert.Contains($"N'{seed.Name}'", script, StringComparison.Ordinal);
        Assert.Contains($"N'{seed.Description}'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void 后台菜单_仅凭新权限码即可见_位于系统管理分组()
    {
        var menu = _navigation.BuildMenu(CreateUser(new Claim("permission", Code)));

        var system = Assert.Single(menu, item => item.Path == "/system");
        var child = Assert.Single(system.Children!);
        Assert.Equal("/system/face-cameras", child.Path);
        Assert.Equal("menu.faceCameras", child.TitleKey);
        Assert.Equal(Code, child.Permission);
        Assert.False(child.RequireAdmin);
    }

    [Theory]
    [InlineData("System.ManageSettings")]
    [InlineData("DailyCloseRecords.View")]
    [InlineData("Stores.View")]
    public void 后台菜单_其他权限看不到摄像机管理(string permission)
    {
        var menu = _navigation.BuildMenu(CreateUser(new Claim("permission", permission)));

        Assert.DoesNotContain(menu.SelectMany(item => item.Children ?? []), item => item.Path == "/system/face-cameras");
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("管理员")]
    public void 后台菜单_管理员角色可见(string role)
    {
        var menu = _navigation.BuildMenu(CreateUser(new Claim(ClaimTypes.Role, role)));

        Assert.Contains(menu.SelectMany(item => item.Children ?? []), item => item.Path == "/system/face-cameras");
    }

    private static ClaimsPrincipal CreateUser(params Claim[] claims) =>
        new(new ClaimsIdentity(claims.Append(new Claim(ClaimTypes.NameIdentifier, "u-1")), "TestAuth"));

    private static string ResolveMigrationPath([System.Runtime.CompilerServices.CallerFilePath] string testFilePath = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFilePath)!, "..", "BlazorApp.Api", "Data", "Migrations",
            "20261013_AddFaceCameraManagePermission.sql"));
}
