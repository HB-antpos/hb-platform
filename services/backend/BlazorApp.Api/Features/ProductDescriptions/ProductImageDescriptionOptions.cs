namespace BlazorApp.Api.Features.ProductDescriptions;

/// <summary>
/// 「看图生成英文商品名」配置（配置节 ProductVision）。
/// 密钥只通过环境变量 ProductVision__Anthropic__ApiKey 注入，不写进 appsettings。
/// </summary>
public sealed class ProductImageDescriptionOptions
{
    public const string SectionName = "ProductVision";

    public ProductVisionAnthropicOptions Anthropic { get; set; } = new();

    /// <summary>英文名最大字符数；超出按单词边界截断（价签、小票都放得下）。</summary>
    public int MaxLength { get; set; } = 60;

    /// <summary>单次请求最多处理的商品条数；前端按此分批串行调用。</summary>
    public int MaxItemsPerRequest { get; set; } = 20;

    /// <summary>同一请求内并发调用 Claude 的条数上限。</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>下载商品图片的总时限（秒）。</summary>
    public int ImageTimeoutSeconds { get; set; } = 10;

    /// <summary>允许下载的原图最大字节数。</summary>
    public int MaxImageBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>发给模型前把长边缩到此像素以内（Claude 建议上限 1568）。</summary>
    public int MaxImageEdge { get; set; } = 1568;

    /// <summary>同图同名结果的缓存时长（小时），避免重复计费。</summary>
    public int CacheHours { get; set; } = 168;
}

public sealed class ProductVisionAnthropicOptions
{
    public string? ApiKey { get; set; }

    public string Model { get; set; } = "claude-sonnet-5-5";
}
