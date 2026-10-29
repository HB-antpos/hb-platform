using System;
using System.Threading.Tasks;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces.React
{
    public interface IPromotionReactService
    {
        Task<GridResponseDto<PromotionListDto>> GetGridAsync(GridRequestDto request);
        Task<ApiResponse<PromotionDetailDto>> GetByIdAsync(string id);
        Task<ApiResponse<PromotionDetailDto>> CreateAsync(CreatePromotionDto dto);
        Task<ApiResponse<PromotionDetailDto>> UpdateAsync(string id, UpdatePromotionDto dto);
        Task<ApiResponse<bool>> DeleteAsync(string id);
        Task<ApiResponse<bool>> EnableAsync(string id, bool enable);
        Task<GridResponseDto<PromotionListDto>> GetStoreGridAsync(StorePromotionGridRequestDto request);
        Task<ApiResponse<PromotionStoreStatusCountsDto>> GetStoreStatusCountsAsync(
            string storeCode,
            string? keyword = null,
            DateTime? asOf = null
        );
        Task<ApiResponse<List<PromotionListDto>>> GetStoreProductPromotionsAsync(
            string productCode,
            string storeCode,
            DateTime? asOf = null
        );
        Task<ApiResponse<PromotionDetailDto>> GetStoreByIdAsync(string id, string storeCode);
        Task<ApiResponse<PromotionDetailDto>> CreateStorePromotionAsync(
            string storeCode,
            CreatePromotionDto dto
        );
        Task<ApiResponse<PromotionDetailDto>> UpdateStorePromotionAsync(
            string id,
            string storeCode,
            UpdatePromotionDto dto
        );
        Task<ApiResponse<PromotionDetailDto>> CopyToStoreAsync(CopyStorePromotionRequestDto dto);
        Task<ApiResponse<bool>> EnableStorePromotionAsync(string id, string storeCode, bool enable);
        Task<ApiResponse<PromotionEvaluateResponse>> EvaluateAsync(PromotionEvaluateRequest req);
        Task<ApiResponse<List<PromotionListDto>>> GetValidByStoreAsync(
            string storeCode,
            DateTime? asOf = null
        );
        Task<ApiResponse<List<PromotionListDto>>> GetValidByProductAndStoreAsync(
            string productCode,
            string storeCode,
            DateTime? asOf = null
        );
    }
}
