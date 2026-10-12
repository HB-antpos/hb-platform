using System.Security.Cryptography;
using System.Text;

namespace BlazorApp.FaceIngest;

/// <summary>
/// 腾讯云 COS XML API 的最小签名实现（q-sign-algorithm=sha1，签名放在 URL 查询串）。
/// 从 BlazorApp.Api/Services/TencentCloudUploadService.cs 原样复制（GenerateCosAuthorization / BuildCos* / CosUrlEncode），
/// 人脸服务不引用主 API 项目；两边签名算法必须保持一致，移植的两条签名测试逐字对照主 API 的期望值。
/// 只做 PUT 预签名：人脸服务的 CAM 子账号只授 PutObject，不需要读 / 删。
/// </summary>
internal sealed class CosRequestSigner
{
    private readonly string _bucket;
    private readonly string _region;
    private readonly string _secretId;
    private readonly string _secretKey;
    private readonly TimeProvider _timeProvider;

    public CosRequestSigner(string bucket, string region, string secretId, string secretKey, TimeProvider timeProvider)
    {
        _bucket = bucket;
        _region = region;
        _secretId = secretId;
        _secretKey = secretKey;
        _timeProvider = timeProvider;
    }

    /// <summary>桶的访问域名 <c>{bucket}.cos.{region}.myqcloud.com</c>。</summary>
    public string Host => $"{_bucket}.cos.{_region}.myqcloud.com";

    /// <summary>
    /// 生成 PUT 预签名地址。<paramref name="signedHeaders"/> 里的头（如 x-cos-acl、x-cos-meta-*）参与签名，
    /// 实际请求必须原样带上这些头，否则 COS 拒绝（这正是「私有 ACL 一定生效」的保证）。
    /// </summary>
    public string GeneratePresignedPutUrl(
        string objectKey,
        string contentType,
        long expiresInSeconds,
        IReadOnlyDictionary<string, string>? signedHeaders = null)
    {
        var host = Host;
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var startTimestamp = new DateTimeOffset(now).ToUnixTimeSeconds();
        var expiredTimestamp = new DateTimeOffset(now.AddSeconds(expiresInSeconds)).ToUnixTimeSeconds();

        var keyTime = $"{startTimestamp};{expiredTimestamp}";
        var path = BuildCosCanonicalPath(objectKey);
        var headersToSign = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["content-type"] = contentType,
            ["host"] = host,
        };
        if (signedHeaders is not null)
        {
            foreach (var header in signedHeaders)
            {
                if (!string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(header.Key)
                    && !string.IsNullOrWhiteSpace(header.Value))
                {
                    headersToSign[header.Key.Trim()] = header.Value.Trim();
                }
            }
        }

        var (headerList, httpHeaders) = BuildCosHeaderSignatureParts(headersToSign);
        var authorization = GenerateCosAuthorization(
            "put",
            path,
            string.Empty,
            httpHeaders,
            keyTime,
            _secretId,
            _secretKey,
            headerList,
            string.Empty);

        return $"https://{host}{BuildCosUrlPath(objectKey)}?{BuildCosAuthorizationQuery(authorization)}";
    }

    private static string GenerateCosAuthorization(
        string method,
        string canonicalPath,
        string canonicalParameters,
        string canonicalHeaders,
        string keyTime,
        string secretId,
        string secretKey,
        string headerList,
        string urlParamList)
    {
        // 腾讯云 COS XML API 签名只支持 sha1；这里和官方 HttpString 格式保持一致。
        var httpString = $"{method.ToLowerInvariant()}\n{canonicalPath}\n{canonicalParameters}\n{canonicalHeaders}\n";
        var stringToSign = $"sha1\n{keyTime}\n{Sha1Hex(httpString)}\n";
        var signKey = HmacSha1(secretKey, keyTime);
        var signature = HmacSha1(signKey, stringToSign);

        return string.Join(
            "&",
            "q-sign-algorithm=sha1",
            $"q-ak={secretId}",
            $"q-sign-time={keyTime}",
            $"q-key-time={keyTime}",
            $"q-header-list={headerList}",
            $"q-url-param-list={urlParamList}",
            $"q-signature={signature}");
    }

    private static string BuildCosAuthorizationQuery(string authorization)
    {
        return string.Join(
            "&",
            authorization
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part =>
                {
                    var index = part.IndexOf('=', StringComparison.Ordinal);
                    if (index < 0)
                    {
                        return CosUrlEncode(part);
                    }

                    var key = part[..index];
                    var value = part[(index + 1)..];
                    return $"{CosUrlEncode(key)}={CosUrlEncode(value)}";
                }));
    }

    private static (string HeaderList, string HttpHeaders) BuildCosHeaderSignatureParts(IReadOnlyDictionary<string, string> headers)
    {
        var normalized = headers
            .Select(pair => new
            {
                Key = CosUrlEncode(pair.Key.Trim().ToLowerInvariant()),
                Value = CosUrlEncode(pair.Value.Trim()),
            })
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToList();

        return (
            string.Join(";", normalized.Select(pair => pair.Key)),
            string.Join("&", normalized.Select(pair => $"{pair.Key}={pair.Value}")));
    }

    private static string BuildCosCanonicalPath(string objectKey) => "/" + objectKey.TrimStart('/');

    private static string BuildCosUrlPath(string objectKey)
        => "/" + string.Join("/", objectKey.TrimStart('/').Split('/', StringSplitOptions.None).Select(CosUrlEncode));

    private static string CosUrlEncode(string value) => Uri.EscapeDataString(value);

    private static string HmacSha1(string key, string data)
    {
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    }

    private static string Sha1Hex(string input)
        => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
}
