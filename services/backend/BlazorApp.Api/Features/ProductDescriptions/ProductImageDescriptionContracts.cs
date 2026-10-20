namespace BlazorApp.Api.Features.ProductDescriptions;

/// <summary>看图生成英文名的请求。</summary>
public sealed class ProductImageDescriptionRequestDto
{
    public List<ProductImageDescriptionItemDto> Items { get; set; } = new();
}

/// <summary>单条商品：Key 由前端给出（行 ID 等），结果按 Key 回填。</summary>
public sealed class ProductImageDescriptionItemDto
{
    public string Key { get; set; } = string.Empty;

    public string? ImageUrl { get; set; }

    /// <summary>中文商品名：看图时作参考，没图或看图失败时用于文字翻译兜底。</summary>
    public string? ChineseName { get; set; }
}

public sealed class ProductImageDescriptionResponseDto
{
    public List<ProductImageDescriptionResultDto> Results { get; set; } = new();

    public int ImageCount { get; set; }

    public int TextCount { get; set; }

    public int FailedCount { get; set; }
}

public sealed class ProductImageDescriptionResultDto
{
    public string Key { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>image=看图生成；text=没图或看图失败，按中文名文字翻译；failed=都没成功。</summary>
    public string Source { get; set; } = ProductImageDescriptionSources.Failed;

    /// <summary>没走看图或失败时的原因，便于前端提示与排查。</summary>
    public string? Reason { get; set; }
}

public static class ProductImageDescriptionSources
{
    public const string Image = "image";
    public const string Text = "text";
    public const string Failed = "failed";
}

/// <summary>已校验、已缩放、可直接发给模型的图片。</summary>
public sealed record ProductImagePayload(byte[] Data, string MediaType);

/// <summary>图片下载或解码失败；Message 是给前端看的简短原因。</summary>
public sealed class ProductImageFetchException : Exception
{
    public ProductImageFetchException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

public interface IProductImageFetcher
{
    Task<ProductImagePayload> FetchAsync(string imageUrl, CancellationToken cancellationToken);
}

/// <summary>模型返回：Text 为原始英文名（未清洗），失败时 FailureReason 非空。</summary>
public sealed record ProductVisionResult(string? Text, string? FailureReason);

public interface IProductVisionClient
{
    bool IsConfigured { get; }

    Task<ProductVisionResult> DescribeAsync(
        ProductImagePayload image,
        string? chineseName,
        int maxLength,
        CancellationToken cancellationToken
    );
}

public interface IProductImageDescriptionService
{
    Task<ProductImageDescriptionResponseDto> DescribeAsync(
        IReadOnlyList<ProductImageDescriptionItemDto> items,
        CancellationToken cancellationToken
    );
}
