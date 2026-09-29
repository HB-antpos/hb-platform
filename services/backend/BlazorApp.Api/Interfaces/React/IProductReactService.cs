using BlazorApp.Shared.DTOs;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BlazorApp.Api.Interfaces.React
{
    /// <summary>
    /// Product React专用服务接口
    /// 提供Product表的CRUD操作、分页查询、排序和过滤功能
    /// </summary>
    public interface IProductReactService
    {
        /// <summary>
        /// 分页查询商品列表（支持排序和过滤）
        /// </summary>
        /// <param name="query">查询条件</param>
        /// <param name="cancellationToken">调用方请求令牌；已取消时取消异常直接上抛、不记错误日志</param>
        /// <returns>商品列表和总数</returns>
        Task<PagedListReactDto<ProductDto>> GetPagedListAsync(
            ProductReactFilterDto query,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 根据ProductCode获取商品详情
        /// </summary>
        /// <param name="productCode">商品编码</param>
        /// <returns>商品详情</returns>
        Task<ApiResponse<ProductDto>> GetByIdAsync(string productCode);

        /// <summary>
        /// 获取商品对应的分店价格记录
        /// </summary>
        /// <param name="productCode">商品编码</param>
        /// <param name="accessibleStoreCodes">当前用户可访问分店代码；null 表示不限制</param>
        /// <returns>分店价格记录列表</returns>
        Task<ApiResponse<List<ProductStoreRecordDto>>> GetStoreRecordsAsync(
            string productCode,
            IReadOnlyCollection<string>? accessibleStoreCodes
        );

        /// <summary>
        /// 批量更新商品分店业务字段
        /// </summary>
        /// <param name="productCode">商品编码</param>
        /// <param name="request">批量更新请求</param>
        /// <param name="accessibleStoreCodes">当前用户可管理分店代码；null 表示不限制</param>
        /// <returns>批量操作结果</returns>
        Task<ApiResponse<BatchOperationReactResult>> BatchUpdateStoreRecordsAsync(
            string productCode,
            BatchUpdateProductStoreRecordsRequest request,
            IReadOnlyCollection<string>? accessibleStoreCodes
        );

        /// <summary>
        /// 创建商品
        /// </summary>
        /// <param name="dto">创建DTO</param>
        /// <returns>创建结果</returns>
        Task<ApiResponse<ProductDto>> CreateAsync(CreateProductDto dto);

        /// <summary>
        /// 更新商品
        /// </summary>
        /// <param name="productCode">商品编码</param>
        /// <param name="dto">更新DTO</param>
        /// <returns>更新结果</returns>
        Task<ApiResponse<ProductDto>> UpdateAsync(string productCode, UpdateProductDto dto);

        /// <summary>
        /// 删除商品（支持软删除和物理删除）
        /// </summary>
        /// <param name="productCode">商品编码</param>
        /// <param name="isSoftDelete">true=软删除，false=物理删除，默认true</param>
        /// <returns>删除结果</returns>
        Task<ApiResponse<bool>> DeleteAsync(string productCode, bool isSoftDelete = true);

        /// <summary>
        /// 批量更新商品（使用事务）
        /// </summary>
        /// <param name="items">批量更新项</param>
        /// <returns>批量操作结果</returns>
        Task<ApiResponse<BatchOperationReactResult>> BatchUpdateAsync(List<BatchUpdateProductReactDto> items);

        Task<ApiResponse<BatchUpdateSupplierImagesResult>> BatchUpdateSupplierImagesAsync(
            BatchUpdateSupplierImagesRequest request
        );

        Task<ApiResponse<BatchUpdateSupplierImagesResult>> BatchUpdateSupplierImagesAsync(
            BatchUpdateSupplierImagesRequest request,
            string? actorUserGuid,
            string? actorName
        );

        /// <summary>
        /// 批量删除商品（使用事务，支持软删除和物理删除）
        /// </summary>
        /// <param name="productCodes">商品编码列表</param>
        /// <param name="isSoftDelete">true=软删除，false=物理删除，默认true</param>
        /// <returns>批量操作结果</returns>
        Task<ApiResponse<BatchOperationReactResult>> BatchDeleteAsync(List<string> productCodes, bool isSoftDelete = true);

        /// <summary>
        /// 高级过滤查询商品列表（支持商品信息表与分店价格表的组合过滤）
        /// </summary>
        /// <param name="filter">过滤条件</param>
        /// <returns>商品价格列表和总数</returns>
        Task<PagedProductPriceListDto> GetPriceFilteredPagedListAsync(ProductPriceFilterDto filter);

        /// <summary>
        /// 从HQ同步商品到本地（含增删改 + 关联表同步）
        /// 按更新日期比对，只更新HQ侧更新的商品
        /// </summary>
        /// <returns>同步结果</returns>
        Task<ApiResponse<HqProductSyncResult>> SyncProductsFromHqAsync();

        /// <summary>
        /// 从HQ同步商品到本地，并使用服务端传入的操作人身份记录历史。
        /// </summary>
        Task<ApiResponse<HqProductSyncResult>> SyncProductsFromHqAsync(
            string? actorUserGuid,
            string? actorName
        );
    }
}
