using System.ComponentModel.DataAnnotations;

namespace BlazorApp.Shared.DTOs;

/// <summary>
/// 供应商分类概览：每个供应商一行。200（Hot Bargain）的 sourceKind 为 warehouse，统计口径为仓库分类。
/// </summary>
public sealed class LocalSupplierCategorySupplierSummaryDto
{
    public string SupplierCode { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public string SourceKind { get; set; } = "website";
    public int CategoryCount { get; set; }
    public int PromotionalCount { get; set; }
    public int ProductCount { get; set; }
    public int AssignedCount { get; set; }
    public int ManualCount { get; set; }
    public int UnassignedCount { get; set; }
    public DateTime? LastCapturedAt { get; set; }
    public DateTime? LastSnapshotAt { get; set; }
}

/// <summary>
/// 供应商分类树节点；200 时由仓库分类映射为同一结构，前端一条渲染路径。
/// </summary>
public sealed class LocalSupplierCategoryNodeDto
{
    public string CategoryGuid { get; set; } = string.Empty;
    public string? ParentGuid { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ExternalKey { get; set; }
    public string FullPath { get; set; } = string.Empty;
    public int Depth { get; set; }
    public bool IsPromotional { get; set; }
    public string PromotionalSource { get; set; } = "pattern";
    public bool IsActive { get; set; } = true;
    public int? SortOrder { get; set; }
    public string? SourceUrl { get; set; }

    /// <summary>直接归到该分类的有效商品数（不含子分类）。</summary>
    public int ProductCount { get; set; }

    public DateTime? LastSeenAt { get; set; }
    public List<LocalSupplierCategoryNodeDto> Children { get; set; } = new();
}

public sealed class LocalSupplierCategoryPromotionalUpdateDto
{
    [Required]
    public bool? IsPromotional { get; set; }
}

public sealed class LocalSupplierCategoryPromotionalResultDto
{
    public int Reassigned { get; set; }
    public int Cleared { get; set; }
}

/// <summary>
/// 每晚重新归类的汇总：逐个供应商按已有采集记录重算，单个供应商失败不影响其他供应商。
/// </summary>
public sealed class LocalSupplierCategoryNightlyResolveResultDto
{
    public int SupplierCount { get; set; }
    public int ProductsScanned { get; set; }
    public int Assigned { get; set; }
    public int Updated { get; set; }
    public int Cleared { get; set; }
    public int ManualSkipped { get; set; }
    public int StaleRemoved { get; set; }
    public List<string> FailedSuppliers { get; set; } = new();
}

public sealed class LocalSupplierCategoryResolveResultDto
{
    public int ProductsScanned { get; set; }
    public int Assigned { get; set; }
    public int Updated { get; set; }
    public int Cleared { get; set; }
    public int Unchanged { get; set; }
    public int ManualSkipped { get; set; }
    public int StaleRemoved { get; set; }
}
