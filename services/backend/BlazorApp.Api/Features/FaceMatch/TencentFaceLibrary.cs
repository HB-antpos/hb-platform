using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlazorApp.Api.Features.FaceMatch;

/// <summary>
/// 腾讯云 API「签名方法 v3」（TC3-HMAC-SHA256）。自实现、零依赖，与 COS 签名同一做法。
/// 只签 content-type 与 host 两个头（官方示例同款），其余 X-TC-* 头不参与签名。
/// 官方示例向量见 FaceMatchTencentSignerTests。
/// </summary>
internal static class TencentCloudV3Signer
{
    public const string Algorithm = "TC3-HMAC-SHA256";
    public const string ContentType = "application/json; charset=utf-8";
    public const string SignedHeaders = "content-type;host";

    /// <summary>规范请求：POST / 空查询串 两个规范头 已签头列表 载荷哈希。</summary>
    public static string BuildCanonicalRequest(string host, string payload)
    {
        return string.Join("\n",
            "POST",
            "/",
            string.Empty,
            $"content-type:{ContentType}\nhost:{host}\n",
            SignedHeaders,
            Sha256Hex(payload));
    }

    public static string BuildStringToSign(long timestamp, string service, string canonicalRequest)
    {
        var date = DateOnlyFromTimestamp(timestamp);
        return string.Join("\n",
            Algorithm,
            timestamp.ToString(CultureInfo.InvariantCulture),
            $"{date}/{service}/tc3_request",
            Sha256Hex(canonicalRequest));
    }

    /// <summary>计算 Authorization 请求头。secretKey 只用于 HMAC，不出现在返回值里。</summary>
    public static string BuildAuthorization(
        string secretId, string secretKey, string service, string host, long timestamp, string payload)
    {
        var date = DateOnlyFromTimestamp(timestamp);
        var canonicalRequest = BuildCanonicalRequest(host, payload);
        var stringToSign = BuildStringToSign(timestamp, service, canonicalRequest);

        var secretDate = Hmac(Encoding.UTF8.GetBytes("TC3" + secretKey), date);
        var secretService = Hmac(secretDate, service);
        var secretSigning = Hmac(secretService, "tc3_request");
        var signature = Convert.ToHexStringLower(Hmac(secretSigning, stringToSign));

        return $"{Algorithm} Credential={secretId}/{date}/{service}/tc3_request, SignedHeaders={SignedHeaders}, Signature={signature}";
    }

    /// <summary>凭据范围里的日期取 UTC（官方示例 1551113065 → 2019-02-25）。</summary>
    internal static string DateOnlyFromTimestamp(long timestamp) =>
        DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static byte[] Hmac(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }
}

/// <summary>腾讯云调用失败的大类（调用方据此决定重试 / 报错 / 当作已完成）。</summary>
public enum FaceLibraryFailureKind
{
    /// <summary>功能关闭（地域或管理凭据缺失 / 非法）：根本没有发请求。</summary>
    Disabled,

    /// <summary>鉴权 / 签名 / 权限类错误：重试无意义，需要管理员检查子账号与策略。</summary>
    Auth,

    /// <summary>请求过于频繁 / 额度不足：稍后重试。</summary>
    RateLimited,

    /// <summary>库 / 人员已存在。</summary>
    AlreadyExists,

    /// <summary>库 / 人员不存在。</summary>
    NotFound,

    /// <summary>照片不可用（解码失败、没有人脸、过小、质量不达标等）。</summary>
    InvalidImage,

    /// <summary>超过库 / 人员 / 人脸数量上限。</summary>
    LimitExceeded,

    /// <summary>超时 / 网络 / 腾讯云服务端错误：可重试。</summary>
    Unavailable,

    Other,
}

/// <summary>腾讯云调用失败。消息固定构造，不含密钥、签名、完整请求体或图片。</summary>
public sealed class FaceLibraryException(FaceLibraryFailureKind kind, string code, string message) : Exception(message)
{
    public FaceLibraryFailureKind Kind { get; } = kind;

