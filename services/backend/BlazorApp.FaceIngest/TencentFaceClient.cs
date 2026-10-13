using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlazorApp.FaceIngest;

/// <summary>
/// 腾讯云人脸识别（IAI，API 版本 2020-03-03）客户端，同时实现 <see cref="IFaceMatcher"/> 与 <see cref="IVisitorRegistry"/>。
/// 业务口径（设计第 5、13.2、15 节）：
/// - 域名 <c>iai.{region}.tencentcloudapi.com</c>，Region 取配置（白名单内），请求头显式带 X-TC-Region；
/// - 用 <see cref="TencentCloudV3Signer"/> 自行签名，不引官方 SDK；凭据只来自 <see cref="FaceMatchSettings"/>（FACE_MATCH_TENCENT_*）；
/// - 超时 10 秒（命名 HttpClient 上设置）；图片 Base64 上传；
/// - SearchFacesReturnsByGroup：MaxFaceNum=1、MaxPersonNumPerGroup=1、QualityControl=2（一般质量，低质量图宁可丢弃也不污染访客库）、
///   NeedRotateDetection=0（海康出的已裁剪人脸是正向的，开启会多几百毫秒）、NeedPersonInfo=0；
/// - 错误码映射见 <see cref="Classify"/>；
/// - 日志与异常里<b>不出现</b>密钥、签名、图片内容、PersonId；异常只带错误码 / 动作 / RequestId。
/// </summary>
internal sealed partial class TencentFaceClient : IFaceMatcher, IVisitorRegistry
{
    /// <summary>IHttpClientFactory 里的命名客户端（超时 10 秒，在 Program 里登记）。</summary>
    public const string HttpClientName = "face-match-tencent";

    public const string Service = "iai";

    public const string ApiVersion = "2020-03-03";

    /// <summary>单次调用超时（设计要求 10 秒）。</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 腾讯云 CreatePerson 不支持按库限定权限（设计第 9 节第 10 点），所以客户端自己守住：
    /// 只允许操作当日访客库 <c>v{店}_{yyyyMMdd}</c>，员工库 / 名单库的写入绝不从接收端发出。
    /// </summary>
    private const string VisitorGroupPattern = @"^v[A-Za-z0-9_-]{1,32}_[0-9]{8}\z";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FaceMatchSettings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly string _host;

    public TencentFaceClient(IHttpClientFactory httpClientFactory, FaceMatchSettings settings, TimeProvider timeProvider)
    {
        if (!settings.MatcherEnabled)
        {
            throw new InvalidOperationException("TencentFaceClient requires a valid FaceMatch configuration.");
        }

        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _timeProvider = timeProvider;
        // 白名单校验已在 FaceMatchSettings 完成，所以主机名只可能是 iai.ap-guangzhou / ap-shanghai / ap-singapore。
        _host = $"{Service}.{settings.Region}.tencentcloudapi.com";
    }

    public async Task<FaceSearchResult> SearchAsync(FaceSearchRequest request, CancellationToken cancellationToken)
    {
        if (request.GroupIds.Count == 0)
        {
            return FaceSearchResult.Empty;
        }

        var payload = new Dictionary<string, object?>
        {
            ["GroupIds"] = request.GroupIds,
            ["Image"] = Convert.ToBase64String(request.Image.Span),
            ["MaxFaceNum"] = 1,
            ["MinFaceSize"] = _settings.MinFaceEdgePixels,
            ["MaxPersonNumPerGroup"] = 1,
            ["NeedPersonInfo"] = 0,
            ["QualityControl"] = 2,
            ["NeedRotateDetection"] = 0,
        };
        if (request.MinScore is double minScore)
        {
            // 腾讯云范围为 [0, 100)；阈值恰为 100 时取 99.99，避免参数被拒。
            payload["FaceMatchThreshold"] = Math.Min(minScore, 99.99);
        }

        const string action = "SearchFacesReturnsByGroup";
        try
        {
            using var document = await SendAsync(action, payload, cancellationToken);
            return ParseSearch(document.RootElement.GetProperty("Response"));
        }
        catch (FaceMatchApiException ex) when (ex.Code.EndsWith("NoFaceInGroups", StringComparison.Ordinal))
        {
            // 中文注释：「指定分组中没有人脸」= 被搜的库是空的（例如当天第一位访客到来前的访客库），不是故障。
            // 腾讯云对「多库里只有部分为空」时是否报错官方文档没写：单库直接视为无命中；
            // 多库时逐库单独搜一次再合并，这样无论对端语义如何，都不会因为空库把员工库的命中一起丢掉。
            if (request.GroupIds.Count == 1)
            {
                return FaceSearchResult.Empty;
            }

            var merged = new List<FaceSearchHit>();
            foreach (var groupId in request.GroupIds)
            {
                var single = await SearchAsync(request with { GroupIds = [groupId] }, cancellationToken);
                if (single.Status == FaceSearchStatus.NoUsableFace)
                {
                    return single;
                }

                merged.AddRange(single.Hits);
            }

            return new FaceSearchResult(FaceSearchStatus.Ok, merged);
        }
    }

