using System.Text;
using Xunit;

namespace BlazorApp.FaceIngest.Tests;

/// <summary>
/// 腾讯云 API 3.0「签名方法 v3」。向量来源：腾讯云官方文档《签名方法 v3》
/// https://cloud.tencent.com/document/api/213/30654 的 cvm.DescribeInstances 示例（时间戳 1551113065）。
/// 注意：文档页面把 SecretKey 打码显示成 32 个星号，文中公布的签名
/// 10b1a37a7301a02ca19a647ad722d5e43b4b3cff309d421d85b46093f6ab6c4f 正是以「32 个字符 '*' 作为 SecretKey」算出的
/// （用独立的 Python hmac 实现复算验证过；规范请求串与待签名串的两个 SHA-256 摘要也与文档逐字一致）。
/// </summary>
public sealed class TencentCloudSignerTests
{
    // 官方示例参数（SecretId 在文档里同样被打码为 AKID + 32 个星号）。
    private const string SecretId = "AKID********************************";
    private const string SecretKey = "********************************";
    private const string Host = "cvm.tencentcloudapi.com";
    private const long Timestamp = 1551113065;
    private const string Payload = "{\"Limit\": 1, \"Filters\": [{\"Values\": [\"\\u672a\\u547d\\u540d\"], \"Name\": \"instance-name\"}]}";

    private const string ExpectedPayloadHash = "35e9c5b0e3ae67532d3c9f17ead6c90222632e5b1ff7f6e89887f1398934f064";
    private const string ExpectedCanonicalRequest =
        "POST\n/\n\ncontent-type:application/json; charset=utf-8\nhost:cvm.tencentcloudapi.com\nx-tc-action:describeinstances\n\ncontent-type;host;x-tc-action\n"
        + ExpectedPayloadHash;

    private const string ExpectedStringToSign =
        "TC3-HMAC-SHA256\n1551113065\n2019-02-25/cvm/tc3_request\n7019a55be8395899b900fb5564e4200d984910f34794a27cb3fb7d10ff6a1e84";

    private const string ExpectedSignature = "10b1a37a7301a02ca19a647ad722d5e43b4b3cff309d421d85b46093f6ab6c4f";

    [Fact]
    public void 规范请求串与待签名串逐字等于官方示例()
    {
        var payload = Encoding.UTF8.GetBytes(Payload);

        var canonical = TencentCloudV3Signer.BuildCanonicalRequest(Host, "DescribeInstances", payload);
        var stringToSign = TencentCloudV3Signer.BuildStringToSign(Timestamp, "2019-02-25", "cvm", canonical);

        Assert.Equal(ExpectedCanonicalRequest, canonical);
        Assert.Equal(ExpectedStringToSign, stringToSign);
    }

    [Fact]
    public void 签名等于官方示例()
    {
        var signature = TencentCloudV3Signer.ComputeSignature(SecretKey, "2019-02-25", "cvm", ExpectedStringToSign);

        Assert.Equal(ExpectedSignature, signature);
    }

    [Fact]
    public void Authorization头等于官方示例_且只有签名字段随密钥变化()
    {
        var payload = Encoding.UTF8.GetBytes(Payload);

        var signed = TencentCloudV3Signer.Sign(
            SecretId, SecretKey, "cvm", Host, "DescribeInstances", "2017-03-12", "ap-guangzhou", payload, DateTimeOffset.FromUnixTimeSeconds(Timestamp));

        Assert.Equal(
            "TC3-HMAC-SHA256 Credential=AKID********************************/2019-02-25/cvm/tc3_request, SignedHeaders=content-type;host;x-tc-action, Signature="
            + ExpectedSignature,
            signed.Authorization);
        Assert.Equal("1551113065", signed.Timestamp);
        Assert.Equal("ap-guangzhou", signed.Region);
        // 签名对象的 ToString 不输出签名（防止被误打进日志）。
        Assert.DoesNotContain(ExpectedSignature, signed.ToString(), StringComparison.Ordinal);

        var otherKey = TencentCloudV3Signer.Sign(
            SecretId, "different-secret-key-xxxxxxxxxxxxxxxx", "cvm", Host, "DescribeInstances", "2017-03-12", null, payload, DateTimeOffset.FromUnixTimeSeconds(Timestamp));
        Assert.NotEqual(signed.Authorization, otherKey.Authorization);
        Assert.Null(otherKey.Region);
    }

    [Fact]
    public void 日期取时间戳对应的UTC日期_跨日边界不受本地时区影响()
    {
        // 2019-02-25T23:59:59Z 与 2019-02-26T00:00:00Z 分属两个 UTC 日期。
        var before = TencentCloudV3Signer.Sign(SecretId, SecretKey, "iai", "iai.ap-guangzhou.tencentcloudapi.com", "SearchFacesReturnsByGroup", "2020-03-03", "ap-guangzhou", [], new DateTimeOffset(2019, 2, 25, 23, 59, 59, TimeSpan.Zero));
        var after = TencentCloudV3Signer.Sign(SecretId, SecretKey, "iai", "iai.ap-guangzhou.tencentcloudapi.com", "SearchFacesReturnsByGroup", "2020-03-03", "ap-guangzhou", [], new DateTimeOffset(2019, 2, 26, 0, 0, 0, TimeSpan.Zero));
        // 同一时刻换成 +10:00 表示，日期也必须仍是 UTC 的 2019-02-25。
        var offset = TencentCloudV3Signer.Sign(SecretId, SecretKey, "iai", "iai.ap-guangzhou.tencentcloudapi.com", "SearchFacesReturnsByGroup", "2020-03-03", "ap-guangzhou", [], new DateTimeOffset(2019, 2, 26, 9, 59, 59, TimeSpan.FromHours(10)));

        Assert.Contains("/2019-02-25/iai/tc3_request", before.Authorization, StringComparison.Ordinal);
        Assert.Contains("/2019-02-26/iai/tc3_request", after.Authorization, StringComparison.Ordinal);
        Assert.Equal(before.Authorization, offset.Authorization);
    }
}
