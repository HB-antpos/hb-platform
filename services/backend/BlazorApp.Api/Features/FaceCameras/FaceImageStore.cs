using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BlazorApp.Api.Models;
using Microsoft.Extensions.Options;

namespace BlazorApp.Api.Features.FaceCameras;

/// <summary>
/// 配置节 <c>FaceCapture:Cos</c>（环境变量 <c>FaceCapture__Cos__*</c>）：后台读取人脸抓拍图片所用的专用私有桶。
/// 桶与区域没有默认值，且绝不回退到主桶（TencentCloud:BucketName）；密钥可单独配置，未配置时回退到主 API 的
/// <c>TencentCloud:SecretId/SecretKey</c>。未配置是合法状态，对应接口返回 503 FACE_IMAGE_STORE_NOT_CONFIGURED。
/// </summary>
public sealed class FaceCaptureCosOptions
{
    public const string SectionName = "FaceCapture:Cos";

    public string? Bucket { get; set; }
    public string? Region { get; set; }
    public string? SecretId { get; set; }
    public string? SecretKey { get; set; }
}

internal enum FaceImageCosConfigProblem
{
    None,
    BucketOrRegionMissing,
    BucketOrRegionInvalid,
    SecretMissing,
    SecretPartial,
    SecretInvalid,
}

/// <summary>已解析的读取凭据。ToString 被覆盖：record 默认会把所有成员（含密钥）打印出来，不能被日志解构带出。</summary>
internal sealed record FaceImageCosCredentials(string Bucket, string Region, string SecretId, string SecretKey)
{
    /// <summary>桶的访问域名；桶与区域经格式校验，只可能落在 *.myqcloud.com 下。</summary>
    public string Host => $"{Bucket}.cos.{Region}.myqcloud.com";

    public override string ToString() => "FaceImageCosCredentials { *** }";
}

internal static partial class FaceImageCosConfiguration
{
    // 桶名 = 名称-APPID，区域形如 ap-singapore；只允许小写字母、数字、横线：域名由它们拼出，不允许夹带点号 / 斜杠 / 端口。
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex BucketPattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,30}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RegionPattern();

    // SecretId 会原样放进 Authorization 请求头：限定字符集，杜绝换行等头注入。
    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SecretIdPattern();

    /// <summary>
    /// 解析读取凭据：桶 / 区域只认 FaceCapture:Cos；专用密钥对（SecretId + SecretKey）必须成对出现，
    /// 两个都为空才回退主 API 的 TencentCloud 密钥；只配了一个视为配置错误（不混用两套凭据）。
    /// </summary>
    public static FaceImageCosCredentials? Resolve(
        FaceCaptureCosOptions cos, TencentCloudSettings main, out FaceImageCosConfigProblem problem)
    {
        problem = FaceImageCosConfigProblem.None;
        var bucket = cos.Bucket?.Trim();
        var region = cos.Region?.Trim();
        if (string.IsNullOrEmpty(bucket) || string.IsNullOrEmpty(region))
        {
            problem = FaceImageCosConfigProblem.BucketOrRegionMissing;
            return null;
        }

        if (!BucketPattern().IsMatch(bucket) || !RegionPattern().IsMatch(region))
        {
            problem = FaceImageCosConfigProblem.BucketOrRegionInvalid;
            return null;
        }

        var secretId = cos.SecretId?.Trim();
        var secretKey = cos.SecretKey?.Trim();
        var hasId = !string.IsNullOrEmpty(secretId);
        var hasKey = !string.IsNullOrEmpty(secretKey);
        if (hasId != hasKey)
        {
            problem = FaceImageCosConfigProblem.SecretPartial;
            return null;
        }

        if (!hasId)
        {
            // 没有专用读取密钥：回退主 API 已有的 TencentCloud 密钥（桶与区域仍然只来自上面的专用配置）。
            secretId = main.SecretId?.Trim();
            secretKey = main.SecretKey?.Trim();
            if (string.IsNullOrEmpty(secretId) || string.IsNullOrEmpty(secretKey))
            {
                problem = FaceImageCosConfigProblem.SecretMissing;
                return null;
            }
        }

        if (!SecretIdPattern().IsMatch(secretId!) || secretKey!.Any(char.IsControl))
        {
            problem = FaceImageCosConfigProblem.SecretInvalid;
            return null;
        }

        return new FaceImageCosCredentials(bucket, region, secretId!, secretKey!);
    }
}

