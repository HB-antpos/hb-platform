using AutoMapper;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Mappings.Profiles
{
    /// <summary>
    /// 商品前缀映射配置
    /// </summary>
    public class ProductPrefixCodeMappingProfile : BaseMappingProfile
    {
        public ProductPrefixCodeMappingProfile()
        {
            // ProductPrefixCode -> ProductPrefixCodeDto 映射
            CreateMap<ProductPrefixCode, ProductPrefixCodeDto>()
                .ForMember(dest => dest.SupplierName, opt => opt.MapFrom(src => src.Supplier != null ? src.Supplier.SupplierName : null));

            // CreateProductPrefixCodeDto -> ProductPrefixCode 映射
            CreateMap<CreateProductPrefixCodeDto, ProductPrefixCode>()
                .ForMember(dest => dest.PrefixCode, opt => opt.Ignore())
                .ForMember(dest => dest.CreatedAt, opt => opt.Ignore())
                .ForMember(dest => dest.UpdatedAt, opt => opt.Ignore())
                .ForMember(dest => dest.CreatedBy, opt => opt.Ignore())
                .ForMember(dest => dest.UpdatedBy, opt => opt.Ignore())
                .ForMember(dest => dest.IsDeleted, opt => opt.MapFrom(src => false))
                .ForMember(dest => dest.Supplier, opt => opt.Ignore());

            // UpdateProductPrefixCodeDto -> ProductPrefixCode 映射（用于更新场景）
            CreateMap<UpdateProductPrefixCodeDto, ProductPrefixCode>()
                // SortOrder 为空表示“请求没有提供”，必须保持库里原值；
                // AutoMapper 默认会把 null 也映射过去，等于每次不带排序的编辑都把它清空。
                // 显式给出的值（包括 0）照常覆盖。
                .ForMember(dest => dest.SortOrder, opt => opt.Condition((src, dest, srcSortOrder) => srcSortOrder.HasValue))
                .ForMember(dest => dest.PrefixCode, opt => opt.Ignore())
                .ForMember(dest => dest.SupplierCode, opt => opt.Ignore())
                .ForMember(dest => dest.CreatedAt, opt => opt.Ignore())
                .ForMember(dest => dest.UpdatedAt, opt => opt.Ignore())
                .ForMember(dest => dest.CreatedBy, opt => opt.Ignore())
                .ForMember(dest => dest.UpdatedBy, opt => opt.Ignore())
                .ForMember(dest => dest.IsDeleted, opt => opt.Ignore())
                .ForMember(dest => dest.Supplier, opt => opt.Ignore());

            // ProductPrefixCode -> ProductPrefixCodeDetailDto 映射
            CreateMap<ProductPrefixCode, ProductPrefixCodeDetailDto>()
                .ForMember(dest => dest.SupplierName, opt => opt.MapFrom(src => src.Supplier != null ? src.Supplier.SupplierName : null))
                .ForMember(dest => dest.Supplier, opt => opt.MapFrom(src => src.Supplier))
                .ForMember(dest => dest.ProductCount, opt => opt.Ignore()); // 需要在服务中单独设置

            // ProductPrefixCode -> SimpleProductPrefixCodeDto 映射
            CreateMap<ProductPrefixCode, SimpleProductPrefixCodeDto>();
        }
    }
}
