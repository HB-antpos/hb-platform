using System.Collections.Generic;

namespace BlazorApp.Shared.DTOs
{
    public class LocalSupplierDto
    {
        public string? Guid { get; set; }
        public string LocalSupplierCode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Status { get; set; }
        public string? ContactPerson { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string? Remark { get; set; }
        public string? ImageBaseUrl { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }

        // 最近一张分店进货单（未删除、按当前用户分店范围）；仅分页列表填充，其余接口为 null。
        public DateTime? LastPurchaseDate { get; set; }
        public string? LastPurchaseStoreCode { get; set; }
        public string? LastPurchaseStoreName { get; set; }
    }

    public class CreateLocalSupplierDto
    {
        public string LocalSupplierCode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Status { get; set; } = 1;
        public string? ContactPerson { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string? Remark { get; set; }
        public string? ImageBaseUrl { get; set; }
    }

    public class UpdateLocalSupplierDto
    {
        public string Name { get; set; } = string.Empty;
        public int Status { get; set; } = 1;
        public string? ContactPerson { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string? Remark { get; set; }
        public string? ImageBaseUrl { get; set; }
    }

    public class LocalSupplierSyncResultDto
    {
        public int CreatedCount { get; set; }
        public int UpdatedCount { get; set; }
        public int DeactivatedCount { get; set; }
        public int SkippedCount { get; set; }
        public List<string> Errors { get; set; } = new();
    }
}
