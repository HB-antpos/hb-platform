using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using BlazorApp.Api.Features.FaceCameras;
using BlazorApp.Api.Models;
using Microsoft.Extensions.Options;

namespace BlazorApp.Api.Features.FaceMatch;

/// <summary>对象存储失败的大类（服务层据此映射业务错误码）。</summary>
public enum FaceMatchStoreFailureKind
{
    NotConfigured,
    InvalidKey,
    NotFound,
    TooLarge,
    Denied,
    Timeout,
    Error,
}

/// <summary>对象存储失败；消息固定，不含桶名、密钥、签名、对象键或完整地址。</summary>
public sealed class FaceMatchStoreException(FaceMatchStoreFailureKind kind, string message) : Exception(message)
{
    public FaceMatchStoreFailureKind Kind { get; } = kind;
}

/// <summary>
/// 人脸比对用的私有对象存储（同意书扫描件、员工登记照、名单参考照片、命中图的读取）。
/// 对象键由服务端生成并存库，读 / 删只接受库里取出的键，实现里再校验一次形态。
/// </summary>
public interface IFaceMatchObjectStore
{
    /// <summary>桶 / 区域 / 凭据是否已配置完整（FaceCapture:Cos）。</summary>
    bool IsConfigured { get; }

    Task PutAsync(string objectKey, byte[] content, string contentType, CancellationToken cancellationToken = default);

    Task<byte[]> ReadAsync(string objectKey, int maxBytes, CancellationToken cancellationToken = default);

    /// <summary>删除对象；对象本来就不存在视为成功（幂等）。</summary>
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default);
}

/// <summary>对象键布局与形态校验。键里只有我们生成的编号，不含用户名、门店名等个人信息。</summary>
public static class FaceMatchObjectKeys
{
    public const string Root = "face-match";
    public const string EnrollmentPrefix = "face-match/enrollment";
    public const string ConsentPrefix = "face-match/consent";
    public const string UnwelcomePrefix = "face-match/unwelcome";

    private const int MaxKeyLength = 400;

    private static readonly Regex IdPattern = new(@"^[0-9a-f]{32}\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Enrollment(string enrollmentId, int index, string extension) =>
        $"{EnrollmentPrefix}/{RequireId(enrollmentId)}/{index}.{extension}";

    public static string Consent(string consentId, string extension) =>
        $"{ConsentPrefix}/{RequireId(consentId)}/scan.{extension}";

    public static string Unwelcome(string entryId, int index, string extension) =>
        $"{UnwelcomePrefix}/{RequireId(entryId)}/{index}.{extension}";

    public static bool IsValidId(string? id) => id != null && IdPattern.IsMatch(id);

    private static string RequireId(string id) =>
        IsValidId(id) ? id : throw new ArgumentException("对象键编号必须是 32 位小写十六进制。", nameof(id));

    /// <summary>
    /// 通用形态：至少两段，每段只允许安全字符且不能是 . / ..，总长有上限。
    /// 用于所有对象存储调用（含接收端写入的命中图键，其布局由接收端决定）。
    /// </summary>
    public static bool IsSafeKey(string? objectKey)
    {
        if (string.IsNullOrEmpty(objectKey) || objectKey.Length > MaxKeyLength)
        {
            return false;
        }

        var segments = objectKey.Split('/');
        if (segments.Length < 2)
        {
            return false;
        }

        return segments.All(segment =>
            segment.Length is > 0 and <= 128
            && segment is not ("." or "..")
            && segment.All(ch => ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' or '.'));
    }

    /// <summary>
    /// 命中图键：必须是安全形态，且不能落在管理端自己写入的三个私有前缀下
    /// （防止库里的命中任务被改成指向登记照 / 同意书 / 名单参考照片后经命中图接口读出）。
    /// </summary>
    public static bool IsAllowedHitImageKey(string? objectKey)
    {
        if (!IsSafeKey(objectKey))
        {
            return false;
        }

        return !(objectKey!.StartsWith(EnrollmentPrefix + "/", StringComparison.Ordinal)
            || objectKey.StartsWith(ConsentPrefix + "/", StringComparison.Ordinal)
            || objectKey.StartsWith(UnwelcomePrefix + "/", StringComparison.Ordinal));
    }
}

/// <summary>
/// 腾讯云 COS 的 <see cref="IFaceMatchObjectStore"/> 实现：凭据解析沿用 FaceCapture:Cos（桶 / 区域必填，不回退主桶；
/// 密钥可专用，缺省回退 TencentCloud 密钥，这是 COS 的既有做法，与 IAI 管理凭据无关）。
/// 需要对 face-match/ 前缀有 Put 与 Delete 权限（Get 沿用看图的读取权限）。
/// 请求头签名（不生成任何带签名的 URL）、15 秒总时限、不跟随重定向、body 超限立即中止；
/// 日志只记状态码 / COS 错误码，不记对象键、桶名、密钥、签名。
/// </summary>
public sealed class CosFaceMatchObjectStore(
    IHttpClientFactory httpClientFactory,
    IOptions<FaceCaptureCosOptions> cosOptions,
    IOptions<TencentCloudSettings> mainCosOptions,
    TimeProvider timeProvider,
    ILogger<CosFaceMatchObjectStore> logger
) : IFaceMatchObjectStore
{
    /// <summary>IHttpClientFactory 里的命名客户端（15 秒总时限、不跟随重定向，在 Program.cs 登记）。</summary>
    public const string HttpClientName = "face-match-cos";

    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SignatureBackdate = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SignatureValidity = TimeSpan.FromMinutes(5);
    private const int MaxErrorBodyBytes = 4096;

    internal TimeSpan RequestTimeout { get; init; } = DefaultRequestTimeout;

    public bool IsConfigured =>
        FaceImageCosConfiguration.Resolve(cosOptions.Value, mainCosOptions.Value, out _) != null;

    public async Task PutAsync(string objectKey, byte[] content, string contentType, CancellationToken cancellationToken = default)
    {
        var credentials = RequireCredentials(objectKey);
        using var request = BuildRequest(
            HttpMethod.Put, credentials, objectKey,
            new Dictionary<string, string> { ["host"] = credentials.Host, ["x-cos-acl"] = "private" });
        request.Headers.TryAddWithoutValidation("x-cos-acl", "private");
        request.Content = new ByteArrayContent(content);
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);

        using var response = await SendAsync(request, "put", cancellationToken);
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.NoContent))
        {
            throw await MapFailureAsync(response, "put", cancellationToken);
        }
    }

