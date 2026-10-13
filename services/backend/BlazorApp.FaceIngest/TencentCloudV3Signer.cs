using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BlazorApp.FaceIngest;

/// <summary>
/// 腾讯云 API 3.0「签名方法 v3」（TC3-HMAC-SHA256）的最小实现，不引入官方 SDK（设计第 13.2 节）。
/// 规范来源：https://cloud.tencent.com/document/api/213/30654 （签名方法 v3）。
/// 固定参与签名的头：content-type（application/json; charset=utf-8）、host、x-tc-action（小写动作名）；
/// X-TC-Timestamp / X-TC-Version / X-TC-Region 随请求发送但不参与签名（官方示例同样如此）。
/// 官方示例向量（cvm.DescribeInstances，时间戳 1551113065）在单元测试里逐项对照，
/// 见 TencentCloudSignerTests。纯函数，线程安全；不记录任何日志。
/// </summary>
internal static class TencentCloudV3Signer
{
    public const string Algorithm = "TC3-HMAC-SHA256";

    public const string ContentType = "application/json; charset=utf-8";

    public const string SignedHeaders = "content-type;host;x-tc-action";

    /// <summary>步骤 1：规范请求串（POST，路径 /，无查询串）。</summary>
    public static string BuildCanonicalRequest(string host, string action, ReadOnlySpan<byte> payload)
    {
        var canonicalHeaders = "content-type:" + ContentType + "\n"
            + "host:" + host + "\n"
            + "x-tc-action:" + action.ToLowerInvariant() + "\n";
        return "POST\n/\n\n" + canonicalHeaders + "\n" + SignedHeaders + "\n" + Sha256Hex(payload);
    }

    /// <summary>步骤 2：待签名字符串。</summary>
    public static string BuildStringToSign(long timestamp, string date, string service, string canonicalRequest)
        => Algorithm + "\n"
            + timestamp.ToString(CultureInfo.InvariantCulture) + "\n"
            + date + "/" + service + "/tc3_request\n"
            + Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest));

    /// <summary>步骤 3：派生密钥并计算签名（小写十六进制）。</summary>
    public static string ComputeSignature(string secretKey, string date, string service, string stringToSign)
    {
        var secretDate = HmacSha256(Encoding.UTF8.GetBytes("TC3" + secretKey), date);
        var secretService = HmacSha256(secretDate, service);
        var secretSigning = HmacSha256(secretService, "tc3_request");
        return Convert.ToHexStringLower(HmacSha256(secretSigning, stringToSign));
    }

    /// <summary>
    /// 生成一次请求要带的全部头。<paramref name="region"/> 为空（全局接口）时不带 X-TC-Region。
    /// </summary>
    public static SignedRequest Sign(
        string secretId,
        string secretKey,
        string service,
        string host,
        string action,
        string version,
        string? region,
        ReadOnlySpan<byte> payload,
        DateTimeOffset utcNow)
    {
        var timestamp = utcNow.ToUnixTimeSeconds();
        // 关键逻辑：日期必须是时间戳对应的 UTC 日期，用本地时区会在跨日附近签名失败。
        var date = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var canonicalRequest = BuildCanonicalRequest(host, action, payload);
        var stringToSign = BuildStringToSign(timestamp, date, service, canonicalRequest);
        var signature = ComputeSignature(secretKey, date, service, stringToSign);
        var authorization = Algorithm
            + " Credential=" + secretId + "/" + date + "/" + service + "/tc3_request"
            + ", SignedHeaders=" + SignedHeaders
            + ", Signature=" + signature;
        return new SignedRequest(authorization, timestamp.ToString(CultureInfo.InvariantCulture), action, version, region);
    }

    private static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static byte[] HmacSha256(byte[] key, string message) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message));
}

/// <summary>签名结果：Authorization 头与随请求发送的 X-TC-* 头取值。<see cref="ToString"/> 不输出签名。</summary>
internal sealed record SignedRequest(string Authorization, string Timestamp, string Action, string Version, string? Region)
{
    public override string ToString() => $"SignedRequest {{ Action = {Action}, Version = {Version}, Region = {Region} }}";
}
