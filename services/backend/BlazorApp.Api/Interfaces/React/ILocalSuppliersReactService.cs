using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces.React
{
    public interface ILocalSuppliersReactService
    {
        /// <param name="imageTemplate">configured = 已配图片模板；missing = 未配；其它值不过滤。</param>
        /// <param name="allowedStoreCodes">最近进货的分店范围：null = 全部分店；空列表 = 无分店（最近进货全为 null）。</param>
        Task<PagedResult<LocalSupplierDto>> GetSuppliersAsync(int pageIndex, int pageSize, string? keyword, int? status, string? sortBy, string? sortOrder, string? imageTemplate, IReadOnlyCollection<string>? allowedStoreCodes);
        Task<List<LocalSupplierDto>> GetActiveSuppliersAsync();
        Task<ApiResponse<LocalSupplierSyncResultDto>> SyncFromDicAsync(DateTime? since, bool overwrite);
        Task<ApiResponse<LocalSupplierSyncResultDto>> SyncToHqAsync(IReadOnlyCollection<string> supplierCodes);
        Task<ApiResponse<LocalSupplierDto>> CreateAsync(CreateLocalSupplierDto dto);
        Task<ApiResponse<LocalSupplierDto>> UpdateAsync(string code, UpdateLocalSupplierDto dto);
        Task<ApiResponse<bool>> DeleteAsync(string code);
        Task<ApiResponse<bool>> ToggleStatusAsync(string code, int status);
        Task<ApiResponse<bool>> CheckCodeExistsAsync(string code);
    }
}
