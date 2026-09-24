using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces.React
{
    public interface IStoreOrderReactService
    {
        /// <summary>
        /// 获取订货商品列表
        /// </summary>
        Task<PagedListReactDto<StoreOrderProductDto>> GetPagedListAsync(StoreOrderFilterDto filter);

        /// <summary>
        /// 批量精确查询订货商品
        /// </summary>
        Task<ApiResponse<List<StoreOrderBatchLookupItemDto>>> BatchLookupProductsAsync(
            StoreOrderBatchLookupRequestDto request
        );

        /// <summary>
        /// 按条码查询订货商品候选列表
        /// </summary>
        Task<ApiResponse<StoreOrderScanLookupResultDto>> ScanLookupProductsAsync(
            StoreOrderScanLookupRequestDto request
        );

        /// <summary>
        /// 扫码查询并加购：单命中直接返回轻量购物车变更，0/多命中只返回候选。
        /// </summary>
        Task<ApiResponse<StoreOrderScanLookupAddResultDto>> ScanLookupAndAddToCartMutationAsync(
            StoreOrderScanLookupAddRequestDto request
        );

        /// <summary>
        /// 获取分店当前的购物车 (FlowStatus=0)
        /// </summary>
        Task<ApiResponse<StoreOrderCartDto?>> GetActiveCartAsync(string storeCode);

        /// <summary>
        /// 获取分店当前购物车的轻量汇总
        /// </summary>
        Task<ApiResponse<StoreOrderCartDto?>> GetActiveCartSummaryAsync(string storeCode);

        /// <summary>
        /// 添加到购物车 (或更新数量)
        /// </summary>
        Task<ApiResponse<StoreOrderCartDto?>> AddToCartAsync(AddToCartRequestDto request);

        /// <summary>
        /// 扫码添加到购物车，只返回摘要和当前变更行。
        /// </summary>
        Task<ApiResponse<StoreOrderCartMutationResultDto?>> AddToCartMutationAsync(
            AddToCartRequestDto request
        );

        /// <summary>
        /// 更新购物车项数量 (覆盖)
        /// </summary>
        Task<ApiResponse<StoreOrderCartDto?>> UpdateCartItemAsync(AddToCartRequestDto request);

        /// <summary>
        /// 扫码更新购物车项数量，只返回摘要和当前变更行。
        /// </summary>
        Task<ApiResponse<StoreOrderCartMutationResultDto?>> UpdateCartItemMutationAsync(
            AddToCartRequestDto request
        );

        /// <summary>
        /// 移除购物车项
        /// </summary>
        Task<ApiResponse<bool>> RemoveFromCartAsync(RemoveFromCartRequestDto request);

        /// <summary>
        /// 清空购物车 (删除 FlowStatus=0 的订单的所有明细)
        /// </summary>
        Task<ApiResponse<StoreOrderCartDto?>> ClearCartAsync(string storeCode);

        /// <summary>
        /// 提交订单 (FlowStatus 0 -> 1)；已暂停供货的行留在购物车里，结果里列出。
        /// </summary>
        Task<ApiResponse<SubmitStoreOrderResultDto>> SubmitOrderAsync(
            SubmitStoreOrderRequestDto request
        );

        /// <summary>
        /// 批量获取商品动态数据 (历史订单 + 购物车数量)
        /// </summary>
        Task<ApiResponse<List<StoreOrderDynamicDataDto>>> GetProductsDynamicDataAsync(
            StoreOrderDynamicDataRequestDto request
        );

        /// <summary>
        /// 查询商品在指定分店的仓库订货发货记录（按订单聚合并分页）。
        /// </summary>
        Task<ApiResponse<PagedListReactDto<StoreOrderProductOrderHistoryItemDto>>> GetProductOrderHistoryAsync(
            StoreOrderProductOrderHistoryRequestDto request
        );

        /// <summary>
        /// 查询分店商品最近一次来货后的每日销售明细。
        /// </summary>
        Task<ApiResponse<StoreOrderSalesSinceLastArrivalResultDto>> GetSalesSinceLastArrivalAsync(
            StoreOrderSalesSinceLastArrivalRequestDto request
        );

        /// <summary>
        /// 查询分店商品的统一订货/发货历史与销售明细合并时间轴。
        /// </summary>
        Task<ApiResponse<StoreOrderProductActivityHistoryResultDto>> GetProductActivityHistoryAsync(
            StoreOrderProductActivityHistoryRequestDto request
        );

        /// <summary>
        /// 批量查询分店商品最近一次来货后的销售数量。
        /// </summary>
        Task<ApiResponse<List<StoreOrderSalesSinceLastArrivalSummaryItemDto>>> GetSalesSinceLastArrivalSummaryAsync(
            StoreOrderSalesSinceLastArrivalSummaryRequestDto request
        );

        /// <summary>
        /// 获取订单列表
        /// </summary>
        Task<PagedListReactDto<StoreOrderListItemDto>> GetOrderListAsync(StoreOrderListFilterDto filter);

        /// <summary>
        /// 获取首次货柜进货价基准差异统计。
        /// </summary>
        Task<ApiResponse<StoreOrderImportPriceVarianceResultDto>> GetImportPriceVarianceAsync(
            StoreOrderImportPriceVarianceQueryDto query
        );

        /// <summary>
        /// 获取首次货柜进货价基准差异单商品订单明细。
        /// </summary>
        Task<ApiResponse<StoreOrderImportPriceVarianceDetailResultDto>> GetImportPriceVarianceDetailsAsync(
            StoreOrderImportPriceVarianceDetailQueryDto query
        );

        /// <summary>
        /// 更新首次货柜价差异统计页展示的仓库当前国内价格。
        /// </summary>
        Task<ApiResponse<StoreOrderImportPriceVarianceDomesticPriceUpdateResultDto>> UpdateImportPriceVarianceDomesticPriceAsync(
            StoreOrderImportPriceVarianceDomesticPriceUpdateDto request
        );

        /// <summary>
        /// 更新首次货柜价差异统计页展示的仓库当前进货价格。
        /// </summary>
        Task<ApiResponse<StoreOrderImportPriceVarianceWarehouseImportPriceUpdateResultDto>> UpdateImportPriceVarianceWarehouseImportPriceAsync(
            StoreOrderImportPriceVarianceWarehouseImportPriceUpdateDto request
        );

        /// <summary>
        /// 批量更新首次货柜价差异统计页展示的仓库当前进货价格。
        /// </summary>
        Task<ApiResponse<StoreOrderImportPriceVarianceWarehouseImportPriceBatchUpdateResultDto>> UpdateImportPriceVarianceWarehouseImportPriceBatchAsync(
            StoreOrderImportPriceVarianceWarehouseImportPriceBatchUpdateDto request
        );

        /// <summary>
        /// 获取订单详情
        /// </summary>
        Task<ApiResponse<StoreOrderDetailDto?>> GetOrderDetailAsync(
            string orderGuid,
            StoreOrderDetailQueryDto? query = null
        );

        /// <summary>
        /// 获取订单全量明细，供拣货单、发票等打印链路使用。
        /// </summary>
        Task<ApiResponse<StoreOrderCartDto?>> GetOrderDetailFullAsync(string orderGuid);

        /// <summary>
        /// 更新订单关联分店的联系信息。
        /// </summary>
        Task<ApiResponse<StoreOrderStoreContactDto>> UpdateStoreContactAsync(
            UpdateStoreOrderStoreContactDto request
        );

        /// <summary>
        /// 获取订单已包含商品编码，用于远程分页详情页的跨页重复校验。
        /// </summary>
        Task<ApiResponse<List<string>>> GetOrderDetailProductCodesAsync(string orderGuid);

        /// <summary>
        /// 获取订单中使用过的分店信息
        /// </summary>
        Task<ApiResponse<List<BranchDto>>> GetUsedBranchesAsync();

        /// <summary>
        /// 获取订单中未能匹配本地分店的旧分店标识聚合。
        /// </summary>
        Task<ApiResponse<List<UnmatchedStoreOrderGroupDto>>> GetUnmatchedStoreOrderGroupsAsync();

        /// <summary>
        /// 批量将订单旧分店标识映射为本地分店编码。
        /// </summary>
        Task<ApiResponse<BatchMapStoreOrderStoreCodeResultDto>> BatchMapStoreOrderStoreCodeAsync(
            BatchMapStoreOrderStoreCodeDto request
        );

        /// <summary>
        /// 创建新订单 (FlowStatus=1)
        /// </summary>
        Task<ApiResponse<string>> CreateOrderAsync(CreateStoreOrderDto request);

        /// <summary>
        /// 添加商品到指定订单
        /// </summary>
        Task<ApiResponse<bool>> AddOrderLineAsync(AddOrderLineDto request);

        /// <summary>
        /// 批量添加商品到指定订单
        /// </summary>
        Task<ApiResponse<bool>> BatchAddOrderLineAsync(BatchAddOrderLineDto request);

        /// <summary>
        /// Excel 粘贴覆盖订单行
        /// </summary>
        Task<ApiResponse<bool>> PasteReplaceOrderLinesAsync(PasteReplaceOrderLinesDto request);

        /// <summary>
        /// 更新指定订单行数量
        /// </summary>
        Task<ApiResponse<bool>> UpdateOrderLineAsync(UpdateOrderLineDto request);

        /// <summary>
        /// 软删除指定订单行
        /// </summary>
        Task<ApiResponse<bool>> RemoveOrderLineAsync(RemoveOrderLineDto request);

        /// <summary>
        /// 批量更新订单行 (数量或价格)
        /// </summary>
        Task<ApiResponse<bool>> BatchUpdateOrderLineAsync(BatchUpdateOrderLineDto request);

        /// <summary>
        /// 从仓库商品表刷新订单明细进口价。
        /// </summary>
        Task<ApiResponse<RefreshStoreOrderImportPricesResultDto>> RefreshOrderLineImportPricesAsync(
            RefreshStoreOrderImportPricesDto request
        );

        /// <summary>
        /// 更新订单头信息
        /// </summary>
        Task<ApiResponse<bool>> UpdateOrderHeaderAsync(UpdateOrderHeaderDto request);

        /// <summary>
        /// 更新订单出库日期，可选同步标记为已完成。
        /// </summary>
        Task<ApiResponse<bool>> UpdateOrderOutboundDateAsync(UpdateOrderOutboundDateDto request);

        /// <summary>
        /// 软删除订单
        /// </summary>
        Task<ApiResponse<bool>> DeleteOrderAsync(string orderGuid);

        /// <summary>
        /// 更新商品状态
        /// </summary>
        Task<ApiResponse<bool>> UpdateProductStatusAsync(UpdateProductStatusDto request);

        /// <summary>
        /// 批量更新商品状态
        /// </summary>
        Task<ApiResponse<bool>> BatchUpdateProductStatusAsync(BatchUpdateProductStatusDto request);

        /// <summary>
        /// 复制订单到另一个分店
        /// </summary>
        Task<ApiResponse<CopyOrderResultDto>> CopyOrderAsync(CopyOrderDto request);

        /// <summary>
        /// 从 HQ 同步本地不存在的仓库订单（主表 + 明细表）
        /// </summary>
        /// <param name="request">同步请求参数</param>
        /// <returns>同步结果</returns>
        Task<SyncMissingOrdersResultDto> SyncMissingOrdersFromHqAsync(
            SyncMissingOrdersRequestDto? request
        );

        /// <summary>
        /// 完成订单 (FlowStatus -> 2)
        /// </summary>
        Task<ApiResponse<bool>> CompleteOrderAsync(string orderGuid);

        /// <summary>
        /// 开始配货 (FlowStatus -> 3)
        /// </summary>
        Task<ApiResponse<bool>> StartPickingAsync(string orderGuid);

        /// <summary>
        /// 更新订单状态 (支持双向切换 Submitted/Completed)
        /// </summary>
        Task<ApiResponse<bool>> UpdateOrderStatusAsync(
            string orderGuid,
            int newStatus,
            bool bypassPreorderGate = false
        );

        /// <summary>
        /// 批量更新订单状态
        /// </summary>
        Task<ApiResponse<int>> BatchUpdateOrderStatusAsync(
            List<string> orderGuids,
            int newStatus,
            bool bypassPreorderGate = false
        );
    }
}