/// <summary>
/// 腾讯云 COS XML API 的最小请求头签名（q-sign-algorithm=sha1，签名放在 Authorization 请求头，GET 读取用）。
/// 算法与 BlazorApp.FaceIngest.CosRequestSigner / TencentCloudUploadService 的 GenerateCosAuthorization 一致
/// （同一 HttpString → StringToSign → SignKey → Signature 流程，header 键小写、值 URL 编码），
/// 只是输出为请求头形式（各段不再 URL 编码）。主 API 的共用上传服务不动，这里独立实现，也不引用 FaceIngest 项目。
/// </summary>
internal static class CosAuthorizationSigner
{
    public static string BuildAuthorization(
        string method,
        string objectKey,
        IReadOnlyDictionary<string, string> signedHeaders,
        string secretId,
        string secretKey,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        var keyTime = $"{start.ToUnixTimeSeconds()};{end.ToUnixTimeSeconds()}";
        var path = "/" + objectKey.TrimStart('/');

        var headers = signedHeaders
            .Select(pair => new
            {
                Key = Encode(pair.Key.Trim().ToLowerInvariant()),
                Value = Encode(pair.Value.Trim()),
            })
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToList();
        var headerList = string.Join(";", headers.Select(pair => pair.Key));
        var httpHeaders = string.Join("&", headers.Select(pair => $"{pair.Key}={pair.Value}"));

        // HttpString：method \n 路径 \n 查询参数（本接口没有，留空） \n 头 \n
        var httpString = $"{method.ToLowerInvariant()}\n{path}\n\n{httpHeaders}\n";
        var stringToSign = $"sha1\n{keyTime}\n{Sha1Hex(httpString)}\n";
        var signKey = HmacSha1Hex(secretKey, keyTime);
        var signature = HmacSha1Hex(signKey, stringToSign);

        return string.Join(
            "&",
            "q-sign-algorithm=sha1",
            $"q-ak={secretId}",
            $"q-sign-time={keyTime}",
            $"q-key-time={keyTime}",
            $"q-header-list={headerList}",
            "q-url-param-list=",
            $"q-signature={signature}");
    }

    private static string Encode(string value) => Uri.EscapeDataString(value);

    private static string HmacSha1Hex(string key, string data)
    {
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(data)));
    }

    private static string Sha1Hex(string input) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(input)));
}

/// <summary>图片读取相关的业务错误：状态码与错误码是前端文案映射的契约，消息不得含桶名、密钥、签名、对象键或完整地址。</summary>
internal static class FaceImageErrors
{
    public static FaceCameraException Forbidden() =>
        new(403, "FACE_IMAGE_FORBIDDEN", "没有权限查看人脸抓拍图片。");

    /// <summary>归属不一致与记录不存在共用同一个错误，不泄露「某个编号是否存在」。</summary>
    public static FaceCameraException NotFound() =>
        new(404, "FACE_IMAGE_NOT_FOUND", "图片不存在。");

    public static FaceCameraException NotInCos() =>
        new(409, "FACE_IMAGE_NOT_IN_COS", "图片仍在接收服务本地，暂不可在后台查看。");

    public static FaceCameraException NotConfigured() =>
        new(503, "FACE_IMAGE_STORE_NOT_CONFIGURED", "图片读取尚未配置。");

    public static FaceCameraException ObjectMissing() =>
        new(404, "FACE_IMAGE_OBJECT_MISSING", "图片文件不存在或已被清理。");

    public static FaceCameraException TooLarge() =>
        new(502, "FACE_IMAGE_TOO_LARGE", "图片超过允许读取的大小上限，已拒绝。");

    public static FaceCameraException Unsupported() =>
        new(415, "FACE_IMAGE_TYPE_UNSUPPORTED", "图片类型不受支持，已拒绝。");

    public static FaceCameraException StoreDenied() =>
        new(502, "FACE_IMAGE_STORE_DENIED", "图片存储拒绝了读取请求，请联系管理员检查读取权限配置。");

    public static FaceCameraException StoreTimeout() =>
        new(502, "FACE_IMAGE_STORE_TIMEOUT", "读取图片超时，请稍后重试。");

    public static FaceCameraException StoreError() =>
        new(502, "FACE_IMAGE_STORE_ERROR", "读取图片失败，请稍后重试。");
}