    public async Task<byte[]> ReadAsync(string objectKey, int maxBytes, CancellationToken cancellationToken = default)
    {
        var credentials = RequireCredentials(objectKey);
        using var request = BuildRequest(
            HttpMethod.Get, credentials, objectKey,
            new Dictionary<string, string> { ["host"] = credentials.Host });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw await MapFailureAsync(response, "get", timeout.Token);
            }

            var declared = response.Content.Headers.ContentLength;
            if (declared > maxBytes)
            {
                throw new FaceMatchStoreException(FaceMatchStoreFailureKind.TooLarge, "对象超过允许读取的大小上限。");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream(declared is > 0 ? (int)declared.Value : 64 * 1024);
            var chunk = new byte[81920];
            int read;
            // 声明的长度可能缺失或作假：按实际读到的字节累计，超过上限立即中止。
            while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    throw new FaceMatchStoreException(FaceMatchStoreFailureKind.TooLarge, "对象超过允许读取的大小上限。");
                }

                buffer.Write(chunk, 0, read);
            }

            if (buffer.Length == 0)
            {
                throw new FaceMatchStoreException(FaceMatchStoreFailureKind.Error, "读取到空对象。");
            }

            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("人脸比对对象读取超时。");
            throw new FaceMatchStoreException(FaceMatchStoreFailureKind.Timeout, "读取对象超时。");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            logger.LogWarning("人脸比对对象读取网络失败: ErrorType={ErrorType}", ex.GetType().Name);
            throw new FaceMatchStoreException(FaceMatchStoreFailureKind.Error, "读取对象网络失败。");
        }
    }

    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var credentials = RequireCredentials(objectKey);
        using var request = BuildRequest(
            HttpMethod.Delete, credentials, objectKey,
            new Dictionary<string, string> { ["host"] = credentials.Host });

        using var response = await SendAsync(request, "delete", cancellationToken);
        // COS 删除对象成功返回 204；对象不存在也返回 204，保险起见把 404 一并视为已删除。
        if (response.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.OK or HttpStatusCode.NotFound))
        {
            throw await MapFailureAsync(response, "delete", cancellationToken);
        }
    }

    private FaceImageCosCredentials RequireCredentials(string objectKey)
    {
        var credentials = FaceImageCosConfiguration.Resolve(cosOptions.Value, mainCosOptions.Value, out var problem);
        if (credentials == null)
        {
            // 只记配置问题的类别，不记任何配置值。
            logger.LogWarning("人脸比对对象存储未配置或配置无效: Problem={Problem}", problem);
            throw new FaceMatchStoreException(FaceMatchStoreFailureKind.NotConfigured, "对象存储尚未配置。");
        }

        if (!FaceMatchObjectKeys.IsSafeKey(objectKey))
        {
            logger.LogWarning("人脸比对对象键形态不合法，已拒绝。");
            throw new FaceMatchStoreException(FaceMatchStoreFailureKind.InvalidKey, "对象键形态不合法。");
        }

        return credentials;
    }

    private HttpRequestMessage BuildRequest(
        HttpMethod method,
        FaceImageCosCredentials credentials,
        string objectKey,
        IReadOnlyDictionary<string, string> signedHeaders)
    {
        var now = timeProvider.GetUtcNow();
        var authorization = CosAuthorizationSigner.BuildAuthorization(
            method.Method.ToLowerInvariant(),
            objectKey,
            signedHeaders,
            credentials.SecretId,
            credentials.SecretKey,
            now - SignatureBackdate,
            now + SignatureValidity);
        var path = "/" + string.Join("/", objectKey.Split('/').Select(Uri.EscapeDataString));
        var request = new HttpRequestMessage(method, $"https://{credentials.Host}{path}");
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string operation, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("人脸比对对象存储调用超时: Operation={Operation}", operation);
            throw new FaceMatchStoreException(FaceMatchStoreFailureKind.Timeout, "对象存储调用超时。");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            logger.LogWarning("人脸比对对象存储调用网络失败: Operation={Operation}, ErrorType={ErrorType}", operation, ex.GetType().Name);
            throw new FaceMatchStoreException(FaceMatchStoreFailureKind.Error, "对象存储调用网络失败。");
        }
    }

    private async Task<FaceMatchStoreException> MapFailureAsync(
        HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var cosCode = await TryReadCosErrorCodeAsync(response, cancellationToken);
        logger.LogWarning("人脸比对对象存储调用失败: Operation={Operation}, Status={Status}, CosCode={CosCode}", operation, status, cosCode);
        return response.StatusCode switch
        {
            HttpStatusCode.NotFound => new FaceMatchStoreException(FaceMatchStoreFailureKind.NotFound, "对象不存在。"),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new FaceMatchStoreException(FaceMatchStoreFailureKind.Denied, "对象存储拒绝了请求，请检查读写权限配置。"),
            _ => new FaceMatchStoreException(FaceMatchStoreFailureKind.Error, "对象存储调用失败。"),
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
}
