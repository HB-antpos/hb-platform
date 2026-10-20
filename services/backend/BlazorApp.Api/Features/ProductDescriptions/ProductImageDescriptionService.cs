using BlazorApp.Api.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BlazorApp.Api.Features.ProductDescriptions;

/// <summary>
/// 看图生成英文商品名：有图走 Claude 看图，没图或看图失败时按中文名走原文字翻译兜底；
/// 两条路径的结果都经 <see cref="ProductDescriptionText.Normalize"/> 清洗并卡长度。
/// </summary>
public sealed class ProductImageDescriptionService : IProductImageDescriptionService
{
    private const string CacheKeyPrefix = "product-image-description";

    private readonly IProductImageFetcher _imageFetcher;
    private readonly IProductVisionClient _visionClient;
    private readonly ITranslationService _translationService;
    private readonly IMemoryCache _cache;
    private readonly ProductImageDescriptionOptions _options;
    private readonly ILogger<ProductImageDescriptionService> _logger;

    public ProductImageDescriptionService(
        IProductImageFetcher imageFetcher,
        IProductVisionClient visionClient,
        ITranslationService translationService,
        IMemoryCache cache,
        IOptions<ProductImageDescriptionOptions> options,
        ILogger<ProductImageDescriptionService> logger
    )
    {
        _imageFetcher = imageFetcher;
        _visionClient = visionClient;
        _translationService = translationService;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ProductImageDescriptionResponseDto> DescribeAsync(
        IReadOnlyList<ProductImageDescriptionItemDto> items,
        CancellationToken cancellationToken
    )
    {
        var maxLength = Math.Max(10, _options.MaxLength);

        // 同一请求里「同图同名」只调用一次模型：同货号在多行重复出现很常见。
        var groups = items
            .Select(item => new
            {
                Item = item,
                ImageUrl = item.ImageUrl?.Trim() ?? string.Empty,
                ChineseName = item.ChineseName?.Trim() ?? string.Empty,
            })
            .GroupBy(entry => (entry.ImageUrl, entry.ChineseName))
            .ToList();

        var outcomes = new Dictionary<(string ImageUrl, string ChineseName), Outcome>();
        using var gate = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrency));
        var imageTasks = groups
            .Where(group => group.Key.ImageUrl.Length > 0)
            .Select(async group =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    return (group.Key, Outcome: await DescribeFromImageAsync(group.Key.ImageUrl, group.Key.ChineseName, maxLength, cancellationToken));
                }
                finally
                {
                    gate.Release();
                }
            })
            .ToList();

        foreach (var (key, outcome) in await Task.WhenAll(imageTasks))
        {
            outcomes[key] = outcome;
        }

        foreach (var group in groups.Where(group => group.Key.ImageUrl.Length == 0))
        {
            outcomes[group.Key] = Outcome.NeedsText("没有商品图片");
        }

        await FillTextFallbacksAsync(outcomes, maxLength);

        var response = new ProductImageDescriptionResponseDto();
        foreach (var group in groups)
        {
            var outcome = outcomes[group.Key];
            foreach (var entry in group)
            {
                response.Results.Add(new ProductImageDescriptionResultDto
                {
                    Key = entry.Item.Key,
                    Description = outcome.Description,
                    Source = outcome.Source,
                    Reason = outcome.Reason,
                });
            }
        }

        response.ImageCount = response.Results.Count(result => result.Source == ProductImageDescriptionSources.Image);
        response.TextCount = response.Results.Count(result => result.Source == ProductImageDescriptionSources.Text);
        response.FailedCount = response.Results.Count(result => result.Source == ProductImageDescriptionSources.Failed);
        return response;
    }

    private async Task<Outcome> DescribeFromImageAsync(
        string imageUrl,
        string chineseName,
        int maxLength,
        CancellationToken cancellationToken
    )
    {
        if (!_visionClient.IsConfigured)
        {
            return Outcome.NeedsText("看图服务未配置");
        }

        // 缓存键带上模型和长度上限：换模型或调长度后不会读到旧口径的结果。
        var cacheKey = $"{CacheKeyPrefix}:{_options.Anthropic.Model}:{maxLength}:{imageUrl}|{chineseName}";
        if (_cache.TryGetValue(cacheKey, out string? cached) && !string.IsNullOrEmpty(cached))
        {
            return Outcome.Image(cached);
        }

        ProductImagePayload image;
        try
        {
            image = await _imageFetcher.FetchAsync(imageUrl, cancellationToken);
        }
        catch (ProductImageFetchException ex)
        {
            _logger.LogInformation(ex, "商品图片不可用，改用中文名翻译：{ImageUrl}", imageUrl);
            return Outcome.NeedsText(ex.Message);
        }

        var result = await _visionClient.DescribeAsync(image, chineseName, maxLength, cancellationToken);
        var description = ProductDescriptionText.Normalize(result.Text, maxLength);
        if (description == null)
        {
            return Outcome.NeedsText(result.FailureReason ?? "看图结果无效");
        }

        _cache.Set(cacheKey, description, TimeSpan.FromHours(Math.Max(1, _options.CacheHours)));
        return Outcome.Image(description);
    }

    private async Task FillTextFallbacksAsync(
        Dictionary<(string ImageUrl, string ChineseName), Outcome> outcomes,
        int maxLength
    )
    {
        var pending = outcomes.Where(pair => pair.Value.Source == PendingText).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var names = pending
            .Select(pair => pair.Key.ChineseName)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Dictionary<string, string> translations = new(StringComparer.Ordinal);
        string? translationError = null;
        if (names.Count > 0)
        {
            try
            {
                translations = await _translationService.BatchTranslateToEnglishAsync(names);
            }
            catch (Exception ex)
            {
                // 文字翻译未配置或调用失败时整体降级为失败，不影响已看图成功的条目。
                _logger.LogError(ex, "看图兜底的文字翻译失败，共 {Count} 个名称", names.Count);
                translationError = "文字翻译失败";
            }
        }

        foreach (var (key, outcome) in pending)
        {
            if (key.ChineseName.Length == 0)
            {
                outcomes[key] = Outcome.Failed(outcome.Reason == null ? "没有图片也没有中文名" : $"{outcome.Reason}，且没有中文名");
                continue;
            }

            translations.TryGetValue(key.ChineseName, out var translated);
            // 原文字翻译失败时会原样返回中文，Normalize 遇到中文会判无效。
            var description = ProductDescriptionText.Normalize(translated, maxLength);
            // 失败时保留看图阶段的真实原因（如图片 404），再补上文字翻译为什么也没成。
            var textFailure = translationError ?? "文字翻译也没有有效结果";
            outcomes[key] = description == null
                ? Outcome.Failed(string.IsNullOrEmpty(outcome.Reason) ? textFailure : $"{outcome.Reason}；{textFailure}")
                : Outcome.Text(description, outcome.Reason);
        }
    }

    private const string PendingText = "pending-text";

    private sealed record Outcome(string Source, string? Description, string? Reason)
    {
        public static Outcome Image(string description) => new(ProductImageDescriptionSources.Image, description, null);

        public static Outcome Text(string description, string? reason) => new(ProductImageDescriptionSources.Text, description, reason);

        public static Outcome Failed(string reason) => new(ProductImageDescriptionSources.Failed, null, reason);

        public static Outcome NeedsText(string reason) => new(PendingText, null, reason);
    }
}
