using Hbpos.Updater;

namespace Hbpos.Client.Tests;

public sealed class UpdateAgentRequestTests
{
    private const string RequestId = "0123456789abcdef0123456789abcdef";
    private const string TrustedUrl = "https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/wpf-releases/production/1.9.0/Hbpos.Client.Wpf-1.9.0-x64.exe";
    private static readonly string Sha256 = new('a', 64);

    [Fact]
    public void TryCreate_accepts_company_cos_release_package()
    {
        Assert.True(UpdateAgentRequest.TryCreate(RequestId, TrustedUrl, Sha256.ToUpperInvariant(), 1_048_576, out var request, out var error));

        Assert.Equal(string.Empty, error);
        Assert.Equal(TrustedUrl, request!.DownloadUrl.AbsoluteUri);
        Assert.Equal(Sha256, request.Sha256);
        Assert.Equal(RequestId, request.RequestId);
    }

    [Theory]
    // 非 HTTPS、非默认端口、带账号、带查询串或片段
    [InlineData("http://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/wpf-releases/a/setup.exe")]
    [InlineData("https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com:8443/wpf-releases/a/setup.exe")]
    [InlineData("https://user:pw@hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/wpf-releases/a/setup.exe")]
    [InlineData("https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/wpf-releases/a/setup.exe?sign=1")]
    [InlineData("https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/wpf-releases/a/setup.exe#x")]
    // 域名不是公司账号的 COS 存储桶
    [InlineData("https://evil.example.com/wpf-releases/a/setup.exe")]
    [InlineData("https://hb-sales-2019-1300114626.cos.ap-singapore.myqcloud.com/wpf-releases/a/setup.exe")]
    [InlineData("https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com.evil.com/wpf-releases/a/setup.exe")]
    [InlineData("https://evil.com/x.cos.ap-singapore.myqcloud.com/wpf-releases/a/setup.exe")]
    [InlineData("https://hb-sales-2019-1300114625.myqcloud.com/wpf-releases/a/setup.exe")]
    [InlineData("https://hb-sales-2019-1300114625.cos.ap-singapore.example.com/wpf-releases/a/setup.exe")]
    // 路径不在发布目录、路径穿越、不是 exe
    [InlineData("https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/YW200/setup.exe")]
    [InlineData("https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/wpf-releases/../YW200/setup.exe")]
    [InlineData("https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/wpf-releases/%2e%2e/setup.exe")]
    [InlineData("https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/wpf-releases//setup.exe")]
    [InlineData("https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/wpf-releases/a/setup.msi")]
    [InlineData("https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/wpf-releases/a/setup.exe.zip")]
    [InlineData("https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/wpf-releases/a/set%20up.exe")]
    [InlineData("file:///C:/Temp/wpf-releases/setup.exe")]
    [InlineData("\\\\server\\share\\wpf-releases\\setup.exe")]
    [InlineData("C:\\Temp\\setup.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void TryCreate_rejects_untrusted_download_urls(string? downloadUrl)
    {
        Assert.False(UpdateAgentRequest.TryCreate(RequestId, downloadUrl, Sha256, 1024, out var request, out var error));

        Assert.Null(request);
        Assert.NotEqual(string.Empty, error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF-")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef")]
    public void TryCreate_rejects_invalid_request_ids(string? requestId)
    {
        Assert.False(UpdateAgentRequest.TryCreate(requestId, TrustedUrl, Sha256, 1024, out _, out var error));
        Assert.Equal("invalid-request-id", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void TryCreate_rejects_invalid_sha256(string? sha256)
    {
        Assert.False(UpdateAgentRequest.TryCreate(RequestId, TrustedUrl, sha256, 1024, out _, out var error));
        Assert.Equal("invalid-sha256", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(UpdateAgentRequest.MaxInstallerBytes + 1)]
    public void TryCreate_rejects_invalid_file_sizes(long? fileSize)
    {
        Assert.False(UpdateAgentRequest.TryCreate(RequestId, TrustedUrl, Sha256, fileSize, out _, out var error));
        Assert.Equal("invalid-file-size", error);
    }

    [Fact]
    public void Encode_round_trips_through_decode_and_stays_argument_safe()
    {
        Assert.True(UpdateAgentRequest.TryCreate(RequestId, TrustedUrl, Sha256, 1_048_576, out var original, out _));

        var encoded = original!.Encode();

        // 中文注释：编码后作为任务计划的唯一参数传递，必须只含 URL 安全字符，不能带空格、引号或等号。
        Assert.Matches("^[A-Za-z0-9_-]+$", encoded);
        Assert.InRange(encoded.Length, 1, UpdateAgentRequest.MaxEncodedLength);
        Assert.True(UpdateAgentRequest.TryDecode(encoded, out var decoded, out var error));
        Assert.Equal(string.Empty, error);
        Assert.Equal(original, decoded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!not-base64!!!")]
    [InlineData("e30")]
    public void TryDecode_rejects_malformed_payloads(string? encoded)
    {
        Assert.False(UpdateAgentRequest.TryDecode(encoded, out var request, out _));
        Assert.Null(request);
    }

    [Fact]
    public void TryDecode_rejects_oversized_payload_before_parsing()
    {
        Assert.False(UpdateAgentRequest.TryDecode(new string('A', UpdateAgentRequest.MaxEncodedLength + 1), out _, out var error));
        Assert.Equal("invalid-encoding", error);
    }

    [Fact]
    public void TryDecode_revalidates_fields_instead_of_trusting_the_encoding()
    {
        var json = $$"""{"v":1,"id":"{{RequestId}}","url":"https://evil.example.com/wpf-releases/a/setup.exe","sha256":"{{Sha256}}","size":1024}""";
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.False(UpdateAgentRequest.TryDecode(encoded, out _, out var error));
        Assert.Equal("untrusted-url", error);
    }

    [Fact]
    public void TryDecode_rejects_unknown_payload_version()
    {
        var json = $$"""{"v":2,"id":"{{RequestId}}","url":"{{TrustedUrl}}","sha256":"{{Sha256}}","size":1024}""";
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.False(UpdateAgentRequest.TryDecode(encoded, out _, out var error));
        Assert.Equal("unsupported-version", error);
    }
}