    /// <summary>腾讯云错误码（如 AuthFailure.SignatureFailure）或本地标识（Timeout / Network / Disabled）。</summary>
    public string Code { get; } = code;

    public bool IsRetryable => Kind is FaceLibraryFailureKind.RateLimited or FaceLibraryFailureKind.Unavailable;
}

public sealed record FaceLibraryGroup(string GroupId, string? GroupName);

/// <summary>
/// 人脸库管理（员工库、访客库、不受欢迎人员名单库）。腾讯云实现可替换（设计 13.2）；
/// 业务逻辑里不出现任何腾讯云类型。失败一律抛 <see cref="FaceLibraryException"/>。
/// </summary>
public interface IFaceLibrary
{
    /// <summary>功能是否可用（地域 + 管理凭据都有效）。后台任务在不可用时空转。</summary>
    bool IsAvailable { get; }

    /// <summary>创建人员库；已存在视为成功（幂等）。</summary>
    Task EnsureGroupAsync(string groupId, string groupName, CancellationToken cancellationToken = default);

    /// <summary>在库里建人并加入第一张照片，返回 FaceId。PersonName 只放不透明编号，不放姓名。</summary>
    Task<string> CreatePersonAsync(
        string groupId, string personId, string personName, byte[] image, CancellationToken cancellationToken = default);

    /// <summary>给已有人员追加照片（一次最多 4 张），返回成功的 FaceId；成功数少于传入数即部分失败，由调用方回滚。</summary>
    Task<IReadOnlyList<string>> CreateFacesAsync(
        string personId, IReadOnlyList<byte[]> images, CancellationToken cancellationToken = default);

    /// <summary>删除人员（含其所有人脸）；不存在视为成功（幂等）。</summary>
    Task DeletePersonAsync(string personId, CancellationToken cancellationToken = default);

    /// <summary>删除人员库；不存在视为成功（幂等）。</summary>
    Task DeleteGroupAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>库内人员数；库不存在时返回 null。</summary>
    Task<int?> GetPersonCountAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>列出账号下所有人员库（分页取完，最多 5000 个）。</summary>
    Task<IReadOnlyList<FaceLibraryGroup>> ListGroupsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 腾讯云 IAI 的 <see cref="IFaceLibrary"/> 实现（HttpClient + TC3 签名）：
/// 1. 域名 iai.&lt;region&gt;.tencentcloudapi.com，Region 来自配置白名单，缺省 / 非法时整体关闭，绝不回退别的地域；
/// 2. 凭据只认 FaceMatch:Tencent:AdminSecretId / AdminSecretKey，绝不回退 TencentCloud:* 主密钥；
/// 3. 整个调用（含读 body）限时 15 秒，不跟随重定向；
/// 4. 日志只记动作、HTTP 状态、腾讯云错误码与 RequestId，不记密钥、签名、完整请求体、图片或腾讯云错误原文。
/// </summary>
public sealed class TencentFaceLibrary(
    IHttpClientFactory httpClientFactory,
    IFaceMatchSettingsProvider settingsProvider,
    TimeProvider timeProvider,
    ILogger<TencentFaceLibrary> logger
) : IFaceLibrary
{
    /// <summary>IHttpClientFactory 里的命名客户端（禁用重定向在 Program.cs 登记）。</summary>
    public const string HttpClientName = "face-match-tencent";

    public const string Service = "iai";
    public const string ApiVersion = "2020-03-03";
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);

    private const int MaxResponseBytes = 1024 * 1024;
    private const int GroupListPageSize = 100;
    private const int MaxGroupPages = 50;

    /// <summary>整个调用的总时限；仅测试会改小。</summary>
    internal TimeSpan RequestTimeout { get; init; } = DefaultRequestTimeout;

    public bool IsAvailable => settingsProvider.Resolved.Enabled;