    public async Task CreateGroupAsync(string groupId, CancellationToken cancellationToken)
    {
        EnsureVisitorGroup(groupId);
        const string action = "CreateGroup";
        try
        {
            using var _ = await SendAsync(
                action,
                new Dictionary<string, object?>
                {
                    ["GroupName"] = groupId,
                    ["GroupId"] = groupId,
                    ["FaceModelVersion"] = "3.0",
                    ["Tag"] = "daily-visitors",
                },
                cancellationToken);
        }
        catch (FaceMatchApiException ex) when (ex.Code is "InvalidParameterValue.GroupIdAlreadyExist" or "InvalidParameterValue.GroupNameAlreadyExist")
        {
            // 中文注释：库已存在视为成功（幂等）。同一天同一家店只会建一次，进程重启 / 多实例重复建都安全。
        }
    }

    public async Task CreatePersonAsync(string groupId, string personId, ReadOnlyMemory<byte> image, CancellationToken cancellationToken)
    {
        EnsureVisitorGroup(groupId);
        if (!PersonIdPattern().IsMatch(personId))
        {
            throw new ArgumentException("Invalid visitor person id.", nameof(personId));
        }

        const string action = "CreatePerson";
        try
        {
            using var _ = await SendAsync(
                action,
                new Dictionary<string, object?>
                {
                    ["GroupId"] = groupId,
                    // 访客没有任何业务标识：名称就用确定性编号，不写姓名、不写性别、不写描述字段。
                    ["PersonName"] = personId,
                    ["PersonId"] = personId,
                    ["Image"] = Convert.ToBase64String(image.Span),
                    // 去重已经由前面那次搜索完成，这里不让腾讯云再判一次（UniquePersonControl=0）。
                    ["UniquePersonControl"] = 0,
                    ["QualityControl"] = 2,
                    ["NeedRotateDetection"] = 0,
                },
                cancellationToken);
        }
        catch (FaceMatchApiException ex) when (ex.Code == "InvalidParameterValue.PersonIdAlreadyExist")
        {
            // 中文注释：PersonId 由抓拍编号确定性生成，已存在说明是上次重试前已建成功，视为成功，不重复建人。
        }
    }

