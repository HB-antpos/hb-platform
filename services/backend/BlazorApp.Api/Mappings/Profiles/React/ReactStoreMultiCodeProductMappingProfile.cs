using AutoMapper;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HqEntities;

namespace BlazorApp.Api.Mappings.Profiles.React
{
    public class ReactStoreMultiCodeProductMappingProfile : Profile
    {
        public ReactStoreMultiCodeProductMappingProfile()
        {
            CreateMap<DIC_分店一品多码表, StoreMultiCodeProduct>()
                .ForMember(
                    dest => dest.UUID,
                    opt =>
                        opt.MapFrom(src => src.HGUID ?? UuidHelper.GenerateUuid7().ToString())
                )
                .ForMember(dest => dest.StoreCode, opt => opt.MapFrom(src => src.H分店代码))
                .ForMember(dest => dest.ProductCode, opt => opt.MapFrom(src => src.H商品编码))
                .ForMember(
                    dest => dest.MultiCodeProductCode,
                    opt => opt.MapFrom(src => src.H多码商品编码)
                )
                .ForMember(
                    dest => dest.StoreMultiCodeProductCode,
                    opt => opt.MapFrom(src => src.H分店多码商品编码)
                )
                .ForMember(dest => dest.MultiBarcode, opt => opt.MapFrom(src => src.H多条形码))
                .ForMember(dest => dest.PurchasePrice, opt => opt.MapFrom(src => src.H进货价))
                .ForMember(
                    dest => dest.MultiCodeRetailPrice,
                    opt => opt.MapFrom(src => src.H一品多码零售价)
                )
                .ForMember(dest => dest.DiscountRate, opt => opt.MapFrom(src => BlazorApp.Shared.Helper.StoreDiscountRate.Normalize(src.H折扣率)))
                .ForMember(
                    dest => dest.IsAutoPricing,
                    opt => opt.MapFrom(src => src.H是否自动定价 ?? false)
                )
                .ForMember(
                    dest => dest.IsSpecialProduct,
                    opt => opt.MapFrom(src => src.H是否特殊商品 ?? false)
                )
                .ForMember(
                    dest => dest.IsActive,
                    opt => opt.MapFrom(src => src.H使用状态 ?? false)
                );
        }
    }
}
