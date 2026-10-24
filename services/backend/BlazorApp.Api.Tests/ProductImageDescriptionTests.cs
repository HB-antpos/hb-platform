using System.Net;
using System.Text;
using System.Text.Json;
using BlazorApp.Api.Features.ProductDescriptions;
using BlazorApp.Api.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace BlazorApp.Api.Tests;

public class ProductImageDescriptionTests
{
    private const string ImageUrl = "https://hbimgoss.hbupplier.com/productimg/HB001.jpg";

    // ---------------- 文字清洗与长度控制 ----------------

    [Theory]
    [InlineData("\"Ceramic Coffee Mug 350ml.\"", "Ceramic Coffee Mug 350ml")]
    [InlineData("  Stainless Steel\nWater Bottle  ", "Stainless Steel Water Bottle")]
    [InlineData("“Kids Plastic Storage Box”", "Kids Plastic Storage Box")]
    public void Normalize_去掉引号换行与结尾标点(string raw, string expected)
    {
        Assert.Equal(expected, ProductDescriptionText.Normalize(raw, 60));
    }

    [Theory]
    [InlineData("陶瓷杯")]
    [InlineData("Ceramic 杯子")]
    [InlineData("12345")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Normalize_含中文或无英文字母_视为无效(string? raw)
    {
        Assert.Null(ProductDescriptionText.Normalize(raw, 60));
    }

    [Fact]
    public void Normalize_超长时按单词边界截断且不超过上限()
    {
        var raw = "Large Foldable Fabric Storage Box With Lid And Handles For Clothes Toys";

        var result = ProductDescriptionText.Normalize(raw, 40);

        Assert.NotNull(result);
        Assert.True(result!.Length <= 40, result);
        Assert.Equal("Large Foldable Fabric Storage Box", result);
    }

    [Fact]
    public void Normalize_截断后去掉末尾悬空的连接词()
    {
        // 27 字符处正好落在 "With" 之后，结果不应以 With 结尾。
        var result = ProductDescriptionText.Normalize("Ceramic Coffee Mug Set With Spoons", 27);

        Assert.Equal("Ceramic Coffee Mug Set", result);
    }

    [Fact]
    public void Normalize_单个超长单词_硬切到上限()
    {
        var result = ProductDescriptionText.Normalize(new string('A', 80), 60);

        Assert.Equal(60, result!.Length);
    }

    // ---------------- 业务编排 ----------------

    [Fact]
    public async Task DescribeAsync_有图_走看图并清洗结果()
    {
        var harness = new ServiceHarness();
        harness.Vision.Setup(v => v.DescribeAsync(It.IsAny<ProductImagePayload>(), "陶瓷杯", 60, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProductVisionResult("\"Ceramic Mug 350ml.\"", null));

        var result = await harness.Service.DescribeAsync(new[] { Item("row-1", ImageUrl, "陶瓷杯") }, CancellationToken.None);

        var row = Assert.Single(result.Results);
        Assert.Equal("Ceramic Mug 350ml", row.Description);
        Assert.Equal(ProductImageDescriptionSources.Image, row.Source);
        Assert.Equal(1, result.ImageCount);
        harness.Translation.Verify(t => t.BatchTranslateToEnglishAsync(It.IsAny<List<string>>()), Times.Never);
    }

    [Fact]
    public async Task DescribeAsync_图片下载失败_退回中文名文字翻译并带原因()
    {
        var harness = new ServiceHarness();
        harness.Fetcher.Setup(f => f.FetchAsync(ImageUrl, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProductImageFetchException("图片下载失败（HTTP 404）"));
        harness.TranslateReturns(new Dictionary<string, string> { ["陶瓷杯"] = "Ceramic Cup" });

        var result = await harness.Service.DescribeAsync(new[] { Item("row-1", ImageUrl, "陶瓷杯") }, CancellationToken.None);

        var row = Assert.Single(result.Results);
        Assert.Equal("Ceramic Cup", row.Description);
        Assert.Equal(ProductImageDescriptionSources.Text, row.Source);
        Assert.Equal("图片下载失败（HTTP 404）", row.Reason);
        harness.Vision.Verify(v => v.DescribeAsync(It.IsAny<ProductImagePayload>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DescribeAsync_看图结果无效_退回文字翻译()
    {
        var harness = new ServiceHarness();
        harness.Vision.Setup(v => v.DescribeAsync(It.IsAny<ProductImagePayload>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProductVisionResult(null, "模型拒绝处理该图片"));
        harness.TranslateReturns(new Dictionary<string, string> { ["陶瓷杯"] = "Ceramic Cup" });

        var result = await harness.Service.DescribeAsync(new[] { Item("row-1", ImageUrl, "陶瓷杯") }, CancellationToken.None);

        var row = Assert.Single(result.Results);
        Assert.Equal(ProductImageDescriptionSources.Text, row.Source);
        Assert.Equal("模型拒绝处理该图片", row.Reason);
    }

    [Fact]
    public async Task DescribeAsync_看图服务未配置_不下载图片直接文字翻译()
    {
        var harness = new ServiceHarness(visionConfigured: false);
        harness.TranslateReturns(new Dictionary<string, string> { ["陶瓷杯"] = "Ceramic Cup" });

        var result = await harness.Service.DescribeAsync(new[] { Item("row-1", ImageUrl, "陶瓷杯") }, CancellationToken.None);

        Assert.Equal(ProductImageDescriptionSources.Text, Assert.Single(result.Results).Source);
        harness.Fetcher.Verify(f => f.FetchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DescribeAsync_没图也没中文名_判失败()
    {
        var harness = new ServiceHarness();

        var result = await harness.Service.DescribeAsync(new[] { Item("row-1", null, null) }, CancellationToken.None);

        var row = Assert.Single(result.Results);
        Assert.Equal(ProductImageDescriptionSources.Failed, row.Source);
        Assert.Null(row.Description);
        Assert.Equal(1, result.FailedCount);
    }

    [Fact]
    public async Task DescribeAsync_文字翻译抛异常_只让兜底条目失败()
    {
        var harness = new ServiceHarness();
        harness.Vision.Setup(v => v.DescribeAsync(It.IsAny<ProductImagePayload>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProductVisionResult("Ceramic Mug", null));
        harness.Translation.Setup(t => t.BatchTranslateToEnglishAsync(It.IsAny<List<string>>()))
            .ThrowsAsync(new InvalidOperationException("DeepSeek ApiKey 未配置"));

        var result = await harness.Service.DescribeAsync(
            new[] { Item("with-image", ImageUrl, "陶瓷杯"), Item("no-image", null, "塑料盒") },
            CancellationToken.None
        );

        Assert.Equal(ProductImageDescriptionSources.Image, result.Results.Single(r => r.Key == "with-image").Source);
        var failed = result.Results.Single(r => r.Key == "no-image");
        Assert.Equal(ProductImageDescriptionSources.Failed, failed.Source);
        Assert.Equal("没有商品图片；文字翻译失败", failed.Reason);
    }

    [Fact]
    public async Task DescribeAsync_文字翻译结果仍含中文_判失败()
    {
        var harness = new ServiceHarness();
        harness.TranslateReturns(new Dictionary<string, string> { ["塑料盒"] = "塑料盒" });

        var result = await harness.Service.DescribeAsync(new[] { Item("row-1", null, "塑料盒") }, CancellationToken.None);

        Assert.Equal(ProductImageDescriptionSources.Failed, Assert.Single(result.Results).Source);
    }

    [Fact]
    public async Task DescribeAsync_同图同名多行_只调用一次模型且每行都回填()
    {
        var harness = new ServiceHarness();
        harness.Vision.Setup(v => v.DescribeAsync(It.IsAny<ProductImagePayload>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProductVisionResult("Ceramic Mug", null));

        var result = await harness.Service.DescribeAsync(
            new[] { Item("a", ImageUrl, "陶瓷杯"), Item("b", ImageUrl, "陶瓷杯") },
            CancellationToken.None
        );

        Assert.Equal(new[] { "a", "b" }, result.Results.Select(r => r.Key).OrderBy(k => k));
        Assert.All(result.Results, r => Assert.Equal("Ceramic Mug", r.Description));
        harness.Vision.Verify(v => v.DescribeAsync(It.IsAny<ProductImagePayload>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DescribeAsync_第二次请求同图同名_命中缓存不再计费()
    {
        var harness = new ServiceHarness();
        harness.Vision.Setup(v => v.DescribeAsync(It.IsAny<ProductImagePayload>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProductVisionResult("Ceramic Mug", null));

        await harness.Service.DescribeAsync(new[] { Item("a", ImageUrl, "陶瓷杯") }, CancellationToken.None);
        var second = await harness.Service.DescribeAsync(new[] { Item("b", ImageUrl, "陶瓷杯") }, CancellationToken.None);

        Assert.Equal("Ceramic Mug", Assert.Single(second.Results).Description);
        harness.Fetcher.Verify(f => f.FetchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.Vision.Verify(v => v.DescribeAsync(It.IsAny<ProductImagePayload>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---------------- Claude 请求体与返回解析 ----------------

    [Fact]
    public async Task ClaudeClient_请求体_用SonnetBetweenTools结构化输出与默认兜底()
    {
        var handler = new CapturingHandler(MessageJson("end_turn", "{\"name\":\"Ceramic Mug 350ml\"}"));
        var client = CreateClaudeClient(handler);

        var result = await client.DescribeAsync(new ProductImagePayload(new byte[] { 1, 2, 3 }, "image/jpeg"), "陶瓷杯", 60, CancellationToken.None);

        Assert.Equal("Ceramic Mug 350ml", result.Text);
        Assert.Null(result.FailureReason);

        using var body = JsonDocument.Parse(handler.Body!);
        var root = body.RootElement;
        Assert.Equal("claude-sonnet-5-5", root.GetProperty("model").GetString());
        Assert.Equal("default", root.GetProperty("fallbacks").GetString());
        Assert.Equal("between_tools", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("low", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("json_schema", root.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString());
        Assert.Contains("60 characters", root.GetProperty("system").GetString());

        var content = root.GetProperty("messages")[0].GetProperty("content");
        var image = content[0];
        Assert.Equal("image", image.GetProperty("type").GetString());
        Assert.Equal("base64", image.GetProperty("source").GetProperty("type").GetString());
        Assert.Equal("image/jpeg", image.GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), image.GetProperty("source").GetProperty("data").GetString());
        Assert.Contains("陶瓷杯", content[1].GetProperty("text").GetString());

        Assert.Contains("server-side-fallback-2026-07-01", handler.BetaHeader);
    }

    [Fact]
    public async Task DescribeAsync_图片404且文字翻译失败_保留图片失败的真因()
    {
        var harness = new ServiceHarness();
        harness.Fetcher.Setup(f => f.FetchAsync(ImageUrl, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProductImageFetchException("图片下载失败（HTTP 404）"));
        harness.Translation.Setup(t => t.BatchTranslateToEnglishAsync(It.IsAny<List<string>>()))
            .ThrowsAsync(new InvalidOperationException("DeepSeek ApiKey 未配置"));

        var result = await harness.Service.DescribeAsync(new[] { Item("row-1", ImageUrl, "陶瓷杯") }, CancellationToken.None);

        Assert.Equal("图片下载失败（HTTP 404）；文字翻译失败", Assert.Single(result.Results).Reason);
    }

    [Theory]
    [InlineData("claude-sonnet-5-5", "between_tools")]
    [InlineData("claude-haiku-5-5", "disabled")]
    public async Task ClaudeClient_思考参数按模型选择(string model, string expectedThinking)
    {
        var handler = new CapturingHandler(MessageJson("end_turn", "{\"name\":\"Ceramic Mug\"}"));
        var options = Microsoft.Extensions.Options.Options.Create(new ProductImageDescriptionOptions
        {
            Anthropic = new ProductVisionAnthropicOptions { ApiKey = "test-key", Model = model },
        });
        var client = new ClaudeProductVisionClient(options, NullLogger<ClaudeProductVisionClient>.Instance, new HttpClient(handler));

        await client.DescribeAsync(new ProductImagePayload(new byte[] { 1 }, "image/jpeg"), null, 60, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(model, body.RootElement.GetProperty("model").GetString());
        Assert.Equal(expectedThinking, body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ClaudeClient_Opus5_5_不传thinking字段()
    {
        // Opus 5.5 的思考关不掉，disabled 会 400；不传 thinking，靠 effort=low 控制深度。
        var handler = new CapturingHandler(MessageJson("end_turn", "{\"name\":\"Ceramic Mug\"}"));
        var options = Microsoft.Extensions.Options.Options.Create(new ProductImageDescriptionOptions
        {
            Anthropic = new ProductVisionAnthropicOptions { ApiKey = "test-key", Model = "claude-opus-5-5" },
        });
        var client = new ClaudeProductVisionClient(options, NullLogger<ClaudeProductVisionClient>.Instance, new HttpClient(handler));

        await client.DescribeAsync(new ProductImagePayload(new byte[] { 1 }, "image/jpeg"), null, 60, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("thinking", out _));
        Assert.Equal("low", body.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
    }

    [Theory]
    [InlineData("refusal", "模型拒绝处理该图片")]
    [InlineData("max_tokens", "模型输出被截断")]
    public async Task ClaudeClient_拒答或截断_返回失败不读内容(string stopReason, string expectedReason)
    {
        var client = CreateClaudeClient(new CapturingHandler(MessageJson(stopReason, "{\"name\":\"Ceramic\"}")));

        var result = await client.DescribeAsync(new ProductImagePayload(new byte[] { 1 }, "image/jpeg"), null, 60, CancellationToken.None);

        Assert.Null(result.Text);
        Assert.Equal(expectedReason, result.FailureReason);
    }

    [Fact]
    public async Task ClaudeClient_网络异常_返回失败而不是抛出()
    {
        var client = CreateClaudeClient(new ThrowingHandler());

        var result = await client.DescribeAsync(new ProductImagePayload(new byte[] { 1 }, "image/jpeg"), null, 60, CancellationToken.None);

        Assert.Null(result.Text);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public async Task ClaudeClient_未配置密钥_不发请求()
    {
        var handler = new CapturingHandler(MessageJson("end_turn", "{}"));
        var client = CreateClaudeClient(handler, apiKey: null);

        var result = await client.DescribeAsync(new ProductImagePayload(new byte[] { 1 }, "image/jpeg"), null, 60, CancellationToken.None);

        Assert.False(client.IsConfigured);
        Assert.Equal("看图服务未配置", result.FailureReason);
        Assert.Null(handler.Body);
    }

    // ---------------- 图片下载：内网防护与缩放 ----------------

    [Theory]
    [InlineData("10.0.0.8", false)]
    [InlineData("172.20.1.1", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("43.159.37.55", true)]
    public void IsPublicAddress_拒绝内网与保留地址(string address, bool expected)
    {
        Assert.Equal(expected, ProductImageFetcher.IsPublicAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task FetchAsync_大图缩到长边上限并统一转JPEG()
    {
        using var source = new Image<Rgba32>(3000, 1000);
        using var png = new MemoryStream();
        await source.SaveAsPngAsync(png);
        var fetcher = CreateFetcher(new CapturingHandler(png.ToArray(), "image/png"));

        var payload = await fetcher.FetchAsync(ImageUrl, CancellationToken.None);

        Assert.Equal("image/jpeg", payload.MediaType);
        var info = Image.Identify(payload.Data);
        Assert.Equal(1568, info.Width);
        Assert.True(info.Height <= 1568);
    }

    [Fact]
    public async Task FetchAsync_不是图片_抛出可读原因()
    {
        var fetcher = CreateFetcher(new CapturingHandler(Encoding.UTF8.GetBytes("<html></html>"), "text/html"));

        var ex = await Assert.ThrowsAsync<ProductImageFetchException>(() => fetcher.FetchAsync(ImageUrl, CancellationToken.None));

        Assert.Equal("不是可识别的图片", ex.Message);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    public async Task FetchAsync_非http地址_直接拒绝(string url)
    {
        var fetcher = CreateFetcher(new CapturingHandler(Array.Empty<byte>(), "image/png"));

        var ex = await Assert.ThrowsAsync<ProductImageFetchException>(() => fetcher.FetchAsync(url, CancellationToken.None));

        Assert.Equal("图片地址无效", ex.Message);
    }

    // ---------------- 测试辅助 ----------------

    private static ProductImageDescriptionItemDto Item(string key, string? imageUrl, string? chineseName) =>
        new() { Key = key, ImageUrl = imageUrl, ChineseName = chineseName };

    private static IOptions<ProductImageDescriptionOptions> Options(string? apiKey = "test-key") =>
        Microsoft.Extensions.Options.Options.Create(new ProductImageDescriptionOptions
        {
            Anthropic = new ProductVisionAnthropicOptions { ApiKey = apiKey },
        });

    private static ClaudeProductVisionClient CreateClaudeClient(HttpMessageHandler handler, string? apiKey = "test-key") =>
        new(Options(apiKey), NullLogger<ClaudeProductVisionClient>.Instance, new HttpClient(handler));

    private static ProductImageFetcher CreateFetcher(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(ProductImageFetcher.HttpClientName)).Returns(() => new HttpClient(handler, disposeHandler: false));
        return new ProductImageFetcher(factory.Object, Options());
    }

    private static string MessageJson(string stopReason, string text) =>
        JsonSerializer.Serialize(new
        {
            id = "msg_test",
            type = "message",
            role = "assistant",
            model = "claude-sonnet-5-5",
            content = new[] { new { type = "text", text } },
            stop_reason = stopReason,
            stop_sequence = (string?)null,
            stop_details = stopReason == "refusal" ? new { type = "refusal", category = (string?)null, explanation = (string?)null } : null,
            usage = new { input_tokens = 10, output_tokens = 5 },
        });

    private sealed class ServiceHarness
    {
        public Mock<IProductImageFetcher> Fetcher { get; } = new();
        public Mock<IProductVisionClient> Vision { get; } = new();
        public Mock<ITranslationService> Translation { get; } = new();
        public ProductImageDescriptionService Service { get; }

        public ServiceHarness(bool visionConfigured = true)
        {
            Vision.SetupGet(v => v.IsConfigured).Returns(visionConfigured);
            Fetcher.Setup(f => f.FetchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProductImagePayload(new byte[] { 1 }, "image/jpeg"));
            Service = new ProductImageDescriptionService(
                Fetcher.Object,
                Vision.Object,
                Translation.Object,
                new MemoryCache(new MemoryCacheOptions()),
                Options(),
                NullLogger<ProductImageDescriptionService>.Instance
            );
        }

        public void TranslateReturns(Dictionary<string, string> translations) =>
            Translation.Setup(t => t.BatchTranslateToEnglishAsync(It.IsAny<List<string>>())).ReturnsAsync(translations);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection reset");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly byte[] _responseBody;
        private readonly string _contentType;

        public CapturingHandler(string json)
            : this(Encoding.UTF8.GetBytes(json), "application/json") { }

        public CapturingHandler(byte[] responseBody, string contentType)
        {
            _responseBody = responseBody;
            _contentType = contentType;
        }

        public string? Body { get; private set; }

        public string BetaHeader { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content != null)
            {
                Body = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            if (request.Headers.TryGetValues("anthropic-beta", out var values))
            {
                BetaHeader = string.Join(",", values);
            }

            var content = new ByteArrayContent(_responseBody);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_contentType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}
