using System.Net;
using System.Reflection;
using System.Text;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Api.Security;
using BlazorApp.Api.Services.Myob;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// MYOB 对接阶段 0：ABN 校验、凭据与 state 加密、MYOB HTTP 客户端、access token 提供器、连接服务（授权回调 / ABN / 验证 / 断开 / 分店映射）与控制器。
/// 用 SQLite 临时库 + 假 MYOB 客户端，不访问真实 MYOB；真实 SQL Server 上的建表见 SchemaMigrationSqlServerIntegrationTests。
/// 注意：MYOB 的真实接口行为（Company 的 ABN 字段名、cf_uri 路径等）需要用真实授权在第 0 阶段联调时再确认。
/// </summary>
public sealed class MyobConnectionTests : IDisposable
{
    // 澳大利亚税务局公开的示例 ABN（51 824 753 556），通过 ABN 校验算法。
    private const string ValidAbn = "51824753556";

    private static readonly DateTime Now = new(2026, 10, 27, 1, 0, 0, DateTimeKind.Utc);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hb-myob-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;
    private readonly MyobSecretProtector _protector;
    private readonly FakeMyobApiClient _client = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly MyobTokenProvider _tokens;
    private readonly MyobConnectionService _service;

    public MyobConnectionTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_dbPath}",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables<MyobConnection, MyobStoreMapping, Store, User>();
        _protector = MyobDataProtection.CreateProtector(new EphemeralDataProtectionProvider());
        _tokens = new MyobTokenProvider(_db, _client, _protector, _cache, _time);
        _service = CreateService(Configured());
    }

    public void Dispose()
    {
        _cache.Dispose();
        _db.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private static MyobOptions Configured() => new()
    {
        ClientId = "client-key",
        ClientSecret = "client-secret",
        RedirectUri = "https://hotbargain.vip/api/myob/oauth/callback",
        MinRequestIntervalMs = 0,
    };

    private MyobConnectionService CreateService(MyobOptions options) =>
        new(_db, _client, _tokens, _protector, Options.Create(options), NullLogger<MyobConnectionService>.Instance, _time);

    private string NewState(string userGuid = "admin-1") =>
        ExtractState(_service.BuildAuthorizeUrlAsync(userGuid, CancellationToken.None).Result.Data!.Url);

    private static string ExtractState(string url)
    {
        var query = new Uri(url).Query.TrimStart('?').Split('&');
        var pair = query.Single(item => item.StartsWith("state=", StringComparison.Ordinal));
        return Uri.UnescapeDataString(pair["state=".Length..]);
    }

    private async Task<MyobConnection> SeedConnectionAsync(
        string guid = "conn-1",
        string businessId = "biz-1",
        string status = MyobConnectionStatuses.Active,
        string? abn = ValidAbn,
        string? refreshToken = "refresh-old")
    {
        var connection = new MyobConnection
        {
            ConnectionGuid = guid,
            BusinessId = businessId,
            BusinessName = "Hot Bargain Pty Ltd",
            Abn = abn,
            RefreshTokenProtected = refreshToken is null ? null : _protector.ProtectRefreshToken(refreshToken),
            Status = status,
            AuthorizedByUserGuid = "admin-1",
            AuthorizedAt = Now,
            CreatedAt = Now,
        };
        await _db.Insertable(connection).ExecuteCommandAsync();
        return connection;
    }

    private async Task SeedStoreAsync(string code, string? abn = null, bool active = true, bool deleted = false) =>
        await _db.Insertable(new Store
        {
            StoreGUID = Guid.NewGuid().ToString(),
            StoreCode = code,
            StoreName = $"Store {code}",
            ABN = abn,
            IsActive = active,
            IsDeleted = deleted,
        }).ExecuteCommandAsync();

    // ───────────────────────── ABN ─────────────────────────

    [Theory]
    [InlineData("51 824 753 556", ValidAbn)]
    [InlineData("ABN: 51-824-753-556", ValidAbn)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    [InlineData("abc", null)]
    public void Abn_Normalize_只保留数字(string? input, string? expected) =>
        Assert.Equal(expected, MyobAbn.Normalize(input));

    [Fact]
    public void Abn_IsValid_要求11位且通过校验算法()
    {
        Assert.True(MyobAbn.IsValid(ValidAbn));
        Assert.False(MyobAbn.IsValid("51824753557")); // 校验位不对
        Assert.False(MyobAbn.IsValid("5182475355")); // 10 位
        Assert.False(MyobAbn.IsValid("123456789")); // TFN 长度
        Assert.False(MyobAbn.IsValid(null));
        Assert.Equal(ValidAbn, MyobConnectionService.ExtractAbn("51 824 753 556"));
        Assert.Null(MyobConnectionService.ExtractAbn("123 456 782")); // TFN 不当作 ABN
    }

    // ───────────────────────── 加密 ─────────────────────────

    [Fact]
    public void Protector_refreshToken往返_密文不含明文()
    {
        var protectedToken = _protector.ProtectRefreshToken("super-secret-refresh");
        Assert.DoesNotContain("super-secret-refresh", protectedToken, StringComparison.Ordinal);
        Assert.Equal("super-secret-refresh", _protector.UnprotectRefreshToken(protectedToken));
    }

    [Fact]
    public void Protector_state过期_被篡改_或来自别的用途都视为无效()
    {
        var valid = _protector.ProtectState("payload", DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.True(_protector.TryUnprotectState(valid, out var value));
        Assert.Equal("payload", value);

        var expired = _protector.ProtectState("payload", DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.False(_protector.TryUnprotectState(expired, out _));

        Assert.False(_protector.TryUnprotectState(valid + "x", out _));
        Assert.False(_protector.TryUnprotectState("not-a-token", out _));
        // refresh token 的密文不能拿来当 state 用（purpose 隔离）。
        Assert.False(_protector.TryUnprotectState(_protector.ProtectRefreshToken("x"), out _));
    }

    // ───────────────────────── MYOB HTTP 客户端 ─────────────────────────

    private static MyobApiClient ClientFor(StubHandler handler) =>
        new(new HttpClient(handler), Options.Create(Configured()));

    [Fact]
    public async Task ApiClient_授权码换token_请求字段齐全_expires_in可为字符串()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            """{"access_token":"at-1","token_type":"bearer","expires_in":"1200","refresh_token":"rt-1","scope":"x","user":{"uid":"u","username":"admin@example.com"}}"""));
        var tokens = await ClientFor(handler).ExchangeCodeAsync("the-code", CancellationToken.None);

        Assert.Equal("at-1", tokens.AccessToken);
        Assert.Equal("rt-1", tokens.RefreshToken);
        Assert.Equal(1200, tokens.ExpiresInSeconds);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://secure.myob.com/oauth2/v1/authorize", request.Url);
        var form = ParseForm(request.Body);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("the-code", form["code"]);
        Assert.Equal("client-key", form["client_id"]);
        Assert.Equal("client-secret", form["client_secret"]);
        Assert.Equal("https://hotbargain.vip/api/myob/oauth/callback", form["redirect_uri"]);
        Assert.Contains("sme-company-file", form["scope"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiClient_刷新token_响应没有refresh_token时返回null让调用方沿用旧值()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"access_token":"at-2","expires_in":1200}"""));
        var tokens = await ClientFor(handler).RefreshAsync("rt-old", CancellationToken.None);

        Assert.Equal("at-2", tokens.AccessToken);
        Assert.Null(tokens.RefreshToken);
        var form = ParseForm(handler.Requests[0].Body);
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("rt-old", form["refresh_token"]);
    }

    [Fact]
    public async Task ApiClient_invalid_grant才算授权失效_invalid_client是配置问题()
    {
        var revoked = await Assert.ThrowsAsync<MyobApiException>(() =>
            ClientFor(new StubHandler(_ => Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""")))
                .RefreshAsync("rt", CancellationToken.None));
        Assert.True(revoked.IsAuthorizationRevoked);
        Assert.Equal(MyobErrorCodes.NeedsReauthorization, revoked.Code);

        var badClient = await Assert.ThrowsAsync<MyobApiException>(() =>
            ClientFor(new StubHandler(_ => Json(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}""")))
                .RefreshAsync("rt", CancellationToken.None));
        Assert.False(badClient.IsAuthorizationRevoked);
        Assert.Equal(MyobErrorCodes.TokenExchangeFailed, badClient.Code);

        // 网关错误页等非 JSON 响应：按状态码处理，错误消息不回显原文。
        var html = await Assert.ThrowsAsync<MyobApiException>(() =>
            ClientFor(new StubHandler(_ => Json(HttpStatusCode.BadGateway, "<html>secret-token-here</html>")))
                .ExchangeCodeAsync("code", CancellationToken.None));
        Assert.DoesNotContain("secret-token-here", html.Message, StringComparison.Ordinal);
        Assert.False(html.IsAuthorizationRevoked);
    }

    [Fact]
    public async Task ApiClient_公司信息_带全部必需请求头_读取ABNOrTFN()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            """{"CompanyName":" Hot Bargain Pty Ltd ","ABNOrTFN":"51 824 753 556","SerialNumber":61000000}"""));
        var company = await ClientFor(handler).GetCompanyAsync("biz/1", "access-token", CancellationToken.None);

        Assert.Equal("Hot Bargain Pty Ltd", company.CompanyName);
        Assert.Equal("51 824 753 556", company.AbnOrTfn);
        var request = Assert.Single(handler.Requests);
        // businessId 要做 URL 转义，不能拼出别的路径。
        Assert.Equal("https://api.myob.com/accountright/biz%2F1/Company", request.Url);
        Assert.Equal("Bearer access-token", request.Headers["Authorization"]);
        Assert.Equal("client-key", request.Headers["x-myobapi-key"]);
        Assert.Equal("v2", request.Headers["x-myobapi-version"]);
    }

    [Fact]
    public async Task ApiClient_公司信息_非200_缺公司名_网络失败都转成ApiCallFailed()
    {
        var notFound = await Assert.ThrowsAsync<MyobApiException>(() =>
            ClientFor(new StubHandler(_ => Json(HttpStatusCode.Forbidden, "{}"))).GetCompanyAsync("b", "t", CancellationToken.None));
        Assert.Equal(MyobErrorCodes.ApiCallFailed, notFound.Code);
        Assert.Equal(HttpStatusCode.Forbidden, notFound.StatusCode);

        var noName = await Assert.ThrowsAsync<MyobApiException>(() =>
            ClientFor(new StubHandler(_ => Json(HttpStatusCode.OK, """{"ABNOrTFN":"1"}"""))).GetCompanyAsync("b", "t", CancellationToken.None));
        Assert.Equal(MyobErrorCodes.ApiCallFailed, noName.Code);

        var network = await Assert.ThrowsAsync<MyobApiException>(() =>
            ClientFor(new StubHandler(_ => throw new HttpRequestException("boom"))).GetCompanyAsync("b", "t", CancellationToken.None));
        Assert.Equal(MyobErrorCodes.ApiCallFailed, network.Code);
    }

    // ───────────────────────── access token 提供器 ─────────────────────────

    [Fact]
    public async Task TokenProvider_第二次命中缓存_轮换的refresh_token加密落库()
    {
        await SeedConnectionAsync();
        _client.NextRefresh = new MyobTokenResponse("at-1", "refresh-new", 1200);

        Assert.Equal("at-1", await _tokens.GetAccessTokenAsync("conn-1", CancellationToken.None));
        Assert.Equal("at-1", await _tokens.GetAccessTokenAsync("conn-1", CancellationToken.None));

        Assert.Equal(1, _client.RefreshCalls);
        Assert.Equal("refresh-old", _client.LastRefreshToken);
        var row = await _db.Queryable<MyobConnection>().FirstAsync(item => item.ConnectionGuid == "conn-1");
        Assert.Equal("refresh-new", _protector.UnprotectRefreshToken(row.RefreshTokenProtected!));
        Assert.DoesNotContain("refresh-new", row.RefreshTokenProtected!, StringComparison.Ordinal);
        Assert.Equal(Now, row.LastRefreshedAt);
        Assert.Null(row.LastError);
    }

    [Fact]
    public async Task TokenProvider_响应没有新refresh_token时保留旧的()
    {
        await SeedConnectionAsync();
        _client.NextRefresh = new MyobTokenResponse("at-1", null, 1200);

        await _tokens.GetAccessTokenAsync("conn-1", CancellationToken.None);

        var row = await _db.Queryable<MyobConnection>().FirstAsync(item => item.ConnectionGuid == "conn-1");
        Assert.Equal("refresh-old", _protector.UnprotectRefreshToken(row.RefreshTokenProtected!));
    }

    [Fact]
    public async Task TokenProvider_invalid_grant_连接标成需重新授权_此后不再请求MYOB()
    {
        await SeedConnectionAsync();
        _client.RefreshError = new MyobApiException(MyobErrorCodes.NeedsReauthorization, "revoked", HttpStatusCode.BadRequest, true);

        var first = await Assert.ThrowsAsync<MyobApiException>(() => _tokens.GetAccessTokenAsync("conn-1", CancellationToken.None));
        Assert.Equal(MyobErrorCodes.NeedsReauthorization, first.Code);
        var row = await _db.Queryable<MyobConnection>().FirstAsync(item => item.ConnectionGuid == "conn-1");
        Assert.Equal(MyobConnectionStatuses.NeedsReauth, row.Status);
        Assert.False(string.IsNullOrWhiteSpace(row.LastError));

        var second = await Assert.ThrowsAsync<MyobApiException>(() => _tokens.GetAccessTokenAsync("conn-1", CancellationToken.None));
        Assert.Equal(MyobErrorCodes.NeedsReauthorization, second.Code);
        Assert.Equal(1, _client.RefreshCalls);
    }

    [Fact]
    public async Task TokenProvider_临时故障只记录原因_状态保持Active()
    {
        await SeedConnectionAsync();
        _client.RefreshError = new MyobApiException(MyobErrorCodes.ApiCallFailed, "MYOB 暂时不可用", HttpStatusCode.ServiceUnavailable);

        await Assert.ThrowsAsync<MyobApiException>(() => _tokens.GetAccessTokenAsync("conn-1", CancellationToken.None));

        var row = await _db.Queryable<MyobConnection>().FirstAsync(item => item.ConnectionGuid == "conn-1");
        Assert.Equal(MyobConnectionStatuses.Active, row.Status);
        Assert.Equal("MYOB 暂时不可用", row.LastError);
    }

    [Fact]
    public async Task TokenProvider_密文无法解密_已断开_不存在_分别报对应错误()
    {
        // 密文损坏（例如密钥环丢失）：只能重新授权。
        await SeedConnectionAsync("conn-bad", "biz-bad");
        await _db.Updateable<MyobConnection>()
            .SetColumns(item => item.RefreshTokenProtected == "garbage")
            .Where(item => item.ConnectionGuid == "conn-bad")
            .ExecuteCommandAsync();
        var bad = await Assert.ThrowsAsync<MyobApiException>(() => _tokens.GetAccessTokenAsync("conn-bad", CancellationToken.None));
        Assert.Equal(MyobErrorCodes.NeedsReauthorization, bad.Code);
        Assert.Equal(0, _client.RefreshCalls);

        await SeedConnectionAsync("conn-off", "biz-off", MyobConnectionStatuses.Disconnected, refreshToken: null);
        var off = await Assert.ThrowsAsync<MyobApiException>(() => _tokens.GetAccessTokenAsync("conn-off", CancellationToken.None));
        Assert.Equal(MyobErrorCodes.ConnectionNotActive, off.Code);

        var none = await Assert.ThrowsAsync<MyobApiException>(() => _tokens.GetAccessTokenAsync("nope", CancellationToken.None));
        Assert.Equal(MyobErrorCodes.ConnectionNotFound, none.Code);
    }

    [Fact]
    public async Task TokenProvider_并发请求只刷新一次()
    {
        await SeedConnectionAsync();
        _client.NextRefresh = new MyobTokenResponse("at-1", "refresh-new", 1200);
        _client.RefreshDelay = TimeSpan.FromMilliseconds(100);

        var results = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => _tokens.GetAccessTokenAsync("conn-1", CancellationToken.None)));

        Assert.All(results, token => Assert.Equal("at-1", token));
        Assert.Equal(1, _client.RefreshCalls);
    }

    // ───────────────────────── 授权入口 ─────────────────────────

    [Fact]
    public async Task 授权入口_未配置应用时报NotConfigured()
    {
        var service = CreateService(new MyobOptions());
        var result = await service.BuildAuthorizeUrlAsync("admin-1", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(MyobErrorCodes.NotConfigured, result.ErrorCode);
    }

    [Fact]
    public async Task 授权入口_地址包含全部授权参数_且不泄露secret()
    {
        var result = await _service.BuildAuthorizeUrlAsync("admin-1", CancellationToken.None);

        Assert.True(result.Success);
        var url = result.Data!.Url;
        Assert.StartsWith("https://secure.myob.com/oauth2/account/authorize?", url, StringComparison.Ordinal);
        Assert.Contains("client_id=client-key", url, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString("https://hotbargain.vip/api/myob/oauth/callback"), url, StringComparison.Ordinal);
        Assert.Contains("response_type=code", url, StringComparison.Ordinal);
        Assert.Contains("scope=sme-company-file%20sme-contacts-employee%20sme-payroll", url, StringComparison.Ordinal);
        Assert.Contains("prompt=consent", url, StringComparison.Ordinal);
        Assert.DoesNotContain("client-secret", url, StringComparison.Ordinal);
    }

    // ───────────────────────── 授权回调 ─────────────────────────

    [Fact]
    public async Task 回调_state无效一律拒绝_且不调用MYOB()
    {
        foreach (var state in new string?[] { null, "", "garbage" })
        {
            var result = await _service.HandleCallbackAsync("code", state, "biz-1", null, CancellationToken.None);
            Assert.False(result.Success);
            Assert.Equal(MyobErrorCodes.InvalidState, result.ErrorCode);
        }

        // 把 state 解密后改写发起人再加密回去不可能：别的 purpose 的密文同样无效。
        var forged = _protector.ProtectRefreshToken("{\"U\":\"admin-1\",\"N\":\"x\"}");
        var forgedResult = await _service.HandleCallbackAsync("code", forged, "biz-1", null, CancellationToken.None);
        Assert.Equal(MyobErrorCodes.InvalidState, forgedResult.ErrorCode);
        Assert.Equal(0, _client.ExchangeCalls);
    }

    [Fact]
    public async Task 回调_用户拒绝授权_或缺少code_businessId_都不落库()
    {
        var denied = await _service.HandleCallbackAsync(null, NewState(), null, "access_denied", CancellationToken.None);
        Assert.Equal(MyobErrorCodes.AuthorizationDenied, denied.ErrorCode);

        var noCode = await _service.HandleCallbackAsync(null, NewState(), "biz-1", null, CancellationToken.None);
        Assert.Equal(MyobErrorCodes.TokenExchangeFailed, noCode.ErrorCode);

        var noBusiness = await _service.HandleCallbackAsync("code", NewState(), "  ", null, CancellationToken.None);
        Assert.Equal(MyobErrorCodes.BusinessIdMissing, noBusiness.ErrorCode);

        Assert.Equal(0, _client.ExchangeCalls);
        Assert.Equal(0, await _db.Queryable<MyobConnection>().CountAsync());
    }

    [Fact]
    public async Task 回调_成功_建立连接_refresh_token加密_ABN与公司名取自MYOB()
    {
        _client.NextExchange = new MyobTokenResponse("at-1", "rt-1", 1200);
        _client.NextCompany = new MyobCompanyInfo("Hot Bargain Pty Ltd", "51 824 753 556");

        var result = await _service.HandleCallbackAsync("the-code", NewState("admin-9"), "biz-1", null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("the-code", _client.LastCode);
        Assert.Equal("at-1", _client.LastCompanyAccessToken);
        var row = await _db.Queryable<MyobConnection>().FirstAsync(item => item.ConnectionGuid == result.ConnectionGuid);
        Assert.Equal("biz-1", row.BusinessId);
        Assert.Equal("Hot Bargain Pty Ltd", row.BusinessName);
        Assert.Equal(ValidAbn, row.Abn);
        Assert.Equal(MyobConnectionStatuses.Active, row.Status);
        Assert.Equal("admin-9", row.AuthorizedByUserGuid);
        Assert.Equal("rt-1", _protector.UnprotectRefreshToken(row.RefreshTokenProtected!));
        Assert.DoesNotContain("rt-1", row.RefreshTokenProtected!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 回调_TFN不当作ABN_读不到ABN时保持为空()
    {
        _client.NextExchange = new MyobTokenResponse("at-1", "rt-1", 1200);
        _client.NextCompany = new MyobCompanyInfo("No ABN Pty", "123 456 782");

        var result = await _service.HandleCallbackAsync("code", NewState(), "biz-1", null, CancellationToken.None);

        Assert.True(result.Success);
        var row = await _db.Queryable<MyobConnection>().FirstAsync(item => item.ConnectionGuid == result.ConnectionGuid);
        Assert.Null(row.Abn);
    }

    [Fact]
    public async Task 回调_同一公司文件重新授权_更新原连接并保留分店映射与手填ABN()
    {
        await SeedConnectionAsync(status: MyobConnectionStatuses.NeedsReauth, abn: ValidAbn);
        await SeedStoreAsync("1001");
        await _db.Insertable(new MyobStoreMapping { StoreCode = "1001", ConnectionGuid = "conn-1", MappedAt = Now }).ExecuteCommandAsync();
        _client.NextExchange = new MyobTokenResponse("at-2", "rt-2", 1200);
        _client.NextCompany = new MyobCompanyInfo("Renamed Pty Ltd", null);

        var result = await _service.HandleCallbackAsync("code", NewState("admin-2"), "biz-1", null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("conn-1", result.ConnectionGuid);
        Assert.Equal(1, await _db.Queryable<MyobConnection>().CountAsync());
        var row = await _db.Queryable<MyobConnection>().FirstAsync(item => item.ConnectionGuid == "conn-1");
        Assert.Equal(MyobConnectionStatuses.Active, row.Status);
        Assert.Equal("Renamed Pty Ltd", row.BusinessName);
        Assert.Equal(ValidAbn, row.Abn); // MYOB 这次没返回 ABN，保留原值
        Assert.Equal("admin-2", row.AuthorizedByUserGuid);
        Assert.Null(row.LastError);
        Assert.Equal(1, await _db.Queryable<MyobStoreMapping>().CountAsync());
    }

    [Fact]
    public async Task 回调_公司信息读取失败_不留下半成品连接()
    {
        _client.NextExchange = new MyobTokenResponse("at-1", "rt-1", 1200);
        _client.CompanyError = new MyobApiException(MyobErrorCodes.ApiCallFailed, "403", HttpStatusCode.Forbidden);

        var result = await _service.HandleCallbackAsync("code", NewState(), "biz-1", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(MyobErrorCodes.ApiCallFailed, result.ErrorCode);
        Assert.Equal(0, await _db.Queryable<MyobConnection>().CountAsync());
    }

    [Fact]
    public async Task 回调_令牌里没有refresh_token_视为授权失败()
    {
        _client.NextExchange = new MyobTokenResponse("at-1", null, 1200);

        var result = await _service.HandleCallbackAsync("code", NewState(), "biz-1", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(MyobErrorCodes.TokenExchangeFailed, result.ErrorCode);
        Assert.Equal(0, await _db.Queryable<MyobConnection>().CountAsync());
    }

    [Fact]
    public async Task 回调_令牌换取失败_带回MYOB错误码()
    {
        _client.ExchangeError = new MyobApiException(MyobErrorCodes.TokenExchangeFailed, "invalid_client", HttpStatusCode.Unauthorized);

        var result = await _service.HandleCallbackAsync("code", NewState(), "biz-1", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(MyobErrorCodes.TokenExchangeFailed, result.ErrorCode);
    }

    // ───────────────────────── ABN / 验证 / 断开 ─────────────────────────

    [Fact]
    public async Task 更新ABN_规范化保存_非法拒绝_空值清除_连接不存在报404码()
    {
        await SeedConnectionAsync(abn: null);

        var saved = await _service.UpdateAbnAsync("conn-1", "51 824 753 556", CancellationToken.None);
        Assert.True(saved.Success);
        Assert.Equal(ValidAbn, saved.Data!.Abn);

        var invalid = await _service.UpdateAbnAsync("conn-1", "51824753557", CancellationToken.None);
        Assert.False(invalid.Success);
        Assert.Equal(MyobErrorCodes.InvalidAbn, invalid.ErrorCode);
        Assert.Equal(ValidAbn, (await _db.Queryable<MyobConnection>().FirstAsync(item => item.ConnectionGuid == "conn-1")).Abn);

        var cleared = await _service.UpdateAbnAsync("conn-1", "  ", CancellationToken.None);
        Assert.True(cleared.Success);
        Assert.Null(cleared.Data!.Abn);

        var missing = await _service.UpdateAbnAsync("nope", ValidAbn, CancellationToken.None);
        Assert.Equal(MyobErrorCodes.ConnectionNotFound, missing.ErrorCode);
    }

    [Fact]
    public async Task 验证_成功时刷新公司名与ABN并清除错误()
    {
        await SeedConnectionAsync(abn: null);
        await _db.Updateable<MyobConnection>().SetColumns(item => item.LastError == "旧错误").Where(item => item.ConnectionGuid == "conn-1").ExecuteCommandAsync();
        _client.NextRefresh = new MyobTokenResponse("at-1", null, 1200);
        _client.NextCompany = new MyobCompanyInfo("New Name Pty", ValidAbn);

        var result = await _service.VerifyAsync("conn-1", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("New Name Pty", result.Data!.BusinessName);
        Assert.Equal(ValidAbn, result.Data.Abn);
        Assert.Null(result.Data.LastError);
        Assert.Equal("biz-1", _client.LastCompanyBusinessId);
    }

    [Fact]
    public async Task 验证_授权已失效时返回带最新状态的失败结果()
    {
        await SeedConnectionAsync();
        _client.RefreshError = new MyobApiException(MyobErrorCodes.NeedsReauthorization, "revoked", HttpStatusCode.BadRequest, true);

        var result = await _service.VerifyAsync("conn-1", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(MyobErrorCodes.NeedsReauthorization, result.ErrorCode);
        Assert.Equal(MyobConnectionStatuses.NeedsReauth, result.Data!.Status);
    }

    [Fact]
    public async Task 验证_MYOB临时故障时记录错误_状态不变()
    {
        await SeedConnectionAsync();
        _client.NextRefresh = new MyobTokenResponse("at-1", null, 1200);
        _client.CompanyError = new MyobApiException(MyobErrorCodes.ApiCallFailed, "MYOB 公司信息接口返回 503", HttpStatusCode.ServiceUnavailable);

        var result = await _service.VerifyAsync("conn-1", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(MyobErrorCodes.ApiCallFailed, result.ErrorCode);
        Assert.Equal(MyobConnectionStatuses.Active, result.Data!.Status);
        Assert.Equal("MYOB 公司信息接口返回 503", result.Data.LastError);
    }

    [Fact]
    public async Task 断开_清除token_移除该连接的分店映射_其他连接的映射不受影响()
    {
        await SeedConnectionAsync("conn-1", "biz-1");
        await SeedConnectionAsync("conn-2", "biz-2");
        await SeedStoreAsync("1001");
        await SeedStoreAsync("1002");
        await _db.Insertable(new List<MyobStoreMapping>
        {
            new() { StoreCode = "1001", ConnectionGuid = "conn-1", MappedAt = Now },
            new() { StoreCode = "1002", ConnectionGuid = "conn-2", MappedAt = Now },
        }).ExecuteCommandAsync();
        _client.NextRefresh = new MyobTokenResponse("at-1", null, 1200);
        await _tokens.GetAccessTokenAsync("conn-1", CancellationToken.None); // 预热缓存

        var result = await _service.DisconnectAsync("conn-1", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(MyobConnectionStatuses.Disconnected, result.Data!.Status);
        Assert.Empty(result.Data.StoreCodes);
        var row = await _db.Queryable<MyobConnection>().FirstAsync(item => item.ConnectionGuid == "conn-1");
        Assert.Null(row.RefreshTokenProtected);
        var remaining = await _db.Queryable<MyobStoreMapping>().ToListAsync();
        Assert.Equal("1002", Assert.Single(remaining).StoreCode);
        // 缓存的 access token 已作废：再取会报连接未启用，而不是返回旧 token。
        var after = await Assert.ThrowsAsync<MyobApiException>(() => _tokens.GetAccessTokenAsync("conn-1", CancellationToken.None));
        Assert.Equal(MyobErrorCodes.ConnectionNotActive, after.Code);
    }

    // ───────────────────────── 连接列表 ─────────────────────────

    [Fact]
    public async Task 连接列表_Active在前_带授权人姓名与分店编号()
    {
        await _db.Insertable(new User { UserGUID = "admin-1", Username = "admin", FullName = "Admin Person", Email = "a@example.com" }).ExecuteCommandAsync();
        await SeedConnectionAsync("conn-z", "biz-z");
        await SeedConnectionAsync("conn-off", "biz-off", MyobConnectionStatuses.Disconnected, refreshToken: null);
        await SeedConnectionAsync("conn-re", "biz-re", MyobConnectionStatuses.NeedsReauth);
        await _db.Insertable(new List<MyobStoreMapping>
        {
            new() { StoreCode = "1002", ConnectionGuid = "conn-z", MappedAt = Now },
            new() { StoreCode = "1001", ConnectionGuid = "conn-z", MappedAt = Now },
        }).ExecuteCommandAsync();

        var result = await _service.ListConnectionsAsync(CancellationToken.None);

        Assert.Equal(
            ["conn-z", "conn-re", "conn-off"],
            result.Data!.Select(item => item.ConnectionGuid).ToArray());
        var active = result.Data[0];
        Assert.Equal("Admin Person", active.AuthorizedByName);
        Assert.Equal(["1001", "1002"], active.StoreCodes);
        // 列表接口永远不返回 token 相关字段（DTO 上没有这类属性）。
        Assert.DoesNotContain(typeof(MyobConnectionDto).GetProperties(), p => p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }

    // ───────────────────────── 分店映射 ─────────────────────────

    [Fact]
    public async Task 分店映射_只有恰好一个Active连接ABN一致才给出建议()
    {
        await SeedConnectionAsync("conn-a", "biz-a", abn: ValidAbn);
        await SeedConnectionAsync("conn-b", "biz-b", abn: "53004085616"); // 另一个有效 ABN
        await SeedConnectionAsync("conn-dup1", "biz-d1", abn: "12345678901");
        await SeedConnectionAsync("conn-dup2", "biz-d2", abn: "12345678901");
        await SeedStoreAsync("1001", "ABN 51 824 753 556");
        await SeedStoreAsync("1002", "53 004 085 616");
        await SeedStoreAsync("1003", "12345678901"); // 两个连接共用 ABN：不替人决定
        await SeedStoreAsync("1004", null);
        await SeedStoreAsync("1005", "99999999999"); // 没有连接匹配

        var result = await _service.GetStoreMappingsAsync(CancellationToken.None);
        var rows = result.Data!.Stores.ToDictionary(item => item.StoreCode);

        Assert.Equal("conn-a", rows["1001"].SuggestedConnectionGuid);
        Assert.Equal(ValidAbn, rows["1001"].StoreAbn);
        Assert.Equal("conn-b", rows["1002"].SuggestedConnectionGuid);
        Assert.Null(rows["1003"].SuggestedConnectionGuid);
        Assert.Null(rows["1004"].SuggestedConnectionGuid);
        Assert.Null(rows["1005"].SuggestedConnectionGuid);
        Assert.Equal(4, result.Data.Connections.Count);
    }

    [Fact]
    public async Task 分店映射_已映射的不再建议_Disconnected与NeedsReauth的连接不作建议也不进候选()
    {
        await SeedConnectionAsync("conn-a", "biz-a", abn: ValidAbn);
        await SeedConnectionAsync("conn-off", "biz-off", MyobConnectionStatuses.Disconnected, abn: "53004085616", refreshToken: null);
        await SeedConnectionAsync("conn-re", "biz-re", MyobConnectionStatuses.NeedsReauth, abn: "12345678901");
        await SeedStoreAsync("1001", ValidAbn);
        await SeedStoreAsync("1002", "53004085616");
        await SeedStoreAsync("1003", "12345678901");
        await _db.Insertable(new MyobStoreMapping { StoreCode = "1001", ConnectionGuid = "conn-a", MappedAt = Now }).ExecuteCommandAsync();

        var result = await _service.GetStoreMappingsAsync(CancellationToken.None);
        var rows = result.Data!.Stores.ToDictionary(item => item.StoreCode);

        Assert.Equal("conn-a", rows["1001"].ConnectionGuid);
        Assert.Null(rows["1001"].SuggestedConnectionGuid);
        Assert.Null(rows["1002"].SuggestedConnectionGuid);
        Assert.Null(rows["1003"].SuggestedConnectionGuid);
        Assert.Equal(["conn-a"], result.Data.Connections.Select(item => item.ConnectionGuid).ToArray());
    }

    [Fact]
    public async Task 分店映射_已删除分店不出现_未启用分店仍列出()
    {
        await SeedStoreAsync("1001");
        await SeedStoreAsync("1002", active: false);
        await SeedStoreAsync("1003", deleted: true);

        var result = await _service.GetStoreMappingsAsync(CancellationToken.None);

        Assert.Equal(["1001", "1002"], result.Data!.Stores.Select(item => item.StoreCode).ToArray());
        Assert.False(result.Data.Stores[1].IsActive);
    }

    [Fact]
    public async Task 更新映射_新增_改绑_移除_并记录操作人()
    {
        await SeedConnectionAsync("conn-a", "biz-a");
        await SeedConnectionAsync("conn-b", "biz-b");
        await SeedStoreAsync("1001");
        await SeedStoreAsync("1002");
        await SeedStoreAsync("1003");
        await _db.Insertable(new List<MyobStoreMapping>
        {
            new() { StoreCode = "1002", ConnectionGuid = "conn-a", MappedAt = Now.AddDays(-1) },
            new() { StoreCode = "1003", ConnectionGuid = "conn-a", MappedAt = Now.AddDays(-1) },
        }).ExecuteCommandAsync();

        var result = await _service.UpdateStoreMappingsAsync(
            new MyobUpdateStoreMappingsRequest
            {
                Items =
                [
                    new() { StoreCode = "1001", ConnectionGuid = "conn-a" }, // 新增
                    new() { StoreCode = "1002", ConnectionGuid = "conn-b" }, // 改绑
                    new() { StoreCode = "1003", ConnectionGuid = null }, // 移除
                ],
            },
            "admin-7",
            CancellationToken.None);

        Assert.True(result.Success);
        var rows = result.Data!.Stores.ToDictionary(item => item.StoreCode);
        Assert.Equal("conn-a", rows["1001"].ConnectionGuid);
        Assert.Equal("conn-b", rows["1002"].ConnectionGuid);
        Assert.Null(rows["1003"].ConnectionGuid);
        var changed = await _db.Queryable<MyobStoreMapping>().FirstAsync(item => item.StoreCode == "1002");
        Assert.Equal("admin-7", changed.MappedBy);
        Assert.Equal(Now, changed.MappedAt);
    }

    [Fact]
    public async Task 更新映射_未提交的分店保持不变()
    {
        await SeedConnectionAsync("conn-a", "biz-a");
        await SeedStoreAsync("1001");
        await SeedStoreAsync("1002");
        await _db.Insertable(new MyobStoreMapping { StoreCode = "1002", ConnectionGuid = "conn-a", MappedAt = Now }).ExecuteCommandAsync();

        await _service.UpdateStoreMappingsAsync(
            new MyobUpdateStoreMappingsRequest { Items = [new() { StoreCode = "1001", ConnectionGuid = "conn-a" }] },
            "admin-1",
            CancellationToken.None);

        Assert.Equal(2, await _db.Queryable<MyobStoreMapping>().CountAsync());
    }

    [Fact]
    public async Task 更新映射_整批校验失败时一行都不写()
    {
        await SeedConnectionAsync("conn-a", "biz-a");
        await SeedConnectionAsync("conn-re", "biz-re", MyobConnectionStatuses.NeedsReauth);
        await SeedStoreAsync("1001");

        async Task<ApiResponse<MyobStoreMappingsDto>> Update(params MyobStoreMappingChange[] items) =>
            await _service.UpdateStoreMappingsAsync(
                new MyobUpdateStoreMappingsRequest { Items = [.. items] }, "admin-1", CancellationToken.None);

        var unknownStore = await Update(
            new MyobStoreMappingChange { StoreCode = "1001", ConnectionGuid = "conn-a" },
            new MyobStoreMappingChange { StoreCode = "9999", ConnectionGuid = "conn-a" });
        Assert.Equal(MyobErrorCodes.StoreNotFound, unknownStore.ErrorCode);

        var inactive = await Update(new MyobStoreMappingChange { StoreCode = "1001", ConnectionGuid = "conn-re" });
        Assert.Equal(MyobErrorCodes.ConnectionNotActive, inactive.ErrorCode);

        var missingConnection = await Update(new MyobStoreMappingChange { StoreCode = "1001", ConnectionGuid = "nope" });
        Assert.Equal(MyobErrorCodes.ConnectionNotActive, missingConnection.ErrorCode);

        var duplicate = await Update(
            new MyobStoreMappingChange { StoreCode = "1001", ConnectionGuid = "conn-a" },
            new MyobStoreMappingChange { StoreCode = "1001", ConnectionGuid = null });
        Assert.Equal(MyobErrorCodes.StoreNotFound, duplicate.ErrorCode);

        var blank = await Update(new MyobStoreMappingChange { StoreCode = " ", ConnectionGuid = "conn-a" });
        Assert.False(blank.Success);

        Assert.Equal(0, await _db.Queryable<MyobStoreMapping>().CountAsync());
    }

    // ───────────────────────── 控制器 ─────────────────────────

    [Fact]
    public void 管理控制器_路由与权限_全部接口都要求Myob_Connection_Manage()
    {
        var controller = typeof(MyobController);
        Assert.Equal("api/react/v1/myob", Assert.Single(controller.GetCustomAttributes<RouteAttribute>()).Template);
        var authorize = Assert.Single(controller.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(Permissions.Myob.Connection.Manage, authorize.Policy);
        Assert.Equal("Myob.Connection.Manage", Permissions.Myob.Connection.Manage);

        // 任何动作都不能用 AllowAnonymous 绕过类级别权限。
        foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>());
        }

        Assert.Equal("connections", RouteOf(controller, nameof(MyobController.ListConnections)));
        Assert.Equal("connections/authorize-url", RouteOf(controller, nameof(MyobController.BuildAuthorizeUrl)));
        Assert.Equal("connections/{connectionGuid}/abn", RouteOf(controller, nameof(MyobController.UpdateAbn)));
        Assert.Equal("connections/{connectionGuid}/verify", RouteOf(controller, nameof(MyobController.Verify)));
        Assert.Equal("connections/{connectionGuid}/disconnect", RouteOf(controller, nameof(MyobController.Disconnect)));
        Assert.Equal("store-mappings", RouteOf(controller, nameof(MyobController.GetStoreMappings)));
        Assert.Equal("store-mappings", RouteOf(controller, nameof(MyobController.UpdateStoreMappings)));
    }

    private static string RouteOf(Type controller, string action) =>
        controller.GetMethod(action)!.GetCustomAttributes().OfType<IRouteTemplateProvider>().Single().Template!;

    [Fact]
    public void 回调控制器_匿名放行_路由与注册的Redirect_Uri一致()
    {
        var controller = typeof(MyobOAuthCallbackController);
        // MYOB 经浏览器跳回的请求没有我方登录令牌，必须匿名；安全性靠加密 state。
        Assert.Single(controller.GetCustomAttributes<AllowAnonymousAttribute>());
        Assert.Equal("api/myob/oauth/callback", Assert.Single(controller.GetCustomAttributes<RouteAttribute>()).Template);
        Assert.NotNull(controller.GetMethod(nameof(MyobOAuthCallbackController.Callback))!.GetCustomAttribute<HttpGetAttribute>());
    }

    [Fact]
    public async Task 回调控制器_成功与失败都302回固定的Web页面_错误码被URL转义()
    {
        var options = Options.Create(Configured());
        var controller = new MyobOAuthCallbackController(new StubConnectionService(), options);

        var ok = Assert.IsType<RedirectResult>(await controller.Callback("code", "state", "biz-1", null, CancellationToken.None));
        Assert.Equal("/pos-admin/myob?myob=connected&connection=conn%201", ok.Url);

        var failed = Assert.IsType<RedirectResult>(await controller.Callback(null, "bad", null, null, CancellationToken.None));
        Assert.Equal("/pos-admin/myob?myob=error&code=MYOB_INVALID_STATE", failed.Url);
    }

    // ───────────────────────── 测试替身 ─────────────────────────

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Dictionary<string, string> ParseForm(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));

    private sealed record RecordedRequest(HttpMethod Method, string Url, string Body, Dictionary<string, string> Headers);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.AbsoluteUri, body, headers));
            return respond(request);
        }
    }

    private sealed class FakeMyobApiClient : IMyobApiClient
    {
        public MyobTokenResponse NextExchange { get; set; } = new("at", "rt", 1200);
        public MyobTokenResponse NextRefresh { get; set; } = new("at", null, 1200);
        public MyobCompanyInfo NextCompany { get; set; } = new("Hot Bargain Pty Ltd", ValidAbn);
        public MyobApiException? ExchangeError { get; set; }
        public MyobApiException? RefreshError { get; set; }
        public MyobApiException? CompanyError { get; set; }
        public TimeSpan RefreshDelay { get; set; }
        public int ExchangeCalls { get; private set; }
        public int RefreshCalls => _refreshCalls;
        public string? LastCode { get; private set; }
        public string? LastRefreshToken { get; private set; }
        public string? LastCompanyBusinessId { get; private set; }
        public string? LastCompanyAccessToken { get; private set; }

        private int _refreshCalls;

        public Task<MyobTokenResponse> ExchangeCodeAsync(string code, CancellationToken cancellationToken)
        {
            ExchangeCalls++;
            LastCode = code;
            return ExchangeError is null ? Task.FromResult(NextExchange) : Task.FromException<MyobTokenResponse>(ExchangeError);
        }

        public async Task<MyobTokenResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _refreshCalls);
            LastRefreshToken = refreshToken;
            if (RefreshDelay > TimeSpan.Zero)
            {
                await Task.Delay(RefreshDelay, cancellationToken);
            }

            return RefreshError is null ? NextRefresh : throw RefreshError;
        }

        public Task<MyobCompanyInfo> GetCompanyAsync(string businessId, string accessToken, CancellationToken cancellationToken)
        {
            LastCompanyBusinessId = businessId;
            LastCompanyAccessToken = accessToken;
            return CompanyError is null ? Task.FromResult(NextCompany) : Task.FromException<MyobCompanyInfo>(CompanyError);
        }
    }

    private sealed class FakeTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    /// <summary>只用于回调控制器的跳转测试：成功返回带空格的连接 ID（验证 URL 转义），失败返回 InvalidState。</summary>
    private sealed class StubConnectionService : IMyobConnectionService
    {
        public Task<MyobCallbackResult> HandleCallbackAsync(string? code, string? state, string? businessId, string? error, CancellationToken cancellationToken) =>
            Task.FromResult(state == "state"
                ? new MyobCallbackResult(true, "conn 1", null)
                : new MyobCallbackResult(false, null, MyobErrorCodes.InvalidState));

        public Task<ApiResponse<List<MyobConnectionDto>>> ListConnectionsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiResponse<MyobAuthorizeUrlDto>> BuildAuthorizeUrlAsync(string userGuid, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiResponse<MyobConnectionDto>> UpdateAbnAsync(string connectionGuid, string? abn, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiResponse<MyobConnectionDto>> VerifyAsync(string connectionGuid, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiResponse<MyobConnectionDto>> DisconnectAsync(string connectionGuid, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiResponse<MyobStoreMappingsDto>> GetStoreMappingsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiResponse<MyobStoreMappingsDto>> UpdateStoreMappingsAsync(MyobUpdateStoreMappingsRequest request, string userGuid, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
