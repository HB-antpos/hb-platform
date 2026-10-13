using System.Net;
using System.Text.Json;
using BlazorApp.FaceIngest.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BlazorApp.FaceIngest.Tests;

/// <summary>腾讯云客户端：请求形状、签名头、结果解析、错误码映射、访客库守卫、超时配置。全部走假 HTTP 处理器，不连外网。</summary>
public sealed class TencentFaceClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 1, 2, 3, TimeSpan.Zero);

    private static (TencentFaceClient Client, ScriptedTencentHandler Http) CreateClient(string region = "ap-guangzhou", Action<Dictionary<string, string?>>? configure = null)
    {
        var data = new Dictionary<string, string?>
        {
            ["FaceMatch:Tencent:Region"] = region,
            ["FaceMatch:Tencent:SecretId"] = MatchSamples.FakeSecretId,
            ["FaceMatch:Tencent:SecretKey"] = MatchSamples.FakeSecretKey,
        };
        configure?.Invoke(data);
        var settings = FaceMatchSettings.Create(new ConfigurationBuilder().AddInMemoryCollection(data).Build());
        Assert.True(settings.MatcherEnabled, settings.DisabledReason);
        var http = new ScriptedTencentHandler();
        return (new TencentFaceClient(new SingleClientFactory(http), settings, new FakeTimeProvider(Now)), http);
    }

    private static readonly byte[] Image = [0xFF, 0xD8, 0xFF, 0x01, 0x02, 0x03, 0xFF, 0xD9];

    [Fact]
    public async Task 搜索请求形状_域名取配置地域_签名头齐全_密钥不在请求体里()
    {
        var (client, http) = CreateClient("ap-shanghai");

        var result = await client.SearchAsync(new FaceSearchRequest(["staff", "vS1001_20261012"], Image, 85), CancellationToken.None);

        Assert.Equal(FaceSearchStatus.Ok, result.Status);
        var request = Assert.Single(http.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("https://iai.ap-shanghai.tencentcloudapi.com/", request.Url);
        Assert.Equal("SearchFacesReturnsByGroup", request.Headers["X-TC-Action"]);
        Assert.Equal("2020-03-03", request.Headers["X-TC-Version"]);
        Assert.Equal("ap-shanghai", request.Headers["X-TC-Region"]);
        Assert.Equal("1791766923", request.Headers["X-TC-Timestamp"]);
        Assert.StartsWith("TC3-HMAC-SHA256 Credential=" + MatchSamples.FakeSecretId + "/2026-10-12/iai/tc3_request, SignedHeaders=content-type;host;x-tc-action, Signature=", request.Headers["Authorization"], StringComparison.Ordinal);
        Assert.Equal("application/json; charset=utf-8", request.Headers["Content-Type"]);

        var body = request.Json();
        Assert.Equal(["staff", "vS1001_20261012"], body.GetProperty("GroupIds").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(Convert.ToBase64String(Image), body.GetProperty("Image").GetString());
        Assert.Equal(1, body.GetProperty("MaxFaceNum").GetInt32());
        Assert.Equal(1, body.GetProperty("MaxPersonNumPerGroup").GetInt32());
        Assert.Equal(2, body.GetProperty("QualityControl").GetInt32());
        Assert.Equal(0, body.GetProperty("NeedRotateDetection").GetInt32());
        Assert.Equal(0, body.GetProperty("NeedPersonInfo").GetInt32());
        Assert.Equal(80, body.GetProperty("MinFaceSize").GetInt32());
        Assert.Equal(85, body.GetProperty("FaceMatchThreshold").GetDouble());
        Assert.DoesNotContain(MatchSamples.FakeSecretKey, request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(MatchSamples.FakeSecretKey, string.Join('\n', request.Headers.Values), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 阈值为空时不带FaceMatchThreshold_标定用()
    {
        var (client, http) = CreateClient();

        await client.SearchAsync(new FaceSearchRequest(["staff"], Image, null), CancellationToken.None);

        Assert.False(Assert.Single(http.Requests).Json().TryGetProperty("FaceMatchThreshold", out _));
    }

    [Fact]
    public async Task 解析各库最高分候选_RetCode非零按不可用()
    {
        var (client, http) = CreateClient();
        http.EnqueueJson("""
            {"Response":{"ResultsReturnsByGroup":[{"RetCode":0,"GroupCandidates":[
              {"GroupId":"staff","Candidates":[{"PersonId":"p-1","Score":91.5}]},
              {"GroupId":"vS1001_20261012","Candidates":[]}]}],"RequestId":"r1"}}
            """);
        http.EnqueueJson("""{"Response":{"ResultsReturnsByGroup":[{"RetCode":-1601,"GroupCandidates":[]}],"RequestId":"r2"}}""");
        http.EnqueueJson("""{"Response":{"ResultsReturnsByGroup":[],"RequestId":"r3"}}""");

        var ok = await client.SearchAsync(new FaceSearchRequest(["staff", "vS1001_20261012"], Image, null), CancellationToken.None);
        var lowQuality = await client.SearchAsync(new FaceSearchRequest(["staff"], Image, null), CancellationToken.None);
        var noFace = await client.SearchAsync(new FaceSearchRequest(["staff"], Image, null), CancellationToken.None);

        Assert.Equal(FaceSearchStatus.Ok, ok.Status);
        var hit = Assert.Single(ok.Hits);
        Assert.Equal(("staff", "p-1", 91.5), (hit.GroupId, hit.PersonId, hit.Score));
        Assert.Equal(FaceSearchStatus.NoUsableFace, lowQuality.Status);
        Assert.Equal(FaceSearchStatus.NoUsableFace, noFace.Status);
    }

    [Theory]
    [InlineData("InvalidParameterValue.NoFaceInPhoto", "Quality")]
    [InlineData("FailedOperation.ImageDecodeFailed", "Quality")]
    [InlineData("FailedOperation.ImageResolutionTooSmall", "Quality")]
    [InlineData("FailedOperation.FaceQualityNotQualified", "Quality")]
    [InlineData("RequestLimitExceeded", "RateLimited")]
    [InlineData("RequestLimitExceeded.UinLimitExceeded", "RateLimited")]
    [InlineData("FailedOperation.RequestLimitExceeded", "RateLimited")]
    [InlineData("InternalError", "Transient")]
    [InlineData("FailedOperation.ServerError", "Transient")]
    [InlineData("FailedOperation.RequestTimeout", "Transient")]
    [InlineData("AuthFailure.SignatureFailure", "AccountFatal")]
    [InlineData("AuthFailure.SecretIdNotFound", "AccountFatal")]
    [InlineData("ResourceUnavailable.InArrears", "AccountFatal")]
    [InlineData("ResourceUnavailable.NotExist", "AccountFatal")]
    [InlineData("UnauthorizedOperation.CamAuthFailure", "AccountFatal")]
    [InlineData("InvalidParameterValue.GroupIdNotExist", "RequestFatal")]
    [InlineData("InvalidParameter.InvalidParameter", "RequestFatal")]
    [InlineData("SomethingBrandNew.Unknown", "RequestFatal")]
    public void 错误码映射(string code, string expected)
        => Assert.Equal(Enum.Parse<FaceMatchErrorKind>(expected), TencentFaceClient.Classify(code));

    [Fact]
    public async Task 腾讯云返回错误_异常只带错误码动作与请求编号_不带自由文本()
    {
        var (client, http) = CreateClient();
        http.EnqueueError("AuthFailure.SignatureFailure");

        var error = await Assert.ThrowsAsync<FaceMatchApiException>(
            () => client.SearchAsync(new FaceSearchRequest(["staff"], Image, null), CancellationToken.None));

        Assert.Equal(FaceMatchErrorKind.AccountFatal, error.Kind);
        Assert.Equal("AuthFailure.SignatureFailure", error.Code);
        Assert.Contains("req-test-1", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("free text", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(MatchSamples.FakeSecretKey, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 网络错误与非JSON响应按瞬时错误_400类非JSON按请求级()
    {
        var (client, http) = CreateClient();
        http.EnqueueThrow(new HttpRequestException("connection reset by peer"));
        http.EnqueueJson("<html>bad gateway</html>", HttpStatusCode.BadGateway);
        http.EnqueueJson("<html>forbidden</html>", HttpStatusCode.Forbidden);

        var network = await Assert.ThrowsAsync<FaceMatchApiException>(() => client.SearchAsync(new FaceSearchRequest(["staff"], Image, null), CancellationToken.None));
        var gateway = await Assert.ThrowsAsync<FaceMatchApiException>(() => client.SearchAsync(new FaceSearchRequest(["staff"], Image, null), CancellationToken.None));
        var forbidden = await Assert.ThrowsAsync<FaceMatchApiException>(() => client.SearchAsync(new FaceSearchRequest(["staff"], Image, null), CancellationToken.None));

        Assert.Equal(FaceMatchErrorKind.Transient, network.Kind);
        Assert.Equal(FaceMatchErrorKind.Transient, gateway.Kind);
        Assert.Equal(FaceMatchErrorKind.RequestFatal, forbidden.Kind);
    }

    [Fact]
    public async Task 空库报NoFaceInGroups_单库视为无命中_多库逐库重搜再合并()
    {
        var (client, http) = CreateClient();
        // 单库：直接空。
        http.EnqueueError("InvalidParameterValue.NoFaceInGroups");
        var single = await client.SearchAsync(new FaceSearchRequest(["vS1001_20261012"], Image, null), CancellationToken.None);
        Assert.Equal(FaceSearchStatus.Ok, single.Status);
        Assert.Empty(single.Hits);

        // 多库：合并请求报错 → 员工库单独搜到命中，访客库单独搜报空 → 员工命中不能丢。
        http.EnqueueError("InvalidParameterValue.NoFaceInGroups");
        http.EnqueueJson("""{"Response":{"ResultsReturnsByGroup":[{"RetCode":0,"GroupCandidates":[{"GroupId":"staff","Candidates":[{"PersonId":"p-1","Score":93}]}]}],"RequestId":"r"}}""");
        http.EnqueueError("InvalidParameterValue.NoFaceInGroups");
        var multi = await client.SearchAsync(new FaceSearchRequest(["staff", "vS1001_20261012"], Image, null), CancellationToken.None);

        Assert.Equal(FaceSearchStatus.Ok, multi.Status);
        Assert.Equal(93, Assert.Single(multi.Hits).Score);
        Assert.Equal(4, http.RequestCount);
    }

    [Fact]
    public async Task 建库与建人_已存在视为成功_请求体只含必要字段()
    {
        var (client, http) = CreateClient();
        http.EnqueueError("InvalidParameterValue.GroupIdAlreadyExist");
        http.EnqueueError("InvalidParameterValue.PersonIdAlreadyExist");

        await client.CreateGroupAsync("vS1001_20261012", CancellationToken.None);
        await client.CreatePersonAsync("vS1001_20261012", "vp0123456789abcdef", Image, CancellationToken.None);

        var requests = http.Requests.ToArray();
        Assert.Equal("CreateGroup", requests[0].Headers["X-TC-Action"]);
        Assert.Equal("vS1001_20261012", requests[0].Json().GetProperty("GroupId").GetString());
        Assert.Equal("3.0", requests[0].Json().GetProperty("FaceModelVersion").GetString());
        Assert.Equal("CreatePerson", requests[1].Headers["X-TC-Action"]);
        var person = requests[1].Json();
        Assert.Equal("vp0123456789abcdef", person.GetProperty("PersonId").GetString());
        Assert.Equal("vp0123456789abcdef", person.GetProperty("PersonName").GetString());
        Assert.Equal(0, person.GetProperty("UniquePersonControl").GetInt32());
        // 访客没有姓名、性别、描述字段等任何业务标识。
        Assert.False(person.TryGetProperty("Gender", out _));
        Assert.False(person.TryGetProperty("PersonExDescriptionInfos", out _));
    }

    [Theory]
    [InlineData("staff")]
    [InlineData("unwelcome")]
    [InlineData("vS1001_2026101")]
    [InlineData("xS1001_20261012")]
    public async Task 接收端只允许操作当日访客库_员工库与名单库的写入被拒绝且不发请求(string groupId)
    {
        var (client, http) = CreateClient();

        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateGroupAsync(groupId, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreatePersonAsync(groupId, "vp1", Image, CancellationToken.None));

        Assert.Equal(0, http.RequestCount);
    }

    [Fact]
    public async Task 建人拒绝非法PersonId()
    {
        var (client, http) = CreateClient();

        await Assert.ThrowsAsync<ArgumentException>(() => client.CreatePersonAsync("vS1001_20261012", "bad id with spaces", Image, CancellationToken.None));

        Assert.Equal(0, http.RequestCount);
    }

    [Fact]
    public void 命名HttpClient的超时是10秒()
    {
        using var factory = new FaceIngestAppFactory();

        var client = factory.GetService<IHttpClientFactory>().CreateClient(TencentFaceClient.HttpClientName);

        Assert.Equal(TimeSpan.FromSeconds(10), client.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(10), TencentFaceClient.RequestTimeout);
    }

    [Fact]
    public void 配置不可用时构造真实客户端直接拒绝()
    {
        var settings = FaceMatchSettings.Create(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build());

        Assert.False(settings.MatcherEnabled);
        Assert.Throws<InvalidOperationException>(() => new TencentFaceClient(new SingleClientFactory(new ScriptedTencentHandler()), settings, TimeProvider.System));
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = TencentFaceClient.RequestTimeout };
    }
}