    public async Task EnsureGroupAsync(string groupId, string groupName, CancellationToken cancellationToken = default)
    {
        try
        {
            await CallAsync("CreateGroup", new JsonObject
            {
                ["GroupName"] = groupName,
                ["GroupId"] = groupId,
                ["FaceModelVersion"] = "3.0",
            }, cancellationToken);
        }
        catch (FaceLibraryException ex) when (ex.Kind == FaceLibraryFailureKind.AlreadyExists)
        {
            // 幂等：库已存在就是目标状态。
        }
    }

    public async Task<string> CreatePersonAsync(
        string groupId, string personId, string personName, byte[] image, CancellationToken cancellationToken = default)
    {
        var response = await CallAsync("CreatePerson", new JsonObject
        {
            ["GroupId"] = groupId,
            ["PersonName"] = personName,
            ["PersonId"] = personId,
            ["Image"] = Convert.ToBase64String(image),
            ["UniquePersonControl"] = 0,
            ["QualityControl"] = settingsProvider.Options.EnrollmentQualityControl is >= 0 and <= 4
                ? settingsProvider.Options.EnrollmentQualityControl
                : 2,
            ["NeedRotateDetection"] = 0,
        }, cancellationToken);
        return response["FaceId"]?.GetValue<string>() ?? string.Empty;
    }

    public async Task<IReadOnlyList<string>> CreateFacesAsync(
        string personId, IReadOnlyList<byte[]> images, CancellationToken cancellationToken = default)
    {
        var array = new JsonArray();
        foreach (var image in images)
        {
            array.Add(Convert.ToBase64String(image));
        }

        var response = await CallAsync("CreateFace", new JsonObject
        {
            ["PersonId"] = personId,
            ["Images"] = array,
            ["QualityControl"] = settingsProvider.Options.EnrollmentQualityControl is >= 0 and <= 4
                ? settingsProvider.Options.EnrollmentQualityControl
                : 2,
            ["NeedRotateDetection"] = 0,
        }, cancellationToken);

        return response["SucFaceIds"] is JsonArray ids
            ? ids.Select(id => id?.GetValue<string>() ?? string.Empty).Where(id => id.Length > 0).ToList()
            : [];
    }

    public async Task DeletePersonAsync(string personId, CancellationToken cancellationToken = default)
    {
        try
        {
            await CallAsync("DeletePerson", new JsonObject { ["PersonId"] = personId }, cancellationToken);
        }
        catch (FaceLibraryException ex) when (ex.Kind == FaceLibraryFailureKind.NotFound)
        {
            // 幂等：人员已经不在就是目标状态。
        }
    }

    public async Task DeleteGroupAsync(string groupId, CancellationToken cancellationToken = default)
    {
        try
        {
            await CallAsync("DeleteGroup", new JsonObject { ["GroupId"] = groupId }, cancellationToken);
        }
        catch (FaceLibraryException ex) when (ex.Kind == FaceLibraryFailureKind.NotFound)
        {
            // 幂等：库已经不在就是目标状态。
        }
    }

