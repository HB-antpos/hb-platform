using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces.React
{
    /// <summary>
    /// 本地进货单批量更新后台任务服务。
    /// </summary>
    public interface ILocalSupplierInvoiceBatchUpdateJobService
    {
        Task<LocalSupplierInvoiceUpdateToStorePricesJobDto> StartUpdateToStorePricesJobAsync(
            UpdateToStorePricesRequest request,
            string updatedBy,
            CancellationToken cancellationToken = default
        );

        Task<LocalSupplierInvoiceUpdateToStorePricesJobDto?> GetUpdateToStorePricesJobAsync(
            string jobId,
            CancellationToken cancellationToken = default
        );

        Task<LocalSupplierInvoiceUpdateHqProductsJobDto> StartUpdateHqProductsJobAsync(
            string invoiceGuid,
            UpdateHqProductsRequest request,
            string updatedBy,
            CancellationToken cancellationToken = default
        );

        Task<LocalSupplierInvoiceUpdateHqProductsJobDto> StartUpdateHqProductsJobAsync(
            string invoiceGuid,
            UpdateHqProductsRequest request,
            string? actorUserGuid,
            string actorName,
            CancellationToken cancellationToken = default
        );

        Task<LocalSupplierInvoiceUpdateHqProductsJobDto?> GetUpdateHqProductsJobAsync(
            string jobId,
            CancellationToken cancellationToken = default
        );

        Task<LocalSupplierInvoicePasteDetailsJobDto> StartPasteDetailsJobAsync(
            PasteDetailsRequest request,
            string updatedBy,
            CancellationToken cancellationToken = default
        );

        Task<LocalSupplierInvoicePasteDetailsJobDto?> GetPasteDetailsJobAsync(
            string jobId,
            CancellationToken cancellationToken = default
        );

        Task<LocalSupplierInvoiceCheckProductsJobDto> StartCheckProductsJobAsync(
            CheckProductsRequest request,
            CancellationToken cancellationToken = default
        );

        Task<LocalSupplierInvoiceCheckProductsJobDto?> GetCheckProductsJobAsync(
            string jobId,
            CancellationToken cancellationToken = default
        );

        /// <summary>列表批量商品检测：按顺序逐张检测，跳过已执行明细。</summary>
        Task<LocalSupplierInvoiceBatchCheckProductsJobDto> StartBatchCheckProductsJobAsync(
            IReadOnlyList<string> invoiceGuids,
            IReadOnlyCollection<string> storeCodes,
            CancellationToken cancellationToken = default
        );

        Task<LocalSupplierInvoiceBatchCheckProductsJobDto?> GetBatchCheckProductsJobAsync(
            string jobId,
            CancellationToken cancellationToken = default
        );

        /// <summary>停止剩余：正在检测的单跑完，排队中的单标记为跳过。</summary>
        Task<LocalSupplierInvoiceBatchCheckProductsJobDto?> CancelBatchCheckProductsJobAsync(
            string jobId,
            CancellationToken cancellationToken = default
        );
    }
}
