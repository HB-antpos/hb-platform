using BlazorApp.Shared.DTOs;
using Microsoft.Extensions.Logging;

namespace BlazorApp.Api.Features.LocalSupplierInvoices
{
    internal sealed class LocalSupplierInvoicesProductReviewHandler
    {
        private readonly ILogger _logger;
        private readonly LocalSupplierInvoicesProductReviewStore _checkProductsStore;
        private readonly LocalSupplierInvoicesProductReviewEvaluator _checkProductsEvaluator;
        private readonly LocalSupplierInvoicesProductReviewWriter _checkProductsWriter;

        public LocalSupplierInvoicesProductReviewHandler(LocalSupplierInvoicesDependencies dependencies)
        {
            _logger = dependencies.Logger;
            _checkProductsStore = new LocalSupplierInvoicesProductReviewStore(dependencies);
            _checkProductsEvaluator = new LocalSupplierInvoicesProductReviewEvaluator(dependencies);
            _checkProductsWriter = new LocalSupplierInvoicesProductReviewWriter(dependencies);
        }

        public Task<ApiResponse<List<SupplierItemDetectResult>>> DetectSupplierItemAsync(
            DetectSupplierItemRequest dto
        ) => _checkProductsStore.DetectSupplierItemAsync(dto);

        public Task<ApiResponse<List<BarcodeDetectResult>>> DetectBarcodeAsync(
            DetectBarcodeRequest dto
        ) => _checkProductsStore.DetectBarcodeAsync(dto);

        /// <summary>保留 façade 反射兼容入口，实际并行查询实现由 Store 唯一拥有。</summary>
        internal Task<List<T>> QueryInChunksParallelAsync<T, TKey>(
            IReadOnlyList<TKey> keys,
            int chunkSize,
            Func<ISqlSugarClient, List<TKey>, Task<List<T>>> fetch,
            int maxConcurrency = 5
        ) => _checkProductsStore.QueryInChunksParallelAsync(keys, chunkSize, fetch, maxConcurrency);

        public async Task<ApiResponse<CheckProductsResponseDto>> CheckProductsAsync(
            CheckProductsRequest dto
        )
        {
            try
            {
                var data = await _checkProductsStore.LoadAsync(dto);
                if (data == null)
                    return ApiResponse<CheckProductsResponseDto>.Error("订单不存在", "NOT_FOUND");

                var evaluation = await _checkProductsEvaluator.EvaluateAsync(data);
                var updates = LocalSupplierInvoicesProductReviewAssembler.CreateUpdateEntities(
                    evaluation,
                    data.Details,
                    DateTime.UtcNow
                );
                await _checkProductsWriter.PersistAsync(updates);
                return ApiResponse<CheckProductsResponseDto>.OK(
                    new CheckProductsResponseDto
                    {
                        Results = evaluation.Results,
                        Summary = evaluation.Summary,
                    }
                );
            }
            catch (ArgumentException ex)
            {
                // 定价规则类异常（如进货价不在策略范围内）把原因透传给前端，不再只显示两个字的「检测失败」。
                _logger.LogError(ex, "检测商品失败");
                return ApiResponse<CheckProductsResponseDto>.Error($"检测失败：{ex.Message}", "CHECK_ERROR");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检测商品失败");
                return ApiResponse<CheckProductsResponseDto>.Error("检测失败", "CHECK_ERROR");
            }
        }
    }
}