    public async Task<int?> GetPersonCountAsync(string groupId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await CallAsync("GetPersonList", new JsonObject
            {
                ["GroupId"] = groupId,
                ["Offset"] = 0,
                ["Limit"] = 1,
            }, cancellationToken);
            return response["PersonNum"]?.GetValue<int>() ?? 0;
        }
        catch (FaceLibraryException ex) when (ex.Kind == FaceLibraryFailureKind.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<FaceLibraryGroup>> ListGroupsAsync(CancellationToken cancellationToken = default)
    {
        var groups = new List<FaceLibraryGroup>();
        for (var page = 0; page < MaxGroupPages; page++)
        {
            var response = await CallAsync("GetGroupList", new JsonObject
            {
                ["Offset"] = page * GroupListPageSize,
                ["Limit"] = GroupListPageSize,
            }, cancellationToken);
            var infos = response["GroupInfos"] as JsonArray;
            if (infos == null || infos.Count == 0)
            {
                break;
            }

            foreach (var info in infos)
            {
                var id = info?["GroupId"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(id))
                {
                    groups.Add(new FaceLibraryGroup(id, info?["GroupName"]?.GetValue<string>()));
                }
            }

            if (infos.Count < GroupListPageSize)
            {
                break;
            }
        }

        return groups;
    }

    // ---------- 底层调用 ----------

    private async Task<JsonObject> CallAsync(string action, JsonObject body, CancellationToken cancellationToken)
    {
        var settings = settingsProvider.Resolved;
        if (!settings.Enabled)
        {
            throw new FaceLibraryException(FaceLibraryFailureKind.Disabled, "Disabled", "人脸比对功能未开启（地域或管理凭据缺失）。");
        }

        var host = $"iai.{settings.Region}.tencentcloudapi.com";
        var payload = body.ToJsonString();
        var timestamp = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var authorization = TencentCloudV3Signer.BuildAuthorization(
            settings.SecretId!, settings.SecretKey!, Service, host, timestamp, payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/");
        request.Content = new StringContent(payload, Encoding.UTF8);
        // StringContent 默认带 "application/json; charset=utf-8" 之外的写法时与签名不一致：显式设成签名里用的值。
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(TencentCloudV3Signer.ContentType);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation("X-TC-Action", action);
        request.Headers.TryAddWithoutValidation("X-TC-Version", ApiVersion);
        request.Headers.TryAddWithoutValidation("X-TC-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-TC-Region", settings.Region);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var text = await ReadLimitedAsync(response, timeout.Token);
            return ParseResponse(action, response.StatusCode, text);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("腾讯云人脸库调用超时: Action={Action}", action);
            throw new FaceLibraryException(FaceLibraryFailureKind.Unavailable, "Timeout", "调用腾讯云人脸库超时。");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // 刻意不记 ex.Message：HttpRequestException 的消息里有主机名。
            logger.LogWarning("腾讯云人脸库调用网络失败: Action={Action}, ErrorType={ErrorType}", action, ex.GetType().Name);
            throw new FaceLibraryException(FaceLibraryFailureKind.Unavailable, "Network", "调用腾讯云人脸库网络失败。");
        }
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private JsonObject ParseResponse(string action, HttpStatusCode status, string text)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            root = null;
        }

        var inner = root?["Response"] as JsonObject;
        if (inner == null)
        {
            logger.LogWarning("腾讯云人脸库响应无法解析: Action={Action}, Status={Status}", action, (int)status);
            throw new FaceLibraryException(
                status is >= HttpStatusCode.InternalServerError ? FaceLibraryFailureKind.Unavailable : FaceLibraryFailureKind.Other,
                $"Http{(int)status}",
                "腾讯云人脸库返回了无法识别的响应。");
        }

        if (inner["Error"] is JsonObject error)
        {
            var code = error["Code"]?.GetValue<string>() ?? "Unknown";
            var requestId = inner["RequestId"]?.GetValue<string>() ?? "-";
            // 错误原文（Message）可能带库 / 人员编号，不进日志；只记错误码与 RequestId 供向腾讯云反查。
            logger.LogWarning("腾讯云人脸库调用失败: Action={Action}, Code={Code}, RequestId={RequestId}", action, code, requestId);
            throw new FaceLibraryException(MapKind(code), code, $"腾讯云人脸库返回错误：{code}。");
        }

        return inner;
    }

