namespace BlazorApp.Shared.DTOs
{
    public class AdvertisementGridRequestDto : GridRequestDto
    {
        public string? Title { get; set; }
        public string? StoreCode { get; set; }
        public string? MediaType { get; set; }
        public bool? IsEnabled { get; set; }
        public DateTime? EffectiveStart { get; set; }
        public DateTime? EffectiveEnd { get; set; }
        public int? PageNumber { get; set; }

        // 版式筛选：Landscape / Portrait / Any，大小写不敏感精确匹配；空 = 不过滤。
        public string? Orientation { get; set; }
    }

    public class AdvertisementListDto
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string MediaType { get; set; } = string.Empty;
        public string MediaUrl { get; set; } = string.Empty;
        public string? ThumbnailUrl { get; set; }
        public string ObjectKey { get; set; } = string.Empty;
        public string OriginalFileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public DateTime EffectiveStart { get; set; }
        public DateTime EffectiveEnd { get; set; }
        public bool IsEnabled { get; set; }
        public int SortOrder { get; set; }

        // 版式（PascalCase：Landscape / Portrait / Any）与素材像素宽高（历史广告为 null）。
        public string Orientation { get; set; } = "Any";
        public int? MediaWidth { get; set; }
        public int? MediaHeight { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? UpdatedBy { get; set; }
        public List<AdvertisementStoreItemDto> Stores { get; set; } = new();
    }

    public class AdvertisementDetailDto : AdvertisementListDto
    {
    }

    public class AdvertisementStoreItemDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string? StoreName { get; set; }
    }

    /// <summary>
    /// 广告后台分店选择器的选项：只暴露编码/名称/品牌，不带地址、ABN、联系方式等分店档案字段。
    /// </summary>
    public class AdvertisementStoreOptionDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string? StoreName { get; set; }
        public string? BrandName { get; set; }
    }

    public class CreateAdvertisementDto
    {
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string MediaType { get; set; } = string.Empty;
        public string MediaUrl { get; set; } = string.Empty;
        public string? ThumbnailUrl { get; set; }
        public string ObjectKey { get; set; } = string.Empty;
        public string OriginalFileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public DateTime EffectiveStart { get; set; }
        public DateTime EffectiveEnd { get; set; }
        public bool IsEnabled { get; set; } = true;
        public int SortOrder { get; set; }

        // 版式：空 = Any；大小写不敏感，服务端规范成 PascalCase，非三种取值返回校验错误。
        public string? Orientation { get; set; }

        // 素材像素宽高：要么都不传，要么都传 1–20000 的正整数。
        public int? MediaWidth { get; set; }
        public int? MediaHeight { get; set; }
        public List<AdvertisementStoreItemDto> Stores { get; set; } = new();
    }

    public class UpdateAdvertisementDto : CreateAdvertisementDto
    {
    }

    public class AdvertisementEnableRequestDto
    {
        public bool IsEnabled { get; set; }
    }

    public class AdvertisementUploadSignatureRequestDto
    {
        public string FileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public long FileSize { get; set; }
    }

    public class AdvertisementUploadSignatureResponseDto
    {
        public string ObjectKey { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string UploadUrl { get; set; } = string.Empty;
        public string MediaUrl { get; set; } = string.Empty;
        public Dictionary<string, string> Headers { get; set; } = new();
    }
}
