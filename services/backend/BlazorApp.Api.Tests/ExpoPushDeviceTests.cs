using System.Reflection;
using System.Security.Claims;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 推送令牌注册 / 撤销：令牌格式校验、upsert、换账号转移（手持换人登录）、活动设备上限、只能撤销自己的令牌、
/// 控制器的 400 / 401 / 500 映射、DELETE 只收请求体，以及日志里不出现令牌明文。
/// </summary>
public sealed class ExpoPushDeviceTests : IDisposable
{
    private const string UserA = "user-a";
    private const string UserB = "user-b";

    private readonly PushTestDatabase _database = new();
    private readonly PushTestClock _clock = new(PushTestDatabase.Start);
    private readonly AuditCapturingLoggerProvider _logs = new();

    private AppPushDeviceService CreateService() =>
        new(_database.Context, _clock, _logs.CreateLogger<AppPushDeviceService>());

    private static PushDeviceRegisterRequestDto Register(string token, string platform = "ios", string? appVersion = "1.0.7", string? runtimeVersion = "1.0.7") =>
        new() { ExpoPushToken = token, Platform = platform, AppVersion = appVersion, RuntimeVersion = runtimeVersion };

    private List<AppPushDevice> Devices(string token) =>
        _database.Db.Queryable<AppPushDevice>().Where(device => device.ExpoPushToken == token).OrderBy(device => device.CreatedAtUtc).ToList();

    // ---------- 令牌格式 ----------

    [Theory]
    [InlineData("ExponentPushToken[abcdefghijklmnopqrstuv]")]
    [InlineData("ExpoPushToken[abcdefghij]")]                       // 内层最短 10 位
    [InlineData("ExponentPushToken[A-Za_z0-9-_AbCdEf]")]            // 允许 - 与 _
    public void 令牌格式_合法写法通过(string token)
    {
        Assert.Equal(token, AppPushDeviceService.ValidateToken(token));
        Assert.Equal(token, AppPushDeviceService.ValidateToken("  " + token + "\n"));   // 首尾空白会被裁掉
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abcdefghijklmnopqrstuv")]                           // 没有前缀
    [InlineData("ExponentPushToken[abcdefghi]")]                     // 内层只有 9 位
    [InlineData("ExponentPushToken[abcdefghij.klmnop]")]             // 非法字符
    [InlineData("ExponentPushToken[abcdefghij klmnop]")]             // 空格
    [InlineData("ExponentPushToken[abcdefghijklmnopqrstuv")]         // 缺右括号
    [InlineData("ExponentPushToken[abcdefghijklmnopqrstuv]x")]       // 括号后多余字符
    [InlineData("ExponentPushToken[abcdefghijklmnopqrstuv]\nExponentPushToken[abcdefghijklmnopqrstuv]")]
    [InlineData("FcmPushToken[abcdefghijklmnopqrstuv]")]             // 前缀不对
    [InlineData("exponentpushtoken[abcdefghijklmnopqrstuv]")]        // 前缀区分大小写
    public void 令牌格式_非法写法一律拒绝(string? token)
    {
        Assert.Throws<ArgumentException>(() => AppPushDeviceService.ValidateToken(token));
    }

    [Fact]
    public void 令牌格式_整串不超过200_内层字符集正则本身会放过超长_须另加整串长度检查()
    {
        // "ExponentPushToken[" + "]" = 19 字符 → 内层 181 位时整串刚好 200。
        var atLimit = "ExponentPushToken[" + new string('a', 181) + "]";
        var overLimit = "ExponentPushToken[" + new string('a', 182) + "]";
        Assert.Equal(200, atLimit.Length);
        Assert.Equal(201, overLimit.Length);

        Assert.Equal(atLimit, AppPushDeviceService.ValidateToken(atLimit));
        Assert.Throws<ArgumentException>(() => AppPushDeviceService.ValidateToken(overLimit));

        // 内层 200 位（正则允许的最大值）整串 219，必须被整串检查拦下。
        var regexMax = "ExponentPushToken[" + new string('a', 200) + "]";
        Assert.Throws<ArgumentException>(() => AppPushDeviceService.ValidateToken(regexMax));
    }

    [Fact]
    public void 令牌校验失败的消息不回显令牌()
    {
        var bad = "ExponentPushToken[bad token with spaces and secret-material]";

        var ex = Assert.Throws<ArgumentException>(() => AppPushDeviceService.ValidateToken(bad));

        Assert.DoesNotContain("secret-material", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ios", "ios")]
    [InlineData("android", "android")]
    [InlineData(" IOS ", "ios")]
    [InlineData("Android", "android")]
    public async Task 注册_平台只认ios与android_大小写与空白归一(string platform, string stored)
    {
        var token = PushTestTokens.Make("p1");

        await CreateService().RegisterAsync(UserA, Register(token, platform));

        Assert.Equal(stored, Assert.Single(Devices(token)).Platform);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("windows")]
    [InlineData("web")]
    public async Task 注册_平台不合法抛ArgumentException(string? platform)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateService().RegisterAsync(UserA, Register(PushTestTokens.Make("p2"), platform!)));
        Assert.Empty(Devices(PushTestTokens.Make("p2")));
    }

    [Fact]
    public async Task 注册_版本字段长度上限50_空白视为未提供_请求体为空抛异常()
    {
        var token = PushTestTokens.Make("v1");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateService().RegisterAsync(UserA, Register(token, appVersion: new string('1', 51))));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateService().RegisterAsync(UserA, Register(token, runtimeVersion: new string('1', 51))));
        await Assert.ThrowsAsync<ArgumentException>(() => CreateService().RegisterAsync(UserA, null));

