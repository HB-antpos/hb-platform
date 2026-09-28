using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.HqEntities;

namespace BlazorApp.Api.Mappings.Profiles
{
    /// <summary>
    /// 仓库管理映射配置
    /// 包含仓库分类、仓库商品相关的实体与DTO的映射关系
    /// </summary>
    public class WarehouseMappingProfile : BaseMappingProfile
    {
        public WarehouseMappingProfile()
        {
            ConfigureWarehouseCategoryMappings();
            ConfigureWarehouseProductMappings();
            ConfigureHqWarehouseMappings();
        }

        /// <summary>
        /// 配置仓库分类映射
        /// </summary>
        private void ConfigureWarehouseCategoryMappings()
        {
            // WarehouseCategory -> WarehouseCategoryDto 映射
            CreateMap<WarehouseCategory, WarehouseCategoryDto>()
                .ForMember(
                    dest => dest.CategoryName,
                    opt =>
                        opt.MapFrom(src =>
                            string.IsNullOrWhiteSpace(src.CategoryName)
                                ? (src.ChineseName ?? src.CategoryName)
                                : src.CategoryName
                        )
                )
                .ForMember(
                    dest => dest.UpdatedAt,
                    opt => opt.MapFrom(src => src.UpdatedAt ?? src.CreatedAt)
                )
                .ForMember(dest => dest.Children, opt => opt.MapFrom(src => src.Children))
                .ForMember(dest => dest.Parent, opt => opt.MapFrom(src => src.Parent));

            // CreateWarehouseCategoryDto -> WarehouseCategory 映射
            CreateMap<CreateWarehouseCategoryDto, WarehouseCategory>()
                .ForMember(
                    dest => dest.CategoryGUID,
                    opt => opt.MapFrom(src => Guid.NewGuid().ToString())
                )
                .ForMember(dest => dest.CreatedAt, opt => opt.MapFrom(src => DateTime.UtcNow))
                .ForMember(dest => dest.UpdatedAt, opt => opt.MapFrom(src => DateTime.UtcNow))
                .ForMember(dest => dest.Children, opt => opt.Ignore())
                .ForMember(dest => dest.Parent, opt => opt.Ignore())
                .ForMember(dest => dest.WarehouseProducts, opt => opt.Ignore());

            // UpdateWarehouseCategoryDto -> WarehouseCategory 映射
            CreateMap<UpdateWarehouseCategoryDto, WarehouseCategory>()
                .ForMember(dest => dest.CategoryGUID, opt => opt.Ignore())
                .ForMember(dest => dest.CreatedAt, opt => opt.Ignore())
                .ForMember(dest => dest.UpdatedAt, opt => opt.MapFrom(src => DateTime.UtcNow))
                .ForMember(dest => dest.Children, opt => opt.Ignore())
                .ForMember(dest => dest.Parent, opt => opt.Ignore())
                .ForMember(dest => dest.WarehouseProducts, opt => opt.Ignore());
        }

