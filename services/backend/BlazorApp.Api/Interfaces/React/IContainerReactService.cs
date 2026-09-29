using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Interfaces.React
{
    /// <summary>
    /// React 专用货柜服务接口（与原有 IContainerService 解耦）
    /// 仅包含 React 控制器所需的方法
    /// </summary>
    public interface IContainerReactService
    {
        /// <summary>
        /// 获取货柜列表（React）
        /// </summary>
        Task<ContainerListResponse> GetContainersAsync(ContainerQueryRequest request);

        /// <summary>
        /// 获取货柜详情（React）
        /// </summary>
        Task<ContainerMainDto?> GetContainerDetailAsync(string containerGuid);

        /// <summary>
        /// 更新货柜信息（React）
        /// </summary>
        Task<bool> UpdateContainerAsync(string containerGuid, UpdateContainerDto dto);

        /// <summary>
        /// 获取货柜商品列表（React）
        /// </summary>
        Task<List<ContainerDetailDto>> GetContainerProductsAsync(string containerGuid);

        /// <summary>
        /// 按服务端筛选、排序和内部分页查询货柜商品明细（React）
        /// </summary>
        Task<ContainerDetailQueryResultDto> QueryContainerDetailsAsync(
            ContainerDetailQueryDto request,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 获取国内套装多码价格明细（货柜明细弹窗专用）。
        /// </summary>
        Task<List<ContainerDomesticSetCodeDto>> GetDomesticSetCodesAsync(string productCode);

        /// <summary>
        /// 批量回写国内套装多码价格（仅更新价格字段）。
        /// </summary>
        Task<int> UpdateDomesticSetCodePricesAsync(
            string productCode,
            UpdateContainerDomesticSetCodePricesRequestDto request,
            string updatedBy
        );

        /// <summary>
        /// 获取符合条件的所有货柜商品明细列表（React）
        /// </summary>
        Task<List<ContainerDetailDto>> GetFilteredContainerProductsAsync(ContainerQueryRequest request);

        /// <summary>
        /// 获取日期过滤选项（React）
        /// </summary>
        Task<List<DateFilterOption>> GetDateFilterOptionsAsync();

        /// <summary>
        /// 批量更新货柜明细（React）
        /// </summary>
        /// <param name="updates">明细更新列表</param>
        /// <returns>成功更新的行数</returns>
        Task<int> BatchUpdateDetailsAsync(List<UpdateContainerDetailDto> updates);

        /// <summary>
        /// 兼容旧客户端的无货柜范围部分成功入口。
        /// </summary>
        Task<ContainerDetailBatchUpdateResultDto> BatchUpdateDetailsDetailedAsync(
            List<UpdateContainerDetailDto> updates
        );

        /// <summary>
        /// 在指定货柜范围内批量更新明细，并返回 React 页面所需的部分成功和字段级错误。
        /// </summary>
        Task<ContainerDetailBatchUpdateResultDto> BatchUpdateDetailsDetailedAsync(
            string containerGuid,
            List<UpdateContainerDetailDto> updates
        );

        /// <summary>
        /// 人工确认后，将国内商品编码反向对齐到本地主档商品编码。
        /// </summary>
        Task<AlignDomesticProductCodeResultDto> AlignDomesticProductCodeAsync(
            AlignDomesticProductCodeRequestDto request
        );

        /// <summary>
        /// 按当前筛选范围批量调浮率并重算成本。
        /// </summary>
        Task<int> ApplyFloatRateByScopeAsync(string containerGuid, ContainerDetailApplyFloatRateRequestDto request);

        /// <summary>
        /// 按当前筛选范围批量改进口价/零售价。
        /// </summary>
        Task<int> ApplyPricesByScopeAsync(string containerGuid, ContainerDetailApplyPricesRequestDto request);

        /// <summary>
        /// 按当前筛选范围批量上下架，并通过预览令牌防止覆盖其他用户更新。
        /// </summary>
        Task<int> SetStatusByScopeAsync(string containerGuid, ContainerDetailSetStatusRequestDto request);

        /// <summary>
        /// 按当前筛选范围批量分配或清空分类，并通过预览令牌防止覆盖其他用户更新。
        /// </summary>
        Task<int> AssignCategoryByScopeAsync(
            string containerGuid,
            ContainerDetailAssignCategoryRequestDto request
        );

        /// <summary>
        /// 按当前筛选范围重算运输成本和进口价。
        /// </summary>
        Task<int> RecalculateCostsByScopeAsync(string containerGuid, ContainerDetailBatchScopeDto request);

        /// <summary>
        /// 按当前筛选范围回填货柜明细上次价格快照。
        /// </summary>
        Task<int> BackfillLastPricesByScopeAsync(string containerGuid, ContainerDetailBatchScopeDto request);

        Task<ContainerDetailBatchPreviewResultDto> PreviewBatchActionAsync(
            string containerGuid,
            ContainerDetailBatchPreviewRequestDto request
        );

        /// <summary>按范围删除明细；返回实际删除数与范围内请求数（前端据此校验删除结果）。</summary>
        Task<(int TotalDeleted, int TotalRequested)> BatchDeleteDetailsScopedAsync(string containerGuid, ContainerDetailBatchScopeDto request);

        /// <summary>
        /// 创建新货柜（React）
        /// </summary>
        /// <param name="dto">创建货柜DTO</param>
        /// <returns>创建的货柜GUID</returns>
        Task<string> CreateContainerAsync(CreateContainerDto dto);

        /// <summary>
        /// 按 ProductCode 检查指定货柜的明细冲突（React）
        /// </summary>
        /// <param name="containerId">货柜编号或货柜编码（皆可）</param>
        /// <param name="productCodes">待检查的商品编码列表</param>
        Task<List<ContainerConflictItemDto>> CheckConflictsAsync(string containerId, List<string> productCodes);

        /// <summary>
        /// 批量分配商品到货柜（React）
        /// </summary>
        /// <param name="containerId">货柜编号或货柜编码（皆可）</param>
        /// <param name="items">分配的商品列表</param>
        /// <param name="resolution">冲突处理策略：override/increase</param>
        /// <param name="notes">备注</param>
        Task<AssignProductsResultDto> AssignProductsAsync(string containerId, List<AssignProductItemDto> items, string resolution, string? notes);

        /// <summary>
        /// 批量删除货柜明细（React）
        /// </summary>
        /// <param name="hguids">明细的 HGUID/DetailCode 列表</param>
        /// <returns>成功删除的行数</returns>
        Task<int> BatchDeleteDetailsAsync(List<string> hguids);

        Task<List<ComingSoonContainerDto>> GetComingSoonContainersAsync();

        Task<SyncResult> SyncContainersWithDetailsFromHqAsync(DateTime? startDate = null);

        Task<SyncResult> PushContainersToHbSalesAsync(List<string> containerGuids);
    }
}