    /// <summary>
    /// 腾讯云错误码 → 处理类别。按「前缀 / 后缀」匹配而不是穷举：官方错误码表会增删，宁可保守归到 RequestFatal 告警，
    /// 也不要把未知错误当成可无限重试的瞬时错误。
    /// </summary>
    internal static FaceMatchErrorKind Classify(string code)
    {
        // 图片 / 人脸质量类：计低质量，不重试。
        if (code.EndsWith("NoFaceInPhoto", StringComparison.Ordinal)
            || code.EndsWith("ImageFacedetectFailed", StringComparison.Ordinal)
            || code.EndsWith("ImageDecodeFailed", StringComparison.Ordinal)
            || code.EndsWith("ImageResolutionTooSmall", StringComparison.Ordinal)
            || code.EndsWith("ImageResolutionExceed", StringComparison.Ordinal)
            || code.EndsWith("ImageSizeExceed", StringComparison.Ordinal)
            || code.EndsWith("ImageEmpty", StringComparison.Ordinal)
            || code.EndsWith("ImageEmptyError", StringComparison.Ordinal)
            || code.EndsWith("FaceSizeTooSmall", StringComparison.Ordinal)
            || code.EndsWith("FaceQualityNotQualified", StringComparison.Ordinal))
        {
            return FaceMatchErrorKind.Quality;
        }

        // 限频（含 UinLimitExceeded 等子码）。
        if (code.StartsWith("RequestLimitExceeded", StringComparison.Ordinal)
            || code.EndsWith("RequestLimitExceeded", StringComparison.Ordinal))
        {
            return FaceMatchErrorKind.RateLimited;
        }

        // 账号级：认证、欠费、未开通、资源被回收、配额耗尽。告警、不重试、触发熔断。
        if (code.StartsWith("AuthFailure", StringComparison.Ordinal)
            || code.StartsWith("ResourceUnavailable", StringComparison.Ordinal)
            || code.StartsWith("ResourcesSoldOut", StringComparison.Ordinal)
            || code.StartsWith("UnauthorizedOperation", StringComparison.Ordinal)
            || code.StartsWith("LimitExceeded", StringComparison.Ordinal))
        {
            return FaceMatchErrorKind.AccountFatal;
        }

        // 服务端暂时异常：退避重试。
        if (code.StartsWith("InternalError", StringComparison.Ordinal)
            || code is "FailedOperation.ServerError" or "FailedOperation.RpcFail" or "FailedOperation.RequestTimeout"
                or "FailedOperation.UnKnowError" or "FailedOperation.CreateFaceConcurrent" or "FailedOperation.ConflictOperation")
        {
            return FaceMatchErrorKind.Transient;
        }

        return FaceMatchErrorKind.RequestFatal;
    }