        /// <summary>
        /// 配置仓库商品映射
        /// </summary>
        private void ConfigureWarehouseProductMappings()
        {
            // WarehouseProduct -> WarehouseProductDto 映射
            CreateMap<WarehouseProduct, WarehouseProductDto>()
                .ForMember(
                    dest => dest.ProductName,
                    opt => opt.MapFrom(src => src.Product != null ? src.Product.ProductName : null)
                )
                .ForMember(
                    dest => dest.PurchasePrice,
                    opt =>
                        opt.MapFrom(src => src.Product != null ? src.Product.PurchasePrice : null)
                )
                .ForMember(
                    dest => dest.RetailPrice,
                    opt => opt.MapFrom(src => src.Product != null ? src.Product.RetailPrice : null)
                )
                .ForMember(
                    dest => dest.MiddlePackageQuantity,
                    opt =>
                        opt.MapFrom(src =>
                            src.Product != null ? src.Product.MiddlePackageQuantity : null
                        )
                )
                .ForMember(dest => dest.PackingQty, opt => opt.MapFrom(src => src.PackingQuantity));

            // WarehouseProduct -> WarehouseProductListDto 映射
            CreateMap<WarehouseProduct, WarehouseProductListDto>()
                .ForMember(dest => dest.Locations, opt => opt.MapFrom(src => src.Locations))
                // 映射Product基础信息
                .ForMember(
                    dest => dest.LocalSupplierCode,
                    opt =>
                        opt.MapFrom(src =>
                            src.Product != null ? src.Product.LocalSupplierCode : null
                        )
                )
                .ForMember(
                    dest => dest.ItemNumber,
                    opt => opt.MapFrom(src => src.Product != null ? src.Product.ItemNumber : null)
                )
                .ForMember(
                    dest => dest.ProductBarcode,
                    opt => opt.MapFrom(src => src.Product != null ? src.Product.Barcode : null)
                )
                .ForMember(
                    dest => dest.ProductBaseName,
                    opt => opt.MapFrom(src => src.Product != null ? src.Product.ProductName : null)
                )
                .ForMember(
                    dest => dest.ProductType,
                    opt => opt.MapFrom(src => src.Product != null ? src.Product.ProductType : null)
                )
                .ForMember(
                    dest => dest.PurchasePrice,
                    opt =>
                        opt.MapFrom(src => src.Product != null ? src.Product.PurchasePrice : null)
                )
                .ForMember(
                    dest => dest.MiddlePackageQuantity,
                    opt =>
                        opt.MapFrom(src =>
                            src.Product != null ? src.Product.MiddlePackageQuantity : null
                        )
                )
                .ForMember(
                    dest => dest.RetailPrice,
                    opt => opt.MapFrom(src => src.Product != null ? src.Product.RetailPrice : null)
                )
                .ForMember(dest => dest.PackingQty, opt => opt.MapFrom(src => src.PackingQuantity))
                .ForMember(
                    dest => dest.IsAutoPricing,
                    opt =>
                        opt.MapFrom(src => src.Product != null ? src.Product.IsAutoPricing : false)
                )
                .ForMember(
                    dest => dest.ProductImage,
                    opt => opt.MapFrom(src => src.Product != null ? src.Product.ProductImage : null)
                )
                .ForMember(
                    dest => dest.ProductCategoryGUID,
                    opt =>
                        opt.MapFrom(src =>
                            src.Product != null && src.Product.WarehouseCategoryGUID != null
                                ? src.Product.WarehouseCategoryGUID
                                : null
                        )
                )
                .ForMember(
                    dest => dest.ProductCategoryName,
                    opt =>
                        opt.MapFrom(src =>
                            src.Product != null && src.Product.WarehouseCategory != null
                                ? src.Product.WarehouseCategory.CategoryName
                                : null
                        )
                )
                .ForMember(
                    dest => dest.IsSpecialProduct,
                    opt =>
                        opt.MapFrom(src =>
                            src.Product != null ? src.Product.IsSpecialProduct : false
                        )
                )
                .ForMember(dest => dest.IsActive, opt => opt.MapFrom(src => src.IsActive));

            // CreateWarehouseProductDto -> WarehouseProduct 映射
            CreateMap<CreateWarehouseProductDto, WarehouseProduct>()
                .ForMember(
                    dest => dest.ProductCode,
                    opt => opt.MapFrom(src => Guid.NewGuid().ToString())
                )
                .ForMember(dest => dest.CreatedAt, opt => opt.MapFrom(src => DateTime.UtcNow))
                .ForMember(dest => dest.UpdatedAt, opt => opt.MapFrom(src => DateTime.UtcNow));

            // UpdateWarehouseProductDto -> WarehouseProduct 映射
            CreateMap<UpdateWarehouseProductDto, WarehouseProduct>()
                .ForMember(dest => dest.ProductCode, opt => opt.Ignore())
                .ForMember(
                    dest => dest.IsActive,
                    opt =>
                    {
                        opt.PreCondition(src => src.IsActive.HasValue);
                        opt.MapFrom(src => src.IsActive!.Value);
                    }
                )
                .ForMember(
                    dest => dest.ImportPrice,
                    opt => opt.MapFrom(src => src.PurchasePrice ?? src.ImportPrice)
                )
                .ForMember(
                    dest => dest.OEMPrice,
                    opt => opt.MapFrom(src => src.RetailPrice ?? src.OEMPrice)
                )
                .ForMember(
                    dest => dest.PackingQuantity,
                    opt => opt.MapFrom(src => src.PackingQty ?? src.PackingQuantity)
                )
                .ForMember(dest => dest.CreatedAt, opt => opt.Ignore())
                .ForMember(dest => dest.UpdatedAt, opt => opt.MapFrom(src => DateTime.UtcNow));
        }

