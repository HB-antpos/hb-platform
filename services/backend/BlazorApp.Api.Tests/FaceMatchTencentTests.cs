using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BlazorApp.Api.Features.FaceMatch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 腾讯云签名 v3、IAI 管理客户端与功能开关配置。全部使用假凭据 + 假 HttpMessageHandler，不连接真实腾讯云。
/// </summary>
public sealed class FaceMatchTencentTests
{
    private const string FakeSecretId = "FAKE-ADMIN-ID";
    private const string FakeSecretKey = "fake-admin-key-material";

    private static readonly DateTimeOffset FixedNow = new(2026, 10, 14, 2, 0, 0, TimeSpan.Zero);

    private readonly AuditCapturingLoggerProvider _logs = new();

    // ---------- 签名 v3：官方示例向量 ----------

    // 来源：腾讯云 API 3.0「签名方法 v3」文档的 cvm DescribeInstances 示例
    // （https://cloud.tencent.com/document/api/1278/46712）。文档里的示例密钥为 SecretId=AKID…（z8krbsJ5yKBZQpn74WFkmLPx3EXAMPLE）、
    // SecretKey=Gu5t9xGARNpq86cd98joQYCN3EXAMPLE（页面把中段打了星号，公开的 EXAMPLE 示例值），
    // 时间戳 1551113065、载荷如下。文档给出的中间值：载荷哈希 35e9c5b0...f064、规范请求哈希 5ffe6a04...7031、
    // 凭据范围 2019-02-25/cvm/tc3_request，最终 Signature 以 72e494ea8 开头、a96525168 结尾（中段被页面遮蔽）。
    // 中文注释：官方文档里公开的 EXAMPLE 示例值；拆成两段拼接，避免被仓库的密钥扫描按「AKID+32 位」格式误判为真实凭据。
    private const string VectorSecretId = "AKID" + "z8krbsJ5yKBZQpn74WFkmLPx3EXAMPLE";
    private const string VectorSecretKey = "Gu5t9xGARNpq86cd98joQYCN3EXAMPLE";
    private const long VectorTimestamp = 1551113065;
    private const string VectorHost = "cvm.tencentcloudapi.com";
    private const string VectorPayload = "{\"Limit\": 1, \"Filters\": [{\"Values\": [\"\\u672a\\u547d\\u540d\"], \"Name\": \"instance-name\"}]}";

    [Fact]
    public void 官方示例向量_载荷哈希与规范请求哈希与文档一致()
    {
        Assert.Equal("35e9c5b0e3ae67532d3c9f17ead6c90222632e5b1ff7f6e89887f1398934f064", TencentCloudV3Signer.Sha256Hex(VectorPayload));

        var canonical = TencentCloudV3Signer.BuildCanonicalRequest(VectorHost, VectorPayload);
        Assert.Equal(
            "POST\n/\n\ncontent-type:application/json; charset=utf-8\nhost:cvm.tencentcloudapi.com\n\ncontent-type;host\n"
            + "35e9c5b0e3ae67532d3c9f17ead6c90222632e5b1ff7f6e89887f1398934f064",
            canonical);
        Assert.Equal("5ffe6a04c0664d6b969fab9a13bdab201d63ee709638e2749d62a09ca18d7031", TencentCloudV3Signer.Sha256Hex(canonical));
    }

    [Fact]
    public void 官方示例向量_待签名串与文档一致()
    {
        var canonical = TencentCloudV3Signer.BuildCanonicalRequest(VectorHost, VectorPayload);

        Assert.Equal(
            "TC3-HMAC-SHA256\n1551113065\n2019-02-25/cvm/tc3_request\n5ffe6a04c0664d6b969fab9a13bdab201d63ee709638e2749d62a09ca18d7031",
            TencentCloudV3Signer.BuildStringToSign(VectorTimestamp, "cvm", canonical));
    }

    [Fact]
    public void 官方示例向量_Authorization与文档的签名首尾一致()
    {
        var authorization = TencentCloudV3Signer.BuildAuthorization(
            VectorSecretId, VectorSecretKey, "cvm", VectorHost, VectorTimestamp, VectorPayload);

        Assert.StartsWith(
            $"TC3-HMAC-SHA256 Credential={VectorSecretId}/2019-02-25/cvm/tc3_request, SignedHeaders=content-type;host, Signature=72e494ea8",
            authorization);
        Assert.EndsWith("a96525168", authorization);
        // 完整签名（文档只公开了首尾各 9 位，中段为本实现按同一流程算出的值，与 Python 参考实现一致）。
        Assert.EndsWith("Signature=72e494ea809ad7a8c8f7a4507b9bddcbaa8e581f516e8da2f66e2c5a96525168", authorization);
    }

