using System.ComponentModel.DataAnnotations;

namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 文本筛选类型枚举
    /// </summary>
    public enum TextFilterType
    {
        /// <summary>
        /// 等于
        /// </summary>
        equals = 0,
        /// <summary>
        /// 不等于
        /// </summary>
        notEquals = 1,
        /// <summary>
        /// 开头是
        /// </summary>
        startsWith = 2,
        /// <summary>
        /// 结尾是
        /// </summary>
        endsWith = 3,
        /// <summary>
        /// 包含
        /// </summary>
        contains = 4,
        /// <summary>
        /// 不包含
        /// </summary>
        notContains = 5
    }

    /// <summary>
    /// 数字筛选类型枚举
    /// </summary>
    public enum NumberFilterType
    {
        /// <summary>
        /// 等于
        /// </summary>
        equals = 0,
        /// <summary>
        /// 不等于
        /// </summary>
        notEquals = 1,
        /// <summary>
        /// 大于
        /// </summary>
        greaterThan = 2,
        /// <summary>
        /// 大于等于
        /// </summary>
        greaterThanOrEqual = 3,
        /// <summary>
        /// 小于
        /// </summary>
        lessThan = 4,
        /// <summary>
        /// 小于等于
        /// </summary>
        lessThanOrEqual = 5,
        /// <summary>
        /// 在范围内
        /// </summary>
        between = 6
    }

    /// <summary>
    /// 日期筛选类型枚举
    /// </summary>
    public enum DateFilterType
    {
        /// <summary>
        /// 等于某日
        /// </summary>
        equals = 0,
        /// <summary>
        /// 晚于或等于
        /// </summary>
        greaterThanOrEqual = 3,
        /// <summary>
        /// 早于或等于
        /// </summary>
        lessThanOrEqual = 5,
        /// <summary>
        /// 日期范围
        /// </summary>
        between = 6
    }

    /// <summary>
    /// React专用：商品查询过滤DTO
    /// </summary>
    public class ProductReactFilterDto
    {
        #region 文本字段高级筛选

        /// <summary>
        /// 商品编码筛选值
        /// </summary>
        public string? ProductCode { get; set; }

        /// <summary>
        /// 商品编码筛选类型
        /// </summary>
        public TextFilterType ProductCodeFilterType { get; set; } = TextFilterType.contains;

        /// <summary>
        /// 货号筛选值
        /// </summary>
        public string? ItemNumber { get; set; }

        /// <summary>
        /// 货号筛选类型
        /// </summary>
        public TextFilterType ItemNumberFilterType { get; set; } = TextFilterType.contains;

        /// <summary>
        /// 条码筛选值
        /// </summary>
        public string? Barcode { get; set; }

        /// <summary>
        /// 条码筛选类型
        /// </summary>
        public TextFilterType BarcodeFilterType { get; set; } = TextFilterType.contains;

        /// <summary>
        /// 商品名称筛选值
        /// </summary>
        public string? ProductName { get; set; }

        /// <summary>
        /// 商品名称筛选类型
        /// </summary>
        public TextFilterType ProductNameFilterType { get; set; } = TextFilterType.contains;

        /// <summary>
        /// 更新人筛选值
        /// </summary>
        public string? UpdatedBy { get; set; }

        /// <summary>
        /// 更新人筛选类型
        /// </summary>
        public TextFilterType UpdatedByFilterType { get; set; } = TextFilterType.contains;

        #endregion

        #region 数字字段高级筛选

        /// <summary>
        /// 采购价最小值
        /// </summary>
        public decimal? PurchasePriceMin { get; set; }

        /// <summary>
        /// 采购价最大值
        /// </summary>
        public decimal? PurchasePriceMax { get; set; }

        /// <summary>
        /// 采购价筛选类型
        /// </summary>
        public NumberFilterType PurchasePriceFilterType { get; set; } = NumberFilterType.between;

        /// <summary>
        /// 零售价最小值
        /// </summary>
        public decimal? RetailPriceMin { get; set; }

        /// <summary>
        /// 零售价最大值
        /// </summary>
        public decimal? RetailPriceMax { get; set; }

        /// <summary>
        /// 零售价筛选类型
        /// </summary>
        public NumberFilterType RetailPriceFilterType { get; set; } = NumberFilterType.between;

        /// <summary>
        /// 中包数最小值
        /// </summary>
        public int? MiddlePackageQuantityMin { get; set; }

        /// <summary>
        /// 中包数最大值
        /// </summary>
        public int? MiddlePackageQuantityMax { get; set; }

        /// <summary>
        /// 中包数筛选类型
        /// </summary>
        public NumberFilterType MiddlePackageQuantityFilterType { get; set; } = NumberFilterType.between;

        /// <summary>
        /// 分店记录数量最小值
        /// </summary>
        public int? StoreRecordCountMin { get; set; }

        /// <summary>
        /// 分店记录数量最大值
        /// </summary>
        public int? StoreRecordCountMax { get; set; }

        #endregion

        #region 日期字段高级筛选

        /// <summary>
        /// 创建时间起始（包含）
        /// </summary>
        public DateTime? CreatedAtFrom { get; set; }

        /// <summary>
        /// 创建时间结束（不包含）
        /// </summary>
        public DateTime? CreatedAtToExclusive { get; set; }

        /// <summary>
        /// 创建时间筛选类型
        /// </summary>
        public DateFilterType CreatedAtFilterType { get; set; } = DateFilterType.between;

        /// <summary>
        /// 更新时间起始（包含）
        /// </summary>
        public DateTime? UpdatedAtFrom { get; set; }

        /// <summary>
        /// 更新时间结束（不包含）
        /// </summary>
        public DateTime? UpdatedAtToExclusive { get; set; }

        /// <summary>
        /// 更新时间筛选类型
        /// </summary>
        public DateFilterType UpdatedAtFilterType { get; set; } = DateFilterType.between;

        #endregion

        #region 原有筛选参数
        /// <summary>
        /// 搜索关键词（商品名称、货号、条码）
        /// </summary>
        public string? Search { get; set; }
        
        /// <summary>
        /// 本地供应商代码过滤
        /// </summary>
        public string? LocalSupplierCode { get; set; }

        /// <summary>
        /// 本地供应商代码多选过滤
        /// </summary>
        public List<string>? LocalSupplierCodes { get; set; }

        /// <summary>
        /// 国内供应商代码多选过滤（来自未删除 DomesticProduct 与 ChinaSupplier 的有效映射）
        /// </summary>
        public List<string>? DomesticSupplierCodes { get; set; }

        /// <summary>
        /// 仓库类别GUID多选过滤；有值时优先于单值 WarehouseCategoryGUID
        /// </summary>
        public List<string>? WarehouseCategoryGUIDs { get; set; }
        
        /// <summary>
        /// 是否启用状态过滤
        /// </summary>
        public bool? IsActive { get; set; }

        /// <summary>
        /// 启用状态多选过滤
        /// </summary>
        public List<bool>? IsActiveValues { get; set; }

        /// <summary>
        /// 自动定价多选过滤
        /// </summary>
        public List<bool>? IsAutoPricingValues { get; set; }
        
        /// <summary>
        /// 是否特殊产品过滤
        /// </summary>
        public bool? IsSpecialProduct { get; set; }
        
        /// <summary>
        /// 仓库类别GUID过滤
        /// </summary>
        public string? WarehouseCategoryGUID { get; set; }

        /// <summary>
        /// 商品分类GUID过滤（支持多个）
        /// </summary>
        public List<string>? ProductCategoryGUIDs { get; set; }

        /// <summary>
        /// 供应商分类GUID过滤（含子分类）；可为网站供应商分类，也可为 200 商品对应的仓库分类
        /// </summary>
        public List<string>? SupplierCategoryGUIDs { get; set; }

        /// <summary>
        /// 仅看未归类：200 商品为未设置仓库分类，其他供应商为没有有效供应商分类归属
        /// </summary>
        public bool? SupplierCategoryUnassignedOnly { get; set; }
        public int? ProductType { get; set; }

        /// <summary>
        /// 商品类型多选过滤
        /// </summary>
        public List<int>? ProductTypeValues { get; set; }

        #endregion

        /// <summary>
        /// 页码（从1开始）
        /// </summary>
        public int PageNumber { get; set; } = 1;

        /// <summary>
        /// 每页大小
        /// </summary>
        public int PageSize { get; set; } = 20;

        /// <summary>
        /// 排序字段
        /// </summary>
        public string? SortBy { get; set; }

        /// <summary>
        /// 排序方向：asc 或 desc
        /// </summary>
        public string SortOrder { get; set; } = "asc";
    }

    /// <summary>
    /// React专用：批量更新商品DTO
    /// </summary>
    public class BatchUpdateProductReactDto
    {
        /// <summary>
        /// 产品编码（必须）
        /// </summary>
        [Required(ErrorMessage = "产品编码不能为空")]
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>
        /// 产品名称
        /// </summary>
        public string? ProductName { get; set; }

        /// <summary>
        /// 英文名称
        /// </summary>
        public string? EnglishName { get; set; }

        /// <summary>
        /// 零售价格
        /// </summary>
        public decimal? RetailPrice { get; set; }

        /// <summary>
        /// 采购价格
        /// </summary>
        public decimal? PurchasePrice { get; set; }

        /// <summary>
        /// 是否启用
        /// </summary>
        public bool? IsActive { get; set; }

        /// <summary>
        /// 中包装数量
        /// </summary>
        public int? MiddlePackageQuantity { get; set; }

        /// <summary>
        /// 是否自动定价
        /// </summary>
        public bool? IsAutoPricing { get; set; }

        /// <summary>
        /// 商品分类GUID
        /// </summary>
        public string? ProductCategoryGUID { get; set; }

        /// <summary>
        /// 本地供应商编码
        /// </summary>
        public string? LocalSupplierCode { get; set; }

        /// <summary>
        /// 人工指定供应商分类GUID；为空表示不修改
        /// </summary>
        public string? SupplierCategoryGUID { get; set; }

        /// <summary>
        /// 为 true 时清除人工指定并恢复网站自动归类
        /// </summary>
        public bool? ClearSupplierCategory { get; set; }
    }

    /// <summary>
    /// 商品分店记录批量更新请求
    /// </summary>
    public class BatchUpdateProductStoreRecordsRequest
    {
        /// <summary>
        /// 需要更新的分店代码列表
        /// </summary>
        [Required(ErrorMessage = "至少选择一个分店")]
        public string[] StoreCodes { get; set; } = Array.Empty<string>();

        /// <summary>
        /// 批量修改内容
        /// </summary>
        [Required(ErrorMessage = "修改内容不能为空")]
        public BatchUpdateProductStoreRecordChangesDto Changes { get; set; } = new();
    }

    /// <summary>
    /// 商品分店记录可修改字段
    /// </summary>
    public class BatchUpdateProductStoreRecordChangesDto
    {
        /// <summary>
        /// 采购价
        /// </summary>
        public decimal? PurchasePrice { get; set; }

        /// <summary>
        /// 分店零售价
        /// </summary>
        public decimal? StoreRetailPriceValue { get; set; }

        /// <summary>
        /// 折扣率
        /// </summary>
        public decimal? DiscountRate { get; set; }

        /// <summary>
        /// 是否自动定价
        /// </summary>
        public bool? IsAutoPricing { get; set; }

        /// <summary>
        /// 是否特殊商品
        /// </summary>
        public bool? IsSpecialProduct { get; set; }

        /// <summary>
        /// 是否启用
        /// </summary>
        public bool? IsActive { get; set; }

        /// <summary>
        /// 是否至少提供了一个要修改的字段
        /// </summary>
        public bool HasAnyChanges()
        {
            return PurchasePrice.HasValue
                || StoreRetailPriceValue.HasValue
                || DiscountRate.HasValue
                || IsAutoPricing.HasValue
                || IsSpecialProduct.HasValue
                || IsActive.HasValue;
        }
    }

    /// <summary>
    /// React专用：批量操作结果
    /// </summary>
    public sealed class BatchOperationFailureDto
    {
        public string ItemKey { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        public string? ErrorCode { get; set; }
    }

    public class BatchOperationReactResult
    {
        /// <summary>
        /// 成功数量
        /// </summary>
        public int SuccessCount { get; set; }

        /// <summary>
        /// 失败数量
        /// </summary>
        public int FailedCount { get; set; }

        /// <summary>
        /// 错误信息列表
        /// </summary>
        public List<string> Errors { get; set; } = new();

        /// <summary>
        /// 结构化失败详情；保留 Errors 以兼容旧客户端。
        /// </summary>
        public List<BatchOperationFailureDto> FailureDetails { get; set; } = new();
    }

    public class BatchUpdateSupplierImagesRequest
    {
        public string LocalSupplierCode { get; set; } = string.Empty;
        public string UrlTemplate { get; set; } = string.Empty;
        public bool UpdateHbweb { get; set; }
        public bool UpdateHq { get; set; }
        public bool SaveSupplierImageBaseUrl { get; set; }
    }

    public class BatchUpdateSupplierImagesResult
    {
        public int TotalCount { get; set; }
        public int HbwebUpdatedCount { get; set; }
        public int HbwebSkippedExistingImageCount { get; set; }
        public int HqUpdatedCount { get; set; }
        public int HqSkippedExistingImageCount { get; set; }
        public int SkippedCount { get; set; }
        public int HqFailedCount { get; set; }
        public List<string> Errors { get; set; } = new();
    }

    public static class BatchUpdateSupplierImagesJobStatusConstants
    {
        public const string Queued = "Queued";
        public const string Running = "Running";
        public const string Succeeded = "Succeeded";
        public const string Failed = "Failed";
    }

    public class BatchUpdateSupplierImagesJobRequest : BatchUpdateSupplierImagesRequest
    {
        public string? OperationId { get; set; }
    }

    public class BatchUpdateSupplierImagesJobDto
    {
        public string JobId { get; set; } = string.Empty;
        public string OperationId { get; set; } = string.Empty;
        public string Status { get; set; } = BatchUpdateSupplierImagesJobStatusConstants.Queued;
        public bool IsDuplicateRequest { get; set; }
        public BatchUpdateSupplierImagesRequest Request { get; set; } = new();
        public BatchUpdateSupplierImagesResult? Result { get; set; }
        public string? Message { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
    }

    /// <summary>
    /// React专用：分页列表响应DTO
    /// </summary>
    public class PagedListReactDto<T>
    {
        /// <summary>
        /// 数据列表
        /// </summary>
        public List<T> Items { get; set; } = new();

        /// <summary>
        /// 总记录数
        /// </summary>
        public int Total { get; set; }

        /// <summary>
        /// 当前页码
        /// </summary>
        public int PageNumber { get; set; }

        /// <summary>
        /// 每页大小
        /// </summary>
        public int PageSize { get; set; }
    }
}