        await CreateService().RegisterAsync(UserA, Register(token, appVersion: new string('1', 50), runtimeVersion: "   "));
        var row = Assert.Single(Devices(token));
        Assert.Equal(50, row.AppVersion!.Length);
        Assert.Null(row.RuntimeVersion);
    }

    // ---------- upsert ----------

    [Fact]
    public async Task 注册_新令牌建一行_返回pushDeviceId()
    {
        var token = PushTestTokens.Make("n1");

        var result = await CreateService().RegisterAsync(UserA, Register(token, "android", "2.0.0", "2.0.0"));

        var row = Assert.Single(Devices(token));
        Assert.Equal(result.PushDeviceId, row.PushDeviceId);
        Assert.Equal(32, row.PushDeviceId.Length);
        Assert.Equal(UserA, row.UserGUID);
        Assert.Equal("android", row.Platform);
        Assert.Equal("2.0.0", row.AppVersion);
        Assert.Equal(_clock.Now, row.CreatedAtUtc);
        Assert.Equal(_clock.Now, row.LastSeenAtUtc);
        Assert.Null(row.RevokedAtUtc);
        Assert.Null(row.RevokeReason);
    }

    [Fact]
    public async Task 注册_同账号同令牌只刷新活跃时间与版本_不新建行_pushDeviceId不变()
    {
        var token = PushTestTokens.Make("n2");
        var first = await CreateService().RegisterAsync(UserA, Register(token, "ios", "1.0.6", "1.0.6"));
        var createdAt = _clock.Now;
        _clock.Advance(TimeSpan.FromHours(3));

        var second = await CreateService().RegisterAsync(UserA, Register(token, "ios", "1.0.7", null));

        Assert.Equal(first.PushDeviceId, second.PushDeviceId);
        var row = Assert.Single(Devices(token));
        Assert.Equal(createdAt, row.CreatedAtUtc);
        Assert.Equal(_clock.Now, row.LastSeenAtUtc);
        Assert.Equal("1.0.7", row.AppVersion);
        Assert.Null(row.RuntimeVersion);   // 新请求没带就清掉
        Assert.Null(row.RevokedAtUtc);
    }

    [Fact]
    public async Task 注册_同账号用户标识大小写不同仍视为同一账号()
    {
        var token = PushTestTokens.Make("n3");
        var first = await CreateService().RegisterAsync("User-A", Register(token));

        var second = await CreateService().RegisterAsync("USER-A", Register(token));

        Assert.Equal(first.PushDeviceId, second.PushDeviceId);
        Assert.Single(Devices(token));
    }

    // ---------- 换账号转移（手持换人登录） ----------

    [Fact]
    public async Task 注册_令牌被另一账号占用_转给当前账号_旧关联撤销为reassigned_新账号另起一行()
    {
        var token = PushTestTokens.Make("t1");
        var a = await CreateService().RegisterAsync(UserA, Register(token));
        _clock.Advance(TimeSpan.FromMinutes(30));

        var b = await CreateService().RegisterAsync(UserB, Register(token));

        Assert.NotEqual(a.PushDeviceId, b.PushDeviceId);
        var rows = Devices(token);
        Assert.Equal(2, rows.Count);
        var old = rows.Single(row => row.PushDeviceId == a.PushDeviceId);
        Assert.Equal(UserA, old.UserGUID);
        Assert.Equal(_clock.Now, old.RevokedAtUtc);
        Assert.Equal("reassigned", old.RevokeReason);
        var current = rows.Single(row => row.PushDeviceId == b.PushDeviceId);
        Assert.Equal(UserB, current.UserGUID);
        Assert.Null(current.RevokedAtUtc);
        // 活动行永远只有一条。
        Assert.Single(rows, row => row.RevokedAtUtc == null);
    }

    [Fact]
    public async Task 注册_令牌来回换人_历史行保留_活动行始终唯一()
    {
        var token = PushTestTokens.Make("t2");
        var service = CreateService();

        await service.RegisterAsync(UserA, Register(token));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await service.RegisterAsync(UserB, Register(token));
        _clock.Advance(TimeSpan.FromMinutes(1));
        var backToA = await service.RegisterAsync(UserA, Register(token));
        _clock.Advance(TimeSpan.FromMinutes(1));
        var again = await service.RegisterAsync(UserA, Register(token));   // 刷新，不再新建

        var rows = Devices(token);
        Assert.Equal(3, rows.Count);
        Assert.Equal(2, rows.Count(row => row.RevokeReason == "reassigned"));
        var active = Assert.Single(rows, row => row.RevokedAtUtc == null);
        Assert.Equal(UserA, active.UserGUID);
        Assert.Equal(backToA.PushDeviceId, active.PushDeviceId);
        Assert.Equal(backToA.PushDeviceId, again.PushDeviceId);
    }

    [Fact]
    public async Task 注册_退出登录撤销后再次登录_另起一行_历史撤销行保留()
    {
        var token = PushTestTokens.Make("t3");
        var service = CreateService();
        var first = await service.RegisterAsync(UserA, Register(token));
        await service.RevokeAsync(UserA, new PushDeviceRevokeRequestDto { ExpoPushToken = token });
        _clock.Advance(TimeSpan.FromMinutes(5));

        var second = await service.RegisterAsync(UserA, Register(token));

        Assert.NotEqual(first.PushDeviceId, second.PushDeviceId);
        var rows = Devices(token);
        Assert.Equal(2, rows.Count);
        Assert.Equal("logout", rows.Single(row => row.PushDeviceId == first.PushDeviceId).RevokeReason);
        Assert.Single(rows, row => row.RevokedAtUtc == null);
    }

    // ---------- 每个账号最多 10 个活动设备 ----------

    [Fact]
    public async Task 注册_超过10个活动设备_撤销最久未活跃的_原因limit_当前设备一定保留()
    {
        var service = CreateService();
        var tokens = Enumerable.Range(1, 11).Select(i => PushTestTokens.Make($"l{i:D2}")).ToList();
        foreach (var token in tokens)
        {
            await service.RegisterAsync(UserA, Register(token));
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        var active = _database.Db.Queryable<AppPushDevice>().Where(device => device.UserGUID == UserA && device.RevokedAtUtc == null).ToList();
        Assert.Equal(10, active.Count);
        var oldest = Assert.Single(Devices(tokens[0]));
        Assert.Equal("limit", oldest.RevokeReason);
        Assert.NotNull(oldest.RevokedAtUtc);
        Assert.Null(Assert.Single(Devices(tokens[10])).RevokedAtUtc);

        // 刷新第 2 个（活跃时间变最新），再注册第 12 个：被撤销的应是现在最久未活跃的第 3 个，而不是刚刷新的第 2 个。
        await service.RegisterAsync(UserA, Register(tokens[1]));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await service.RegisterAsync(UserA, Register(PushTestTokens.Make("l12")));

        Assert.Null(Assert.Single(Devices(tokens[1])).RevokedAtUtc);
        Assert.Equal("limit", Assert.Single(Devices(tokens[2])).RevokeReason);
        Assert.Equal(10, _database.Db.Queryable<AppPushDevice>().Count(device => device.UserGUID == UserA && device.RevokedAtUtc == null));
    }

    [Fact]
    public async Task 注册_设备上限按账号计_别的账号的设备不受影响()
    {
        var service = CreateService();
        for (var i = 1; i <= 12; i++)
        {
            await service.RegisterAsync(UserA, Register(PushTestTokens.Make($"m{i:D2}")));
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        var bToken = PushTestTokens.Make("mb01");
        await service.RegisterAsync(UserB, Register(bToken));

        Assert.Equal(10, _database.Db.Queryable<AppPushDevice>().Count(device => device.UserGUID == UserA && device.RevokedAtUtc == null));
        Assert.Null(Assert.Single(Devices(bToken)).RevokedAtUtc);
    }

    // ---------- 撤销 ----------

    [Fact]
    public async Task 撤销_只撤销属于当前账号的活动令牌_原因logout_幂等()
    {
        var token = PushTestTokens.Make("r1");
        await CreateService().RegisterAsync(UserA, Register(token));
        _clock.Advance(TimeSpan.FromMinutes(2));

        var first = await CreateService().RevokeAsync(UserA, new PushDeviceRevokeRequestDto { ExpoPushToken = token });
        var second = await CreateService().RevokeAsync(UserA, new PushDeviceRevokeRequestDto { ExpoPushToken = token });

        Assert.Equal(1, first.Revoked);
        Assert.Equal(0, second.Revoked);   // 幂等：再撤销一次仍成功，只是没有可撤销的
        var row = Assert.Single(Devices(token));
        Assert.Equal("logout", row.RevokeReason);
        Assert.Equal(_clock.Now, row.RevokedAtUtc);
    }

    [Fact]
    public async Task 撤销_别人的令牌_返回0且原样不动()
    {
        var token = PushTestTokens.Make("r2");
        await CreateService().RegisterAsync(UserA, Register(token));

        var result = await CreateService().RevokeAsync(UserB, new PushDeviceRevokeRequestDto { ExpoPushToken = token });

        Assert.Equal(0, result.Revoked);
        var row = Assert.Single(Devices(token));
        Assert.Null(row.RevokedAtUtc);
        Assert.Null(row.RevokeReason);
        Assert.Equal(UserA, row.UserGUID);
    }

    [Fact]
    public async Task 撤销_令牌不存在返回0_格式不合法或请求体为空抛异常()
    {
        var service = CreateService();

        Assert.Equal(0, (await service.RevokeAsync(UserA, new PushDeviceRevokeRequestDto { ExpoPushToken = PushTestTokens.Make("zz9") })).Revoked);
        await Assert.ThrowsAsync<ArgumentException>(() => service.RevokeAsync(UserA, new PushDeviceRevokeRequestDto { ExpoPushToken = "bad" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RevokeAsync(UserA, new PushDeviceRevokeRequestDto()));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RevokeAsync(UserA, null));
    }

    [Fact]
    public async Task 撤销_转移后原账号再撤销不会撤到新账号的行()
    {
        var token = PushTestTokens.Make("r3");
        await CreateService().RegisterAsync(UserA, Register(token));
        await CreateService().RegisterAsync(UserB, Register(token));   // 手持换给 B

        // A 退出登录时晚到的撤销请求（没网重试）：令牌现在属于 B，不能把 B 的撤了。
        var late = await CreateService().RevokeAsync(UserA, new PushDeviceRevokeRequestDto { ExpoPushToken = token });

        Assert.Equal(0, late.Revoked);
        var active = Assert.Single(Devices(token), row => row.RevokedAtUtc == null);
        Assert.Equal(UserB, active.UserGUID);
    }

    // ---------- 日志不带令牌明文 ----------

    [Fact]
    public async Task 日志_注册转移撤销全过程都不出现令牌明文_只有长度与哈希前缀()
    {
        var token = PushTestTokens.Make("log1");
        var inner = token["ExponentPushToken[".Length..^1];
        var service = CreateService();

        await service.RegisterAsync(UserA, Register(token));
        await service.RegisterAsync(UserB, Register(token));
        await service.RevokeAsync(UserB, new PushDeviceRevokeRequestDto { ExpoPushToken = token });

        var dump = _logs.Dump();
        Assert.DoesNotContain(token, dump, StringComparison.Ordinal);
        Assert.DoesNotContain(inner, dump, StringComparison.Ordinal);
        Assert.Contains("len=", dump, StringComparison.Ordinal);
        Assert.Contains("sha256=", dump, StringComparison.Ordinal);
    }

    [Fact]
    public void 令牌指纹_只含长度与sha256前8位十六进制()
    {
        var fingerprint = TokenFingerprint.Of("ExponentPushToken[abcdefghijklmnopqrstuv]");

        Assert.Matches(@"^len=41 sha256=[0-9a-f]{8}$", fingerprint);
        Assert.Equal("len=0", TokenFingerprint.Of(null));
        Assert.Equal(fingerprint, TokenFingerprint.Of("ExponentPushToken[abcdefghijklmnopqrstuv]"));
        Assert.NotEqual(fingerprint, TokenFingerprint.Of("ExponentPushToken[abcdefghijklmnopqrstuw]"));
    }

    // ---------- 控制器 ----------

    private static NotificationsController CreateController(IAppPushDeviceService service, ILogger<NotificationsController> logger, string? userGuid = UserA, string? queryString = null)
    {
        var httpContext = new DefaultHttpContext();
        if (userGuid != null)
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("userId", userGuid)], "test"));
        }

        if (queryString != null)
        {
            httpContext.Request.QueryString = new QueryString(queryString);
        }

        return new NotificationsController(
            Mock.Of<INotificationInboxService>(), Mock.Of<INotificationPublisher>(), service, logger)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    [Fact]
    public async Task 控制器_POST_合法请求返回pushDeviceId_非法返回400_无用户标识返回401()
    {
        var service = CreateService();
        var token = PushTestTokens.Make("c1");

        var ok = Assert.IsType<OkObjectResult>(await CreateController(service, _logs.CreateLogger<NotificationsController>())
            .RegisterPushDevice(Register(token), default));
        var data = Assert.IsType<ApiResponse<PushDeviceRegisterResultDto>>(ok.Value).Data!;
        Assert.Equal(Assert.Single(Devices(token)).PushDeviceId, data.PushDeviceId);

        var bad = Assert.IsType<BadRequestObjectResult>(await CreateController(service, _logs.CreateLogger<NotificationsController>())
            .RegisterPushDevice(Register("not-a-token"), default));
        Assert.Equal("BAD_REQUEST", Assert.IsType<ApiResponse<PushDeviceRegisterResultDto>>(bad.Value).ErrorCode);

        Assert.IsType<BadRequestObjectResult>(await CreateController(service, _logs.CreateLogger<NotificationsController>())
            .RegisterPushDevice(null, default));

        Assert.IsType<UnauthorizedObjectResult>(await CreateController(service, _logs.CreateLogger<NotificationsController>(), userGuid: null)
            .RegisterPushDevice(Register(PushTestTokens.Make("c2")), default));
        Assert.Empty(Devices(PushTestTokens.Make("c2")));
    }

    [Fact]
    public void 线上JSON契约_请求与响应字段名是camelCase_与App约定一致()
    {
        // 与 Program.cs 的 AddJsonOptions 一致：camelCase、空值不输出。
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
        };

        var register = System.Text.Json.JsonSerializer.Deserialize<PushDeviceRegisterRequestDto>(
            """{"expoPushToken":"ExponentPushToken[abcdefghijklmnopqrstuv]","platform":"ios","appVersion":"1.0.7","runtimeVersion":"1.0.7"}""", options)!;
        Assert.Equal("ExponentPushToken[abcdefghijklmnopqrstuv]", register.ExpoPushToken);
        Assert.Equal("ios", register.Platform);
        Assert.Equal("1.0.7", register.AppVersion);
        Assert.Equal("1.0.7", register.RuntimeVersion);
        var revoke = System.Text.Json.JsonSerializer.Deserialize<PushDeviceRevokeRequestDto>(
            """{"expoPushToken":"ExponentPushToken[abcdefghijklmnopqrstuv]"}""", options)!;
        Assert.Equal("ExponentPushToken[abcdefghijklmnopqrstuv]", revoke.ExpoPushToken);

        using var ok = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(
            ApiResponse<PushDeviceRegisterResultDto>.OK(new PushDeviceRegisterResultDto { PushDeviceId = "abc" }), options));
        Assert.True(ok.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("abc", ok.RootElement.GetProperty("data").GetProperty("pushDeviceId").GetString());
        using var revoked = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(
            ApiResponse<PushDeviceRevokeResultDto>.OK(new PushDeviceRevokeResultDto { Revoked = 1 }), options));
        Assert.Equal(1, revoked.RootElement.GetProperty("data").GetProperty("revoked").GetInt32());
        using var bad = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(
            ApiResponse<PushDeviceRegisterResultDto>.Error("expoPushToken 格式不正确。", "BAD_REQUEST"), options));
        Assert.False(bad.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("BAD_REQUEST", bad.RootElement.GetProperty("errorCode").GetString());
        Assert.False(bad.RootElement.TryGetProperty("data", out _));   // 空值不输出
    }

    [Fact]
    public async Task 控制器_DELETE_撤销自己的令牌返回200_别人的令牌也返回200但不动()
    {
        var service = CreateService();
        var mine = PushTestTokens.Make("d1");
        var theirs = PushTestTokens.Make("d2");
        await service.RegisterAsync(UserA, Register(mine));
        await service.RegisterAsync(UserB, Register(theirs));
        var controller = CreateController(service, _logs.CreateLogger<NotificationsController>());

        var revoked = Assert.IsType<OkObjectResult>(await controller.RevokePushDevice(new PushDeviceRevokeRequestDto { ExpoPushToken = mine }, default));
        Assert.Equal(1, Assert.IsType<ApiResponse<PushDeviceRevokeResultDto>>(revoked.Value).Data!.Revoked);

        var foreign = Assert.IsType<OkObjectResult>(await controller.RevokePushDevice(new PushDeviceRevokeRequestDto { ExpoPushToken = theirs }, default));
        Assert.Equal(0, Assert.IsType<ApiResponse<PushDeviceRevokeResultDto>>(foreign.Value).Data!.Revoked);
        Assert.Null(Assert.Single(Devices(theirs)).RevokedAtUtc);

        Assert.IsType<BadRequestObjectResult>(await controller.RevokePushDevice(new PushDeviceRevokeRequestDto { ExpoPushToken = "x" }, default));
    }

    [Fact]
    public async Task 控制器_DELETE_不支持query_令牌放在query里等于没传_不会撤销任何东西()
    {
        var token = PushTestTokens.Make("q1");
        await CreateService().RegisterAsync(UserA, Register(token));
        var controller = CreateController(
            CreateService(), _logs.CreateLogger<NotificationsController>(), queryString: $"?expoPushToken={Uri.EscapeDataString(token)}");

        var result = await controller.RevokePushDevice(null, default);   // 没有请求体

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(Assert.Single(Devices(token)).RevokedAtUtc);
    }

    [Fact]
    public void 控制器_DELETE_路由与参数契约_只有FromBody_没有FromQuery或路径参数()
    {
        var method = typeof(NotificationsController).GetMethod(nameof(NotificationsController.RevokePushDevice))!;

        var route = method.GetCustomAttribute<HttpDeleteAttribute>();
        Assert.NotNull(route);
        Assert.Equal("push-devices", route!.Template);
        Assert.DoesNotContain('{', route.Template!);

        var requestParameter = method.GetParameters()[0];
        Assert.NotNull(requestParameter.GetCustomAttribute<FromBodyAttribute>());
        foreach (var parameter in method.GetParameters())
        {
            Assert.Null(parameter.GetCustomAttribute<FromQueryAttribute>());
            Assert.Null(parameter.GetCustomAttribute<FromRouteAttribute>());
        }

        // 登记接口同样只收请求体，且整个控制器要求登录。
        var post = typeof(NotificationsController).GetMethod(nameof(NotificationsController.RegisterPushDevice))!;
        Assert.Equal("push-devices", post.GetCustomAttribute<HttpPostAttribute>()!.Template);
        Assert.NotNull(post.GetParameters()[0].GetCustomAttribute<FromBodyAttribute>());
        Assert.NotNull(typeof(NotificationsController).GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>());
        Assert.Equal("api/react/v1/notifications", typeof(NotificationsController).GetCustomAttribute<RouteAttribute>()!.Template);
    }

    [Fact]
    public async Task 控制器_服务抛未预期异常_返回500_日志只记异常类型_不带异常消息里的令牌()
    {
        var token = PushTestTokens.Make("e1");
        var failing = new Mock<IAppPushDeviceService>();
        failing
            .Setup(service => service.RegisterAsync(It.IsAny<string>(), It.IsAny<PushDeviceRegisterRequestDto?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException($"INSERT failed, parameters: @p0={token}"));
        failing
            .Setup(service => service.RevokeAsync(It.IsAny<string>(), It.IsAny<PushDeviceRevokeRequestDto?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException($"UPDATE failed, parameters: @p0={token}"));
        var controller = CreateController(failing.Object, _logs.CreateLogger<NotificationsController>());

        var register = Assert.IsType<ObjectResult>(await controller.RegisterPushDevice(Register(token), default));
        var revoke = Assert.IsType<ObjectResult>(await controller.RevokePushDevice(new PushDeviceRevokeRequestDto { ExpoPushToken = token }, default));

        Assert.Equal(500, register.StatusCode);
        Assert.Equal(500, revoke.StatusCode);
        var dump = _logs.Dump();
        Assert.DoesNotContain(token, dump, StringComparison.Ordinal);
        Assert.DoesNotContain("parameters", dump, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", dump, StringComparison.Ordinal);
        Assert.All(_logs.Entries, entry => Assert.Null(entry.Exception));
    }

    public void Dispose() => _database.Dispose();
}
