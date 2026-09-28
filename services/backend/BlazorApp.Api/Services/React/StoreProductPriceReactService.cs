using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// 分店商品价格服务实现
    /// </summary>
    public class StoreProductPriceReactService : IStoreProductPriceReactService
    {
        private readonly SqlSugarContext _context;
        private readonly ILogger<StoreProductPriceReactService> _logger;

        /// <summary>
        /// 构造函数
        /// </summary>
        private readonly IStorePriceUpdateTaskService? _priceTaskService;

        public StoreProductPriceReactService(
            SqlSugarContext context,
            ILogger<StoreProductPriceReactService> logger,
            // 可选：同步其它分店覆盖了对方的价格后，为其登记「待换标签」通知。
            IStorePriceUpdateTaskService? priceTaskService = null
        )
        {
            _context = context;
            _logger = logger;
            _priceTaskService = priceTaskService;
        }

        /// <summary>
        /// 获取分店商品价格网格数据
        /// </summary>
        /// <param name="query">查询参数</param>
        /// <returns>分页结果</returns>
        public async Task<GridResponseDto<StoreProductPriceListDto>> GetGridDataAsync(
            StoreProductPriceQueryDto query
        )
        {
            try
            {
                var db = _context.Db;
                var pageIndex = query.PageNumber;
                var pageSize = query.PageSize;

                // 兼容旧的单个 StoreCode 与新的多选 StoreCodes；多个分店走按商品分页的展开查询
                var storeCodes = ResolveGridStoreCodes(query);
                if (storeCodes.Count > MaxGridStoreCount)
                {
                    return GridResponseDto<StoreProductPriceListDto>.Error(
                        $"一次最多查询 {MaxGridStoreCount} 个分店"
                    );
                }

                if (storeCodes.Count > 1)
                {
                    return await GetMultiStoreGridDataAsync(db, query, storeCodes);
                }

                var storeCode = storeCodes.FirstOrDefault();
                var baseQuery = BuildGridProductQuery(db, query);

                var joinQuery = baseQuery
                    .LeftJoin<StoreRetailPrice>(
                        (p, srp) =>
                            p.ProductCode == srp.ProductCode
                            && srp.StoreCode == storeCode
                            && srp.IsDeleted == false
                    )
                    .LeftJoin<HBLocalSupplier>(
                        (p, srp, sup) =>
                            p.LocalSupplierCode == sup.LocalSupplierCode && sup.IsDeleted == false
                    );

                if (query.PurchasePriceGt.HasValue)
                {
                    joinQuery = joinQuery.Where(
                        (p, srp, sup) => srp.PurchasePrice >= query.PurchasePriceGt.Value
                    );
                }

                if (query.PurchasePriceLt.HasValue)
                {
                    joinQuery = joinQuery.Where(
                        (p, srp, sup) => srp.PurchasePrice <= query.PurchasePriceLt.Value
                    );
                }

                if (query.RetailPriceGt.HasValue)
                {
                    joinQuery = joinQuery.Where(
                        (p, srp, sup) => srp.StoreRetailPriceValue >= query.RetailPriceGt.Value
                    );
                }

                if (query.RetailPriceLt.HasValue)
                {
                    joinQuery = joinQuery.Where(
                        (p, srp, sup) => srp.StoreRetailPriceValue <= query.RetailPriceLt.Value
                    );
                }

                if (!string.IsNullOrWhiteSpace(query.SortBy))
                {
                    var asc = query.SortOrder?.ToLower() == "asc";
                    joinQuery = query.SortBy.ToLower() switch
                    {
                        "productname" => joinQuery.OrderBy(
                            (p, srp, sup) => p.ProductName,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "productcode" => joinQuery.OrderBy(
                            (p, srp, sup) => p.ProductCode,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "itemnumber" => joinQuery.OrderBy(
                            (p, srp, sup) => p.ItemNumber,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "barcode" => joinQuery.OrderBy(
                            (p, srp, sup) => p.Barcode,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "middlesackagequantity" => joinQuery.OrderBy(
                            (p, srp, sup) => p.MiddlePackageQuantity,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "purchaseprice" => joinQuery.OrderBy(
                            (p, srp, sup) => srp.PurchasePrice,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "retailprice" => joinQuery.OrderBy(
                            (p, srp, sup) => srp.StoreRetailPriceValue,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "discountrate" => joinQuery.OrderBy(
                            (p, srp, sup) => srp.DiscountRate,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "updatedat" => joinQuery.OrderBy(
                            (p, srp, sup) => p.UpdatedAt,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        _ => joinQuery.OrderBy((p, srp, sup) => p.UpdatedAt, OrderByType.Desc),
                    };
                }
                else
                {
                    joinQuery = joinQuery.OrderBy((p, srp, sup) => p.UpdatedAt, OrderByType.Desc);
                }

                var totalRef = new RefAsync<int>(0);
                var items = await joinQuery
                    .Select(
                        (p, srp, sup) =>
                            new StoreProductPriceListDto
                            {
                                ProductCode = p.ProductCode,
                                ProductName = p.ProductName,
                                ProductImage = p.ProductImage,
                                ItemNumber = p.ItemNumber,
                                Barcode = p.Barcode,
                                LocalSupplierCode = p.LocalSupplierCode,
                                LocalSupplierName = sup.Name,
                                ProductType = p.ProductType,
                                MiddlePackageQuantity = p.MiddlePackageQuantity,
                                IsActive = p.IsActive,
                                UpdatedAt = srp.UpdatedAt,
                                UpdatedBy = srp.UpdatedBy,
                                StoreCode = srp.StoreCode,
                                StorePurchasePrice = srp.PurchasePrice,
                                StoreRetailPrice = srp.StoreRetailPriceValue,
                                IsStoreAutoPricing = srp.IsAutoPricing,
                                IsStoreSpecialProduct = srp.IsSpecialProduct,
                                DiscountRate = srp.DiscountRate,
                            }
                    )
                    .ToPageListAsync(pageIndex, pageSize, totalRef);
                return GridResponseDto<StoreProductPriceListDto>.OK(items, totalRef.Value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StoreProductPrice Grid 查询失败");
                return GridResponseDto<StoreProductPriceListDto>.Error("查询失败");
            }
        }

        /// <summary>
        /// 网格一次最多查询的分店数（生产约 58 家，留足余量，防止异常请求拼出超长 IN 列表）
        /// </summary>
        public const int MaxGridStoreCount = 200;

        /// <summary>
        /// 合并 StoreCode 与 StoreCodes，去空白、忽略大小写去重，并按编码排序（多分店时即同一商品下的分店顺序）
        /// </summary>
        public static List<string> ResolveGridStoreCodes(StoreProductPriceQueryDto query)
        {
            var codes = new List<string>();
            if (!string.IsNullOrWhiteSpace(query.StoreCode))
            {
                codes.Add(query.StoreCode.Trim());
            }

            if (query.StoreCodes != null)
            {
                codes.AddRange(
                    query.StoreCodes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim())
                );
            }

            return codes
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// 多分店网格：每个商品在每个所选分店各占一行，分店没有价格记录时价格列为空。
        /// 行按「商品排序 → 分店编码」排列，总行数恒为「商品数 × 分店数」，
        /// 所以分页可以先换算出本页覆盖的商品区间，只查这一小段商品及其在所选分店的价格，
        /// 避免把约 17 万商品与全部分店整表连接后再排序分页（生产分店价约 490 万行）。
        /// </summary>
        private async Task<GridResponseDto<StoreProductPriceListDto>> GetMultiStoreGridDataAsync(
            ISqlSugarClient db,
            StoreProductPriceQueryDto query,
            List<string> storeCodes
        )
        {
            if (
                query.PurchasePriceGt.HasValue
                || query.PurchasePriceLt.HasValue
                || query.RetailPriceGt.HasValue
                || query.RetailPriceLt.HasValue
            )
            {
                // 价格区间会让每个商品的行数不再固定为分店数，无法按商品换算分页
                return GridResponseDto<StoreProductPriceListDto>.Error(
                    "多分店查询不支持价格区间筛选，请只选择一个分店"
                );
            }

            var pageNumber = Math.Max(1, query.PageNumber);
            var pageSize = Math.Max(1, query.PageSize);
            var storeCount = storeCodes.Count;

            var productQuery = BuildGridProductQuery(db, query);
            var productTotal = await productQuery.Clone().CountAsync();
            var rowTotal = (long)productTotal * storeCount;
            var total = (int)Math.Min(rowTotal, int.MaxValue);
            var rowStart = (long)(pageNumber - 1) * pageSize;
            if (rowStart >= rowTotal)
            {
                return GridResponseDto<StoreProductPriceListDto>.OK(
                    new List<StoreProductPriceListDto>(),
                    total
                );
            }

            // 本页行区间 [rowStart, rowEnd) 换算成商品区间：第 n 个商品占行 [n × 分店数, (n + 1) × 分店数)
            var rowEnd = Math.Min(rowStart + pageSize, rowTotal);
            var firstProductIndex = (int)(rowStart / storeCount);
            var productTake = (int)((rowEnd - 1) / storeCount) - firstProductIndex + 1;

            var products = await ApplyMultiStoreProductOrder(
                    productQuery.LeftJoin<HBLocalSupplier>(
                        (p, sup) =>
                            p.LocalSupplierCode == sup.LocalSupplierCode && sup.IsDeleted == false
                    ),
                    query
                )
                .Select(
                    (p, sup) =>
                        new StoreProductPriceListDto
                        {
                            ProductCode = p.ProductCode,
                            ProductName = p.ProductName,
                            ProductImage = p.ProductImage,
                            ItemNumber = p.ItemNumber,
                            Barcode = p.Barcode,
                            LocalSupplierCode = p.LocalSupplierCode,
                            LocalSupplierName = sup.Name,
                            ProductType = p.ProductType,
                            MiddlePackageQuantity = p.MiddlePackageQuantity,
                            IsActive = p.IsActive,
                        }
                )
                .Skip(firstProductIndex)
                .Take(productTake)
                .ToListAsync();

            var pageProductCodes = products
                .Select(p => p.ProductCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var prices =
                pageProductCodes.Count == 0
                    ? new List<StoreRetailPrice>()
                    : await db.Queryable<StoreRetailPrice>()
                        .With(SqlWith.NoLock)
                        .Where(srp =>
                            srp.IsDeleted == false
                            && pageProductCodes.Contains(srp.ProductCode)
                            && storeCodes.Contains(srp.StoreCode)
                        )
                        .Select(srp => new StoreRetailPrice
                        {
                            StoreCode = srp.StoreCode,
                            ProductCode = srp.ProductCode,
                            PurchasePrice = srp.PurchasePrice,
                            StoreRetailPriceValue = srp.StoreRetailPriceValue,
                            DiscountRate = srp.DiscountRate,
                            IsAutoPricing = srp.IsAutoPricing,
                            IsSpecialProduct = srp.IsSpecialProduct,
                            UpdatedAt = srp.UpdatedAt,
                            UpdatedBy = srp.UpdatedBy,
                        })
                        .ToListAsync();

            // 同一分店同一商品若有重复价格记录，取最近更新的一条，保证每个「商品 × 分店」只有一行
            var priceByKey = new Dictionary<string, StoreRetailPrice>(StringComparer.OrdinalIgnoreCase);
            foreach (var price in prices.OrderByDescending(x => x.UpdatedAt))
            {
                priceByKey.TryAdd(BuildStoreProductKey(price.StoreCode, price.ProductCode), price);
            }

            var items = new List<StoreProductPriceListDto>(pageSize);
            for (var productOffset = 0; productOffset < products.Count; productOffset++)
            {
                var product = products[productOffset];
                var productRowStart = (long)(firstProductIndex + productOffset) * storeCount;
                for (var storeIndex = 0; storeIndex < storeCount; storeIndex++)
                {
                    var rowIndex = productRowStart + storeIndex;
                    if (rowIndex < rowStart || rowIndex >= rowEnd)
                    {
                        continue;
                    }

                    var storeCode = storeCodes[storeIndex];
                    priceByKey.TryGetValue(
                        BuildStoreProductKey(storeCode, product.ProductCode),
                        out var price
                    );
                    items.Add(
                        new StoreProductPriceListDto
                        {
                            ProductCode = product.ProductCode,
                            ProductName = product.ProductName,
                            ProductImage = product.ProductImage,
                            ItemNumber = product.ItemNumber,
                            Barcode = product.Barcode,
                            LocalSupplierCode = product.LocalSupplierCode,
                            LocalSupplierName = product.LocalSupplierName,
                            ProductType = product.ProductType,
                            MiddlePackageQuantity = product.MiddlePackageQuantity,
                            IsActive = product.IsActive,
                            // 多分店时分店编码取所选分店本身，缺价格记录的行也能知道属于哪个分店
                            StoreCode = storeCode,
                            UpdatedAt = price?.UpdatedAt,
                            UpdatedBy = price?.UpdatedBy,
                            StorePurchasePrice = price?.PurchasePrice,
                            StoreRetailPrice = price?.StoreRetailPriceValue,
                            IsStoreAutoPricing = price?.IsAutoPricing ?? false,
                            IsStoreSpecialProduct = price?.IsSpecialProduct ?? false,
                            DiscountRate = price?.DiscountRate,
                        }
                    );
                }
            }

            return GridResponseDto<StoreProductPriceListDto>.OK(items, total);
        }

        private static string BuildStoreProductKey(string? storeCode, string? productCode) =>
            $"{storeCode?.Trim()}\u001f{productCode?.Trim()}";

        /// <summary>
        /// 多分店网格的商品排序：只支持商品自身字段；价格类排序依赖单个分店的价格，多分店时回落到默认顺序。
        /// 最后补商品编码作为稳定次序，保证按商品换算的分页不会因排序值相同而跨页重复或遗漏。
        /// </summary>
        private static ISugarQueryable<Product, HBLocalSupplier> ApplyMultiStoreProductOrder(
            ISugarQueryable<Product, HBLocalSupplier> productQuery,
            StoreProductPriceQueryDto query
        )
        {
            var sortBy = query.SortBy?.ToLower();
            var orderType = query.SortOrder?.ToLower() == "asc" ? OrderByType.Asc : OrderByType.Desc;
            productQuery = sortBy switch
            {
                "productname" => productQuery.OrderBy((p, sup) => p.ProductName, orderType),
                "productcode" => productQuery.OrderBy((p, sup) => p.ProductCode, orderType),
                "itemnumber" => productQuery.OrderBy((p, sup) => p.ItemNumber, orderType),
                "barcode" => productQuery.OrderBy((p, sup) => p.Barcode, orderType),
                "middlesackagequantity" => productQuery.OrderBy(
                    (p, sup) => p.MiddlePackageQuantity,
                    orderType
                ),
                "updatedat" => productQuery.OrderBy((p, sup) => p.UpdatedAt, orderType),
                _ => productQuery.OrderBy((p, sup) => p.UpdatedAt, OrderByType.Desc),
            };

            // SQL Server 不允许 ORDER BY 重复同一列，按商品编码排序时无需再补
            return sortBy == "productcode"
                ? productQuery
                : productQuery.OrderBy((p, sup) => p.ProductCode, OrderByType.Asc);
        }

        /// <summary>
        /// 网格的商品侧筛选（单店与多分店共用）
        /// </summary>
        private static ISugarQueryable<Product> BuildGridProductQuery(
            ISqlSugarClient db,
            StoreProductPriceQueryDto query
        )
        {
            var baseQuery = db.Queryable<Product>()
                .With(SqlWith.NoLock)
                .Where(p => p.IsDeleted == false);

            if (!string.IsNullOrWhiteSpace(query.LocalSupplierCode))
            {
                baseQuery = baseQuery.Where(p =>
                    p.LocalSupplierCode == query.LocalSupplierCode
                );
            }

            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var keyword = query.Search.Trim();
                baseQuery = baseQuery.Where(p =>
                    p.ProductName.Contains(keyword)
                    || (p.ProductCode != null && p.ProductCode.Contains(keyword))
                    || (p.ItemNumber != null && p.ItemNumber.Contains(keyword))
                    || (p.Barcode != null && p.Barcode.Contains(keyword))
                );
            }

            if (!string.IsNullOrWhiteSpace(query.ProductName))
            {
                baseQuery = baseQuery.Where(p => p.ProductName.Contains(query.ProductName));
            }

            if (!string.IsNullOrWhiteSpace(query.ProductCode))
            {
                baseQuery = baseQuery.Where(p =>
                    p.ProductCode != null && p.ProductCode.Contains(query.ProductCode)
                );
            }

            if (!string.IsNullOrWhiteSpace(query.ItemNumber))
            {
                baseQuery = baseQuery.Where(p =>
                    p.ItemNumber != null && p.ItemNumber.Contains(query.ItemNumber)
                );
            }

            if (!string.IsNullOrWhiteSpace(query.Barcode))
            {
                baseQuery = baseQuery.Where(p =>
                    p.Barcode != null && p.Barcode.Contains(query.Barcode)
                );
            }

            if (query.ProductType.HasValue)
            {
                baseQuery = baseQuery.Where(p => p.ProductType == query.ProductType.Value);
            }

            if (query.IsActive.HasValue)
            {
                baseQuery = baseQuery.Where(p => p.IsActive == query.IsActive.Value);
            }

            if (query.IsSpecialProduct.HasValue)
            {
                baseQuery = baseQuery.Where(p =>
                    p.IsSpecialProduct == query.IsSpecialProduct.Value
                );
            }

            return baseQuery;
        }

        /// <summary>
        /// 批量更新分店商品价格
        /// </summary>
        /// <param name="dto">批量更新数据传输对象</param>
        /// <param name="updatedBy">更新人</param>
        /// <returns>API响应</returns>
        public async Task<ApiResponse<object>> BatchUpdateStoreRetailPricesAsync(
            BatchUpdateStoreRetailPriceDto dto,
            string updatedBy
        )
        {
            try
            {
                if (dto.ProductCodes == null || !dto.ProductCodes.Any())
                {
                    return ApiResponse<object>.Error(
                        "请选择要更新的分店和商品",
                        "VALIDATION_ERROR"
                    );
                }

                // 验证分店编码
                if (string.IsNullOrEmpty(dto.StoreCode))
                {
                    return ApiResponse<object>.Error("请选择要更新的分店", "VALIDATION_ERROR");
                }

                var db = _context.Db;

                await db.Ado.BeginTranAsync();

                try
                {
                    var lockScope = await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                        db,
                        dto.ProductCodes
                    );
                    // 锁内重读目标记录，确保本次写入和后续成本分摊观察同一业务快照。
                    await db.Queryable<StoreRetailPrice>()
                        .Where(x =>
                            x.ProductCode != null
                            && dto.ProductCodes.Contains(x.ProductCode)
                            && x.StoreCode == dto.StoreCode
                            && !x.IsDeleted
                        )
                        .ToListAsync();
                    var query = db.Updateable<StoreRetailPrice>();

                    if (dto.PurchasePrice.HasValue)
                    {
                        query = query.SetColumns(x => x.PurchasePrice == dto.PurchasePrice.Value);
                    }

                    if (dto.StoreRetailPriceValue.HasValue)
                    {
                        query = query.SetColumns(x =>
                            x.StoreRetailPriceValue == dto.StoreRetailPriceValue.Value
                        );
                    }

                    if (dto.IsAutoPricing.HasValue)
                    {
                        query = query.SetColumns(x => x.IsAutoPricing == dto.IsAutoPricing.Value);
                    }

                    if (dto.IsSpecialProduct.HasValue)
                    {
                        query = query.SetColumns(x =>
                            x.IsSpecialProduct == dto.IsSpecialProduct.Value
                        );
                    }

                    if (dto.DiscountRate.HasValue)
                    {
                        query = query.SetColumns(x => x.DiscountRate == dto.DiscountRate.Value);
                    }

                    query = query
                        .SetColumns(x => x.UpdatedAt == DateTime.Now)
                        .SetColumns(x => x.UpdatedBy == updatedBy)
                        .Where(x =>
                            x.ProductCode != null
                            && dto.ProductCodes.Contains(x.ProductCode)
                            && x.IsDeleted == false
                            && x.StoreCode == dto.StoreCode
                        );

                    var affectedRows = await query.ExecuteCommandAsync();

                    if (dto.PurchasePrice.HasValue)
                    {
                        var writeback = await new SetChildPurchasePriceService(db)
                            .RecalculateStoresLockedAsync(
                            lockScope,
                            dto.ProductCodes,
                            new[] { dto.StoreCode },
                            updatedBy
                        );
                        if (writeback.StoreMultiCodeProduct.SkippedGroupCount > 0)
                            throw new InvalidOperationException("目标门店套装子项成本重算不完整");
                    }

                    await db.Ado.CommitTranAsync();

                    return ApiResponse<object>.CreateSuccess($"成功更新 {affectedRows} 条记录");
                }
                catch (Exception)
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量更新分店零售价失败");
                if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
                    return BuildSetChildPurchasePriceBusyResponse<object>();
                return ApiResponse<object>.Error("批量更新失败", "DATABASE_ERROR", ex.Message);
            }
        }

        /// <summary>
        /// 同步商品价格到其他分店
        /// </summary>
        /// <param name="dto">同步数据传输对象</param>
        /// <param name="updatedBy">更新人</param>
        /// <returns>API响应</returns>
        public async Task<ApiResponse<object>> SyncToOtherStoresAsync(
            SyncToOtherStoresDto dto,
            string updatedBy
        )
        {
            try
            {
                if (dto.ProductCodes == null || !dto.ProductCodes.Any())
                {
                    return ApiResponse<object>.Error("请选择要同步的商品", "VALIDATION_ERROR");
                }

                if (dto.TargetStoreCodes == null || !dto.TargetStoreCodes.Any())
                {
                    return ApiResponse<object>.Error("请至少选择一个目标分店", "VALIDATION_ERROR");
                }

                var hasAnySyncField =
                    dto.SyncPurchasePrice
                    || dto.SyncRetailPrice
                    || dto.SyncIsAutoPricing
                    || dto.SyncIsSpecialProduct
                    || dto.SyncDiscountRate;

                if (!hasAnySyncField)
                {
                    return ApiResponse<object>.Error(
                        "请至少选择一个要同步的字段",
                        "VALIDATION_ERROR"
                    );
                }

                var db = _context.Db;

                await db.Ado.BeginTranAsync();

                try
                {
                    var lockScope = await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                        db,
                        dto.ProductCodes
                    );
                    var sourcePrices = await db.Queryable<StoreRetailPrice>()
                        .Where(x =>
                            x.StoreCode == dto.SourceStoreCode
                            && x.ProductCode != null
                            && dto.ProductCodes.Contains(x.ProductCode)
                            && x.IsDeleted == false
                        )
                        .ToListAsync();

                    if (!sourcePrices.Any())
                    {
                        await db.Ado.RollbackTranAsync();
                        return ApiResponse<object>.Error("未找到源分店的价格数据", "NOT_FOUND");
                    }

                    var sourcePriceMap = sourcePrices
                        .Where(x => !string.IsNullOrWhiteSpace(x.ProductCode))
                        .GroupBy(x => x.ProductCode!)
                        .ToDictionary(g => g.Key, g => g.First());

                    var updateable = db.Updateable<StoreRetailPrice>()
                        .SetColumns(x => x.UpdatedAt == DateTime.Now)
                        .SetColumns(x => x.UpdatedBy == updatedBy);

                    if (dto.Mode == SyncModeConstants.Overwrite)
                    {
                        if (dto.SyncRetailPrice)
                        {
                            updateable.SetColumns(x =>
                                x.StoreRetailPriceValue
                                == SqlFunc
                                    .Subqueryable<StoreRetailPrice>()
                                    .Where(s =>
                                        s.StoreCode == dto.SourceStoreCode
                                        && s.ProductCode == x.ProductCode
                                        && s.IsDeleted == false
                                    )
                                    .Select(s => s.StoreRetailPriceValue)
                            );
                        }

                        if (dto.SyncIsAutoPricing)
                        {
                            updateable.SetColumns(x =>
                                x.IsAutoPricing
                                == SqlFunc
                                    .Subqueryable<StoreRetailPrice>()
                                    .Where(s =>
                                        s.StoreCode == dto.SourceStoreCode
                                        && s.ProductCode == x.ProductCode
                                        && s.IsDeleted == false
                                    )
                                    .Select(s => s.IsAutoPricing)
                            );
                        }

                        if (dto.SyncIsSpecialProduct)
                        {
                            updateable.SetColumns(x =>
                                x.IsSpecialProduct
                                == SqlFunc
                                    .Subqueryable<StoreRetailPrice>()
                                    .Where(s =>
                                        s.StoreCode == dto.SourceStoreCode
                                        && s.ProductCode == x.ProductCode
                                        && s.IsDeleted == false
                                    )
                                    .Select(s => s.IsSpecialProduct)
                            );
                        }

                        if (dto.SyncDiscountRate)
                        {
                            updateable.SetColumns(x =>
                                x.DiscountRate
                                == SqlFunc
                                    .Subqueryable<StoreRetailPrice>()
                                    .Where(s =>
                                        s.StoreCode == dto.SourceStoreCode
                                        && s.ProductCode == x.ProductCode
                                        && s.IsDeleted == false
                                    )
                                    .Select(s => s.DiscountRate)
                            );
                        }
                    }
                    else if (dto.Mode == SyncModeConstants.OnlyUpdateNull)
                    {
                        if (dto.SyncRetailPrice)
                        {
                            updateable.SetColumns(x =>
                                x.StoreRetailPriceValue
                                == SqlFunc
                                    .Subqueryable<StoreRetailPrice>()
                                    .Where(s =>
                                        s.StoreCode == dto.SourceStoreCode
                                        && s.ProductCode == x.ProductCode
                                        && s.IsDeleted == false
                                    )
                                    .Select(s => s.StoreRetailPriceValue)
                            );
                            updateable.Where(x => x.StoreRetailPriceValue == null);
                        }

                        if (dto.SyncIsAutoPricing)
                        {
                            updateable.SetColumns(x =>
                                x.IsAutoPricing
                                == SqlFunc
                                    .Subqueryable<StoreRetailPrice>()
                                    .Where(s =>
                                        s.StoreCode == dto.SourceStoreCode
                                        && s.ProductCode == x.ProductCode
                                        && s.IsDeleted == false
                                    )
                                    .Select(s => s.IsAutoPricing)
                            );
                            updateable.Where(x => x.IsAutoPricing == false);
                        }

                        if (dto.SyncIsSpecialProduct)
                        {
                            updateable.SetColumns(x =>
                                x.IsSpecialProduct
                                == SqlFunc
                                    .Subqueryable<StoreRetailPrice>()
                                    .Where(s =>
                                        s.StoreCode == dto.SourceStoreCode
                                        && s.ProductCode == x.ProductCode
                                        && s.IsDeleted == false
                                    )
                                    .Select(s => s.IsSpecialProduct)
                            );
                            updateable.Where(x => x.IsSpecialProduct == false);
                        }

                        if (dto.SyncDiscountRate)
                        {
                            updateable.SetColumns(x =>
                                x.DiscountRate
                                == SqlFunc
                                    .Subqueryable<StoreRetailPrice>()
                                    .Where(s =>
                                        s.StoreCode == dto.SourceStoreCode
                                        && s.ProductCode == x.ProductCode
                                        && s.IsDeleted == false
                                    )
                                    .Select(s => s.DiscountRate)
                            );
                            updateable.Where(x => x.DiscountRate == null);
                        }
                    }

                    // 目标分店被覆盖前的零售价/折扣就是其货架标签上的价格，覆盖后无从推断，必须先留存。
                    var overwrittenPrices = new List<StorePriceOverwrite>();
                    if (_priceTaskService != null && (dto.SyncRetailPrice || dto.SyncDiscountRate))
                    {
                        var targetRows = await db.Queryable<StoreRetailPrice>()
                            .Where(x =>
                                x.StoreCode != null
                                && dto.TargetStoreCodes.Contains(x.StoreCode)
                                && x.ProductCode != null
                                && dto.ProductCodes.Contains(x.ProductCode)
                                && x.IsDeleted == false
                            )
                            .Select(x => new
                            {
                                x.StoreCode,
                                x.ProductCode,
                                x.StoreRetailPriceValue,
                                x.DiscountRate,
                            })
                            .ToListAsync();
                        overwrittenPrices = targetRows
                            .Select(x => new StorePriceOverwrite(
                                x.StoreCode!,
                                x.ProductCode!,
                                x.StoreRetailPriceValue,
                                x.DiscountRate
                            ))
                            .ToList();
                    }

                    var affectedRows = await updateable
                        .Where(x =>
                            x.StoreCode != null
                            && dto.TargetStoreCodes.Contains(x.StoreCode)
                            && x.ProductCode != null
                            && dto.ProductCodes.Contains(x.ProductCode)
                            && x.IsDeleted == false
                        )
                        .ExecuteCommandAsync();

                    if (_priceTaskService != null && overwrittenPrices.Count > 0)
                    {
                        try
                        {
                            await _priceTaskService.RecordStoreOverwritesAsync(
                                overwrittenPrices,
                                new PriceTaskInitiator(updatedBy, "StoreSync", dto.SourceStoreCode)
                            );
                        }
                        catch (Exception notifyEx)
                        {
                            // 通知失败不应回滚价格同步；移动端列表与定时对账会兜底。
                            _logger.LogError(notifyEx, "登记分店待换标签通知失败");
                        }
                    }

                    if (dto.SyncPurchasePrice)
                    {
                        var writeback = await new SetChildPurchasePriceService(db)
                            .RecalculateStoresLockedAsync(
                            lockScope,
                            dto.ProductCodes,
                            dto.TargetStoreCodes,
                            updatedBy
                        );
                        if (writeback.StoreMultiCodeProduct.SkippedGroupCount > 0)
                            throw new InvalidOperationException("目标门店套装子项成本重算不完整");
                    }

                    await db.Ado.CommitTranAsync();

                    return ApiResponse<object>.CreateSuccess($"成功同步 {affectedRows} 条记录");
                }
                catch (Exception)
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "同步到其他分店失败");
                if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
                    return BuildSetChildPurchasePriceBusyResponse<object>();
                return ApiResponse<object>.Error("同步失败", "DATABASE_ERROR", ex.Message);
            }
        }

        /// <summary>
        /// 复制分店数据：将源分店的零售价表和多码表数据复制到目标分店
        /// 使用 Channel 生产者-消费者管道模式，每个目标分店独立 Pipeline 并发执行
        /// </summary>
        public async Task<ApiResponse<CopyStoreDataResultDto>> CopyStoreDataAsync(
            CopyStoreDataDto dto,
            string updatedBy
        )
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dto.SourceStoreCode))
                    return ApiResponse<CopyStoreDataResultDto>.Error(
                        "请选择源分店",
                        "VALIDATION_ERROR"
                    );

                if (dto.TargetStoreCodes == null || !dto.TargetStoreCodes.Any())
                    return ApiResponse<CopyStoreDataResultDto>.Error(
                        "请至少选择一个目标分店",
                        "VALIDATION_ERROR"
                    );

                if (dto.TargetStoreCodes.Contains(dto.SourceStoreCode))
                    return ApiResponse<CopyStoreDataResultDto>.Error(
                        "目标分店不能包含源分店",
                        "VALIDATION_ERROR"
                    );

                _logger.LogInformation(
                    "开始复制分店数据（Pipeline模式）：源分店={SourceStore}，目标分店={TargetStores}，模式={Mode}，操作人={User}",
                    dto.SourceStoreCode,
                    string.Join(",", dto.TargetStoreCodes),
                    dto.Mode,
                    updatedBy
                );

                const int sourcePageSize = 40000;
                const int mergeBatchSize = 10000;
                var db = _context.Db;

                var storeLimiter = new SemaphoreSlim(3, 3);
                var producerLimiter = new SemaphoreSlim(3, 3);
                var consumerLimiter = new SemaphoreSlim(5, 5);

                var allTasks = new List<Task<CopyStoreTargetResult>>();

                foreach (var targetStore in dto.TargetStoreCodes)
                {
                    await storeLimiter.WaitAsync();
                    var store = targetStore;

                    allTasks.Add(
                        Task.Run(async () =>
                        {
                            var targetResult = new CopyStoreTargetResult(store);
                            try
                            {
                                // 批次事务各自提交；后续失败时仍要保留已提交批次的真实计数。
                                var progress = new InlineProgress<CopyProgressDto>(
                                    targetResult.RecordCommittedBatch
                                );
                                int retailCount = await ProcessStoreRetailPricePipelineAsync(
                                    db.CopyNew(),
                                    dto.SourceStoreCode,
                                    store,
                                    dto,
                                    updatedBy,
                                    sourcePageSize,
                                    mergeBatchSize,
                                    producerLimiter,
                                    consumerLimiter,
                                    progress,
                                    CancellationToken.None
                                );
                                targetResult.RecordRetailCompletion(retailCount);

                                int multiCount = 0;
                                if (dto.SyncMultiCode)
                                {
                                    multiCount =
                                        await ProcessStoreMultiCodePipelineAsync(
                                            db.CopyNew(),
                                            dto.SourceStoreCode,
                                            store,
                                            dto,
                                            updatedBy,
                                            sourcePageSize,
                                            mergeBatchSize,
                                            producerLimiter,
                                            consumerLimiter,
                                            progress,
                                            CancellationToken.None
                                    );
                                    targetResult.RecordMultiCodeCompletion(multiCount);
                                }

                                await RecalculateCopiedStoreCostsAsync(
                                    db.CopyNew(),
                                    dto.SourceStoreCode,
                                    store,
                                    dto,
                                    updatedBy
                                );

                                targetResult.MarkCompleted();
                            }
                            catch (Exception ex)
                            {
                                // 单个目标失败不取消其它目标，最终按已提交目标/批次返回真实结果。
                                targetResult.RecordFailure(ex);
                            }
                            finally
                            {
                                storeLimiter.Release();
                            }

                            return targetResult;
                        })
                    );
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var results = await Task.WhenAll(allTasks);
                sw.Stop();

                int retailPriceCopied = results.Sum(r => r.RetailPriceCopied);
                int multiCodeCopied = results.Sum(r => r.MultiCodeCopied);
                var failures = results.Where(r => !r.Completed).ToList();
                var submitted = results.Where(r => r.HasCommittedWork).ToList();

                _logger.LogInformation(
                    "复制完成（Pipeline模式）：零售价 {RetailCopied} 条，多码 {MultiCodeCopied} 条，耗时 {Elapsed}ms",
                    retailPriceCopied,
                    multiCodeCopied,
                    sw.ElapsedMilliseconds
                );

                var copyResult = new CopyStoreDataResultDto
                {
                    StoreRetailPriceCopied = retailPriceCopied,
                    StoreMultiCodeProductCopied = multiCodeCopied,
                };
                if (!failures.Any())
                {
                    return ApiResponse<CopyStoreDataResultDto>.OK(
                        copyResult,
                        $"复制完成：零售价 {retailPriceCopied} 条，多码 {multiCodeCopied} 条"
                    );
                }

                var failureDetails = failures.Select(result => new
                {
                    result.TargetStoreCode,
                    ErrorCode = result.IsBusy
                        ? SetChildPurchasePriceMutationLock.BusyErrorCode
                        : "COPY_STORE_DATA_FAILED",
                    Message = result.Failure?.Message ?? "复制失败",
                    result.RetailPriceCopied,
                    result.MultiCodeCopied,
                    result.CommittedBatchCount,
                }).ToList();

                if (submitted.Any())
                {
                    // 已提交批次不能回滚；以成功响应避免客户端将已落库数据错误重试。
                    return new ApiResponse<CopyStoreDataResultDto>
                    {
                        Success = true,
                        Data = copyResult,
                        ErrorCode = "PARTIAL_SUCCESS",
                        Message = $"部分成功：已提交 {submitted.Count} 个目标门店，零售价 {retailPriceCopied} 条，多码 {multiCodeCopied} 条",
                        Details = new
                        {
                            SubmittedTargetCount = submitted.Count,
                            SubmittedBatchCount = submitted.Sum(result => result.CommittedBatchCount),
                            FailureDetails = failureDetails,
                        },
                    };
                }

                var allBusy = failures.All(result => result.IsBusy);
                return ApiResponse<CopyStoreDataResultDto>.Error(
                    allBusy ? "套装商品正在被其他操作修改，请稍后重试" : "复制失败",
                    allBusy
                        ? SetChildPurchasePriceMutationLock.BusyErrorCode
                        : "DATABASE_ERROR",
                    new { FailureDetails = failureDetails }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "复制分店数据失败");
                if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out var conflict))
                {
                    return ApiResponse<CopyStoreDataResultDto>.Error(
                        "套装商品正在被其他操作修改，请稍后重试",
                        SetChildPurchasePriceMutationLock.BusyErrorCode,
                        new
                        {
                            FailureDetails = new[]
                            {
                                new
                                {
                                    TargetStoreCode = string.Empty,
                                    ErrorCode = SetChildPurchasePriceMutationLock.BusyErrorCode,
                                    Message = conflict?.Message ?? ex.Message,
                                    RetailPriceCopied = 0,
                                    MultiCodeCopied = 0,
                                    CommittedBatchCount = 0,
                                },
                            },
                        }
                    );
                }
                return ApiResponse<CopyStoreDataResultDto>.Error(
                    "复制失败",
                    "DATABASE_ERROR",
                    ex.Message
                );
            }
        }

        public async IAsyncEnumerable<CopyProgressDto> CopyStoreDataWithProgressAsync(
            CopyStoreDataDto dto,
            string updatedBy,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            if (string.IsNullOrWhiteSpace(dto.SourceStoreCode))
            {
                yield return new CopyProgressDto
                {
                    EventType = "error",
                    Message = "请选择源分店",
                    Timestamp = DateTime.UtcNow
                };
                yield break;
            }

            if (dto.TargetStoreCodes == null || !dto.TargetStoreCodes.Any())
            {
                yield return new CopyProgressDto
                {
                    EventType = "error",
                    Message = "请至少选择一个目标分店",
                    Timestamp = DateTime.UtcNow
                };
                yield break;
            }

            if (dto.TargetStoreCodes.Contains(dto.SourceStoreCode))
            {
                yield return new CopyProgressDto
                {
                    EventType = "error",
                    Message = "目标分店不能包含源分店",
                    Timestamp = DateTime.UtcNow
                };
                yield break;
            }

            _logger.LogInformation(
                "开始复制分店数据（SSE进度模式）：源分店={SourceStore}，目标分店={TargetStores}，模式={Mode}，操作人={User}",
                dto.SourceStoreCode,
                string.Join(",", dto.TargetStoreCodes),
                dto.Mode,
                updatedBy
            );

            const int sourcePageSize = 40000;
            const int mergeBatchSize = 10000;
            var db = _context.Db;

            var storeLimiter = new SemaphoreSlim(3, 3);
            var producerLimiter = new SemaphoreSlim(3, 3);
            var consumerLimiter = new SemaphoreSlim(5, 5);

            var progressChannel = Channel.CreateBounded<CopyProgressDto>(
                new BoundedChannelOptions(50)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = true,
                }
            );

            int totalRetail = 0;
            int totalMulti = 0;
            int storeIndex = 0;

            Exception? processingFailure = null;
            var processingTask = Task.Run(async () =>
            {
                try
                {
                    foreach (var targetStore in dto.TargetStoreCodes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await storeLimiter.WaitAsync(cancellationToken);
                        var store = targetStore;
                        storeIndex++;

                        await progressChannel.Writer.WriteAsync(new CopyProgressDto
                        {
                            EventType = "store_started",
                            StoreCode = store,
                            StoreIndex = storeIndex,
                            TotalStores = dto.TargetStoreCodes.Count,
                            Message = $"开始处理分店 {store}...",
                            Timestamp = DateTime.UtcNow
                        }, cancellationToken);

                        try
                        {
                            var progress = new Progress<CopyProgressDto>(p =>
                            {
                                p.StoreIndex = storeIndex;
                                p.TotalStores = dto.TargetStoreCodes.Count;
                                progressChannel.Writer.TryWrite(p);
                            });

                            var retailCount = await ProcessStoreRetailPricePipelineAsync(
                                db.CopyNew(),
                                dto.SourceStoreCode,
                                store,
                                dto,
                                updatedBy,
                                sourcePageSize,
                                mergeBatchSize,
                                producerLimiter,
                                consumerLimiter,
                                progress,
                                cancellationToken
                            );

                            var multiCount = dto.SyncMultiCode
                                ? await ProcessStoreMultiCodePipelineAsync(
                                    db.CopyNew(),
                                    dto.SourceStoreCode,
                                    store,
                                    dto,
                                    updatedBy,
                                    sourcePageSize,
                                    mergeBatchSize,
                                    producerLimiter,
                                    consumerLimiter,
                                    progress,
                                    cancellationToken
                                )
                                : 0;
                            await RecalculateCopiedStoreCostsAsync(
                                db.CopyNew(),
                                dto.SourceStoreCode,
                                store,
                                dto,
                                updatedBy
                            );
                            totalRetail += retailCount;
                            totalMulti += multiCount;

                            await progressChannel.Writer.WriteAsync(new CopyProgressDto
                            {
                                EventType = "store_completed",
                                StoreCode = store,
                                StoreIndex = storeIndex,
                                TotalStores = dto.TargetStoreCodes.Count,
                                RetailPriceCopied = totalRetail,
                                MultiCodeCopied = totalMulti,
                                Message = $"分店 {store} 完成",
                                Timestamp = DateTime.UtcNow
                            }, cancellationToken);
                        }
                        finally
                        {
                            storeLimiter.Release();
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // 客户端断开时读端会自行取消；仍需完成 Channel，避免后台任务遗留。
                }
                catch (Exception ex)
                {
                    processingFailure = ex;
                    _logger.LogError(ex, "复制分店数据 SSE 处理失败");
                    var isBusy = SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _);
                    var message = isBusy
                        ? "套装商品正在被其他操作修改，请稍后重试"
                        : ex.Message;
                    try
                    {
                        await progressChannel.Writer.WriteAsync(new CopyProgressDto
                        {
                            EventType = "error",
                            ErrorCode = isBusy
                                ? SetChildPurchasePriceMutationLock.BusyErrorCode
                                : "COPY_STORE_DATA_FAILED",
                            Message = message,
                            Timestamp = DateTime.UtcNow,
                        }, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // 响应已断开，无需再等待错误事件写入。
                    }
                }
                finally
                {
                    // 所有路径都关闭写端，保证 ReadAllAsync 不会因 Channel 未完成而挂起。
                    progressChannel.Writer.TryComplete();
                }
            }, CancellationToken.None);

            await foreach (var progress in progressChannel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return progress;
            }

            await processingTask;

            if (processingFailure != null)
                yield break;

            yield return new CopyProgressDto
            {
                EventType = "completed",
                TotalStores = dto.TargetStoreCodes.Count,
                RetailPriceCopied = totalRetail,
                MultiCodeCopied = totalMulti,
                Message = $"全部完成：零售价 {totalRetail} 条，多码 {totalMulti} 条",
                Timestamp = DateTime.UtcNow
            };
        }

        private async Task<int> ProcessStoreRetailPricePipelineAsync(
            ISqlSugarClient db,
            string sourceStoreCode,
            string targetStoreCode,
            CopyStoreDataDto dto,
            string updatedBy,
            int sourcePageSize,
            int mergeBatchSize,
            SemaphoreSlim producerLimiter,
            SemaphoreSlim consumerLimiter,
            IProgress<CopyProgressDto>? progress,
            CancellationToken cancellationToken = default
        )
        {
            var channel = Channel.CreateBounded<List<string>>(
                new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = true,
                }
            );

            var producerTask = Task.Run(async () =>
            {
                var producerDb = db.CopyNew();
                try
                {
                    int pageIndex = 0;
                    while (true)
                    {
                        await producerLimiter.WaitAsync();
                        List<string> page;
                        try
                        {
                            page = await producerDb
                                .Queryable<StoreRetailPrice>()
                                .With(SqlWith.NoLock)
                                .Where(x => x.StoreCode == sourceStoreCode && x.IsDeleted == false)
                                .Skip(pageIndex * sourcePageSize)
                                .Take(sourcePageSize)
                                // 锁前只读取候选商品键；绝不把 NOLOCK 行快照带入写入决策。
                                .Select(x => x.ProductCode!)
                                .ToListAsync();
                        }
                        finally
                        {
                            producerLimiter.Release();
                        }
                        if (page == null || !page.Any())
                            break;
                        await channel.Writer.WriteAsync(page);
                        _logger.LogDebug(
                            "[零售价-Pipeline] 源分店 {Source} → 目标 {Target}：第 {Page} 页加载 {Count} 条",
                            sourceStoreCode,
                            targetStoreCode,
                            pageIndex + 1,
                            page.Count
                        );
                        if (page.Count < sourcePageSize)
                            break;
                        pageIndex++;
                    }
                }
                finally
                {
                    channel.Writer.Complete();
                }
            });

            var consumerTask = Task.Run(async () =>
            {
                var toMerge = new List<string>();
                int totalCopied = 0;
                int batchCount = 0;

                await foreach (var sourcePage in channel.Reader.ReadAllAsync())
                {
                    foreach (var source in sourcePage)
                    {
                        if (string.IsNullOrWhiteSpace(source))
                            continue;

                        // 缓存只保留候选键；源、目标和套装关系均在写批次锁内重新读取。
                        toMerge.Add(source);
                    }

                    if (toMerge.Count >= mergeBatchSize)
                    {
                        var batch = toMerge.Take(mergeBatchSize).ToList();
                        toMerge = toMerge.Skip(mergeBatchSize).ToList();
                        await consumerLimiter.WaitAsync();
                        try
                        {
                            var batchDb = db.CopyNew();
                            await batchDb.Ado.BeginTranAsync();
                            try
                            {
                                int count = await ApplyRetailCopyBatchAsync(
                                    batchDb, batch, sourceStoreCode, targetStoreCode, dto, updatedBy
                                );
                                await batchDb.Ado.CommitTranAsync();
                                totalCopied += count;
                                _logger.LogDebug(
                                    "[零售价-Pipeline] 目标 {Target} 批量合并 {Count} 条",
                                    targetStoreCode,
                                    count
                                );
                                batchCount++;
                                progress?.Report(new CopyProgressDto
                                {
                                    EventType = "batch_completed",
                                    StoreCode = targetStoreCode,
                                    RetailPriceCopied = totalCopied,
                                    BatchCount = batchCount,
                                    Message = $"[零售价] 批量写入 {count} 条 (累计 {totalCopied})",
                                    Timestamp = DateTime.UtcNow
                                });
                            }
                            catch (Exception)
                            {
                                await batchDb.Ado.RollbackTranAsync();
                                throw;
                            }
                        }
                        finally
                        {
                            consumerLimiter.Release();
                        }
                    }
                }

                if (toMerge.Any())
                {
                    await consumerLimiter.WaitAsync();
                    try
                    {
                        var batchDb = db.CopyNew();
                        await batchDb.Ado.BeginTranAsync();
                        try
                        {
                            int count = await ApplyRetailCopyBatchAsync(
                                batchDb, toMerge, sourceStoreCode, targetStoreCode, dto, updatedBy
                            );
                            await batchDb.Ado.CommitTranAsync();
                            totalCopied += count;
                            batchCount++;
                            progress?.Report(new CopyProgressDto
                            {
                                EventType = "batch_completed",
                                StoreCode = targetStoreCode,
                                RetailPriceCopied = totalCopied,
                                BatchCount = batchCount,
                                Message = $"[零售价] 最终批量写入 {count} 条",
                                Timestamp = DateTime.UtcNow
                            });
                        }
                        catch (Exception)
                        {
                            await batchDb.Ado.RollbackTranAsync();
                            throw;
                        }
                    }
                    finally
                    {
                        consumerLimiter.Release();
                    }
                }

                _logger.LogInformation(
                    "[零售价-Pipeline] 目标分店 {Target} 处理完成，总计 {Count} 条",
                    targetStoreCode,
                    totalCopied
                );
                progress?.Report(new CopyProgressDto
                {
                    EventType = "store_completed",
                    StoreCode = targetStoreCode,
                    RetailPriceCopied = totalCopied,
                    Message = $"[零售价] 分店 {targetStoreCode} 完成，共 {totalCopied} 条",
                    Timestamp = DateTime.UtcNow
                });
                return totalCopied;
            });

            await Task.WhenAll(producerTask, consumerTask);
            return consumerTask.Result;
        }

        private static async Task<int> ApplyRetailCopyBatchAsync(
            ISqlSugarClient db,
            List<string> candidateProductCodes,
            string sourceStoreCode,
            string targetStoreCode,
            CopyStoreDataDto dto,
            string updatedBy
        )
        {
            var productCodes = candidateProductCodes
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (productCodes.Count == 0)
                return 0;

            _ = await SetChildPurchasePriceMutationLock.AcquireProductsAsync(db, productCodes);
            // 锁内重读源、目标与关系；锁前 NOLOCK 结果只负责缩小候选范围。
            var sources = await db.Queryable<StoreRetailPrice>()
                .Where(x =>
                    x.StoreCode == sourceStoreCode
                    && x.ProductCode != null
                    && productCodes.Contains(x.ProductCode)
                    && !x.IsDeleted
                )
                .ToListAsync();
            var targets = await db.Queryable<StoreRetailPrice>()
                .Where(x => x.StoreCode == targetStoreCode && x.ProductCode != null && productCodes.Contains(x.ProductCode) && !x.IsDeleted)
                .ToListAsync();
            var targetByProduct = targets
                .Where(x => !string.IsNullOrWhiteSpace(x.ProductCode))
                .GroupBy(x => x.ProductCode!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            var count = 0;

            foreach (var source in sources.Where(x => !string.IsNullOrWhiteSpace(x.ProductCode)))
            {
                if (!targetByProduct.TryGetValue(source.ProductCode!, out var target))
                {
                    await db.Insertable(new StoreRetailPrice
                    {
                        UUID = UuidHelper.GenerateUuid7(), StoreCode = targetStoreCode,
                        ProductCode = source.ProductCode, StoreProductCode = targetStoreCode + source.ProductCode,
                        SupplierCode = source.SupplierCode,
                        // 跨店复制绝不传播源分店主成本；目标成本由本店主成本和统一重算确定。
                        PurchasePrice = null,
                        StoreRetailPriceValue = dto.SyncRetailPrice ? source.StoreRetailPriceValue : null,
                        DiscountRate = dto.SyncDiscountRate ? source.DiscountRate : null,
                        IsAutoPricing = dto.SyncIsAutoPricing ? source.IsAutoPricing : false,
                        IsSpecialProduct = dto.SyncIsSpecialProduct ? source.IsSpecialProduct : false,
                        IsActive = true, IsDeleted = false, CreatedAt = DateTime.UtcNow,
                        CreatedBy = updatedBy, UpdatedAt = DateTime.UtcNow, UpdatedBy = updatedBy,
                    }).ExecuteCommandAsync();
                    count++;
                    continue;
                }

                var update = db.Updateable<StoreRetailPrice>().Where(x => x.UUID == target.UUID && !x.IsDeleted);
                var changed = false;
                if (dto.SyncRetailPrice && source.StoreRetailPriceValue.HasValue && (dto.Mode == "Overwrite" || !target.StoreRetailPriceValue.HasValue)) { update = update.SetColumns(x => x.StoreRetailPriceValue == source.StoreRetailPriceValue); changed = true; }
                if (dto.SyncDiscountRate && source.DiscountRate.HasValue && (dto.Mode == "Overwrite" || !target.DiscountRate.HasValue)) { update = update.SetColumns(x => x.DiscountRate == source.DiscountRate); changed = true; }
                if (dto.SyncIsAutoPricing && (dto.Mode == "Overwrite" || !target.IsAutoPricing)) { update = update.SetColumns(x => x.IsAutoPricing == source.IsAutoPricing); changed = true; }
                if (dto.SyncIsSpecialProduct && (dto.Mode == "Overwrite" || !target.IsSpecialProduct)) { update = update.SetColumns(x => x.IsSpecialProduct == source.IsSpecialProduct); changed = true; }
                if (changed)
                {
                    await update.SetColumns(x => x.UpdatedAt == DateTime.UtcNow).SetColumns(x => x.UpdatedBy == updatedBy).ExecuteCommandAsync();
                    count++;
                }
            }

            return count;
        }

        private async Task<int> ProcessStoreMultiCodePipelineAsync(
            ISqlSugarClient db,
            string sourceStoreCode,
            string targetStoreCode,
            CopyStoreDataDto dto,
            string updatedBy,
            int sourcePageSize,
            int mergeBatchSize,
            SemaphoreSlim producerLimiter,
            SemaphoreSlim consumerLimiter,
            IProgress<CopyProgressDto>? progress,
            CancellationToken cancellationToken = default
        )
        {
            var channel = Channel.CreateBounded<List<StoreMultiCodeCopyCandidate>>(
                new BoundedChannelOptions(2)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = true,
                }
            );

            var producerTask = Task.Run(async () =>
            {
                var producerDb = db.CopyNew();
                try
                {
                    int pageIndex = 0;
                    while (true)
                    {
                        await producerLimiter.WaitAsync();
                        List<StoreMultiCodeCopyCandidate> page;
                        try
                        {
                            page = await producerDb
                                .Queryable<StoreMultiCodeProduct>()
                                .With(SqlWith.NoLock)
                                .Where(x => x.StoreCode == sourceStoreCode && x.IsDeleted == false)
                                .Skip(pageIndex * sourcePageSize)
                                .Take(sourcePageSize)
                                // 管道缓存只能承载候选业务键，不能承载用于写入的 NOLOCK 快照值。
                                .Select(x => new StoreMultiCodeCopyCandidate
                                {
                                    ProductCode = x.ProductCode,
                                    MultiCodeProductCode = x.MultiCodeProductCode,
                                })
                                .ToListAsync();
                        }
                        finally
                        {
                            producerLimiter.Release();
                        }
                        if (page == null || !page.Any())
                            break;
                        await channel.Writer.WriteAsync(page);
                        _logger.LogDebug(
                            "[多码-Pipeline] 源分店 {Source} → 目标 {Target}：第 {Page} 页加载 {Count} 条",
                            sourceStoreCode,
                            targetStoreCode,
                            pageIndex + 1,
                            page.Count
                        );
                        if (page.Count < sourcePageSize)
                            break;
                        pageIndex++;
                    }
                }
                finally
                {
                    channel.Writer.Complete();
                }
            });

            var consumerTask = Task.Run(async () =>
            {
                var toMerge = new List<StoreMultiCodeCopyCandidate>();
                int totalCopied = 0;
                int batchCount = 0;

                await foreach (var sourcePage in channel.Reader.ReadAllAsync())
                {
                    foreach (var source in sourcePage)
                    {
                        if (string.IsNullOrWhiteSpace(source.ProductCode))
                            continue;

                        // SetType 与目标行必须在锁内复读，不能以管道缓存决定成本写入。
                        toMerge.Add(source);
                    }

                    if (toMerge.Count >= mergeBatchSize)
                    {
                        var batch = toMerge.Take(mergeBatchSize).ToList();
                        toMerge = toMerge.Skip(mergeBatchSize).ToList();
                        await consumerLimiter.WaitAsync();
                        try
                        {
                            var batchDb = db.CopyNew();
                            await batchDb.Ado.BeginTranAsync();
                            try
                            {
                                int count = await ApplyMultiCodeCopyBatchAsync(
                                    batchDb, batch, sourceStoreCode, targetStoreCode, dto, updatedBy
                                );
                                await batchDb.Ado.CommitTranAsync();
                                totalCopied += count;
                                _logger.LogDebug(
                                    "[多码-Pipeline] 目标 {Target} 批量合并 {Count} 条",
                                    targetStoreCode,
                                    count
                                );
                                batchCount++;
                                progress?.Report(new CopyProgressDto
                                {
                                    EventType = "batch_completed",
                                    StoreCode = targetStoreCode,
                                    MultiCodeCopied = totalCopied,
                                    BatchCount = batchCount,
                                    Message = $"[多码] 批量写入 {count} 条 (累计 {totalCopied})",
                                    Timestamp = DateTime.UtcNow
                                });
                            }
                            catch (Exception)
                            {
                                await batchDb.Ado.RollbackTranAsync();
                                throw;
                            }
                        }
                        finally
                        {
                            consumerLimiter.Release();
                        }
                    }
                }

                if (toMerge.Any())
                {
                    await consumerLimiter.WaitAsync();
                    try
                    {
                        var batchDb = db.CopyNew();
                        await batchDb.Ado.BeginTranAsync();
                        try
                        {
                            int count = await ApplyMultiCodeCopyBatchAsync(
                                batchDb, toMerge, sourceStoreCode, targetStoreCode, dto, updatedBy
                            );
                            await batchDb.Ado.CommitTranAsync();
                            totalCopied += count;
                            batchCount++;
                            progress?.Report(new CopyProgressDto
                            {
                                EventType = "batch_completed",
                                StoreCode = targetStoreCode,
                                MultiCodeCopied = totalCopied,
                                BatchCount = batchCount,
                                Message = $"[多码] 最终批量写入 {count} 条",
                                Timestamp = DateTime.UtcNow
                            });
                        }
                        catch (Exception)
                        {
                            await batchDb.Ado.RollbackTranAsync();
                            throw;
                        }
                    }
                    finally
                    {
                        consumerLimiter.Release();
                    }
                }

                _logger.LogInformation(
                    "[多码-Pipeline] 目标分店 {Target} 处理完成，总计 {Count} 条",
                    targetStoreCode,
                    totalCopied
                );
                progress?.Report(new CopyProgressDto
                {
                    EventType = "store_completed",
                    StoreCode = targetStoreCode,
                    MultiCodeCopied = totalCopied,
                    Message = $"[多码] 分店 {targetStoreCode} 完成，共 {totalCopied} 条",
                    Timestamp = DateTime.UtcNow
                });
                return totalCopied;
            });

            await Task.WhenAll(producerTask, consumerTask);
            return consumerTask.Result;
        }

        private static async Task<int> ApplyMultiCodeCopyBatchAsync(
            ISqlSugarClient db,
            List<StoreMultiCodeCopyCandidate> candidateRows,
            string sourceStoreCode,
            string targetStoreCode,
            CopyStoreDataDto dto,
            string updatedBy
        )
        {
            var productCodes = candidateRows
                .Select(x => x.ProductCode)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (productCodes.Count == 0)
                return 0;

            _ = await SetChildPurchasePriceMutationLock.AcquireProductsAsync(db, productCodes);
            // 进入业务锁后才读取源、目标和套装关系，避免 NOLOCK 快照决定任何写入值。
            var candidateKeys = candidateRows
                .Where(x => !string.IsNullOrWhiteSpace(x.ProductCode))
                .Select(x => $"{x.ProductCode}\u0001{x.MultiCodeProductCode}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sources = (await db.Queryable<StoreMultiCodeProduct>()
                    .Where(x =>
                        x.StoreCode == sourceStoreCode
                        && x.ProductCode != null
                        && productCodes.Contains(x.ProductCode)
                        && !x.IsDeleted
                    )
                    .ToListAsync())
                .Where(x => candidateKeys.Contains($"{x.ProductCode}\u0001{x.MultiCodeProductCode}"))
                .ToList();
            var targets = await db.Queryable<StoreMultiCodeProduct>()
                .Where(x => x.StoreCode == targetStoreCode && x.ProductCode != null && productCodes.Contains(x.ProductCode) && !x.IsDeleted)
                .ToListAsync();
            var targetByKey = targets
                .Where(x => !string.IsNullOrWhiteSpace(x.ProductCode))
                .GroupBy(x => $"{x.ProductCode}\u0001{x.MultiCodeProductCode}", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            _ = await db.Queryable<ProductSetCode>()
                .Where(x =>
                    x.ProductCode != null
                    && productCodes.Contains(x.ProductCode)
                    && x.SetProductCode != null
                )
                .Select(x => new { x.ProductCode, x.SetProductCode, x.SetType, x.IsActive, x.IsDeleted })
                .ToListAsync();
            var count = 0;

            foreach (var source in sources.Where(x => !string.IsNullOrWhiteSpace(x.ProductCode)))
            {
                var key = $"{source.ProductCode}\u0001{source.MultiCodeProductCode}";
                if (!targetByKey.TryGetValue(key, out var target))
                {
                    await db.Insertable(new StoreMultiCodeProduct
                    {
                        UUID = UuidHelper.GenerateUuid7(), StoreCode = targetStoreCode, ProductCode = source.ProductCode,
                        MultiCodeProductCode = source.MultiCodeProductCode, StoreMultiCodeProductCode = targetStoreCode + source.MultiCodeProductCode,
                        MultiBarcode = source.MultiBarcode,
                        // Type1/Type2 一律不复制源分店成本；最终成本由目标主成本统一计算。
                        PurchasePrice = null,
                        MultiCodeRetailPrice = dto.SyncMultiCodeRetailPrice ? source.MultiCodeRetailPrice : null,
                        DiscountRate = dto.SyncDiscountRate ? source.DiscountRate : null,
                        IsAutoPricing = dto.SyncIsAutoPricing ? source.IsAutoPricing : false,
                        IsSpecialProduct = dto.SyncIsSpecialProduct ? source.IsSpecialProduct : false,
                        IsActive = true, IsDeleted = false, CreatedAt = DateTime.UtcNow, CreatedBy = updatedBy,
                        UpdatedAt = DateTime.UtcNow, UpdatedBy = updatedBy,
                    }).ExecuteCommandAsync();
                    count++;
                    continue;
                }

                var update = db.Updateable<StoreMultiCodeProduct>().Where(x => x.UUID == target.UUID && !x.IsDeleted);
                var changed = false;
                if (dto.SyncMultiCodeRetailPrice && source.MultiCodeRetailPrice.HasValue && (dto.Mode == "Overwrite" || !target.MultiCodeRetailPrice.HasValue)) { update = update.SetColumns(x => x.MultiCodeRetailPrice == source.MultiCodeRetailPrice); changed = true; }
                if (dto.SyncDiscountRate && source.DiscountRate.HasValue && (dto.Mode == "Overwrite" || !target.DiscountRate.HasValue)) { update = update.SetColumns(x => x.DiscountRate == source.DiscountRate); changed = true; }
                if (dto.SyncIsAutoPricing && (dto.Mode == "Overwrite" || !target.IsAutoPricing)) { update = update.SetColumns(x => x.IsAutoPricing == source.IsAutoPricing); changed = true; }
                if (dto.SyncIsSpecialProduct && (dto.Mode == "Overwrite" || !target.IsSpecialProduct)) { update = update.SetColumns(x => x.IsSpecialProduct == source.IsSpecialProduct); changed = true; }
                if (changed)
                {
                    await update.SetColumns(x => x.UpdatedAt == DateTime.UtcNow).SetColumns(x => x.UpdatedBy == updatedBy).ExecuteCommandAsync();
                    count++;
                }
            }

            return count;
        }

        private static async Task RecalculateCopiedStoreCostsAsync(
            ISqlSugarClient db,
            string sourceStoreCode,
            string targetStoreCode,
            CopyStoreDataDto dto,
            string updatedBy
        )
        {
            if (!dto.SyncMultiCode)
            {
                return;
            }

            // 锁前仍只取候选主商品键；真正用于回写的主成本、关系和子项都在事务锁内重读。
            var candidateProductCodes = await db.Queryable<StoreMultiCodeProduct>()
                .With(SqlWith.NoLock)
                .Where(x => x.StoreCode == sourceStoreCode && x.ProductCode != null && !x.IsDeleted)
                .Select(x => x.ProductCode!)
                .ToListAsync();
            var productCodes = candidateProductCodes
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (productCodes.Count == 0)
            {
                return;
            }

            await db.Ado.BeginTranAsync();
            try
            {
                var lockScope = await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                    db,
                    productCodes
                );
                var recalculation = await new SetChildPurchasePriceService(db)
                    .RecalculateStoresLockedAsync(
                        lockScope,
                        productCodes,
                        new[] { targetStoreCode },
                        updatedBy
                    );
                if (recalculation.StoreMultiCodeProduct.SkippedGroupCount > 0)
                {
                    throw new InvalidOperationException("目标门店套装子项成本重算不完整");
                }

                var type2Relations = await db.Queryable<ProductSetCode>()
                    .Where(x =>
                        x.ProductCode != null
                        && x.SetProductCode != null
                        && productCodes.Contains(x.ProductCode)
                        && x.SetType == 2
                        && x.IsActive
                        && !x.IsDeleted
                    )
                    .ToListAsync();
                if (type2Relations.Count > 0)
                {
                    var targetPrices = await db.Queryable<StoreRetailPrice>()
                        .Where(x =>
                            x.StoreCode == targetStoreCode
                            && x.ProductCode != null
                            && productCodes.Contains(x.ProductCode)
                            && x.IsActive
                            && !x.IsDeleted
                        )
                        .ToListAsync();
                    var products = await db.Queryable<Product>()
                        .Where(x => x.ProductCode != null && productCodes.Contains(x.ProductCode) && !x.IsDeleted)
                        .ToListAsync();
                    var parentCosts = targetPrices
                        .Where(x => !string.IsNullOrWhiteSpace(x.ProductCode))
                        .GroupBy(x => x.ProductCode!, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(x => x.Key, x => x.First().PurchasePrice, StringComparer.OrdinalIgnoreCase);
                    foreach (var product in products.Where(x => !string.IsNullOrWhiteSpace(x.ProductCode)))
                    {
                        if (!parentCosts.ContainsKey(product.ProductCode!))
                        {
                            parentCosts[product.ProductCode!] = product.PurchasePrice;
                        }
                    }
                    var type2Keys = type2Relations
                        .Select(x => $"{x.ProductCode}\u0001{x.SetProductCode}")
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var targetMultiRows = await db.Queryable<StoreMultiCodeProduct>()
                        .Where(x =>
                            x.StoreCode == targetStoreCode
                            && x.ProductCode != null
                            && productCodes.Contains(x.ProductCode)
                            && x.IsActive
                            && !x.IsDeleted
                        )
                        .ToListAsync();
                    var updates = targetMultiRows
                        .Where(x =>
                            !string.IsNullOrWhiteSpace(x.MultiCodeProductCode)
                            && type2Keys.Contains($"{x.ProductCode}\u0001{x.MultiCodeProductCode}")
                            && parentCosts.TryGetValue(x.ProductCode!, out var parentCost)
                            && x.PurchasePrice != parentCost
                        )
                        .ToList();
                    foreach (var row in updates)
                    {
                        row.PurchasePrice = parentCosts[row.ProductCode!];
                        row.UpdatedAt = DateTime.UtcNow;
                        row.UpdatedBy = updatedBy;
                    }
                    if (updates.Count > 0)
                    {
                        await db.Updateable(updates)
                            .UpdateColumns(x => new { x.PurchasePrice, x.UpdatedAt, x.UpdatedBy })
                            .ExecuteCommandAsync();
                    }
                }

                await db.Ado.CommitTranAsync();
            }
            catch
            {
                await db.Ado.RollbackTranAsync();
                throw;
            }
        }

        private sealed class StoreMultiCodeCopyCandidate
        {
            public string? ProductCode { get; init; }
            public string? MultiCodeProductCode { get; init; }
        }

        private sealed class CopyStoreTargetResult
        {
            private int _retailPriceCopied;
            private int _multiCodeCopied;
            private int _retailBatchCount;
            private int _multiCodeBatchCount;

            public CopyStoreTargetResult(string targetStoreCode)
            {
                TargetStoreCode = targetStoreCode;
            }

            public string TargetStoreCode { get; }
            public Exception? Failure { get; private set; }
            public bool Completed { get; private set; }
            public int RetailPriceCopied => Volatile.Read(ref _retailPriceCopied);
            public int MultiCodeCopied => Volatile.Read(ref _multiCodeCopied);
            public int CommittedBatchCount =>
                Volatile.Read(ref _retailBatchCount) + Volatile.Read(ref _multiCodeBatchCount);
            public bool HasCommittedWork => Completed || CommittedBatchCount > 0;
            public bool IsBusy => SetChildPurchasePriceMutationLock.TryResolveConflict(Failure, out _);

            public void RecordCommittedBatch(CopyProgressDto progress)
            {
                if (progress.EventType != "batch_completed")
                    return;

                if (progress.Message.StartsWith("[零售价]", StringComparison.Ordinal))
                {
                    Interlocked.Exchange(ref _retailPriceCopied, progress.RetailPriceCopied);
                    Interlocked.Exchange(ref _retailBatchCount, progress.BatchCount);
                }
                else if (progress.Message.StartsWith("[多码]", StringComparison.Ordinal))
                {
                    Interlocked.Exchange(ref _multiCodeCopied, progress.MultiCodeCopied);
                    Interlocked.Exchange(ref _multiCodeBatchCount, progress.BatchCount);
                }
            }

            public void RecordRetailCompletion(int count) =>
                Interlocked.Exchange(ref _retailPriceCopied, count);

            public void RecordMultiCodeCompletion(int count) =>
                Interlocked.Exchange(ref _multiCodeCopied, count);

            public void MarkCompleted() => Completed = true;

            public void RecordFailure(Exception exception) => Failure = exception;
        }

        private sealed class InlineProgress<T> : IProgress<T>
        {
            private readonly Action<T> _report;

            public InlineProgress(Action<T> report)
            {
                _report = report;
            }

            public void Report(T value) => _report(value);
        }

        private static ApiResponse<T> BuildSetChildPurchasePriceBusyResponse<T>()
        {
            return ApiResponse<T>.Error(
                "套装商品正在被其他操作修改，请稍后重试",
                SetChildPurchasePriceMutationLock.BusyErrorCode
            );
        }

        private StoreRetailPrice CloneStoreRetailPrice(StoreRetailPrice source)
        {
            return new StoreRetailPrice
            {
                UUID = source.UUID,
                StoreCode = source.StoreCode,
                ProductCode = source.ProductCode,
                StoreProductCode = source.StoreProductCode,
                SupplierCode = source.SupplierCode,
                PurchasePrice = source.PurchasePrice,
                StoreRetailPriceValue = source.StoreRetailPriceValue,
                DiscountRate = source.DiscountRate,
                IsAutoPricing = source.IsAutoPricing,
                IsSpecialProduct = source.IsSpecialProduct,
                IsActive = source.IsActive,
                IsDeleted = source.IsDeleted,
                CreatedAt = source.CreatedAt,
                CreatedBy = source.CreatedBy,
                UpdatedAt = source.UpdatedAt,
                UpdatedBy = source.UpdatedBy,
            };
        }

        private StoreMultiCodeProduct CloneStoreMultiCode(StoreMultiCodeProduct source)
        {
            return new StoreMultiCodeProduct
            {
                UUID = source.UUID,
                StoreCode = source.StoreCode,
                ProductCode = source.ProductCode,
                MultiCodeProductCode = source.MultiCodeProductCode,
                StoreMultiCodeProductCode = source.StoreMultiCodeProductCode,
                MultiBarcode = source.MultiBarcode,
                PurchasePrice = source.PurchasePrice,
                MultiCodeRetailPrice = source.MultiCodeRetailPrice,
                DiscountRate = source.DiscountRate,
                IsAutoPricing = source.IsAutoPricing,
                IsSpecialProduct = source.IsSpecialProduct,
                IsActive = source.IsActive,
                IsDeleted = source.IsDeleted,
                CreatedAt = source.CreatedAt,
                CreatedBy = source.CreatedBy,
                UpdatedAt = source.UpdatedAt,
                UpdatedBy = source.UpdatedBy,
            };
        }
    }
}
