using SqlSugar;
using System.ComponentModel.DataAnnotations;

namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 仓库产品数据传输对象，用于在应用各层之间传输仓库产品数据
    /// </summary>
    public class WarehouseProductDto
    {
        /// <summary>
        /// 产品代码
        /// </summary>
        public string ProductCode { get; set; } = string.Empty;
        
        /// <summary>
        /// 国内价格
        /// </summary>
        public decimal? DomesticPrice { get; set; }
        
        /// <summary>
        /// 零售价
        /// </summary>
        public decimal? OEMPrice { get; set; }

        /// <summary>
        /// 零售价格（兼容前端 retail 字段，持久化到 Product.RetailPrice / StoreRetailPriceValue）
        /// </summary>
        public decimal? RetailPrice { get; set; }
        
        /// <summary>
        /// 进口价格
        /// </summary>
        public decimal? ImportPrice { get; set; }

        /// <summary>
        /// 进货价（兼容前端 purchase 字段，持久化到 Product.PurchasePrice / StoreRetailPrice.PurchasePrice）
        /// </summary>
        public decimal? PurchasePrice { get; set; }
        
        /// <summary>
        /// 库存数量
        /// </summary>
        public int? StockQuantity { get; set; }
        
        /// <summary>
        /// 最小订购数量
        /// </summary>
        public int? MinOrderQuantity { get; set; }
        
        /// <summary>
        /// 库存价值
        /// </summary>
        public decimal? StockValue { get; set; }
        
        /// <summary>
        /// 库存警报数量
        /// </summary>
        public int? StockAlertQuantity { get; set; }
        
        /// <summary>
        /// 是否启用
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// 商品名称
        /// </summary>
        [StringLength(200, ErrorMessage = "商品名称不能超过200个字符")]
        public string? ProductName { get; set; }



        /// <summary>
        /// 条码
        /// </summary>
        [StringLength(50, ErrorMessage = "条码不能超过50个字符")]
        public string? Barcode { get; set; }

        /// <summary>
        /// 单件体积
        /// </summary>
        
        [Display(Name = "单件体积")]
        public decimal? Volume { get; set; }

        /// <summary>
        /// 中包数（来自 Product 表）
        /// </summary>
        public int? MiddlePackageQuantity { get; set; }

        /// <summary>
        /// PackingQty（前端字段名，持久化到 WarehouseProduct.PackingQuantity）
        /// </summary>
        public int? PackingQty { get; set; }

        /// <summary>
        /// 单件装箱数（后端字段名）
        /// </summary>
        public int? PackingQuantity { get; set; }

        /// <summary>
        /// 分店代码；传入时限定 StoreRetailPrice 更新目标分店。
        /// </summary>
        public string? StoreCode { get; set; }

        /// <summary>
        /// 类别GUID
        /// </summary>
        public string? CategoryGUID { get; set; }
        
        /// <summary>
        /// 仓库类别信息
        /// </summary>
        public WarehouseCategoryDto? WarehouseCategory { get; set; }
        
        /// <summary>
        /// 产品信息
        /// </summary>
        public ProductDto? Product { get; set; }
        
        /// <summary>
        /// 位置列表
        /// </summary>
        public List<LocationDto> Locations { get; set; } = new List<LocationDto>();
    }

    /// <summary>
    /// 创建仓库产品数据传输对象，用于创建新的仓库产品
    /// </summary>
    public class CreateWarehouseProductDto
    {
        /// <summary>
        /// 国内价格
        /// </summary>
        public decimal? DomesticPrice { get; set; }
        
        /// <summary>
        /// 零售价
        /// </summary>
        public decimal? OEMPrice { get; set; }

        /// <summary>
        /// 零售价格（兼容前端 retail 字段）
        /// </summary>
        public decimal? RetailPrice { get; set; }
        
        /// <summary>
        /// 进口价格
        /// </summary>
        public decimal? ImportPrice { get; set; }

        /// <summary>
        /// 进货价（兼容前端 purchase 字段）
        /// </summary>
        public decimal? PurchasePrice { get; set; }
        
        /// <summary>
        /// 库存数量
        /// </summary>
        public int? StockQuantity { get; set; }
        
        /// <summary>
        /// 最小订购数量
        /// </summary>
        public int? MinOrderQuantity { get; set; }
        
        /// <summary>
        /// 库存价值
        /// </summary>
        public decimal? StockValue { get; set; }
        
        /// <summary>
        /// 库存警报数量
        /// </summary>
        public int? StockAlertQuantity { get; set; }
        
        /// <summary>
        /// 是否启用
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// 商品名称
        /// </summary>
        [StringLength(200, ErrorMessage = "商品名称不能超过200个字符")]
        public string? ProductName { get; set; }



        /// <summary>
        /// 条码
        /// </summary>
        [StringLength(50, ErrorMessage = "条码不能超过50个字符")]
        public string? Barcode { get; set; }

        /// <summary>
        /// 单件体积
        /// </summary>
      
        [Display(Name = "单件体积")]
        public decimal? Volume { get; set; }

        /// <summary>
        /// 中包数
        /// </summary>
        public int? MiddlePackageQuantity { get; set; }

        /// <summary>
        /// PackingQty（前端字段名）
        /// </summary>
        public int? PackingQty { get; set; }

        /// <summary>
        /// 单件装箱数（后端字段名）
        /// </summary>
        public int? PackingQuantity { get; set; }

        /// <summary>
        /// 分店代码；传入时限定 StoreRetailPrice 更新目标分店。
        /// </summary>
        public string? StoreCode { get; set; }

        /// <summary>
        /// 类别GUID
        /// </summary>
        public string? CategoryGUID { get; set; }
    }

    /// <summary>
    /// 更新仓库产品数据传输对象，用于更新现有仓库产品
    /// </summary>
    public class UpdateWarehouseProductDto
    {
        /// <summary>
        /// 产品代码
        /// </summary>
        [Required(ErrorMessage = "ProductCode不能为空")]
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>
        /// 国内价格
        /// </summary>
        public decimal? DomesticPrice { get; set; }
        
        /// <summary>
        /// 零售价
        /// </summary>
        public decimal? OEMPrice { get; set; }

        /// <summary>
        /// 零售价格（兼容前端 retail 字段）
        /// </summary>
        public decimal? RetailPrice { get; set; }
        
        /// <summary>
        /// 进口价格
        /// </summary>
        public decimal? ImportPrice { get; set; }

        /// <summary>
        /// 进货价（兼容前端 purchase 字段）
        /// </summary>
        public decimal? PurchasePrice { get; set; }
        
        /// <summary>
        /// 库存数量
        /// </summary>
        public int? StockQuantity { get; set; }
        
        /// <summary>
        /// 最小订购数量
        /// </summary>
        public int? MinOrderQuantity { get; set; }
        
        /// <summary>
        /// 库存价值
        /// </summary>
        public decimal? StockValue { get; set; }
        
        /// <summary>
        /// 库存警报数量
        /// </summary>
        public int? StockAlertQuantity { get; set; }
        
        /// <summary>
        /// 是否启用
        /// </summary>
        public bool? IsActive { get; set; }

        /// <summary>
        /// 商品名称
        /// </summary>
        [StringLength(200, ErrorMessage = "商品名称不能超过200个字符")]
        public string? ProductName { get; set; }



        /// <summary>
        /// 条码
        /// </summary>
        [StringLength(50, ErrorMessage = "条码不能超过50个字符")]
        public string? Barcode { get; set; }

        /// <summary>
        /// 单件体积
        /// </summary>
        ///
        [Display(Name = "单件体积")]
        public decimal? Volume { get; set; }

        /// <summary>
        /// 中包数
        /// </summary>
        public int? MiddlePackageQuantity { get; set; }

        /// <summary>
        /// PackingQty（前端字段名）
        /// </summary>
        public int? PackingQty { get; set; }

        /// <summary>
        /// 单件装箱数（后端字段名）
        /// </summary>
        public int? PackingQuantity { get; set; }

        /// <summary>
        /// 分店代码；传入时限定 StoreRetailPrice 更新目标分店。
        /// </summary>
        public string? StoreCode { get; set; }

        /// <summary>
        /// 类别GUID
        /// </summary>
        public string? CategoryGUID { get; set; }
    }

    /// <summary>
    /// 仓库产品过滤数据传输对象，用于查询时的过滤条件
    /// </summary>
    public class WarehouseProductFilterDto
    {
        /// <summary>
        /// 商品名称过滤条件
        /// </summary>
        public string? ProductName { get; set; }

        /// <summary>
        /// 货号过滤条件
        /// </summary>
        public string? ItemNumber { get; set; }

        /// <summary>
        /// 国内供应商代码过滤条件
        /// </summary>
        public string? SupplierCode { get; set; }
        
        /// <summary>
        /// 条码过滤条件
        /// </summary>
        public string? Barcode { get; set; }

        /// <summary>
        /// 单件体积
        /// </summary>
      
        public decimal? Volume { get; set; }

        /// <summary>
        /// 类别GUID过滤条件
        /// </summary>
        public string? CategoryGUID { get; set; }
        
        /// <summary>
        /// 是否启用状态过滤条件
        /// </summary>
        public bool? IsActive { get; set; }
        
        /// <summary>
        /// 最低价格过滤条件
        /// </summary>
        public decimal? MinPrice { get; set; }
        
        /// <summary>
        /// 最高价格过滤条件
        /// </summary>
        public decimal? MaxPrice { get; set; }
        
        /// <summary>
        /// 最低库存过滤条件
        /// </summary>
        public int? MinStock { get; set; }
        
        /// <summary>
        /// 最高库存过滤条件
        /// </summary>
        public int? MaxStock { get; set; }
        
        /// <summary>
        /// 页码（从1开始）
        /// </summary>
        public int PageNumber { get; set; } = 1;
        
        /// <summary>
        /// 每页大小
        /// </summary>
        public int PageSize { get; set; } = 10;
        
        /// <summary>
        /// 排序字段
        /// </summary>
        public string? SortBy { get; set; } = "ProductName";
        
        /// <summary>
        /// 是否降序排列
        /// </summary>
        public bool SortDescending { get; set; } = false;
    }
}