        /// <summary>
        /// 配置从HQ数据库同步的仓库相关映射
        /// </summary>
        private void ConfigureHqWarehouseMappings()
        {
            // 从HQ商品库存表到WarehouseProduct的映射配置
            CreateMap<CBP_DIC_商品库存表, WarehouseProduct>()
                .ForMember(
                    dest => dest.ProductCode,
                    opt =>
                        opt.MapFrom(src =>
                            !string.IsNullOrEmpty(src.H商品编码)
                                ? src.H商品编码
                                : Guid.NewGuid().ToString()
                        )
                )
                .ForMember(
                    dest => dest.DomesticPrice,
                    opt => opt.MapFrom(src => src.H国内价格 ?? 0)
                )
                .ForMember(dest => dest.OEMPrice, opt => opt.MapFrom(src => src.H贴牌价格 ?? 0))
                .ForMember(dest => dest.ImportPrice, opt => opt.MapFrom(src => src.H进口价格 ?? 0))
                .ForMember(
                    dest => dest.StockQuantity,
                    opt => opt.MapFrom(src => (int?)(src.H库存 ?? 0))
                )
                .ForMember(
                    dest => dest.MinOrderQuantity,
                    opt => opt.MapFrom(src => (int?)(src.H最小订货量 ?? 0))
                )
                .ForMember(dest => dest.StockValue, opt => opt.MapFrom(src => src.H库存金额 ?? 0))
                .ForMember(
                    dest => dest.StockAlertQuantity,
                    opt => opt.MapFrom(src => (int?)(src.H库存预警数 ?? 0))
                )
                .ForMember(dest => dest.IsActive, opt => opt.MapFrom(src => src.H使用状态 == 1))
                // 通过导航属性映射商品详细信息（移除冗余字段：ProductName, EnglishProductName, Barcode, ProductSpecification, Unit）
                .ForMember(
                    dest => dest.Volume,
                    opt =>
                        opt.MapFrom(src => src.商品信息 != null ? (src.商品信息.单件体积 ?? 0) : 0)
                )
                // 设置创建和更新时间
                .ForMember(dest => dest.CreatedAt, opt => opt.MapFrom(src => DateTime.Now))
                .ForMember(dest => dest.UpdatedAt, opt => opt.MapFrom(src => DateTime.Now))
                // 忽略导航属性和其他不需要的字段
                .ForMember(dest => dest.Product, opt => opt.Ignore())
                .ForMember(dest => dest.Locations, opt => opt.Ignore())
                .ForMember(
                    dest => dest.CreatedBy,
                    opt => opt.MapFrom(src => src.FGC_Creator ?? "DataSync")
                )
                .ForMember(
                    dest => dest.UpdatedBy,
                    opt => opt.MapFrom(src => src.FGC_LastModifier ?? "DataSync")
                )
                .ForMember(dest => dest.IsDeleted, opt => opt.MapFrom(src => false));

            // 从HQ商品分类码表到WarehouseCategory的映射配置
            CreateMap<CBP_DIC_商品分类码表, WarehouseCategory>()
                .ForMember(
                    dest => dest.CategoryGUID,
                    opt => opt.MapFrom(src => src.HGUID ?? Guid.NewGuid().ToString())
                )
                .ForMember(dest => dest.ParentGUID, opt => opt.MapFrom(src => src.H父级GUID))
                .ForMember(
                    dest => dest.CategoryName,
                    opt => opt.MapFrom(src => src.H类别名称 ?? "")
                )
                .ForMember(dest => dest.ChineseName, opt => opt.MapFrom(src => src.H中文名称))
                .ForMember(dest => dest.IsActive, opt => opt.MapFrom(src => true)) // 默认启用
                .ForMember(dest => dest.SortOrder, opt => opt.MapFrom(src => (int?)null))
                .ForMember(dest => dest.Remarks, opt => opt.MapFrom(src => (string?)null))
                .ForMember(
                    dest => dest.CreatedAt,
                    opt => opt.MapFrom(src => src.FGC_CreateDate ?? DateTime.Now)
                )
                .ForMember(
                    dest => dest.UpdatedAt,
                    opt => opt.MapFrom(src => src.FGC_LastModifyDate ?? DateTime.Now)
                )
                .ForMember(
                    dest => dest.CreatedBy,
                    opt => opt.MapFrom(src => src.FGC_Creator ?? "DataSync")
                )
                .ForMember(
                    dest => dest.UpdatedBy,
                    opt => opt.MapFrom(src => src.FGC_LastModifier ?? "DataSync")
                )
                .ForMember(dest => dest.IsDeleted, opt => opt.MapFrom(src => false))
                // 忽略导航属性
                .ForMember(dest => dest.Children, opt => opt.Ignore())
                .ForMember(dest => dest.Parent, opt => opt.Ignore())
                .ForMember(dest => dest.WarehouseProducts, opt => opt.Ignore());
        }
    }
}
