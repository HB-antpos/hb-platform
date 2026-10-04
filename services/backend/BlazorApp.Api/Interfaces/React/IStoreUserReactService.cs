using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces.React
{
    public interface IStoreUserReactService
    {
        Task<GridResponseDto<StoreUserListDto>> GetGridDataAsync(StoreUserGridRequestDto request);
        Task<ApiResponse<StoreUserDetailDto>> GetByUserGuidAsync(string userGuid, string? storeCode);
        Task<ApiResponse<StoreUserDetailDto>> CreateAsync(
            CreateStoreUserDto dto,
            string createdBy
        );
        Task<ApiResponse<StoreUserDetailDto>> UpdateAsync(
            string userGuid,
            UpdateStoreUserDto dto,
            string updatedBy
        );
        Task<ApiResponse<bool>> UpdateStatusAsync(
            string userGuid,
            UpdateStoreUserStatusDto dto,
            string updatedBy
        );
        Task<ApiResponse<bool>> UpdatePasswordAsync(
            string userGuid,
            UpdateStoreUserPasswordDto dto,
            string updatedBy
        );
        Task<ApiResponse<PasswordSetupEmailResultDto>> SendPasswordSetupEmailAsync(
            string userGuid,
            SendStoreUserPasswordSetupEmailDto dto,
            string requestedBy
        );
    }
}