/// <summary>
/// 从私有 COS 桶读取人脸抓拍图片（只 GET）。设计约束：
/// 1. 对象键只来自数据库，调用方传入前这里仍会再校验形态（只允许 images 目录下的安全字符路径）；
/// 2. 用服务端凭据在 Authorization 请求头里签名，不生成也不外泄任何带签名的 URL；
/// 3. 不跟随重定向；整个读取（含读 body）限时 15 秒；body 超过 maxBytes 立即中止；
/// 4. 所有失败映射成 <see cref="FaceCameraException"/>（固定文案）；日志只记状态码 / COS 错误码，
///    不记对象键、完整地址、桶名、密钥、签名，也不记 HttpRequestException 的消息（里面带主机名）。
/// </summary>
public sealed class CosFaceImageStore(
    IHttpClientFactory httpClientFactory,
    IOptions<FaceCaptureCosOptions> cosOptions,
    IOptions<TencentCloudSettings> mainCosOptions,
    TimeProvider timeProvider,
    ILogger<CosFaceImageStore> logger
) : IFaceImageStore
{
    /// <summary>IHttpClientFactory 里的命名客户端（超时与禁用重定向在 Program.cs 登记）。</summary>
    public const string HttpClientName = "face-capture-cos";

    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);

    // 签名起点回拨 60 秒容忍服务器与 COS 的时钟偏差；有效期 5 分钟，只用于这一次请求。
    private static readonly TimeSpan SignatureBackdate = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SignatureValidity = TimeSpan.FromMinutes(5);
    private const int MaxErrorBodyBytes = 4096;
    private const int MaxObjectKeyLength = 400;

    /// <summary>整个读取（发请求 + 读 body）的总时限；仅测试会改小。</summary>
    internal TimeSpan RequestTimeout { get; init; } = DefaultRequestTimeout;

    public async Task<byte[]> ReadAsync(string objectKey, int maxBytes, CancellationToken cancellationToken = default)
    {
        var credentials = FaceImageCosConfiguration.Resolve(cosOptions.Value, mainCosOptions.Value, out var problem);
        if (credentials == null)
        {
            // 只记配置问题的类别，不记任何配置值。
            logger.LogWarning("人脸抓拍图片读取未配置或配置无效: Problem={Problem}", problem);
            throw FaceImageErrors.NotConfigured();
        }

        if (!IsSafeObjectKey(objectKey))
        {
            logger.LogWarning("人脸抓拍图片对象键形态不合法，已拒绝读取。");
            throw FaceImageErrors.StoreError();
        }

        var host = credentials.Host;
        var now = timeProvider.GetUtcNow();
        var authorization = CosAuthorizationSigner.BuildAuthorization(
            "get",
            objectKey,
            new Dictionary<string, string> { ["host"] = host },
            credentials.SecretId,
            credentials.SecretKey,
            now - SignatureBackdate,
            now + SignatureValidity);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}{BuildUrlPath(objectKey)}");
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        // HttpClient.Timeout 只覆盖到响应头，读 body 可能无限期挂起：用链接令牌给整个读取加总时限。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw await MapFailureAsync(response, timeout.Token);
            }

            return await ReadBodyAsync(response, maxBytes, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 不是调用方取消（页面关闭），而是总时限或 HttpClient.Timeout 到了。
            logger.LogWarning("人脸抓拍图片读取超时。");
            throw FaceImageErrors.StoreTimeout();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // 刻意不记 ex.Message：HttpRequestException 的消息里有主机名（含桶名）。
            logger.LogWarning("人脸抓拍图片读取网络失败: ErrorType={ErrorType}", ex.GetType().Name);
            throw FaceImageErrors.StoreError();
        }
    }

    private async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        var declared = response.Content.Headers.ContentLength;
        if (declared > maxBytes)
        {
            throw FaceImageErrors.TooLarge();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream(declared is > 0 ? (int)declared.Value : 64 * 1024);
        var chunk = new byte[81920];
        int read;
        // 声明的长度可能缺失或作假：按实际读到的字节累计，超过上限立即中止，内存最多多读一个分片。
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw FaceImageErrors.TooLarge();
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
        {
            logger.LogWarning("人脸抓拍图片读取到空内容。");
            throw FaceImageErrors.StoreError();
        }

        return buffer.ToArray();
    }

    /// <summary>非 200：映射业务错误。只读错误体前 4KB 里的 COS 错误码（如 AccessDenied），用于排查，不记别的内容。</summary>
    private async Task<FaceCameraException> MapFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var cosCode = await TryReadCosErrorCodeAsync(response, cancellationToken);
        logger.LogWarning("人脸抓拍图片读取失败: Status={Status}, CosCode={CosCode}", status, cosCode);
        return response.StatusCode switch
        {
            HttpStatusCode.NotFound => FaceImageErrors.ObjectMissing(),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => FaceImageErrors.StoreDenied(),
            _ => FaceImageErrors.StoreError(),
        };
    }

    private static async Task<string> TryReadCosErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[MaxErrorBodyBytes];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken)) > 0)
            {
                total += read;
            }

            var match = Regex.Match(Encoding.UTF8.GetString(buffer, 0, total), @"<Code>([A-Za-z0-9]{1,64})</Code>");
            return match.Success ? match.Groups[1].Value : "-";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            return "-";
        }
    }

    /// <summary>
    /// 对象键形态：与接收服务写入的布局一致 {前缀…}/images/{门店}/{yyyy}/{MM}/{dd}/{摄像机}/{文件}，
    /// 每段只允许安全字符且不能是 . / ..，images 必须恰好在倒数第 7 段。只读 images 目录（不含 raw 原始报文）。
    /// 对象键本身只来自数据库；这里是第二道防线，防止库里被写入越界路径后经本接口读出别处对象。
    /// </summary>
    internal static bool IsSafeObjectKey(string? objectKey)
    {
        if (string.IsNullOrEmpty(objectKey) || objectKey.Length > MaxObjectKeyLength)
        {
            return false;
        }

        var segments = objectKey.Split('/');
        if (segments.Length < 8 || segments[^7] != "images")
        {
            return false;
        }

        return segments.All(segment =>
            segment.Length is > 0 and <= 128
            && segment is not ("." or "..")
            && segment.All(ch => ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' or '.'));
    }

    private static string BuildUrlPath(string objectKey) =>
        "/" + string.Join("/", objectKey.Split('/').Select(Uri.EscapeDataString));
}
