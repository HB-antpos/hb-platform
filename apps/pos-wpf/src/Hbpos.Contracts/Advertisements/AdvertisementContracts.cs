namespace Hbpos.Contracts.Advertisements;

public sealed record AdvertisementPlaybackItemDto(
    string Id,
    string Title,
    string? Description,
    string MediaType,
    string MediaUrl,
    string? ThumbnailUrl,
    string ObjectKey,
    string OriginalFileName,
    string ContentType,
    long FileSize,
    DateTimeOffset EffectiveStart,
    DateTimeOffset EffectiveEnd,
    int SortOrder,
    // 以下为客显广告版式（10-09 追加，末尾可选参数保持旧客户端兼容）：
    // Orientation 小写 landscape（横版，只在空闲全屏播）/ portrait（竖版，只在收银右侧播）/ any（通用，两处都播）；
    // MediaWidth / MediaHeight 为素材像素宽高，历史广告为 null。
    string Orientation = "any",
    int? MediaWidth = null,
    int? MediaHeight = null);

public sealed record AdvertisementPlaybackResponse(
    string StoreCode,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<AdvertisementPlaybackItemDto> Items);
