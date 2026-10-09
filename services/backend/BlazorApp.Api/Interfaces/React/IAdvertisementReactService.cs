using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces.React
{
    public interface IAdvertisementReactService
    {
        Task<GridResponseDto<AdvertisementListDto>> GetGridAsync(AdvertisementGridRequestDto request);
        Task<ApiResponse<List<AdvertisementStoreOptionDto>>> GetStoreOptionsAsync();
        Task<ApiResponse<AdvertisementDetailDto>> GetByIdAsync(string id);
        Task<ApiResponse<AdvertisementDetailDto>> CreateAsync(CreateAdvertisementDto dto);
        Task<ApiResponse<AdvertisementDetailDto>> UpdateAsync(
            string id,
            UpdateAdvertisementDto dto
        );
        Task<ApiResponse<bool>> DeleteAsync(string id);
        Task<ApiResponse<bool>> EnableAsync(string id, bool isEnabled);
        Task<ApiResponse<AdvertisementUploadSignatureResponseDto>> GetUploadSignatureAsync(
            AdvertisementUploadSignatureRequestDto request
        );
    }
}