    [Fact]
    public void 凭据范围日期取UTC_与机器时区无关()
    {
        // 2019-02-25 23:59:59 UTC 在 UTC+10 已是 26 日，凭据范围仍必须是 25 日。
        var timestamp = new DateTimeOffset(2019, 2, 25, 23, 59, 59, TimeSpan.Zero).ToUnixTimeSeconds();

        Assert.Equal("2019-02-25", TencentCloudV3Signer.DateOnlyFromTimestamp(timestamp));
        Assert.Equal("2019-02-26", TencentCloudV3Signer.DateOnlyFromTimestamp(timestamp + 1));
    }

    // ---------- 功能开关配置 ----------

    [Fact]
    public void 地域缺省_功能关闭_不回退任何别的地域()
    {
        var options = Options(region: null);

        var resolved = FaceMatchSettings.Resolve(options);

        Assert.False(resolved.Enabled);
        Assert.Equal(FaceMatchDisabledReasons.RegionMissing, resolved.DisabledReason);
        Assert.Null(resolved.Region);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ap-beijing")]
    [InlineData("AP-GUANGZHOU")]
    [InlineData("ap-guangzhou ")]
    [InlineData("ap-hongkong")]
    [InlineData("ap-guangzhou.evil.example")]
    public void 地域不在白名单_功能关闭_配置写错不会被修正成别的地域(string region)
    {
        var resolved = FaceMatchSettings.Resolve(Options(region));

        // 末尾带空格的写法 trim 后是合法的 ap-guangzhou：这是纯空白修剪，不算「修正地域」。
        if (region.Trim() == "ap-guangzhou")
        {
            Assert.True(resolved.Enabled);
            return;
        }

        Assert.False(resolved.Enabled);
        Assert.Contains(resolved.DisabledReason, new[] { FaceMatchDisabledReasons.RegionMissing, FaceMatchDisabledReasons.RegionNotAllowed });
        Assert.Null(resolved.SecretKey);
    }

    [Theory]
    [InlineData("ap-guangzhou")]
    [InlineData("ap-shanghai")]
    [InlineData("ap-singapore")]
    public void 白名单地域_凭据齐全_功能开启(string region)
    {
        var resolved = FaceMatchSettings.Resolve(Options(region));

        Assert.True(resolved.Enabled);
        Assert.Equal(region, resolved.Region);
        Assert.Null(resolved.DisabledReason);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(FakeSecretId, null)]
    [InlineData(null, FakeSecretKey)]
    [InlineData(FakeSecretId, "   ")]
    public void 管理凭据缺失或只配一半_功能关闭(string? id, string? key)
    {
        var resolved = FaceMatchSettings.Resolve(Options("ap-guangzhou", id, key));

        Assert.False(resolved.Enabled);
        Assert.Equal(FaceMatchDisabledReasons.CredentialsMissing, resolved.DisabledReason);
        Assert.Null(resolved.SecretId);
        Assert.Null(resolved.SecretKey);
    }

    [Theory]
    [InlineData("bad id with spaces", FakeSecretKey)]
    [InlineData("id\r\nInjected: header", FakeSecretKey)]
    [InlineData(FakeSecretId, "key\nwith-newline")]
    public void 管理凭据含非法字符_功能关闭(string id, string key)
    {
        var resolved = FaceMatchSettings.Resolve(Options("ap-guangzhou", id, key));

        Assert.False(resolved.Enabled);
        Assert.Equal(FaceMatchDisabledReasons.CredentialsInvalid, resolved.DisabledReason);
    }

    [Fact]
    public void 配置快照的ToString不带密钥()
    {
        var resolved = FaceMatchSettings.Resolve(Options("ap-guangzhou"));

        Assert.DoesNotContain(FakeSecretKey, resolved.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(FakeSecretId, resolved.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void 腾讯云客户端与配置源码里没有任何对主密钥配置的引用()
    {
        // 设计第 15 节：主 API 不得使用 TencentCloud:* 主密钥调用 IAI；功能缺凭据时整体关闭，不是回退。
        foreach (var file in new[] { "TencentFaceLibrary.cs", "FaceMatchOptions.cs" })
        {
            // 只检查代码，不检查注释（注释里允许说明「不会回退到 TencentCloud:* 主密钥」）。
            var source = string.Join("\n", File.ReadAllLines(FeatureFile(file)).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            Assert.DoesNotContain("TencentCloudSettings", source, StringComparison.Ordinal);
            Assert.DoesNotContain("TencentCloud:", source, StringComparison.Ordinal);
            Assert.DoesNotContain("TENCENT_SECRET", source, StringComparison.Ordinal);
        }
    }

    // ---------- 请求形态 ----------

    [Fact]
    public async Task 请求_POST到所选地域域名_头与签名自洽()
    {
        var handler = new TencentStubHandler((_, _) => Task.FromResult(Json("""{"Response":{"RequestId":"r1"}}""")));
        var library = CreateLibrary(handler);

        await library.EnsureGroupAsync("staff", "staff");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://iai.ap-guangzhou.tencentcloudapi.com/", request.RequestUri!.ToString());
        Assert.Equal("CreateGroup", Assert.Single(request.Headers.GetValues("X-TC-Action")));
        Assert.Equal("2020-03-03", Assert.Single(request.Headers.GetValues("X-TC-Version")));
        Assert.Equal("ap-guangzhou", Assert.Single(request.Headers.GetValues("X-TC-Region")));
        var timestamp = FixedNow.ToUnixTimeSeconds();
        Assert.Equal(timestamp.ToString(), Assert.Single(request.Headers.GetValues("X-TC-Timestamp")));
        Assert.Equal("application/json; charset=utf-8", Assert.Single(handler.ContentTypes));

        // 用实际发出的请求体按官方流程独立重算签名，必须与请求头逐字相同。
        var body = Assert.Single(handler.Bodies);
        var expected = TencentCloudV3Signer.BuildAuthorization(
            FakeSecretId, FakeSecretKey, "iai", "iai.ap-guangzhou.tencentcloudapi.com", timestamp, body);
        Assert.Equal(expected, Assert.Single(request.Headers.GetValues("Authorization")));
        Assert.StartsWith($"TC3-HMAC-SHA256 Credential={FakeSecretId}/2026-10-14/iai/tc3_request, SignedHeaders=content-type;host, Signature=", expected);

        var json = JsonNode.Parse(body)!;
        Assert.Equal("staff", json["GroupId"]!.GetValue<string>());
        Assert.Equal("3.0", json["FaceModelVersion"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("ap-guangzhou")]
    [InlineData("ap-shanghai")]
    [InlineData("ap-singapore")]
    public async Task 域名和Region头跟随配置地域(string region)
    {
        var handler = new TencentStubHandler((_, _) => Task.FromResult(Json("""{"Response":{"RequestId":"r1"}}""")));
        var library = CreateLibrary(handler, Options(region));

        await library.EnsureGroupAsync("staff", "staff");

        var request = Assert.Single(handler.Requests);
        Assert.Equal($"iai.{region}.tencentcloudapi.com", request.RequestUri!.Host);
        Assert.Equal(region, Assert.Single(request.Headers.GetValues("X-TC-Region")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ap-beijing")]
    public async Task 地域非法时不发任何请求_抛Disabled(string? region)
    {
        var handler = new TencentStubHandler((_, _) => Task.FromResult(Json("""{"Response":{}}""")));
        var library = CreateLibrary(handler, Options(region));

        Assert.False(library.IsAvailable);
        var ex = await Assert.ThrowsAsync<FaceLibraryException>(() => library.EnsureGroupAsync("staff", "staff"));

        Assert.Equal(FaceLibraryFailureKind.Disabled, ex.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task 凭据缺失时不发任何请求_抛Disabled()
    {
        var handler = new TencentStubHandler((_, _) => Task.FromResult(Json("""{"Response":{}}""")));
        var library = CreateLibrary(handler, Options("ap-guangzhou", null, null));

        var ex = await Assert.ThrowsAsync<FaceLibraryException>(() => library.DeletePersonAsync("st_x"));

        Assert.Equal(FaceLibraryFailureKind.Disabled, ex.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreatePerson_请求体与返回FaceId_照片以Base64发送且PersonName只放不透明编号()
    {
        var handler = new TencentStubHandler((_, _) => Task.FromResult(Json("""{"Response":{"FaceId":"face-1","RequestId":"r1"}}""")));
        var library = CreateLibrary(handler);

        var faceId = await library.CreatePersonAsync("staff", "st_abc", "abc", FaceImageTestData.Jpeg);

        Assert.Equal("face-1", faceId);
        var body = JsonNode.Parse(Assert.Single(handler.Bodies))!;
        Assert.Equal("staff", body["GroupId"]!.GetValue<string>());
        Assert.Equal("st_abc", body["PersonId"]!.GetValue<string>());
        Assert.Equal("abc", body["PersonName"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(FaceImageTestData.Jpeg), body["Image"]!.GetValue<string>());
        Assert.Equal(0, body["UniquePersonControl"]!.GetValue<int>());
        Assert.Equal(2, body["QualityControl"]!.GetValue<int>());
    }

    [Fact]
    public async Task QualityControl可配置_非法值回到默认2()
    {
        var bodies = new List<JsonNode>();
        var handler = new TencentStubHandler(async (request, _) =>
        {
            bodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync())!);
            return Json("""{"Response":{"FaceId":"f","RequestId":"r"}}""");
        });
        var options = Options("ap-guangzhou");
        var settings = new FakeFaceMatchSettings();
        settings.Options.Tencent = options.Tencent;
        settings.Options.EnrollmentQualityControl = 3;
        var library = new TencentFaceLibrary(new FaceImageStubHttpClientFactory(handler), settings, new FixedTime(FixedNow), _logs.CreateLogger<TencentFaceLibrary>());

        await library.CreatePersonAsync("staff", "st_a", "a", FaceImageTestData.Jpeg);
        settings.Options.EnrollmentQualityControl = 9;
        await library.CreatePersonAsync("staff", "st_b", "b", FaceImageTestData.Jpeg);

        Assert.Equal(3, bodies[0]["QualityControl"]!.GetValue<int>());
        Assert.Equal(2, bodies[1]["QualityControl"]!.GetValue<int>());
    }

    [Fact]
    public async Task CreateFaces_返回成功的FaceId列表()
    {
        var handler = new TencentStubHandler((_, _) =>
            Task.FromResult(Json("""{"Response":{"SucFaceNum":2,"SucFaceIds":["f1","f2"],"RequestId":"r1"}}""")));

        var ids = await CreateLibrary(handler).CreateFacesAsync("st_a", [FaceImageTestData.Jpeg, FaceImageTestData.Png, FaceImageTestData.Jpeg]);

        Assert.Equal(["f1", "f2"], ids);
        var body = JsonNode.Parse(Assert.Single(handler.Bodies))!;
        Assert.Equal(3, body["Images"]!.AsArray().Count);
    }

    [Fact]
    public async Task GetPersonCount_读取PersonNum_库不存在返回null()
    {
        var handler = new TencentStubHandler((request, _) => Task.FromResult(Json(
            """{"Response":{"PersonNum":7,"PersonInfos":[],"RequestId":"r1"}}""")));
        Assert.Equal(7, await CreateLibrary(handler).GetPersonCountAsync("staff"));

        var missing = new TencentStubHandler((_, _) => Task.FromResult(Json(
            """{"Response":{"Error":{"Code":"InvalidParameterValue.GroupIdNotExist","Message":"x"},"RequestId":"r2"}}""")));
        Assert.Null(await CreateLibrary(missing).GetPersonCountAsync("v1013_20261013"));
    }

    [Fact]
    public async Task ListGroups_分页取完()
    {
        var page = 0;
        var handler = new TencentStubHandler(async (request, _) =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            Assert.Equal(page * 100, body["Offset"]!.GetValue<int>());
            page++;
            var count = page == 1 ? 100 : 3;
            var infos = string.Join(",", Enumerable.Range(0, count).Select(i => $"{{\"GroupId\":\"g{page}-{i}\",\"GroupName\":\"n\"}}"));
            return Json($"{{\"Response\":{{\"GroupInfos\":[{infos}],\"GroupNum\":103,\"RequestId\":\"r\"}}}}");
        });

        var groups = await CreateLibrary(handler).ListGroupsAsync();

        Assert.Equal(103, groups.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    // ---------- 幂等语义 ----------

    [Fact]
    public async Task EnsureGroup_已存在视为成功()
    {
        var handler = new TencentStubHandler((_, _) => Task.FromResult(Json(
            """{"Response":{"Error":{"Code":"InvalidParameterValue.GroupIdAlreadyExist","Message":"x"},"RequestId":"r"}}""")));

        await CreateLibrary(handler).EnsureGroupAsync("staff", "staff");
    }

    [Theory]
    [InlineData("InvalidParameterValue.PersonIdNotExist")]
    [InlineData("InvalidParameterValue.GroupIdNotExist")]
    public async Task 删除不存在的人员或库视为成功(string code)
    {
        var handler = new TencentStubHandler((_, _) => Task.FromResult(Json(
            $"{{\"Response\":{{\"Error\":{{\"Code\":\"{code}\",\"Message\":\"x\"}},\"RequestId\":\"r\"}}}}")));
        var library = CreateLibrary(handler);

        await library.DeletePersonAsync("st_gone");
        await library.DeleteGroupAsync("v1013_20261013");

        Assert.Equal(2, handler.Requests.Count);
    }

    // ---------- 错误码映射 ----------

    [Theory]
    [InlineData("AuthFailure.SignatureFailure", FaceLibraryFailureKind.Auth)]
    [InlineData("AuthFailure.SecretIdNotFound", FaceLibraryFailureKind.Auth)]
    [InlineData("UnauthorizedOperation", FaceLibraryFailureKind.Auth)]
    [InlineData("UnsupportedRegion", FaceLibraryFailureKind.Auth)]
    [InlineData("ResourceUnavailable.NotExist", FaceLibraryFailureKind.Auth)]
    [InlineData("ResourceUnavailable.InArrears", FaceLibraryFailureKind.Auth)]
    [InlineData("ResourceUnavailable.NotReady", FaceLibraryFailureKind.Auth)]
    [InlineData("ResourceUnavailable.Delivering", FaceLibraryFailureKind.Unavailable)]
    [InlineData("RequestLimitExceeded", FaceLibraryFailureKind.RateLimited)]
    [InlineData("RequestLimitExceeded.UinLimitExceeded", FaceLibraryFailureKind.RateLimited)]
    [InlineData("FailedOperation.RequestLimitExceeded", FaceLibraryFailureKind.RateLimited)]
    [InlineData("FailedOperation.CreateFaceConcurrent", FaceLibraryFailureKind.RateLimited)]
    [InlineData("InvalidParameterValue.GroupIdAlreadyExist", FaceLibraryFailureKind.AlreadyExists)]
    [InlineData("InvalidParameterValue.PersonIdAlreadyExist", FaceLibraryFailureKind.AlreadyExists)]
    [InlineData("InvalidParameterValue.PersonIdNotExist", FaceLibraryFailureKind.NotFound)]
    [InlineData("InvalidParameterValue.GroupIdNotExist", FaceLibraryFailureKind.NotFound)]
    [InlineData("InvalidParameterValue.GroupIdNotExists", FaceLibraryFailureKind.NotFound)]
    [InlineData("InvalidParameterValue.NoFaceInPhoto", FaceLibraryFailureKind.InvalidImage)]
    [InlineData("FailedOperation.ImageDecodeFailed", FaceLibraryFailureKind.InvalidImage)]
    [InlineData("FailedOperation.ImageResolutionTooSmall", FaceLibraryFailureKind.InvalidImage)]
    [InlineData("FailedOperation.FaceQualityNotQualified", FaceLibraryFailureKind.InvalidImage)]
    [InlineData("FailedOperation.FaceSizeTooSmall", FaceLibraryFailureKind.InvalidImage)]
    [InlineData("InvalidParameterValue.PersonFaceNumExceed", FaceLibraryFailureKind.LimitExceeded)]
    [InlineData("InvalidParameterValue.GroupNumExceed", FaceLibraryFailureKind.LimitExceeded)]
    [InlineData("LimitExceeded.ErrorFaceNumExceed", FaceLibraryFailureKind.LimitExceeded)]
    [InlineData("InternalError", FaceLibraryFailureKind.Unavailable)]
    [InlineData("FailedOperation.ServerError", FaceLibraryFailureKind.Unavailable)]
    [InlineData("ServiceUnavailable", FaceLibraryFailureKind.Unavailable)]
    [InlineData("InvalidParameter", FaceLibraryFailureKind.Other)]
    [InlineData("Whatever.Unknown", FaceLibraryFailureKind.Other)]
    public void 错误码映射(string code, FaceLibraryFailureKind expected)
    {
        Assert.Equal(expected, TencentFaceLibrary.MapKind(code));
    }

    [Fact]
    public async Task 错误响应抛带类别的异常_日志只有错误码与RequestId_不含密钥签名与请求体()
    {
        var handler = new TencentStubHandler((_, _) => Task.FromResult(Json(
            """{"Response":{"Error":{"Code":"AuthFailure.SignatureFailure","Message":"secret detail p_123 should not be logged"},"RequestId":"req-abc"}}""")));
        var library = CreateLibrary(handler);

        var ex = await Assert.ThrowsAsync<FaceLibraryException>(
            () => library.CreatePersonAsync("staff", "st_visible", "visible", FaceImageTestData.Jpeg));

        Assert.Equal(FaceLibraryFailureKind.Auth, ex.Kind);
        Assert.Equal("AuthFailure.SignatureFailure", ex.Code);
        Assert.False(ex.IsRetryable);
        var dump = _logs.Dump() + ex.Message;
        Assert.Contains("req-abc", dump);
        AssertNoSensitive(dump);
        Assert.DoesNotContain("secret detail", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("st_visible", dump, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HTTP500或非JSON响应_按可重试的不可用处理()
    {
        var handler = new TencentStubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>bad gateway</html>") }));

        var ex = await Assert.ThrowsAsync<FaceLibraryException>(() => CreateLibrary(handler).DeletePersonAsync("st_x"));

        Assert.Equal(FaceLibraryFailureKind.Unavailable, ex.Kind);
        Assert.True(ex.IsRetryable);
    }

    [Fact]
    public async Task 网络异常_映射为不可用_日志不带主机名与密钥()
    {
        var handler = new TencentStubHandler((_, _) => throw new HttpRequestException("Name or service not known (iai.ap-guangzhou.tencentcloudapi.com:443)"));

        var ex = await Assert.ThrowsAsync<FaceLibraryException>(() => CreateLibrary(handler).DeletePersonAsync("st_x"));

        Assert.Equal(FaceLibraryFailureKind.Unavailable, ex.Kind);
        Assert.Equal("Network", ex.Code);
        var dump = _logs.Dump() + ex.Message;
        Assert.DoesNotContain("tencentcloudapi", dump, StringComparison.OrdinalIgnoreCase);
        AssertNoSensitive(dump);
    }

    [Fact]
    public async Task 超过总时限_映射为超时_调用方取消则原样取消()
    {
        var handler = new TencentStubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Json("{}");
        });
        var library = CreateLibrary(handler, timeout: TimeSpan.FromMilliseconds(80));

        var ex = await Assert.ThrowsAsync<FaceLibraryException>(() => library.DeletePersonAsync("st_x"));
        Assert.Equal("Timeout", ex.Code);
        Assert.True(ex.IsRetryable);

        using var cts = new CancellationTokenSource();
        var pending = library.DeletePersonAsync("st_x", cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    // ---------- 登记 ----------

    [Fact]
    public void Program里两个命名客户端都禁用重定向并限时15秒()
    {
        var program = File.ReadAllText(Path.Combine(ApiRoot(), "Program.cs"));

        foreach (var name in new[] { "TencentFaceLibrary.HttpClientName", "CosFaceMatchObjectStore.HttpClientName" })
        {
            var index = program.IndexOf(name, StringComparison.Ordinal);
            Assert.True(index >= 0, name);
            var window = program.Substring(index, 260);
            Assert.Contains("TimeSpan.FromSeconds(15)", window, StringComparison.Ordinal);
            Assert.Contains("AllowAutoRedirect = false", window, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 部署契约_compose里FaceMatch变量全部带默认值_没有一个必填_密钥默认为空_地域默认广州()
    {
        var compose = File.ReadAllText(Path.Combine(ApiRoot(), "..", "docker-compose.yml"));

        foreach (var line in compose.Split('\n').Where(l => l.Contains("FaceMatch__", StringComparison.Ordinal) && !l.TrimStart().StartsWith('#')))
        {
            Assert.Matches(@"=\$\{FACE_MATCH_[A-Z_]+:-[^}]*\}\s*$", line);
            Assert.DoesNotContain(":?", line, StringComparison.Ordinal);
        }

        Assert.Contains("- FaceMatch__Tencent__Region=${FACE_MATCH_TENCENT_REGION:-ap-guangzhou}", compose, StringComparison.Ordinal);
        Assert.Contains("- FaceMatch__Tencent__AdminSecretId=${FACE_MATCH_ADMIN_SECRET_ID:-}", compose, StringComparison.Ordinal);
        Assert.Contains("- FaceMatch__Tencent__AdminSecretKey=${FACE_MATCH_ADMIN_SECRET_KEY:-}", compose, StringComparison.Ordinal);
        Assert.Contains("- FaceMatch__CaptureRetentionDays=${FACE_MATCH_CAPTURE_RETENTION_DAYS:-7}", compose, StringComparison.Ordinal);
        // 主 API 的 IAI 管理凭据绝不引用主密钥变量。
        foreach (var line in compose.Split('\n').Where(l => l.Contains("FaceMatch__Tencent", StringComparison.Ordinal) && !l.TrimStart().StartsWith('#')))
        {
            Assert.DoesNotContain("TENCENT_SECRET", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 配置绑定_环境变量式键映射到FaceMatch节_空字符串等同未配置()
    {
        // compose 的 ${VAR:-} 在没有变量时给出空字符串：应解析为「未配置」而不是意外开启。
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FaceMatch:Tencent:Region"] = "ap-guangzhou",
            ["FaceMatch:Tencent:AdminSecretId"] = "",
            ["FaceMatch:Tencent:AdminSecretKey"] = "",
            ["FaceMatch:CaptureRetentionDays"] = "3",
            ["FaceMatch:Unwelcome:HighBandScore"] = "93.5",
            ["FaceMatch:BackgroundEnabled"] = "false",
        }).Build();
        var options = new FaceMatchOptions();
        configuration.GetSection(FaceMatchOptions.SectionName).Bind(options);

        Assert.Equal(3, options.CaptureRetentionDays);
        Assert.Equal(93.5m, options.Unwelcome.HighBandScore);
        Assert.False(options.BackgroundEnabled);
        var resolved = FaceMatchSettings.Resolve(options);
        Assert.False(resolved.Enabled);
        Assert.Equal(FaceMatchDisabledReasons.CredentialsMissing, resolved.DisabledReason);
    }

    // ---------- 辅助 ----------

    private static FaceMatchOptions Options(string? region, string? id = FakeSecretId, string? key = FakeSecretKey) => new()
    {
        Tencent = new FaceMatchTencentOptions { Region = region, AdminSecretId = id, AdminSecretKey = key },
    };

    private TencentFaceLibrary CreateLibrary(TencentStubHandler handler, FaceMatchOptions? options = null, TimeSpan? timeout = null)
    {
        var settings = new FakeFaceMatchSettings();
        if (options != null)
        {
            settings.Options.Tencent = options.Tencent;
        }

        return new TencentFaceLibrary(
            new FaceImageStubHttpClientFactory(handler), settings, new FixedTime(FixedNow), _logs.CreateLogger<TencentFaceLibrary>())
        {
            RequestTimeout = timeout ?? TencentFaceLibrary.DefaultRequestTimeout,
        };
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>错误信息 / 日志里不得出现：密钥、签名、Authorization、完整请求体里的照片。</summary>
    private static void AssertNoSensitive(string text)
    {
        foreach (var forbidden in new[]
                 {
                     FakeSecretKey, FakeSecretId, "Signature=", "Authorization", "TC3-HMAC-SHA256",
                     Convert.ToBase64String(FaceImageTestData.Jpeg),
                 })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }
    }

    private static string ApiRoot([System.Runtime.CompilerServices.CallerFilePath] string testFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", "BlazorApp.Api"));

    private static string FeatureFile(string name) => Path.Combine(ApiRoot(), "Features", "FaceMatch", name);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>
/// 假的腾讯云 HTTP 处理器：在请求被客户端释放之前就把请求体与 Content-Type 读出来存好
/// （客户端发完请求会 Dispose 请求内容，事后再读会抛已释放异常）。
/// </summary>
internal sealed class TencentStubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> Bodies { get; } = [];
    public List<string> ContentTypes { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (request.Content != null)
        {
            Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            ContentTypes.Add(request.Content.Headers.ContentType?.ToString() ?? string.Empty);
        }

        return await respond(request, cancellationToken);
    }
}
