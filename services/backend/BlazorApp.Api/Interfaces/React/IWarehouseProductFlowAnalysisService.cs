using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces.React
{
    public interface IWarehouseProductFlowAnalysisService
    {
        Task<ApiResponse<WarehouseProductFlowAnalysisOptionsDto>> GetOptionsAsync(
            WarehouseProductFlowAnalysisFilterDto filter,
            List<string>? branchCodes,
            bool forceRefresh = false
        );

        Task<ApiResponse<WarehouseProductFlowCandidatePageDto>> GetCandidatesAsync(WarehouseProductFlowCandidateRequest request);

        Task<ApiResponse<WarehouseProductFlowAnalysisSummaryDto>> GetSummaryAsync(
            WarehouseProductFlowAnalysisRequest request,
            List<string>? branchCodes
        );

        Task<ApiResponse<List<WarehouseProductFlowDailyDto>>> GetProductDailyAsync(
            WarehouseProductFlowAnalysisRequest request,
            List<string>? branchCodes
        );

        Task<ApiResponse<List<WarehouseProductFlowDailyDto>>> GetOrderShipmentDailyAsync(
            WarehouseProductFlowAnalysisRequest request,
            List<string>? branchCodes
        );

        Task<ApiResponse<List<WarehouseProductFlowDailyDto>>> GetSalesDailyAsync(
            WarehouseProductFlowAnalysisRequest request,
            List<string>? branchCodes
        );

        Task<ApiResponse<List<WarehouseProductFlowContainerDto>>> GetContainersAsync(
            WarehouseProductFlowAnalysisRequest request,
            List<string>? branchCodes
        );

        Task<ApiResponse<List<WarehouseProductFlowOrderDto>>> GetOrdersAsync(
            WarehouseProductFlowAnalysisRequest request,
            List<string>? branchCodes
        );

        Task<ApiResponse<List<WarehouseProductFlowShipmentDto>>> GetShipmentsAsync(
            WarehouseProductFlowAnalysisRequest request,
            List<string>? branchCodes
        );

        Task<ApiResponse<List<WarehouseProductFlowBranchDto>>> GetBranchesAsync(
            WarehouseProductFlowAnalysisRequest request,
            List<string>? branchCodes
        );

        Task<ApiResponse<List<WarehouseProductFlowDailyDto>>> GetBranchDailyAsync(
            WarehouseProductFlowAnalysisRequest request,
            List<string>? branchCodes
        );

        /// <summary>移动端仓库商品进销查询：单商品一次返回合计、分店分布与货柜/订货/发货明细。</summary>
        Task<ApiResponse<WarehouseProductInsightDto>> GetProductInsightAsync(
            WarehouseProductInsightQuery query,
            List<string>? branchCodes
        );
    }
}
