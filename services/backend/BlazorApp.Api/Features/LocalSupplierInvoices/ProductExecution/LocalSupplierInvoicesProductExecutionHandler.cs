using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Logging;

namespace BlazorApp.Api.Features.LocalSupplierInvoices
{
    /// <summary>批量商品操作入口，只编排请求、读取、写入与 API 响应。</summary>
    internal sealed class LocalSupplierInvoicesProductExecutionHandler
    {
        private readonly ILogger _logger;
        private readonly LocalSupplierInvoicesProductExecutionRequestValidator _requestValidator;
        private readonly LocalSupplierInvoicesProductExecutionSource _source;
        private readonly LocalSupplierInvoicesProductExecutionCommandWriter _commandWriter;

        public LocalSupplierInvoicesProductExecutionHandler(LocalSupplierInvoicesDependencies dependencies)
        {
            _logger = dependencies.Logger;
            _source = new LocalSupplierInvoicesProductExecutionSource(dependencies.Context);
            _requestValidator = new LocalSupplierInvoicesProductExecutionRequestValidator(_source);
            _commandWriter = new LocalSupplierInvoicesProductExecutionCommandWriter(
                dependencies,
                _source,
                _requestValidator
            );
        }

        public async Task<ApiResponse<BatchExecuteActionsResultDto>> BatchExecuteActionsAsync(
            string invoiceGuid,
            List<string> detailGuids,
            string userName,
            List<BatchExecuteNewProductProductTypeSelectionDto>? newProductProductTypeSelections = null,
            List<BatchExecuteExpectedActionDto>? expectedActions = null,
            IReadOnlyCollection<StoreLocalSupplierInvoiceDetails>? confirmedDetails = null
        )
        {
            var result = new BatchExecuteActionsResultDto();
            try
            {
                if (
                    !_requestValidator.TryCreateRequest(
                        invoiceGuid,
                        detailGuids,
                        userName,
                        newProductProductTypeSelections,
                        expectedActions,
                        confirmedDetails,
                        out var request,
                        out var requestError
                    )
                )
                {
                    return ApiResponse<BatchExecuteActionsResultDto>.Error(
                        requestError!,
                        "VALIDATION_ERROR"
                    );
                }

                var validatedRequest = request!;
                var initialData = await _source.LoadInitialAsync(validatedRequest);
                if (initialData.Header == null)
                    return ApiResponse<BatchExecuteActionsResultDto>.Error("进货单不存在", "NOT_FOUND");
                if (initialData.Details.Count != validatedRequest.SelectedDetailGuids.Count)
                {
                    return ApiResponse<BatchExecuteActionsResultDto>.Error(
                        "部分明细不存在或不属于当前进货单",
                        "VALIDATION_ERROR"
                    );
                }

                // 「更新货号」未关联商品的行先按条码解析商品编码，供加身份锁；锁内会重新解析并比对。
                var resolvedItemNumberProductCodes = await _source.ResolveItemNumberUpdateProductCodesAsync(
                    initialData.Details,
                    initialData.Header.SupplierCode
                );
                var plan = LocalSupplierInvoicesProductExecutionPlan.Create(
                    validatedRequest,
                    initialData,
                    resolvedItemNumberProductCodes
                );
                return (await _commandWriter.ExecuteAsync(plan)).ToApiResponse();
            }
            catch (Exception ex)
            {
                return ToUnexpectedFailure(ex, result);
            }
        }

        private ApiResponse<BatchExecuteActionsResultDto> ToUnexpectedFailure(
            Exception exception,
            BatchExecuteActionsResultDto result
        )
        {
            _logger.LogError(exception, "批量执行操作失败");
            var isBusy = Services.ProductCosts.ProductCostMutationLock.TryResolveConflict(
                exception,
                out var conflict
            );
            return ApiResponse<BatchExecuteActionsResultDto>.Error(
                isBusy ? conflict!.Message : "批量执行失败",
                isBusy ? Services.ProductCosts.ProductCostMutationLock.BusyErrorCode : "BATCH_EXECUTE_ERROR",
                result
            );
        }
    }
}