    /// <summary>错误码分类（按腾讯云 IAI 错误码文档的前缀 / 全名）。</summary>
    internal static FaceLibraryFailureKind MapKind(string code)
    {
        // 鉴权 / 权限 / 账号与计费状态：重试无意义。ResourceUnavailable.NotExist 是「计费状态未知，请确认已开通服务」
        // （2026-10-13 探测新加坡地域时的报错），Delivering（开通中）例外，按可重试处理。
        if (code.StartsWith("AuthFailure", StringComparison.Ordinal)
            || code.StartsWith("UnauthorizedOperation", StringComparison.Ordinal)
            || code.StartsWith("UnsupportedRegion", StringComparison.Ordinal)
            || code.StartsWith("IpNotInWhitelist", StringComparison.Ordinal)
            || code.StartsWith("IpInBlacklist", StringComparison.Ordinal)
            || code.StartsWith("ResourcesSoldOut", StringComparison.Ordinal)
            || (code.StartsWith("ResourceUnavailable", StringComparison.Ordinal)
                && !code.StartsWith("ResourceUnavailable.Delivering", StringComparison.Ordinal)))
        {
            return FaceLibraryFailureKind.Auth;
        }

        // 频率限制与同一人员的并发 / 冲突操作：稍后重试。
        if (code.StartsWith("RequestLimitExceeded", StringComparison.Ordinal)
            || code == "FailedOperation.RequestLimitExceeded"
            || code == "FailedOperation.ConflictOperation"
            || code == "FailedOperation.CreateFaceConcurrent")
        {
            return FaceLibraryFailureKind.RateLimited;
        }

        if (code.EndsWith("AlreadyExist", StringComparison.Ordinal)
            || code.EndsWith("AlreadyExists", StringComparison.Ordinal)
            || code == "InvalidParameterValue.PersonExistInGroup"
            || code == "FailedOperation.GroupPersonMapExist")
        {
            return FaceLibraryFailureKind.AlreadyExists;
        }

        if (code.EndsWith("NotExist", StringComparison.Ordinal)
            || code.EndsWith("NotExists", StringComparison.Ordinal)
            || code.StartsWith("ResourceNotFound", StringComparison.Ordinal))
        {
            return FaceLibraryFailureKind.NotFound;
        }

        if (code.StartsWith("LimitExceeded", StringComparison.Ordinal)
            || code.StartsWith("ResourceInsufficient", StringComparison.Ordinal)
            || code.StartsWith("InvalidParameterValue.AccountFaceNumExceed", StringComparison.Ordinal)
            || code.StartsWith("InvalidParameterValue.GroupFaceNumExceed", StringComparison.Ordinal)
            || code.StartsWith("InvalidParameterValue.GroupNumExceed", StringComparison.Ordinal)
            || code.StartsWith("InvalidParameterValue.PersonFaceNumExceed", StringComparison.Ordinal)
            || code.StartsWith("InvalidParameterValue.UploadFaceNumExceed", StringComparison.Ordinal))
        {
            return FaceLibraryFailureKind.LimitExceeded;
        }

        // 照片本身不可用：没有人脸 / 解码失败 / 分辨率或体积超限 / 质量不达标 / 人脸过小。
        if (code == "InvalidParameterValue.NoFaceInPhoto"
            || code == "FailedOperation.FaceQualityNotQualified"
            || code == "FailedOperation.FaceSizeTooSmall"
            || code.StartsWith("InvalidParameterValue.ImageEmpty", StringComparison.Ordinal)
            || code.StartsWith("FailedOperation.Image", StringComparison.Ordinal))
        {
            return FaceLibraryFailureKind.InvalidImage;
        }

        if (code.StartsWith("InternalError", StringComparison.Ordinal)
            || code.StartsWith("ServiceUnavailable", StringComparison.Ordinal)
            || code.StartsWith("ResourceUnavailable.Delivering", StringComparison.Ordinal)
            || code == "FailedOperation.GroupInDeletedState"
            || code == "FailedOperation.ServerError"
            || code == "FailedOperation.RpcFail"
            || code == "FailedOperation.UnKnowError"
            || code == "FailedOperation.RequestTimeout")
        {
            return FaceLibraryFailureKind.Unavailable;
        }

        return FaceLibraryFailureKind.Other;
    }
}
