using System.ComponentModel.DataAnnotations;

namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 国内商品DTO
    /// </summary>
    public class DomesticProductDto
    {
        /// <summary>
        /// 商品编码
        /// </summary>
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>
        /// 供应商编码
        /// </summary>
        public string? SupplierCode { get; set; }

        /// <summary>
        /// 供应商名称
        /// </summary>
        public string? SupplierName { get; set; }

        /// <summary>
        /// 商品名称
        /// </summary>
        public string? ProductName { get; set; }

        /// <summary>
        /// 商品英文名称
        /// </summary>
        public string? EnglishProductName { get; set; }

        /// <summary>
        /// HB货号
        /// </summary>
        public string? HBProductNo { get; set; }

        /// <summary>
        /// 条形码
        /// </summary>
        public string? Barcode { get; set; }

        /// <summary>
        /// 商品规格
        /// </summary>
        public string? ProductSpecification { get; set; }

        /// <summary>
        /// 商品类型枚举
        /// 说明：定义商品的分类类型，影响业务处理逻辑
        /// 
        /// 类型定义：
        /// - 0：普通商品（单一SKU商品）
        /// - 1：套装商品（由多个商品组成的套装）
        /// - 2：多码商品（一个商品对应多个条码或规格）
        /// 
        /// 业务影响：
        /// - 普通商品：标准的单品处理流程
        /// - 套装商品：需要关联套装明细表，计算套装数量
        /// - 多码商品：支持多种规格和条码，库存分别管理
        /// 
        /// 默认值：0（普通商品）
        /// </summary>
        public int ProductType { get; set; } = 0;

        /// <summary>
        /// 商品类型名称
        /// </summary>
        public string ProductTypeName => ProductType switch
        {
            0 => "普通商品",
            1 => "套装商品",
            2 => "多码商品",
            _ => "未知"
        };

        /// <summary>
        /// 商品数量信息
        /// - 普通商品：固定为1
        /// - 套装商品：套装中包含的商品数量（排除已删除的商品）
        /// - 多码商品：商品的条码或规格数量
        /// </summary>
        public int SetQuantity => SetProducts?.Count(sp => !sp.IsDeleted) ?? 0;


        /// <summary>
        /// 国内价格
        /// </summary>
        public decimal? DomesticPrice { get; set; }

        /// <summary>
        /// 零售价
        /// </summary>
        public decimal? OEMPrice { get; set; }

        /// <summary>
        /// 进口价格
        /// </summary>
        public decimal? ImportPrice { get; set; }

        /// <summary>
        /// 单件装箱数
        /// </summary>
        public int? PackingQuantity { get; set; }

        /// <summary>
        /// 单件体积
        /// </summary>
        public decimal? UnitVolume { get; set; }

        /// <summary>
        /// 中包数量
        /// </summary>
        public int? MiddlePackQuantity { get; set; }

        /// <summary>
        /// 包装尺寸（仅为 DTO 形状兼容：DomesticProduct 实体没有对应列，查询结果恒为 null，不落库）
        /// </summary>
        public string? PackingSize { get; set; }

        /// <summary>
        /// 材质（同上：实体无对应列，恒为 null，不落库）
        /// </summary>
        public string? Material { get; set; }

        /// <summary>
        /// 备注（同上：实体无对应列，恒为 null，不落库）
        /// </summary>
        public string? Remarks { get; set; }

        /// <summary>
        /// 商品图片URL
        /// </summary>
        public string? ProductImage { get; set; }

        /// <summary>
        /// 使用状态
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// 状态名称
        /// </summary>
        public string StatusName => IsActive ? "启用" : "禁用";

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// 更新时间
        /// </summary>
        public DateTime? UpdatedAt { get; set; }

        /// <summary>
        /// 创建人
        /// </summary>
        public string? CreatedBy { get; set; }

        /// <summary>
        /// 更新人
        /// </summary>
        public string? UpdatedBy { get; set; }

        /// <summary>
        /// 套装商品列表（仅用于计算套装数量）
        /// </summary>
        public List<DomesticSetProductDto>? SetProducts { get; set; }
    }

    /// <summary>
    /// 创建国内商品DTO
    /// </summary>
    /// <remarks>
    /// 材质 / 备注 / 包装尺寸 刻意不在此 DTO 中：DomesticProduct 实体没有对应列，
    /// 业务已确认（2026-10-06）不需要落库；请求里带了这些成员也会被忽略。
    /// 装箱数 / 中包数量的校验口径为 ≥ 1（见下方 Range），批量路径由服务内 &lt;= 0 判断保持同一口径。
    /// </remarks>
    public class CreateDomesticProductDto
    {
        /// <summary>
        /// 供应商编码
        /// </summary>
        [Required(ErrorMessage = "供应商编码不能为空")]
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>
        /// 商品名称
        /// </summary>
        [StringLength(200, ErrorMessage = "商品名称长度不能超过200个字符")]
        public string ProductName { get; set; } = string.Empty;

        /// <summary>
        /// 商品英文名称
        /// </summary>
        [StringLength(500, ErrorMessage = "商品英文名称长度不能超过500个字符")]
        public string? EnglishProductName { get; set; }

        /// <summary>
        /// HB货号（可选，如果不提供则自动生成）
        /// </summary>
        [StringLength(50, ErrorMessage = "HB货号长度不能超过50个字符")]
        public string? HBProductNo { get; set; }

        /// <summary>
        /// 条形码（可选，如果不提供则自动生成）
        /// </summary>
        [StringLength(50, ErrorMessage = "条形码长度不能超过50个字符")]
        public string? Barcode { get; set; }

        /// <summary>
        /// 商品规格
        /// </summary>
        [StringLength(100, ErrorMessage = "商品规格长度不能超过100个字符")]
        public string? ProductSpecification { get; set; }

        /// <summary>
        /// 商品类型：0-普通商品，1-套装商品，2-多码商品
        /// </summary>
        [Range(0, 2, ErrorMessage = "商品类型必须在0-2之间")]
        public int ProductType { get; set; } = 0;


        /// <summary>
        /// 国内价格
        /// </summary>
        [Range(0, double.MaxValue, ErrorMessage = "国内价格不能为负数")]
        public decimal? DomesticPrice { get; set; }

        /// <summary>
        /// 零售价
        /// </summary>
        [Range(0, double.MaxValue, ErrorMessage = "零售价不能为负数")]
        public decimal? OEMPrice { get; set; }

        /// <summary>
        /// 进口价格
        /// </summary>
        [Range(0, double.MaxValue, ErrorMessage = "进口价格不能为负数")]
        public decimal? ImportPrice { get; set; }

        /// <summary>
        /// 单件装箱数
        /// </summary>
        [Range(1, int.MaxValue, ErrorMessage = "单件装箱数必须大于0")]
        public int? PackingQuantity { get; set; }

        /// <summary>
        /// 单件体积
        /// </summary>
        [Range(0, double.MaxValue, ErrorMessage = "单件体积不能为负数")]
        public decimal? UnitVolume { get; set; }

        /// <summary>
        /// 中包数量
        /// </summary>
        [Range(1, int.MaxValue, ErrorMessage = "中包数量必须大于0")]
        public int? MiddlePackQuantity { get; set; }

        /// <summary>
        /// 商品图片URL
        /// </summary>
        [StringLength(500, ErrorMessage = "商品图片URL长度不能超过500个字符")]
        public string? ProductImage { get; set; }

        /// <summary>
        /// 使用状态
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// 前缀代码（可选，用于生成带前缀的货号）
        /// </summary>
        public string? PrefixCode { get; set; }
    }

    /// <summary>
    /// 更新国内商品DTO
    /// </summary>
    /// <remarks>
    /// 以下成员刻意不在此 DTO 中，请求里带了也会被忽略（业务已于 2026-10-06 确认）：
    /// - Barcode：条码是扫码、HQ 同步、门店价与 POS 目录共用的标识，创建时确定，之后不允许编辑
    ///   （映射 UpdateDomesticProductDto → DomesticProduct 也显式忽略 Barcode；前端编辑表单为只读）。
    /// - Material / Remarks / PackingSize：DomesticProduct 实体没有对应列，不需要落库。
    /// 装箱数 / 中包数量的校验口径为 ≥ 1（见下方 Range），批量路径由服务内 &lt;= 0 判断保持同一口径。
    /// </remarks>
    public class UpdateDomesticProductDto
    {
        /// <summary>
        /// 商品名称（可选）
        /// </summary>
        [StringLength(200, ErrorMessage = "商品名称长度不能超过200个字符")]
        public string? ProductName { get; set; }

        /// <summary>
        /// 商品英文名称
        /// </summary>
        [StringLength(500, ErrorMessage = "商品英文名称长度不能超过500个字符")]
        public string? EnglishProductName { get; set; }

        /// <summary>
        /// 商品规格
        /// </summary>
        [StringLength(100, ErrorMessage = "商品规格长度不能超过100个字符")]
        public string? ProductSpecification { get; set; }

        /// <summary>
        /// 商品类型：0-普通商品，1-套装商品，2-多码商品
        /// </summary>
        [Range(0, 2, ErrorMessage = "商品类型必须在0-2之间")]
        public int ProductType { get; set; } = 0;


        /// <summary>
        /// 国内价格
        /// </summary>
        [Range(0, double.MaxValue, ErrorMessage = "国内价格不能为负数")]
        public decimal? DomesticPrice { get; set; }

        /// <summary>
        /// 零售价
        /// </summary>
        [Range(0, double.MaxValue, ErrorMessage = "零售价不能为负数")]
        public decimal? OEMPrice { get; set; }

        /// <summary>
        /// 进口价格
        /// </summary>
        [Range(0, double.MaxValue, ErrorMessage = "进口价格不能为负数")]
        public decimal? ImportPrice { get; set; }

        /// <summary>
        /// 单件装箱数
        /// </summary>
        [Range(1, int.MaxValue, ErrorMessage = "单件装箱数必须大于0")]
        public int? PackingQuantity { get; set; }

        /// <summary>
        /// 单件体积
        /// </summary>
        [Range(0, double.MaxValue, ErrorMessage = "单件体积不能为负数")]
        public decimal? UnitVolume { get; set; }

        /// <summary>
        /// 中包数量
        /// </summary>
        [Range(1, int.MaxValue, ErrorMessage = "中包数量必须大于0")]
        public int? MiddlePackQuantity { get; set; }

        /// <summary>
        /// 商品图片URL
        /// </summary>
        [StringLength(500, ErrorMessage = "商品图片URL长度不能超过500个字符")]
        public string? ProductImage { get; set; }

        /// <summary>
        /// 使用状态
        /// </summary>
        public bool IsActive { get; set; } = true;
    }

    /// <summary>
    /// 国内商品高级查询DTO
    /// </summary>
    public class DomesticProductAdvancedQueryDto : AdvancedQuery
    {
        /// <summary>
        /// 快速过滤 - 供应商编码
        /// </summary>
        public string? SupplierCode { get; set; }

        /// <summary>
        /// 快速过滤 - 商品类型
        /// </summary>
        public int? ProductType { get; set; }

        /// <summary>
        /// 快速过滤 - 使用状态
        /// </summary>
        public bool? IsActive { get; set; }
    }

    /// <summary>
    /// 国内商品查询DTO
    /// </summary>
    public class DomesticProductQueryDto : PagedQuery
    {
        /// <summary>
        /// 搜索关键词（商品名称、HB货号、条形码）
        /// </summary>
        public string? Search { get; set; }

        /// <summary>
        /// 供应商编码
        /// </summary>
        public string? SupplierCode { get; set; }

        /// <summary>
        /// 商品类型
        /// </summary>
        public int? ProductType { get; set; }

        /// <summary>
        /// 使用状态
        /// </summary>
        public bool? IsActive { get; set; }

        /// <summary>
        /// 价格范围 - 最小值
        /// </summary>
        public decimal? MinPrice { get; set; }

        /// <summary>
        /// 价格范围 - 最大值
        /// </summary>
        public decimal? MaxPrice { get; set; }

        /// <summary>
        /// 供应商名称筛选
        /// </summary>
        public string? SupplierName { get; set; }

        /// <summary>
        /// 商品名称筛选
        /// </summary>
        public string? ProductName { get; set; }

        /// <summary>
        /// 商品货号筛选
        /// </summary>
        public string? ProductNo { get; set; }

        /// <summary>
        /// 排序字段
        /// </summary>
        public string? SortBy { get; set; }

        /// <summary>
        /// 排序方向 (asc/desc)
        /// </summary>
        public string? SortDirection { get; set; }
    }

    /// <summary>
    /// 国内商品详情DTO
    /// </summary>
    public class DomesticProductDetailDto : DomesticProductDto
    {
        /// <summary>
        /// 套装商品列表（如果是套装商品）
        /// </summary>
        public new List<DomesticSetProductDto>? SetProducts { get; set; }

        /// <summary>
        /// 供应商信息
        /// </summary>
        public ChinaSupplierDto? Supplier { get; set; }
    }

    /// <summary>
    /// 批量创建商品DTO
    /// </summary>
    public class BatchCreateDomesticProductDto
    {
        /// <summary>
        /// 供应商编码
        /// </summary>
        [Required(ErrorMessage = "供应商编码不能为空")]
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>
        /// 前缀代码（可选）
        /// </summary>
        public string? PrefixCode { get; set; }

        /// <summary>
        /// 商品列表
        /// </summary>
        [Required(ErrorMessage = "商品列表不能为空")]
        [MinLength(1, ErrorMessage = "至少需要一个商品")]
        public List<BatchProductItem> Products { get; set; } = new();
    }

    /// <summary>
    /// 批量商品项
    /// </summary>
    public class BatchProductItem
    {
        /// <summary>
        /// 商品名称（可选）
        /// </summary>
        public string ProductName { get; set; } = string.Empty;

        /// <summary>
        /// 商品英文名称
        /// </summary>
        public string? EnglishProductName { get; set; }

        /// <summary>
        /// 商品规格
        /// </summary>
        public string? ProductSpecification { get; set; }

        /// <summary>
        /// 商品类型
        /// </summary>
        public int ProductType { get; set; } = 0;


        /// <summary>
        /// 国内价格
        /// </summary>
        public decimal? DomesticPrice { get; set; }

        /// <summary>
        /// 零售价
        /// </summary>
        public decimal? OEMPrice { get; set; }

        /// <summary>
        /// 进口价格
        /// </summary>
        public decimal? ImportPrice { get; set; }
        
        /// <summary>
        /// 装箱数量
        /// </summary>
        public int? PackingQuantity { get; set; }
        
        /// <summary>
        /// 单件体积
        /// </summary>
        public decimal? UnitVolume { get; set; }
        
        /// <summary>
        /// 中包数量
        /// </summary>
        public int? MiddlePackQuantity { get; set; }
    }

    /// <summary>
    /// 批量更新状态请求DTO
    /// </summary>
    public class BatchUpdateStatusRequest
    {
        /// <summary>
        /// 要更新的商品编码列表
        /// </summary>
        [Required(ErrorMessage = "商品编码列表不能为空")]
        [MinLength(1, ErrorMessage = "至少需要一个商品编码")]
        public List<string> ProductCodes { get; set; } = new();

        /// <summary>
        /// 新的状态 (true为启用，false为禁用)
        /// </summary>
        public bool IsActive { get; set; }
    }

    /// <summary>
    /// 批量检测商品请求DTO
    /// </summary>
    public class BatchProductDetectionDto
    {
        /// <summary>
        /// 供应商编码
        /// </summary>
        [Required(ErrorMessage = "供应商编码不能为空")]
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>
        /// 要检测的商品列表
        /// </summary>
        [Required(ErrorMessage = "商品列表不能为空")]
        [MinLength(1, ErrorMessage = "至少需要一个商品")]
        public List<BatchProductInputDto> Products { get; set; } = new();
    }

    /// <summary>
    /// 批量商品输入DTO
    /// </summary>
    public class BatchProductInputDto
    {
        /// <summary>
        /// HB货号（可为空，创建时自动生成）
        /// </summary>
        public string? HBProductNo { get; set; }

        /// <summary>
        /// 商品名称
        /// </summary>
        public string? ProductName { get; set; }

        /// <summary>
        /// 商品英文名称
        /// </summary>
        public string? EnglishProductName { get; set; }

        /// <summary>
        /// 条形码
        /// </summary>
        public string? Barcode { get; set; }

        /// <summary>
        /// 国内价格
        /// </summary>
        public decimal? DomesticPrice { get; set; }

        /// <summary>
        /// 零售价
        /// </summary>
        public decimal? OEMPrice { get; set; }

        /// <summary>
        /// 单件装箱数
        /// </summary>
        public int? PackingQuantity { get; set; }

        /// <summary>
        /// 单件体积
        /// </summary>
        public decimal? UnitVolume { get; set; }

        /// <summary>
        /// 中包数量
        /// </summary>
        public int? MiddlePackQuantity { get; set; }

        /// <summary>
        /// 商品图片URL
        /// </summary>
        public string? ProductImage { get; set; }
    }

    /// <summary>
    /// 批量检测结果DTO
    /// </summary>
    public class BatchProductDetectionResultDto
    {
        /// <summary>
        /// 输入的商品数据
        /// </summary>
        public BatchProductInputDto InputData { get; set; } = new();

        /// <summary>
        /// 供应商编码
        /// </summary>
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>
        /// 供应商名称
        /// </summary>
        public string? SupplierName { get; set; }

        /// <summary>
        /// 是否为新商品
        /// </summary>
        public bool IsNewProduct { get; set; }

        /// <summary>
        /// 系统已有的商品数据（如果不是新商品）
        /// </summary>
        public DomesticProductDto? ExistingData { get; set; }

        /// <summary>
        /// 是否有变更
        /// </summary>
        public bool HasChanges { get; set; }

        /// <summary>
        /// 变更字段列表
        /// </summary>
        public List<string> ChangeList { get; set; } = new();

        /// <summary>
        /// 该货号在数据库中是否存在重复（2个以上商品使用同一货号）
        /// </summary>
        public bool HasDuplicateInDatabase { get; set; }

        /// <summary>
        /// 重复的商品编码列表（当 HasDuplicateInDatabase=true 时返回）
        /// </summary>
        public List<string> DuplicateProductCodes { get; set; } = new();
    }

    /// <summary>
    /// 批量操作商品请求DTO
    /// </summary>
    public class BatchProductOperationDto
    {
        /// <summary>
        /// 供应商编码
        /// </summary>
        [Required(ErrorMessage = "供应商编码不能为空")]
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>
        /// 要新建的商品列表
        /// </summary>
        public List<BatchProductInputDto>? NewProducts { get; set; }

        /// <summary>
        /// 要更新的商品列表
        /// </summary>
        public List<BatchProductUpdateDto>? UpdateProducts { get; set; }
    }

    /// <summary>
    /// 批量更新商品DTO
    /// </summary>
    public class BatchProductUpdateDto
    {
        /// <summary>
        /// 商品编码
        /// </summary>
        [Required(ErrorMessage = "商品编码不能为空")]
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>
        /// 商品名称
        /// </summary>
        public string? ProductName { get; set; }

        /// <summary>
        /// 商品英文名称
        /// </summary>
        public string? EnglishProductName { get; set; }

        /// <summary>
        /// 条形码
        /// </summary>
        public string? Barcode { get; set; }

        /// <summary>
        /// 国内价格
        /// </summary>
        public decimal? DomesticPrice { get; set; }

        /// <summary>
        /// 零售价
        /// </summary>
        public decimal? OEMPrice { get; set; }

        /// <summary>
        /// 单件装箱数
        /// </summary>
        public int? PackingQuantity { get; set; }

        /// <summary>
        /// 单件体积
        /// </summary>
        public decimal? UnitVolume { get; set; }

        /// <summary>
        /// 中包数量
        /// </summary>
        public int? MiddlePackQuantity { get; set; }

        /// <summary>
        /// 商品图片URL
        /// </summary>
        public string? ProductImage { get; set; }
    }

    /// <summary>
    /// 批量操作结果DTO
    /// </summary>
    public class BatchProductOperationResultDto
    {
        /// <summary>
        /// 成功创建的商品列表
        /// </summary>
        public List<DomesticProductDto> CreatedProducts { get; set; } = new();

        /// <summary>
        /// 成功更新的商品列表
        /// </summary>
        public List<DomesticProductDto> UpdatedProducts { get; set; } = new();

        /// <summary>
        /// 更新成功的条目的变更字段列表（每条记录包含商品编码和本次更新的字段名集合）
        /// 字段名使用与前端DTO一致的 PascalCase：
        /// ProductName、EnglishProductName、Barcode、DomesticPrice、OEMPrice、PackingQuantity、UnitVolume、MiddlePackQuantity
        /// </summary>
        public List<ProductChangeInfo> UpdatedChanges { get; set; } = new();

        /// <summary>
        /// 错误信息列表
        /// </summary>
        public List<string> Errors { get; set; } = new();

        /// <summary>
        /// 是否全部成功
        /// </summary>
        public bool IsAllSuccess => !Errors.Any();

        /// <summary>
        /// 成功总数
        /// </summary>
        public int SuccessCount => CreatedProducts.Count + UpdatedProducts.Count;

        /// <summary>
        /// 失败总数
        /// </summary>
        public int FailureCount => Errors.Count;
    }

    /// <summary>
    /// 商品更新变更信息
    /// </summary>
    public class ProductChangeInfo
    {
        /// <summary>
        /// 商品编码
        /// </summary>
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>
        /// 本次更新的变化字段列表（PascalCase）
        /// </summary>
        public List<string> ChangeList { get; set; } = new();
    }

    /// <summary>
    /// 批量创建套装商品请求DTO
    /// </summary>
    public class BatchCreateSetProductsDto
    {
        /// <summary>
        /// 供应商编码
        /// </summary>
        [Required(ErrorMessage = "供应商编码不能为空")]
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>
        /// 前缀代码（可选）
        /// </summary>
        public string? PrefixCode { get; set; }

        /// <summary>
        /// 套装规格（套10、套15等）
        /// </summary>
        [Range(1, 50, ErrorMessage = "套装规格必须在1-50之间")]
        public int SetType { get; set; } = 10;

        /// <summary>
        /// 商品列表
        /// </summary>
        [Required(ErrorMessage = "商品列表不能为空")]
        [MinLength(1, ErrorMessage = "至少需要一个商品")]
        public List<BatchCreateSetProductItem> Products { get; set; } = new();

        /// <summary>
        /// 套装价格配置（统一应用到所有商品）
        /// </summary>
        [Required(ErrorMessage = "套装价格配置不能为空")]
        [MinLength(1, ErrorMessage = "至少需要一个套装价格")]
        public List<SetPriceItem> SetPrices { get; set; } = new();
    }

    /// <summary>
    /// 批量创建套装商品的商品项
    /// </summary>
    public class BatchCreateSetProductItem
    {
        /// <summary>
        /// 商品名称
        /// </summary>
        [Required(ErrorMessage = "商品名称不能为空")]
        [StringLength(200, ErrorMessage = "商品名称长度不能超过200个字符")]
        public string ProductName { get; set; } = string.Empty;

        /// <summary>
        /// 商品英文名称
        /// </summary>
        [StringLength(500, ErrorMessage = "商品英文名称长度不能超过500个字符")]
        public string? EnglishProductName { get; set; }

        /// <summary>
        /// 商品规格
        /// </summary>
        [StringLength(100, ErrorMessage = "商品规格长度不能超过100个字符")]
        public string? ProductSpecification { get; set; }

        /// <summary>
        /// 商品类型（固定为1-套装商品）
        /// </summary>
        public int ProductType { get; set; } = 1;
    }

    /// <summary>
    /// 套装价格项
    /// </summary>
    public class SetPriceItem
    {
        /// <summary>
        /// 国内价格
        /// </summary>
        [Range(0, double.MaxValue, ErrorMessage = "国内价格不能为负数")]
        public decimal DomesticPrice { get; set; }

        /// <summary>
        /// 进口价格
        /// </summary>
        [Range(0, double.MaxValue, ErrorMessage = "进口价格不能为负数")]
        public decimal? ImportPrice { get; set; }

        /// <summary>
        /// 零售价
        /// </summary>
        [Range(0, double.MaxValue, ErrorMessage = "零售价不能为负数")]
        public decimal? OEMPrice { get; set; }
    }

    /// <summary>
    /// 批量创建套装商品结果DTO
    /// </summary>
    public class BatchCreateSetProductsResultDto
    {
        /// <summary>
        /// 成功创建的商品列表
        /// </summary>
        public List<DomesticProductDto> CreatedProducts { get; set; } = new();

        /// <summary>
        /// 失败的商品列表
        /// </summary>
        public List<object> FailedProducts { get; set; } = new();

        /// <summary>
        /// 成功数量
        /// </summary>
        public int SuccessCount { get; set; }

        /// <summary>
        /// 失败数量
        /// </summary>
        public int FailureCount { get; set; }

        /// <summary>
        /// 套装明细总数
        /// </summary>
        public int TotalSetItems { get; set; }

        /// <summary>
        /// 错误信息列表
        /// </summary>
        public List<string> Errors { get; set; } = new();

        /// <summary>
        /// 是否全部成功
        /// </summary>
        public bool IsAllSuccess => FailureCount == 0 && !Errors.Any();
    }

    /// <summary>
    /// 批量更新国内商品DTO
    /// </summary>
    public class BatchUpdateDomesticProductsDto
    {
        /// <summary>
        /// 商品列表
        /// </summary>
        [Required(ErrorMessage = "商品列表不能为空")]
        [MinLength(1, ErrorMessage = "至少需要一个商品")]
        public List<BatchProductUpdateDto> Products { get; set; } = new();
    }

    /// <summary>
    /// 批量更新 HBweb 商品主表商品名称 DTO
    /// </summary>
    public class BatchUpdateHbwebProductNamesDto
    {
        /// <summary>
        /// 需要按 HBweb Product.LocalSupplierCode + Product.ItemNumber 更新的商品列表
        /// </summary>
        public List<HbwebProductNameUpdateItemDto> Products { get; set; } = new();

        /// <summary>
        /// 是否同时同步 HQ 商品名称，默认不访问 HQ
        /// </summary>
        public bool SyncToHq { get; set; }
    }

    /// <summary>
    /// HBweb 商品主表商品名称更新项
    /// </summary>
    public class HbwebProductNameUpdateItemDto
    {
        /// <summary>
        /// HBweb Product.LocalSupplierCode（本地供应商代码，不是国内供应商代码）
        /// </summary>
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>
        /// HBweb Product.ItemNumber，对应导入页货号
        /// </summary>
        public string ItemNumber { get; set; } = string.Empty;

        /// <summary>
        /// 写入 Product.ProductName 的商品名称
        /// </summary>
        public string ProductName { get; set; } = string.Empty;
    }

    /// <summary>
    /// 批量更新 HBweb 商品主表商品名称结果
    /// </summary>
    public class BatchUpdateHbwebProductNamesResultDto
    {
        /// <summary>
        /// 实际更新数量
        /// </summary>
        public int UpdatedCount { get; set; }

        /// <summary>
        /// 名称已一致、未写入数量
        /// </summary>
        public int UnchangedCount { get; set; }

        /// <summary>
        /// HBweb 主表未找到的货号
        /// </summary>
        public List<string> MissingItemNumbers { get; set; } = new();

        /// <summary>
        /// 跳过或失败原因
        /// </summary>
        public List<string> Errors { get; set; } = new();

        /// <summary>
        /// 可选的 HQ 商品名称同步结果；未要求同步时为空
        /// </summary>
        public HqProductNameSyncResultDto? HqSyncResult { get; set; }
    }

    /// <summary>
    /// HQ 商品名称同步结果
    /// </summary>
    public class HqProductNameSyncResultDto
    {
        /// <summary>
        /// HQ 同步是否整体成功
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 实际更新数量
        /// </summary>
        public int UpdatedCount { get; set; }

        /// <summary>
        /// 名称已一致、未写入数量
        /// </summary>
        public int UnchangedCount { get; set; }

        /// <summary>
        /// HQ 商品字典未找到的货号
        /// </summary>
        public List<string> MissingItemNumbers { get; set; } = new();

        /// <summary>
        /// 跳过或失败原因
        /// </summary>
        public List<string> Errors { get; set; } = new();
    }

    /// <summary>
    /// 同步商品到HBSales请求DTO
    /// </summary>
    public class SyncToHBSalesRequestDto
    {
        /// <summary>
        /// 商品编码列表
        /// </summary>
        [Required(ErrorMessage = "商品编码列表不能为空")]
        public List<string> ProductCodes { get; set; } = new();

        /// <summary>
        /// 是否同时更新商品图片（默认false）
        /// </summary>
        public bool IncludeImage { get; set; } = false;
    }
}