    private static FaceSearchResult ParseSearch(JsonElement response)
    {
        if (!response.TryGetProperty("ResultsReturnsByGroup", out var results)
            || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() == 0)
        {
            // 没有返回任何人脸结果 = 没检测到人脸。
            return FaceSearchResult.NoUsableFace;
        }

        var face = results[0];
        // RetCode：0 正常；-1601 不符合图片质量控制要求（此时候选为空）。非 0 一律按「不可用」处理。
        if (face.TryGetProperty("RetCode", out var retCode)
            && retCode.ValueKind == JsonValueKind.Number
            && retCode.TryGetInt32(out var ret)
            && ret != 0)
        {
            return FaceSearchResult.NoUsableFace;
        }

        var hits = new List<FaceSearchHit>();
        if (face.TryGetProperty("GroupCandidates", out var groups) && groups.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in groups.EnumerateArray())
            {
                var groupId = group.TryGetProperty("GroupId", out var groupIdElement) ? groupIdElement.GetString() : null;
                if (groupId is null || !group.TryGetProperty("Candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var candidate in candidates.EnumerateArray())
                {
                    if (!candidate.TryGetProperty("Score", out var score) || score.ValueKind != JsonValueKind.Number)
                    {
                        continue;
                    }

                    var personId = candidate.TryGetProperty("PersonId", out var personIdElement) ? personIdElement.GetString() : null;
                    hits.Add(new FaceSearchHit(groupId, personId ?? string.Empty, score.GetDouble()));
                }
            }
        }

        return new FaceSearchResult(FaceSearchStatus.Ok, hits);
    }

    private async Task<JsonDocument> SendAsync(string action, IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        var signed = TencentCloudV3Signer.Sign(
            _settings.SecretId,
            _settings.SecretKey,
            Service,
            _host,
            action,
            ApiVersion,
            _settings.Region,
            body,
            _timeProvider.GetUtcNow());

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://" + _host + "/");
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(TencentCloudV3Signer.ContentType);
        request.Headers.TryAddWithoutValidation("Authorization", signed.Authorization);
        request.Headers.TryAddWithoutValidation("X-TC-Action", signed.Action);
        request.Headers.TryAddWithoutValidation("X-TC-Version", signed.Version);
        request.Headers.TryAddWithoutValidation("X-TC-Timestamp", signed.Timestamp);
        request.Headers.TryAddWithoutValidation("X-TC-Region", signed.Region);

        string text;
        int statusCode;
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, cancellationToken);
            statusCode = (int)response.StatusCode;
            text = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // 中文注释：超时（HttpClient.Timeout 触发的 TaskCanceledException）与网络错误都按瞬时错误重试；
            // 内部异常只保留类型，不带 URL 之外的任何东西（请求头里有签名，异常消息不含头）。
            throw new FaceMatchApiException(FaceMatchErrorKind.Transient, "Network." + ex.GetType().Name, action);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new FaceMatchApiException(
                statusCode >= 500 || statusCode == 429 ? FaceMatchErrorKind.Transient : FaceMatchErrorKind.RequestFatal,
                "Http." + statusCode.ToString(CultureInfo.InvariantCulture),
                action);
        }

        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("Response", out var responseElement)
            && responseElement.ValueKind == JsonValueKind.Object)
        {
            if (responseElement.TryGetProperty("Error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var code = error.TryGetProperty("Code", out var codeElement) ? codeElement.GetString() ?? "Unknown" : "Unknown";
                var requestId = responseElement.TryGetProperty("RequestId", out var requestIdElement) ? requestIdElement.GetString() : null;
                document.Dispose();
                // 中文注释：腾讯云的 Message 是自由文本（可能含库 ID 等），不进异常、不进日志，只留错误码。
                throw new FaceMatchApiException(SanitizeCodeKind(code), SanitizeCode(code), action, requestId);
            }

            return document;
        }

        document.Dispose();
        throw new FaceMatchApiException(
            statusCode >= 500 ? FaceMatchErrorKind.Transient : FaceMatchErrorKind.RequestFatal,
            "Http." + statusCode.ToString(CultureInfo.InvariantCulture),
            action);
    }

    private static FaceMatchErrorKind SanitizeCodeKind(string code) => Classify(SanitizeCode(code));

    /// <summary>错误码只允许字母数字与 . _，其它字符一律换掉，防止对端返回的字符串污染日志。</summary>
    private static string SanitizeCode(string code)
    {
        if (code.Length is 0 or > 96)
        {
            return "Unknown";
        }

        var builder = new StringBuilder(code.Length);
        foreach (var ch in code)
        {
            builder.Append(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' ? ch : '?');
        }

        return builder.ToString();
    }

    private static void EnsureVisitorGroup(string groupId)
    {
        if (!VisitorGroupRegex().IsMatch(groupId))
        {
            throw new ArgumentException("The receiver may only operate on daily visitor groups.", nameof(groupId));
        }
    }

    [GeneratedRegex(VisitorGroupPattern, RegexOptions.CultureInvariant)]
    private static partial Regex VisitorGroupRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PersonIdPattern();
}

/// <summary>
/// 匹配未启用（地域非法 / 缺凭据 / 配置非法）时注册的占位实现：任何调用都抛异常，
/// 保证「整体关闭」时不可能发出网络请求。正常流程根本不会走到这里（处理器按模式策略已降级）。
/// </summary>
internal sealed class DisabledFaceMatchService : IFaceMatcher, IVisitorRegistry
{
    private static InvalidOperationException Disabled() => new("Face matching is disabled by configuration.");

    public Task<FaceSearchResult> SearchAsync(FaceSearchRequest request, CancellationToken cancellationToken) => throw Disabled();

    public Task CreateGroupAsync(string groupId, CancellationToken cancellationToken) => throw Disabled();

    public Task CreatePersonAsync(string groupId, string personId, ReadOnlyMemory<byte> image, CancellationToken cancellationToken) => throw Disabled();
}
