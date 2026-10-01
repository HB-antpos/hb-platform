using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces.React
{
    public interface IProductGradeReactService
    {
        Task<ApiResponse<PagedResult<ProductGradeDto>>> GetProductGradesAsync(ProductGradeListQueryDto query);
        /// <summary>全部在用的商品等级（去重、大写、升序），供订货页等级筛选下拉使用。</summary>
        Task<ApiResponse<List<string>>> GetGradeOptionsAsync();
        Task<ApiResponse<ProductGradeDto>> CreateOrUpdateProductGradeAsync(CreateProductGradeDto dto);
        Task<ApiResponse<bool>> BatchUpdateGradesAsync(BatchUpdateGradeDto dto);
        Task<ApiResponse<PasteImportResultDto>> PasteImportGradesAsync(PasteImportGradeDto dto);
        Task<ApiResponse<bool>> DeleteProductGradeAsync(string id);
        Task<ApiResponse<List<ProductGradeDto>>> GetProductGradesByProductCodesAsync(List<string> productCodes);
        Task<ApiResponse<BatchUpdateGradePriceResult>> BatchUpdateGradePriceAsync(BatchUpdateGradePriceDto dto);
    }

    public class PasteImportResultDto
    {
        public int TotalCount { get; set; }
        public int MatchedCount { get; set; }
        public int CreatedCount { get; set; }
        public int UpdatedCount { get; set; }
        public List<PasteImportPreviewItem> PreviewItems { get; set; } = new();
    }
}
