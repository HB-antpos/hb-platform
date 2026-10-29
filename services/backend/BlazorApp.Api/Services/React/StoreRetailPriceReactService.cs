using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Repositories.Interfaces;
using BlazorApp.Api.Services.Performance;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HqEntities;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    public class StoreRetailPriceReactService : IStoreRetailPriceReactService
    {
        private readonly SqlSugarContext _context;
        private readonly HqSqlSugarContext _hqContext;
        private readonly IStoreRetailPriceRepository _storeRetailPriceRepository;
        private readonly ILogger<StoreRetailPriceReactService> _logger;
        private readonly IStoreRetailPriceHqSyncService _hqSyncService;

        public StoreRetailPriceReactService(
            SqlSugarContext context,
            HqSqlSugarContext hqContext,
            IStoreRetailPriceRepository storeRetailPriceRepository,
            ILogger<StoreRetailPriceReactService> logger,
            IStoreRetailPriceHqSyncService hqSyncService
        )
        {
            _context = context;
            _hqContext = hqContext;
            _storeRetailPriceRepository = storeRetailPriceRepository;
            _logger = logger;
            _hqSyncService = hqSyncService;
        }

        public async Task<GridResponseDto<StoreRetailPriceListDto>> GetGridDataAsync(
            GridRequestDto request
        )
        {
            try
            {
                var db = _context.Db;
                var reqId = Guid.NewGuid().ToString("N");
                Action<string, SugarParameter[]> logExec = (sql, pars) =>
                {
                    _logger.LogInformation($"[{reqId}] SQL: {sql}");
                    if (pars != null && pars.Any())
                    {
                        _logger.LogInformation(
                            $"[{reqId}] 参数: {string.Join(", ", pars.Select(p => $"{p.ParameterName}={p.Value}"))}"
                        );
                    }
                };
                Action<string, SugarParameter[]> logExecuted = (sql, pars) =>
                {
                    _logger.LogInformation($"[{reqId}] 执行完成");
                };
                db.Aop.OnLogExecuting = logExec;
                SqlPerformanceAttachmentService.SetOnLogExecuted(db, logExecuted);
                var swTotal = Stopwatch.StartNew();
                var sw = Stopwatch.StartNew();
                var pageIndex = (request.StartRow / request.PageSize) + 1;
                var pageSize = request.PageSize;

                var baseQuery = db.Queryable<StoreRetailPrice>()
                    .With(SqlWith.NoLock)
                    .Where(p => p.IsDeleted == false);
                _logger.LogInformation($"[{reqId}] 构建基础查询耗时(ms): {sw.ElapsedMilliseconds}");
                sw.Restart();

                if (
                    request.FilterModel != null
                    && request.FilterModel.TryGetValue("storeCode", out var fStore)
                    && fStore.FilterType?.ToLower() == "text"
                    && (fStore.Type ?? "equals").ToLower() == "equals"
                    && !string.IsNullOrWhiteSpace(fStore.Filter)
                )
                {
                    var v = fStore.Filter.Trim();
                    baseQuery = baseQuery.Where(p => p.StoreCode == v);
                }
                if (
                    request.FilterModel != null
                    && request.FilterModel.TryGetValue("supplierCode", out var fSup)
                    && fSup.FilterType?.ToLower() == "text"
                    && (fSup.Type ?? "equals").ToLower() == "equals"
                    && !string.IsNullOrWhiteSpace(fSup.Filter)
                )
                {
                    var v = fSup.Filter.Trim();
                    baseQuery = baseQuery.Where(p => p.SupplierCode == v);
                }
                if (
                    request.FilterModel != null
                    && request.FilterModel.TryGetValue("productCode", out var fProd)
                    && fProd.FilterType?.ToLower() == "text"
                    && (fProd.Type ?? "equals").ToLower() == "equals"
                    && !string.IsNullOrWhiteSpace(fProd.Filter)
                )
                {
                    var v = fProd.Filter.Trim();
                    baseQuery = baseQuery.Where(p => p.ProductCode == v);
                }

                var query = baseQuery
                    .InnerJoin<Product>(
                        (p, prod) => p.ProductCode == prod.ProductCode && prod.IsDeleted == false
                    )
                    .LeftJoin<HBLocalSupplier>(
                        (p, prod, sup) =>
                            p.SupplierCode == sup.LocalSupplierCode && sup.IsDeleted == false
                    )
                    .LeftJoin<Store>(
                        (p, prod, sup, st) => p.StoreCode == st.StoreCode && st.IsDeleted == false
                    );
                _logger.LogInformation($"[{reqId}] 应用过滤耗时(ms): {sw.ElapsedMilliseconds}");
                sw.Restart();

                if (!string.IsNullOrWhiteSpace(request.GlobalSearch))
                {
                    var keyword = request.GlobalSearch.Trim();
                    var longEnough = keyword.Length >= 2;
                    query = query.Where(
                        (p, prod, sup, st) =>
                            (
                                p.StoreCode != null
                                && (
                                    longEnough
                                        ? p.StoreCode.StartsWith(keyword)
                                        : p.StoreCode.Contains(keyword)
                                )
                            )
                            || (st.StoreName != null && st.StoreName.Contains(keyword))
                            || (
                                p.SupplierCode != null
                                && (
                                    longEnough
                                        ? p.SupplierCode.StartsWith(keyword)
                                        : p.SupplierCode.Contains(keyword)
                                )
                            )
                            || (sup.Name != null && sup.Name.Contains(keyword))
                            || (
                                p.ProductCode != null
                                && (
                                    longEnough
                                        ? p.ProductCode.StartsWith(keyword)
                                        : p.ProductCode.Contains(keyword)
                                )
                            )
                            || (prod.ProductName != null && prod.ProductName.Contains(keyword))
                            || (
                                prod.ItemNumber != null
                                && (
                                    longEnough
                                        ? prod.ItemNumber.StartsWith(keyword)
                                        : prod.ItemNumber.Contains(keyword)
                                )
                            )
                    );
                }

                if (request.FilterModel != null && request.FilterModel.Any())
                {
                    foreach (var kv in request.FilterModel)
                    {
                        var col = kv.Key;
                        var f = kv.Value;
                        if (f == null || f.FilterType == null)
                            continue;
                        var type = f.FilterType.ToLower();
                        if (type == "text" && f.Filter != null)
                        {
                            var v = f.Filter?.ToString()?.Trim();
                            if (string.IsNullOrEmpty(v))
                                continue;
                            var op = (f.Type ?? "contains").ToLower();
                            switch (col)
                            {
                                case "storeCode":
                                    query = ApplyText(query, op, v, x => x.StoreCode);
                                    break;
                                case "storeName":
                                    query = op switch
                                    {
                                        "equals" => query.Where(
                                            (p, prod, sup, st) => st.StoreName == v
                                        ),
                                        "notequal" => query.Where(
                                            (p, prod, sup, st) => st.StoreName != v
                                        ),
                                        "contains" => query.Where(
                                            (p, prod, sup, st) =>
                                                st.StoreName != null && st.StoreName.Contains(v)
                                        ),
                                        "notcontains" => query.Where(
                                            (p, prod, sup, st) =>
                                                st.StoreName == null || !st.StoreName.Contains(v)
                                        ),
                                        "startswith" => query.Where(
                                            (p, prod, sup, st) =>
                                                st.StoreName != null && st.StoreName.StartsWith(v)
                                        ),
                                        "endswith" => query.Where(
                                            (p, prod, sup, st) =>
                                                st.StoreName != null && st.StoreName.EndsWith(v)
                                        ),
                                        "blank" => query.Where(
                                            (p, prod, sup, st) => string.IsNullOrEmpty(st.StoreName)
                                        ),
                                        "notblank" => query.Where(
                                            (p, prod, sup, st) =>
                                                !string.IsNullOrEmpty(st.StoreName)
                                        ),
                                        _ => query,
                                    };
                                    break;
                                case "supplierCode":
                                    query = ApplyText(query, op, v, x => x.SupplierCode);
                                    break;
                                case "supplierName":
                                    query = op switch
                                    {
                                        "equals" => query.Where(
                                            (p, prod, sup, st) => sup.Name == v
                                        ),
                                        "notequal" => query.Where(
                                            (p, prod, sup, st) => sup.Name != v
                                        ),
                                        "contains" => query.Where(
                                            (p, prod, sup, st) =>
                                                sup.Name != null && sup.Name.Contains(v)
                                        ),
                                        "notcontains" => query.Where(
                                            (p, prod, sup, st) =>
                                                sup.Name == null || !sup.Name.Contains(v)
                                        ),
                                        "startswith" => query.Where(
                                            (p, prod, sup, st) =>
                                                sup.Name != null && sup.Name.StartsWith(v)
                                        ),
                                        "endswith" => query.Where(
                                            (p, prod, sup, st) =>
                                                sup.Name != null && sup.Name.EndsWith(v)
                                        ),
                                        "blank" => query.Where(
                                            (p, prod, sup, st) => string.IsNullOrEmpty(sup.Name)
                                        ),
                                        "notblank" => query.Where(
                                            (p, prod, sup, st) => !string.IsNullOrEmpty(sup.Name)
                                        ),
                                        _ => query,
                                    };
                                    break;
                                case "productCode":
                                    query = ApplyText(query, op, v, x => x.ProductCode);
                                    break;
                                case "productName":
                                    query = op switch
                                    {
                                        "equals" => query.Where(
                                            (p, prod, sup, st) => prod.ProductName == v
                                        ),
                                        "notequal" => query.Where(
                                            (p, prod, sup, st) => prod.ProductName != v
                                        ),
                                        "contains" => query.Where(
                                            (p, prod, sup, st) =>
                                                prod.ProductName != null
                                                && prod.ProductName.Contains(v)
                                        ),
                                        "notcontains" => query.Where(
                                            (p, prod, sup, st) =>
                                                prod.ProductName == null
                                                || !prod.ProductName.Contains(v)
                                        ),
                                        "startswith" => query.Where(
                                            (p, prod, sup, st) =>
                                                prod.ProductName != null
                                                && prod.ProductName.StartsWith(v)
                                        ),
                                        "endswith" => query.Where(
                                            (p, prod, sup, st) =>
                                                prod.ProductName != null
                                                && prod.ProductName.EndsWith(v)
                                        ),
                                        "blank" => query.Where(
                                            (p, prod, sup, st) =>
                                                string.IsNullOrEmpty(prod.ProductName)
                                        ),
                                        "notblank" => query.Where(
                                            (p, prod, sup, st) =>
                                                !string.IsNullOrEmpty(prod.ProductName)
                                        ),
                                        _ => query,
                                    };
                                    break;
                                case "itemNumber":
                                    query = op switch
                                    {
                                        "equals" => query.Where(
                                            (p, prod, sup, st) => prod.ItemNumber == v
                                        ),
                                        "notequal" => query.Where(
                                            (p, prod, sup, st) => prod.ItemNumber != v
                                        ),
                                        "contains" => query.Where(
                                            (p, prod, sup, st) =>
                                                prod.ItemNumber != null
                                                && prod.ItemNumber.Contains(v)
                                        ),
                                        "notcontains" => query.Where(
                                            (p, prod, sup, st) =>
                                                prod.ItemNumber == null
                                                || !prod.ItemNumber.Contains(v)
                                        ),
                                        "startswith" => query.Where(
                                            (p, prod, sup, st) =>
                                                prod.ItemNumber != null
                                                && prod.ItemNumber.StartsWith(v)
                                        ),
                                        "endswith" => query.Where(
                                            (p, prod, sup, st) =>
                                                prod.ItemNumber != null
                                                && prod.ItemNumber.EndsWith(v)
                                        ),
                                        "blank" => query.Where(
                                            (p, prod, sup, st) =>
                                                string.IsNullOrEmpty(prod.ItemNumber)
                                        ),
                                        "notblank" => query.Where(
                                            (p, prod, sup, st) =>
                                                !string.IsNullOrEmpty(prod.ItemNumber)
                                        ),
                                        _ => query,
                                    };
                                    break;
                            }
                        }
                        else if (type == "number" && f.Filter != null)
                        {
                            var sv = f.Filter?.ToString()?.Trim();
                            if (decimal.TryParse(sv, out var dv))
                            {
                                var op = f.Type?.ToLower();
                                switch (col)
                                {
                                    case "purchasePrice":
                                        query = ApplyNumber(
                                            query,
                                            op,
                                            x => x.PurchasePrice,
                                            dv,
                                            f.FilterTo
                                        );
                                        break;
                                    case "storeRetailPriceValue":
                                        query = ApplyNumber(
                                            query,
                                            op,
                                            x => x.StoreRetailPriceValue,
                                            dv,
                                            f.FilterTo
                                        );
                                        break;
                                    case "discountRate":
                                        query = ApplyNumber(
                                            query,
                                            op,
                                            x => x.DiscountRate,
                                            dv,
                                            f.FilterTo
                                        );
                                        break;
                                }
                            }
                        }
                        else if (type == "set" && f.Values != null && f.Values.Any())
                        {
                            switch (col)
                            {
                                case "isActive":
                                    var boolsA = f
                                        .Values.Select(v =>
                                            v.Equals("true", StringComparison.OrdinalIgnoreCase)
                                        )
                                        .ToList();
                                    query = query.Where(
                                        (p, prod, sup, st) => boolsA.Contains(p.IsActive)
                                    );
                                    break;
                                case "isAutoPricing":
                                    var boolsB = f
                                        .Values.Select(v =>
                                            v.Equals("true", StringComparison.OrdinalIgnoreCase)
                                        )
                                        .ToList();
                                    query = query.Where(
                                        (p, prod, sup, st) => boolsB.Contains(p.IsAutoPricing)
                                    );
                                    break;
                                case "isSpecialProduct":
                                    var boolsC = f
                                        .Values.Select(v =>
                                            v.Equals("true", StringComparison.OrdinalIgnoreCase)
                                        )
                                        .ToList();
                                    query = query.Where(
                                        (p, prod, sup, st) => boolsC.Contains(prod.IsSpecialProduct)
                                    );
                                    break;
                            }
                        }
                    }
                }

                if (request.SortModel != null && request.SortModel.Any())
                {
                    var s = request.SortModel.First();
                    var asc = s.Sort.ToLower() == "asc";
                    query = s.ColId switch
                    {
                        "storeCode" => query.OrderBy(
                            (p, prod, sup, st) => p.StoreCode,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "storeName" => query.OrderBy(
                            (p, prod, sup, st) => st.StoreName,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "supplierCode" => query.OrderBy(
                            (p, prod, sup, st) => p.SupplierCode,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "supplierName" => query.OrderBy(
                            (p, prod, sup, st) => sup.Name,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "productCode" => query.OrderBy(
                            (p, prod, sup, st) => p.ProductCode,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "productName" => query.OrderBy(
                            (p, prod, sup, st) => prod.ProductName,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "itemNumber" => query.OrderBy(
                            (p, prod, sup, st) => prod.ItemNumber,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "barcode" => query.OrderBy(
                            (p, prod, sup, st) => prod.Barcode,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "purchasePrice" => query.OrderBy(
                            (p, prod, sup, st) => p.PurchasePrice,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "storeRetailPriceValue" => query.OrderBy(
                            (p, prod, sup, st) => p.StoreRetailPriceValue,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "discountRate" => query.OrderBy(
                            (p, prod, sup, st) => p.DiscountRate,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "isSpecialProduct" => query.OrderBy(
                            (p, prod, sup, st) => prod.IsSpecialProduct,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "isAutoPricing" => query.OrderBy(
                            (p, prod, sup, st) => p.IsAutoPricing,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "isActive" => query.OrderBy(
                            (p, prod, sup, st) => p.IsActive,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "updatedAt" => query.OrderBy(
                            (p, prod, sup, st) => p.UpdatedAt,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "createdAt" => query.OrderBy(
                            (p, prod, sup, st) => p.CreatedAt,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        _ => query.OrderBy((p, prod, sup, st) => p.CreatedAt, OrderByType.Desc),
                    };
                }
                else
                {
                    query = query.OrderBy((p, prod, sup, st) => p.CreatedAt, OrderByType.Desc);
                }
                _logger.LogInformation($"[{reqId}] 排序耗时(ms): {sw.ElapsedMilliseconds}");
                sw.Restart();

                var joinOnlyFilterKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "supplierName",
                    "productName",
                    "itemNumber",
                    "storeName",
                    "isSpecialProduct",
                };
                var hasJoinOnlyFilters =
                    request.FilterModel != null
                    && request.FilterModel.Keys.Any(k => joinOnlyFilterKeys.Contains(k));
                var requiresJoinCount =
                    hasJoinOnlyFilters || !string.IsNullOrWhiteSpace(request.GlobalSearch);

                if (requiresJoinCount)
                {
                    var totalRef = new RefAsync<int>(0);
                    var swQuery = Stopwatch.StartNew();
                    var items = await query
                        .Select(
                            (p, prod, sup, st) =>
                                new StoreRetailPriceListDto
                                {
                                    UUID = p.UUID,
                                    StoreCode = p.StoreCode,
                                    StoreName = st.StoreName,
                                    SupplierCode = p.SupplierCode,
                                    SupplierName = sup.Name,
                                    ProductCode = p.ProductCode,
                                    ProductName = prod.ProductName,
                                    ProductImage = prod.ProductImage,
                                    ItemNumber = prod.ItemNumber,
                                    Barcode = prod.Barcode,
                                    PurchasePrice = p.PurchasePrice,
                                    StoreRetailPriceValue = p.StoreRetailPriceValue,
                                    DiscountRate = p.DiscountRate,
                                    IsActive = p.IsActive,
                                    IsAutoPricing = p.IsAutoPricing,
                                    UpdatedBy = p.UpdatedBy,
                                    UpdatedAt = p.UpdatedAt,
                                    IsSpecialProduct = prod.IsSpecialProduct,
                                }
                        )
                        .ToPageListAsync(pageIndex, pageSize, totalRef);
                    swQuery.Stop();
                    _logger.LogInformation(
                        $"[{reqId}] 分页查询耗时(ms): {swQuery.ElapsedMilliseconds}"
                    );
                    _logger.LogInformation($"[{reqId}] 总耗时(ms): {swTotal.ElapsedMilliseconds}");
                    db.Aop.OnLogExecuting = null;
                    SqlPerformanceAttachmentService.SetOnLogExecuted(db, null);
                    return GridResponseDto<StoreRetailPriceListDto>.OK(items, totalRef.Value);
                }
                else
                {
                    var swCount = Stopwatch.StartNew();
                    var total = await baseQuery.CountAsync();
                    swCount.Stop();
                    _logger.LogInformation(
                        $"[{reqId}] 计数耗时(ms): {swCount.ElapsedMilliseconds}"
                    );
                    var swList = Stopwatch.StartNew();
                    var items = await query
                        .Select(
                            (p, prod, sup, st) =>
                                new StoreRetailPriceListDto
                                {
                                    UUID = p.UUID,
                                    StoreCode = p.StoreCode,
                                    StoreName = st.StoreName,
                                    SupplierCode = p.SupplierCode,
                                    SupplierName = sup.Name,
                                    ProductCode = p.ProductCode,
                                    ProductName = prod.ProductName,
                                    ProductImage = prod.ProductImage,
                                    ItemNumber = prod.ItemNumber,
                                    Barcode = prod.Barcode,
                                    PurchasePrice = p.PurchasePrice,
                                    StoreRetailPriceValue = p.StoreRetailPriceValue,
                                    DiscountRate = p.DiscountRate,
                                    IsActive = p.IsActive,
                                    IsAutoPricing = p.IsAutoPricing,
                                    UpdatedBy = p.UpdatedBy,
                                    UpdatedAt = p.UpdatedAt,
                                    IsSpecialProduct = prod.IsSpecialProduct,
                                }
                        )
                        .Skip(request.StartRow)
                        .Take(request.PageSize)
                        .ToListAsync();
                    swList.Stop();
                    _logger.LogInformation(
                        $"[{reqId}] 列表查询耗时(ms): {swList.ElapsedMilliseconds}"
                    );
                    _logger.LogInformation($"[{reqId}] 总耗时(ms): {swTotal.ElapsedMilliseconds}");
                    db.Aop.OnLogExecuting = null;
                    SqlPerformanceAttachmentService.SetOnLogExecuted(db, null);
                    return GridResponseDto<StoreRetailPriceListDto>.OK(items, total);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StoreRetailPrice Grid 查询失败");
                return GridResponseDto<StoreRetailPriceListDto>.Error("查询失败");
            }
        }

        public async Task<ApiResponse<StoreRetailPriceDetailDto>> GetByUuidAsync(string uuid)
        {
            try
            {
                var db = _context.Db;
                var reqId = Guid.NewGuid().ToString("N");
                Action<string, SugarParameter[]> logExec = (sql, pars) =>
                {
                    _logger.LogInformation($"[{reqId}] SQL: {sql}");
                    if (pars != null && pars.Any())
                    {
                        _logger.LogInformation(
                            $"[{reqId}] 参数: {string.Join(", ", pars.Select(p => $"{p.ParameterName}={p.Value}"))}"
                        );
                    }
                };
                Action<string, SugarParameter[]> logExecuted = (sql, pars) =>
                {
                    _logger.LogInformation($"[{reqId}] 执行完成");
                };
                db.Aop.OnLogExecuting = logExec;
                SqlPerformanceAttachmentService.SetOnLogExecuted(db, logExecuted);
                var sw = Stopwatch.StartNew();
                var item = await _storeRetailPriceRepository.GetDetailByUuidAsync(uuid);
                _logger.LogInformation($"[{reqId}] 详情查询耗时(ms): {sw.ElapsedMilliseconds}");
                db.Aop.OnLogExecuting = null;
                SqlPerformanceAttachmentService.SetOnLogExecuted(db, null);

                if (item == null)
                    return ApiResponse<StoreRetailPriceDetailDto>.Error("数据不存在", "NOT_FOUND");

                return ApiResponse<StoreRetailPriceDetailDto>.OK(item);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取详情失败");
                return ApiResponse<StoreRetailPriceDetailDto>.Error("获取失败", "GET_ERROR");
            }
        }

        public async Task<ApiResponse<StoreRetailPriceDetailDto>> CreateAsync(
            CreateStoreRetailPriceDto dto,
            string createdBy
        )
        {
            try
            {
                var db = _context.Db;
                var store = await db.Queryable<Store>()
                    .Where(x => x.StoreCode == dto.StoreCode && x.IsDeleted == false)
                    .FirstAsync();
                var supplier = await db.Queryable<HBLocalSupplier>()
                    .Where(x => x.LocalSupplierCode == dto.SupplierCode && x.IsDeleted == false)
                    .FirstAsync();
                var product = await db.Queryable<Product>()
                    .Where(x => x.ProductCode == dto.ProductCode && x.IsDeleted == false)
                    .FirstAsync();
                if (store == null || supplier == null || product == null)
                    return ApiResponse<StoreRetailPriceDetailDto>.Error(
                        "分店/供应商/商品不存在",
                        "REF_NOT_FOUND"
                    );

                var now = DateTime.UtcNow;
                var entity = new StoreRetailPrice
                {
                    UUID = UuidHelper.GenerateUuid7(),
                    StoreCode = dto.StoreCode,
                    ProductCode = dto.ProductCode,
                    SupplierCode = dto.SupplierCode,
                    StoreProductCode = UuidHelper.GenerateUuid7(),
                    PurchasePrice = dto.PurchasePrice,
                    StoreRetailPriceValue = dto.StoreRetailPriceValue,
                    DiscountRate = dto.DiscountRate,
                    IsActive = dto.IsActive ?? true,
                    IsAutoPricing = dto.IsAutoPricing ?? false,
                    CreatedAt = now,
                    UpdatedAt = now,
                    CreatedBy = createdBy,
                    UpdatedBy = createdBy,
                    IsDeleted = false,
                };

                await db.Ado.BeginTranAsync();
                try
                {
                    var lockScope = await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                        db,
                        new[] { dto.ProductCode }
                    );
                    // 业务锁后复读主商品，避免等待锁时套装关系或商品状态发生变化。
                    if (
                        !await db.Queryable<Product>()
                            .Where(x => x.ProductCode == dto.ProductCode && !x.IsDeleted)
                            .AnyAsync()
                    )
                    {
                        throw new InvalidOperationException("商品在等待业务锁期间已失效");
                    }
                    await db.Insertable(entity).ExecuteCommandAsync();
                    var writeback = await new SetChildPurchasePriceService(db).RecalculateStoresLockedAsync(
                        lockScope,
                        new[] { entity.ProductCode },
                        new[] { entity.StoreCode! },
                        createdBy
                    );
                    if (writeback.StoreMultiCodeProduct.SkippedGroupCount > 0)
                        throw new InvalidOperationException("目标门店套装子项成本重算不完整");
                    await db.Ado.CommitTranAsync();
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }

                var detail = await GetByUuidAsync(entity.UUID);
                if (!detail.Success)
                    return ApiResponse<StoreRetailPriceDetailDto>.Error("创建失败", "CREATE_ERROR");
                return detail;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建失败");
                if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
                    return BuildSetChildPurchasePriceBusyResponse<StoreRetailPriceDetailDto>();
                return ApiResponse<StoreRetailPriceDetailDto>.Error("创建失败", "CREATE_ERROR");
            }
        }

        public async Task<ApiResponse<StoreRetailPriceDetailDto>> UpdateAsync(
            string uuid,
            UpdateStoreRetailPriceDto dto,
            string updatedBy
        )
        {
            try
            {
                var db = _context.Db;
                var entity = await db.Queryable<StoreRetailPrice>()
                    .Where(x => x.UUID == uuid && x.IsDeleted == false)
                    .FirstAsync();
                if (entity == null)
                    return ApiResponse<StoreRetailPriceDetailDto>.Error("数据不存在", "NOT_FOUND");

                var expectedProductCode = entity.ProductCode;
                var expectedStoreCode = entity.StoreCode;
                if (string.IsNullOrWhiteSpace(expectedProductCode) || string.IsNullOrWhiteSpace(expectedStoreCode))
                    return ApiResponse<StoreRetailPriceDetailDto>.Error("记录缺少门店或商品编码", "DATA_ERROR");

                await db.Ado.BeginTranAsync();
                try
                {
                    var lockScope = await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                        db,
                        new[] { expectedProductCode }
                    );
                    // 锁内复读并验证业务键，禁止把锁前实体整行回写。
                    entity = await db.Queryable<StoreRetailPrice>()
                        .Where(x => x.UUID == uuid && !x.IsDeleted)
                        .FirstAsync();
                    if (
                        entity == null
                        || !string.Equals(entity.ProductCode, expectedProductCode, StringComparison.Ordinal)
                        || !string.Equals(entity.StoreCode, expectedStoreCode, StringComparison.Ordinal)
                    )
                        throw new InvalidOperationException("门店价格记录在等待业务锁期间已变更");

                    var update = db.Updateable<StoreRetailPrice>()
                        .Where(x => x.UUID == uuid && !x.IsDeleted);
                    if (dto.PurchasePrice.HasValue)
                        update = update.SetColumns(x => x.PurchasePrice == dto.PurchasePrice.Value);
                    if (dto.StoreRetailPriceValue.HasValue)
                        update = update.SetColumns(x => x.StoreRetailPriceValue == dto.StoreRetailPriceValue.Value);
                    if (dto.DiscountRate.HasValue)
                    {
                        // SetColumns 绕过实体 AOP，这里显式校验；表达式里只放局部变量，避免 SqlSugar 解析方法调用。
                        var discountRate = StoreDiscountRate.RequireValid(dto.DiscountRate.Value);
                        update = update.SetColumns(x => x.DiscountRate == discountRate);
                    }
                    if (dto.IsActive.HasValue)
                        update = update.SetColumns(x => x.IsActive == dto.IsActive.Value);
                    if (dto.IsAutoPricing.HasValue)
                        update = update.SetColumns(x => x.IsAutoPricing == dto.IsAutoPricing.Value);
                    if (string.IsNullOrWhiteSpace(entity.StoreProductCode))
                        update = update.SetColumns(x => x.StoreProductCode == UuidHelper.GenerateUuid7());
                    await update
                        .SetColumns(x => x.UpdatedAt == DateTime.UtcNow)
                        .SetColumns(x => x.UpdatedBy == updatedBy)
                        .ExecuteCommandAsync();

                    var writeback = await new SetChildPurchasePriceService(db).RecalculateStoresLockedAsync(
                        lockScope,
                        new[] { expectedProductCode },
                        new[] { expectedStoreCode },
                        updatedBy
                    );
                    if (writeback.StoreMultiCodeProduct.SkippedGroupCount > 0)
                        throw new InvalidOperationException("目标门店套装子项成本重算不完整");
                    await db.Ado.CommitTranAsync();
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }

                var detail = await GetByUuidAsync(entity.UUID);
                if (!detail.Success)
                    return ApiResponse<StoreRetailPriceDetailDto>.Error("更新失败", "UPDATE_ERROR");
                return detail;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新失败");
                if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
                    return BuildSetChildPurchasePriceBusyResponse<StoreRetailPriceDetailDto>();
                return ApiResponse<StoreRetailPriceDetailDto>.Error("更新失败", "UPDATE_ERROR");
            }
        }

        public async Task<ApiResponse<bool>> DeleteAsync(string uuid, string updatedBy)
        {
            try
            {
                var db = _context.Db;
                var entity = await db.Queryable<StoreRetailPrice>()
                    .Where(x => x.UUID == uuid && !x.IsDeleted)
                    .FirstAsync();
                if (entity == null || string.IsNullOrWhiteSpace(entity.ProductCode) || string.IsNullOrWhiteSpace(entity.StoreCode))
                    return ApiResponse<bool>.OK(false, "未找到数据");
                await db.Ado.BeginTranAsync();
                try
                {
                    var lockScope = await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                        db,
                        new[] { entity.ProductCode }
                    );
                    entity = await db.Queryable<StoreRetailPrice>()
                        .Where(x => x.UUID == uuid && !x.IsDeleted)
                        .FirstAsync();
                    if (entity == null || string.IsNullOrWhiteSpace(entity.ProductCode) || string.IsNullOrWhiteSpace(entity.StoreCode))
                        throw new InvalidOperationException("门店价格记录在等待业务锁期间已变更");
                    var result = await db.Updateable<StoreRetailPrice>()
                        .SetColumns(x => x.IsDeleted == true)
                        .SetColumns(x => x.UpdatedAt == DateTime.UtcNow)
                        .SetColumns(x => x.UpdatedBy == updatedBy)
                        .Where(x => x.UUID == uuid && !x.IsDeleted)
                        .ExecuteCommandAsync();
                    var writeback = await new SetChildPurchasePriceService(db).RecalculateStoresLockedAsync(
                        lockScope,
                        new[] { entity.ProductCode },
                        new[] { entity.StoreCode },
                        updatedBy
                    );
                    if (writeback.StoreMultiCodeProduct.SkippedGroupCount > 0)
                        throw new InvalidOperationException("目标门店套装子项成本重算不完整");
                    await db.Ado.CommitTranAsync();
                    return ApiResponse<bool>.OK(
                        result > 0,
                        result > 0 ? "删除成功" : "未找到数据"
                    );
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除失败");
                if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
                    return BuildSetChildPurchasePriceBusyResponse<bool>();
                return ApiResponse<bool>.Error("删除失败", "DELETE_ERROR");
            }
        }

        public async Task<ApiResponse<BatchResultDto>> BatchUpsertAsync(
            List<StoreRetailPriceUpsertItemDto> items,
            string updatedBy
        )
        {
            try
            {
                var db = _context.Db;
                var now = DateTime.UtcNow;
                var insertList = new List<StoreRetailPrice>();
                var updateList = new List<StoreRetailPrice>();
                var errors = new List<string>();

                _logger.LogInformation(
                    $"BatchUpsertAsync 开始, 操作人: {updatedBy}, 数据条数: {items.Count}"
                );

                var uuids = items
                    .Select(it => it.UUID)
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Select(u => u!.Trim())
                    .Distinct()
                    .ToList();
                var storeCodes = items
                    .Select(it => it.StoreCode)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!.Trim())
                    .Distinct()
                    .ToList();
                var productCodes = items
                    .Select(it => it.ProductCode)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p!.Trim())
                    .Distinct()
                    .ToList();
                var requestedKeys = items
                    .Where(it =>
                        !string.IsNullOrWhiteSpace(it.StoreCode)
                        && !string.IsNullOrWhiteSpace(it.ProductCode)
                    )
                    .Select(it => $"{it.StoreCode!.Trim()}|{it.ProductCode!.Trim()}")
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                // 事务外 UUID 解析仅用于确定完整产品锁集合，锁内还会复读并核对归属。
                var preflightUuidRows = uuids.Count == 0
                    ? new List<StoreRetailPrice>()
                    : await db.Queryable<StoreRetailPrice>()
                        .Where(x => x.UUID != null && uuids.Contains(x.UUID) && !x.IsDeleted)
                        .Select(x => new StoreRetailPrice
                        {
                            UUID = x.UUID,
                            StoreCode = x.StoreCode,
                            ProductCode = x.ProductCode,
                        })
                        .ToListAsync();
                var preflightByUuid = preflightUuidRows
                    .Where(x => !string.IsNullOrWhiteSpace(x.UUID))
                    .ToDictionary(x => x.UUID!);
                var lockProductCodes = productCodes
                    .Concat(
                        preflightUuidRows
                            .Where(x => !string.IsNullOrWhiteSpace(x.ProductCode))
                            .Select(x => x.ProductCode!)
                    )
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (lockProductCodes.Count == 0)
                {
                    return ApiResponse<BatchResultDto>.Error(
                        "批量保存缺少可锁定的商品编码",
                        "VALIDATION_ERROR"
                    );
                }

                await db.Ado.BeginTranAsync();
                try
                {
                    var lockScope = await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                        db,
                        lockProductCodes
                    );

                    _logger.LogInformation(
                        $"查询条件 - UUIDs: {uuids.Count}, StoreCodes: {string.Join(",", storeCodes)}, ProductCodes: {string.Join(",", productCodes)}"
                    );

                    var existing = uuids.Count == 0
                        ? new List<StoreRetailPrice>()
                        : await _storeRetailPriceRepository.QueryActive()
                            .Where(x => x.UUID != null && uuids.Contains(x.UUID))
                            .ToListAsync();
                    if (storeCodes.Any() && productCodes.Any())
                    {
                        var keyRows = await _storeRetailPriceRepository.QueryActive()
                            .Where(x =>
                                x.StoreCode != null
                                && storeCodes.Contains(x.StoreCode)
                                && x.ProductCode != null
                                && productCodes.Contains(x.ProductCode)
                            )
                            .ToListAsync();
                        existing.AddRange(
                            keyRows.Where(row =>
                                requestedKeys.Contains($"{row.StoreCode}|{row.ProductCode}")
                            )
                        );
                    }
                    existing = existing
                        .GroupBy(x =>
                            !string.IsNullOrWhiteSpace(x.UUID)
                                ? $"UUID:{x.UUID}"
                                : $"KEY:{x.StoreCode}|{x.ProductCode}"
                        )
                        .Select(group => group.First())
                        .ToList();
                    foreach (var uuid in uuids)
                    {
                        preflightByUuid.TryGetValue(uuid, out var expected);
                        var current = existing.FirstOrDefault(x => x.UUID == uuid);
                        if (
                            (expected == null) != (current == null)
                            || (
                                expected != null
                                && current != null
                                && (
                                    !string.Equals(
                                        expected.ProductCode?.Trim(),
                                        current.ProductCode?.Trim(),
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                    || !string.Equals(
                                        expected.StoreCode?.Trim(),
                                        current.StoreCode?.Trim(),
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                            )
                        )
                        {
                            throw new InvalidOperationException(
                                $"分店价格记录 {uuid} 在等待业务锁期间归属发生变化，请重试"
                            );
                        }
                    }
                    lockScope.EnsureCovers(
                        db,
                        existing
                            .Where(x => !string.IsNullOrWhiteSpace(x.ProductCode))
                            .Select(x => x.ProductCode!)
                            .Concat(productCodes)
                    );
                    _logger.LogInformation($"查询到现有记录: {existing.Count} 条");

                    var byUuid = existing
                        .Where(x => !string.IsNullOrWhiteSpace(x.UUID))
                        .ToDictionary(x => x.UUID);

                    // 使用 StoreCode + ProductCode 组合判断是否已存在分店价格
                    var byKey = existing
                        .GroupBy(x => $"{x.StoreCode}|{x.ProductCode}")
                        .ToDictionary(
                            g => g.Key,
                            g => g.OrderByDescending(x => x.UpdatedAt ?? x.CreatedAt).First()
                        );

                    if (existing.Count != byKey.Count)
                    {
                        _logger.LogWarning(
                            $"检测到重复数据: 查询到 {existing.Count} 条记录,去重后 {byKey.Count} 条"
                        );
                    }

                    foreach (var it in items)
                    {
                        try
                        {
                            StoreRetailPrice? entity = null;
                            if (
                                !string.IsNullOrWhiteSpace(it.UUID)
                                && byUuid.TryGetValue(it.UUID!, out var foundByUuid)
                            )
                                entity = foundByUuid;
                            if (entity == null)
                            {
                                var sc = it.StoreCode?.Trim();
                                var pc = it.ProductCode?.Trim();
                                if (string.IsNullOrWhiteSpace(sc) || string.IsNullOrWhiteSpace(pc))
                                {
                                    throw new Exception("缺少关键键值(storeCode/productCode)");
                                }
                                var key = $"{sc}|{pc}";
                                if (byKey.TryGetValue(key, out var foundByKey))
                                    entity = foundByKey;
                            }
                            if (entity == null)
                            {
                                entity = new StoreRetailPrice
                                {
                                    UUID = UuidHelper.GenerateUuid7(),
                                    StoreCode = it.StoreCode,
                                    ProductCode = it.ProductCode,
                                    SupplierCode = it.SupplierCode,
                                    StoreProductCode = it.StoreCode + it.ProductCode,
                                    PurchasePrice = it.PurchasePrice,
                                    StoreRetailPriceValue = it.StoreRetailPriceValue,
                                    DiscountRate = it.DiscountRate,
                                    IsActive = it.IsActive ?? true,
                                    IsAutoPricing = it.IsAutoPricing ?? false,
                                    CreatedAt = now,
                                    UpdatedAt = now,
                                    CreatedBy = updatedBy,
                                    UpdatedBy = updatedBy,
                                    IsDeleted = false,
                                };
                                insertList.Add(entity);
                                _logger.LogDebug(
                                    $"准备插入: StoreCode={entity.StoreCode}, ProductCode={entity.ProductCode}, SupplierCode={entity.SupplierCode}"
                                );
                            }
                            else
                            {
                                // 只更新非空字段
                                if (string.IsNullOrWhiteSpace(entity.StoreProductCode))
                                    entity.StoreProductCode = UuidHelper.GenerateUuid7();
                                if (it.PurchasePrice.HasValue)
                                    entity.PurchasePrice = it.PurchasePrice;
                                if (it.StoreRetailPriceValue.HasValue)
                                    entity.StoreRetailPriceValue = it.StoreRetailPriceValue;
                                if (it.DiscountRate.HasValue)
                                    entity.DiscountRate = it.DiscountRate;
                                if (it.IsActive.HasValue)
                                    entity.IsActive = it.IsActive.Value;
                                if (it.IsAutoPricing.HasValue)
                                    entity.IsAutoPricing = it.IsAutoPricing.Value;
                                entity.UpdatedAt = now;
                                entity.UpdatedBy = updatedBy;
                                updateList.Add(entity);
                                _logger.LogDebug(
                                    $"准备更新: UUID={entity.UUID}, StoreCode={entity.StoreCode}, ProductCode={entity.ProductCode}, SupplierCode={entity.SupplierCode}"
                                );
                            }
                        }
                        catch (Exception exItem)
                        {
                            var errorMsg =
                                $"处理数据失败: {exItem.Message}, 数据: {System.Text.Json.JsonSerializer.Serialize(it)}";
                            errors.Add(errorMsg);
                            _logger.LogError(exItem, errorMsg);
                        }
                    }

                    if (insertList.Any())
                    {
                        var inserted = await db.Insertable(insertList).ExecuteCommandAsync();
                        _logger.LogInformation($"插入完成: {inserted} 条");
                    }
                    if (updateList.Any())
                    {
                        var updated = await db.Updateable(updateList)
                            .UpdateColumns(x => new
                            {
                                x.StoreProductCode,
                                x.PurchasePrice,
                                x.StoreRetailPriceValue,
                                x.DiscountRate,
                                x.IsActive,
                                x.IsAutoPricing,
                                x.UpdatedAt,
                                x.UpdatedBy,
                            })
                            .ExecuteCommandAsync();
                        _logger.LogInformation($"更新完成: {updated} 条");
                    }
                    var affectedRows = insertList
                        .Concat(updateList)
                        .Where(x =>
                            !string.IsNullOrWhiteSpace(x.ProductCode)
                            && !string.IsNullOrWhiteSpace(x.StoreCode)
                        )
                        .ToList();
                    if (affectedRows.Count > 0)
                    {
                        var writeback = await new SetChildPurchasePriceService(db)
                            .RecalculateStoreGroupsLockedAsync(
                            lockScope,
                            affectedRows.Select(x => (x.StoreCode, x.ProductCode)),
                            updatedBy
                        );
                        if (writeback.StoreMultiCodeProduct.SkippedGroupCount > 0)
                            throw new InvalidOperationException("目标门店套装子项成本重算不完整");
                    }
                    await db.Ado.CommitTranAsync();
                    _logger.LogInformation("事务提交成功");

                    var result = new BatchResultDto
                    {
                        Inserted = insertList.Count,
                        Updated = updateList.Count,
                        Failed = errors.Count,
                        Errors = errors,
                    };
                    return ApiResponse<BatchResultDto>.OK(result, "批量保存成功");
                }
                catch (Exception ex)
                {
                    await db.Ado.RollbackTranAsync();
                    _logger.LogError(ex, "批量保存事务失败, 事务已回滚");
                    if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
                        return BuildSetChildPurchasePriceBusyResponse<BatchResultDto>();
                    return ApiResponse<BatchResultDto>.Error(
                        $"批量保存失败: {ex.Message}",
                        "BATCH_UPSERT_ERROR"
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量保存失败");
                if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
                    return BuildSetChildPurchasePriceBusyResponse<BatchResultDto>();
                return ApiResponse<BatchResultDto>.Error(
                    $"批量保存失败: {ex.Message}",
                    "BATCH_UPSERT_ERROR"
                );
            }
        }

        public async Task<ApiResponse<bool>> BatchDeleteAsync(List<string> uuids, string updatedBy)
        {
            try
            {
                var db = _context.Db;
                var normalizedUuids = uuids
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                // 事务外 UUID 解析只用于确定完整产品锁集合，锁内仍会复读并核对业务键。
                var preflightRows = normalizedUuids.Count == 0
                    ? new List<StoreRetailPrice>()
                    : await db.Queryable<StoreRetailPrice>()
                        .Where(x =>
                            x.UUID != null
                            && normalizedUuids.Contains(x.UUID)
                            && !x.IsDeleted
                        )
                        .Select(x => new StoreRetailPrice
                        {
                            UUID = x.UUID,
                            StoreCode = x.StoreCode,
                            ProductCode = x.ProductCode,
                        })
                        .ToListAsync();
                if (preflightRows.Count == 0)
                    return ApiResponse<bool>.OK(true, "成功删除 0 条");

                var lockProductCodes = preflightRows
                    .Where(x => !string.IsNullOrWhiteSpace(x.ProductCode))
                    .Select(x => x.ProductCode!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (preflightRows.Any(x => string.IsNullOrWhiteSpace(x.ProductCode)))
                    return ApiResponse<bool>.Error(
                        "待删除分店价格记录缺少商品编码",
                        "BATCH_DELETE_ERROR"
                    );

                await db.Ado.BeginTranAsync();
                try
                {
                    var lockScope = await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                        db,
                        lockProductCodes
                    );
                    var affectedRows = await db.Queryable<StoreRetailPrice>()
                        .Where(x =>
                            x.UUID != null
                            && normalizedUuids.Contains(x.UUID)
                            && !x.IsDeleted
                        )
                        .ToListAsync();
                    var currentByUuid = affectedRows
                        .Where(x => !string.IsNullOrWhiteSpace(x.UUID))
                        .ToDictionary(x => x.UUID!, StringComparer.OrdinalIgnoreCase);
                    foreach (var expected in preflightRows)
                    {
                        if (
                            string.IsNullOrWhiteSpace(expected.UUID)
                            || !currentByUuid.TryGetValue(expected.UUID, out var current)
                            || !string.Equals(
                                expected.StoreCode?.Trim(),
                                current.StoreCode?.Trim(),
                                StringComparison.OrdinalIgnoreCase
                            )
                            || !string.Equals(
                                expected.ProductCode?.Trim(),
                                current.ProductCode?.Trim(),
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        {
                            throw new InvalidOperationException(
                                $"分店价格记录 {expected.UUID} 在等待业务锁期间归属发生变化，请重试"
                            );
                        }
                    }
                    lockScope.EnsureCovers(
                        db,
                        affectedRows
                            .Where(x => !string.IsNullOrWhiteSpace(x.ProductCode))
                            .Select(x => x.ProductCode!)
                    );

                    var count = 0;
                    var now = DateTime.UtcNow;
                    foreach (var row in affectedRows)
                    {
                        var changed = await db.Updateable<StoreRetailPrice>()
                            .SetColumns(x => x.IsDeleted == true)
                            .SetColumns(x => x.UpdatedAt == now)
                            .SetColumns(x => x.UpdatedBy == updatedBy)
                            .Where(x =>
                                x.UUID == row.UUID
                                && x.StoreCode == row.StoreCode
                                && x.ProductCode == row.ProductCode
                                && !x.IsDeleted
                            )
                            .ExecuteCommandAsync();
                        if (changed != 1)
                            throw new InvalidOperationException(
                                $"分店价格记录 {row.UUID} 在更新时发生并发变化，请重试"
                            );
                        count += changed;
                    }
                    if (affectedRows.Count > 0)
                    {
                        var writeback = await new SetChildPurchasePriceService(db)
                            .RecalculateStoreGroupsLockedAsync(
                            lockScope,
                            affectedRows.Select(x => (x.StoreCode, x.ProductCode)),
                            updatedBy
                        );
                        if (writeback.StoreMultiCodeProduct.SkippedGroupCount > 0)
                            throw new InvalidOperationException("目标门店套装子项成本重算不完整");
                    }
                    await db.Ado.CommitTranAsync();
                    return ApiResponse<bool>.OK(true, "成功删除 " + count + " 条");
                }
                catch (Exception ex)
                {
                    await db.Ado.RollbackTranAsync();
                    _logger.LogError(ex, "批量删除事务失败");
                    if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
                        return BuildSetChildPurchasePriceBusyResponse<bool>();
                    return ApiResponse<bool>.Error("批量删除失败", "BATCH_DELETE_ERROR");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量删除失败");
                if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
                    return BuildSetChildPurchasePriceBusyResponse<bool>();
                return ApiResponse<bool>.Error("批量删除失败", "BATCH_DELETE_ERROR");
            }
        }

        public async Task<ApiResponse<bool>> BatchUpdateSpecialFlagAsync(
            List<string> productCodes,
            bool isSpecial,
            string updatedBy
        )
        {
            try
            {
                var db = _context.Db;
                await db.Ado.BeginTranAsync();
                try
                {
                    var count = await db.Updateable<Product>()
                        .SetColumns(x => x.IsSpecialProduct == isSpecial)
                        .SetColumns(x => x.UpdatedBy == updatedBy)
                        .SetColumns(x => x.UpdatedAt == DateTime.UtcNow)
                        .Where(x => x.ProductCode != null && productCodes.Contains(x.ProductCode))
                        .ExecuteCommandAsync();
                    await db.Ado.CommitTranAsync();
                    return ApiResponse<bool>.OK(true, "已更新 " + count + " 条产品特殊标记");
                }
                catch (Exception ex)
                {
                    await db.Ado.RollbackTranAsync();
                    _logger.LogError(ex, "批量更新特殊产品标记失败");
                    return ApiResponse<bool>.Error("批量更新失败", "BATCH_UPDATE_SPECIAL_ERROR");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量更新特殊产品标记失败");
                return ApiResponse<bool>.Error("批量更新失败", "BATCH_UPDATE_SPECIAL_ERROR");
            }
        }

        public async Task<ApiResponse<List<StoreRetailPriceListDto>>> GetListByUuidsAsync(
            List<string> uuids
        )
        {
            try
            {
                var items = await _storeRetailPriceRepository.GetListByUuidsAsync(uuids);

                return ApiResponse<List<StoreRetailPriceListDto>>.OK(items);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "按UUID批量获取列表失败");
                return ApiResponse<List<StoreRetailPriceListDto>>.Error(
                    "查询失败",
                    "BATCH_BY_UUIDS_ERROR"
                );
            }
        }

        public async Task<ApiResponse<BatchResultDto>> BatchDeleteByProductCodesAsync(
            List<string> productCodes,
            List<string> storeCodes,
            string updatedBy
        )
        {
            try
            {
                var db = _context.Db;
                var now = DateTime.UtcNow;
                var errors = new List<string>();
                var deleted = 0;

                _logger.LogInformation(
                    $"BatchDeleteByProductCodesAsync 开始, 操作人: {updatedBy}, 商品编码数量: {productCodes.Count}, 分店数量: {storeCodes.Count}"
                );

                await db.Ado.BeginTranAsync();
                try
                {
                    var lockScope = productCodes.Count == 0
                        ? await SetChildPurchasePriceMutationLock.AcquireAllAsync(db)
                        : await SetChildPurchasePriceMutationLock.AcquireProductsAsync(db, productCodes);
                    var q = _storeRetailPriceRepository.QueryActive();

                    if (productCodes.Any())
                    {
                        q = q.Where(x =>
                            x.ProductCode != null && productCodes.Contains(x.ProductCode)
                        );
                    }

                    if (storeCodes.Any())
                    {
                        q = q.Where(x => x.StoreCode != null && storeCodes.Contains(x.StoreCode));
                    }

                    var toDelete = await q.ToListAsync();
                    _logger.LogInformation($"查询到待删除记录: {toDelete.Count} 条");

                    if (toDelete.Any())
                    {
                        var deleteUuids = toDelete
                            .Where(x => !string.IsNullOrWhiteSpace(x.UUID))
                            .Select(x => x.UUID!)
                            .ToList();
                        deleted = await db.Updateable<StoreRetailPrice>()
                            .SetColumns(x => x.IsDeleted == true)
                            .SetColumns(x => x.UpdatedAt == now)
                            .SetColumns(x => x.UpdatedBy == updatedBy)
                            .Where(x => deleteUuids.Contains(x.UUID) && !x.IsDeleted)
                            .ExecuteCommandAsync();
                        _logger.LogInformation($"已删除: {deleted} 条记录");
                        var writeback = await new SetChildPurchasePriceService(db)
                            .RecalculateStoreGroupsLockedAsync(
                            lockScope,
                            toDelete.Select(x => (x.StoreCode, x.ProductCode)),
                            updatedBy
                        );
                        if (writeback.StoreMultiCodeProduct.SkippedGroupCount > 0)
                            throw new InvalidOperationException("目标门店套装子项成本重算不完整");
                    }

                    await db.Ado.CommitTranAsync();
                    _logger.LogInformation("事务提交成功");

                    var result = new BatchResultDto
                    {
                        Inserted = 0,
                        Updated = 0,
                        Failed = errors.Count,
                        Errors = errors,
                    };

                    return ApiResponse<BatchResultDto>.OK(result, $"成功删除 {deleted} 条记录");
                }
                catch (Exception ex)
                {
                    await db.Ado.RollbackTranAsync();
                    _logger.LogError(ex, "批量删除事务失败, 事务已回滚");
                    if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
                        return BuildSetChildPurchasePriceBusyResponse<BatchResultDto>();
                    return ApiResponse<BatchResultDto>.Error(
                        $"批量删除失败: {ex.Message}",
                        "BATCH_DELETE_BY_PRODUCT_CODES_ERROR"
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量删除失败");
                if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
                    return BuildSetChildPurchasePriceBusyResponse<BatchResultDto>();
                return ApiResponse<BatchResultDto>.Error(
                    $"批量删除失败: {ex.Message}",
                    "BATCH_DELETE_BY_PRODUCT_CODES_ERROR"
                );
            }
        }

        private static ApiResponse<T> BuildSetChildPurchasePriceBusyResponse<T>()
        {
            return ApiResponse<T>.Error(
                "套装商品正在被其他操作修改，请稍后重试",
                SetChildPurchasePriceMutationLock.BusyErrorCode
            );
        }

        private ISugarQueryable<StoreRetailPrice, Product, HBLocalSupplier, Store> ApplyText(
            ISugarQueryable<StoreRetailPrice, Product, HBLocalSupplier, Store> query,
            string operation,
            string value,
            System.Linq.Expressions.Expression<System.Func<StoreRetailPrice, string?>> selector
        )
        {
            var oldParam = selector.Parameters[0];
            var newParam = System.Linq.Expressions.Expression.Parameter(
                typeof(StoreRetailPrice),
                "p"
            );
            var member = new ParamReplaceVisitor(oldParam, newParam).Visit(selector.Body);
            return operation switch
            {
                "equals" => query.Where(
                    System.Linq.Expressions.Expression.Lambda<System.Func<StoreRetailPrice, bool>>(
                        System.Linq.Expressions.Expression.Equal(
                            member,
                            System.Linq.Expressions.Expression.Constant(value, typeof(string))
                        ),
                        newParam
                    )
                ),
                "notequal" => query.Where(
                    System.Linq.Expressions.Expression.Lambda<System.Func<StoreRetailPrice, bool>>(
                        System.Linq.Expressions.Expression.NotEqual(
                            member,
                            System.Linq.Expressions.Expression.Constant(value, typeof(string))
                        ),
                        newParam
                    )
                ),
                "contains" => query.Where(
                    System.Linq.Expressions.Expression.Lambda<System.Func<StoreRetailPrice, bool>>(
                        System.Linq.Expressions.Expression.AndAlso(
                            System.Linq.Expressions.Expression.NotEqual(
                                member,
                                System.Linq.Expressions.Expression.Constant(null, typeof(string))
                            ),
                            System.Linq.Expressions.Expression.Call(
                                member,
                                typeof(string).GetMethod("Contains", new[] { typeof(string) })!,
                                System.Linq.Expressions.Expression.Constant(value)
                            )
                        ),
                        newParam
                    )
                ),
                "notcontains" => query.Where(
                    System.Linq.Expressions.Expression.Lambda<System.Func<StoreRetailPrice, bool>>(
                        System.Linq.Expressions.Expression.OrElse(
                            System.Linq.Expressions.Expression.Equal(
                                member,
                                System.Linq.Expressions.Expression.Constant(null, typeof(string))
                            ),
                            System.Linq.Expressions.Expression.Not(
                                System.Linq.Expressions.Expression.Call(
                                    member,
                                    typeof(string).GetMethod("Contains", new[] { typeof(string) })!,
                                    System.Linq.Expressions.Expression.Constant(value)
                                )
                            )
                        ),
                        newParam
                    )
                ),
                "startswith" => query.Where(
                    System.Linq.Expressions.Expression.Lambda<System.Func<StoreRetailPrice, bool>>(
                        System.Linq.Expressions.Expression.AndAlso(
                            System.Linq.Expressions.Expression.NotEqual(
                                member,
                                System.Linq.Expressions.Expression.Constant(null, typeof(string))
                            ),
                            System.Linq.Expressions.Expression.Call(
                                member,
                                typeof(string).GetMethod("StartsWith", new[] { typeof(string) })!,
                                System.Linq.Expressions.Expression.Constant(value)
                            )
                        ),
                        newParam
                    )
                ),
                "endswith" => query.Where(
                    System.Linq.Expressions.Expression.Lambda<System.Func<StoreRetailPrice, bool>>(
                        System.Linq.Expressions.Expression.AndAlso(
                            System.Linq.Expressions.Expression.NotEqual(
                                member,
                                System.Linq.Expressions.Expression.Constant(null, typeof(string))
                            ),
                            System.Linq.Expressions.Expression.Call(
                                member,
                                typeof(string).GetMethod("EndsWith", new[] { typeof(string) })!,
                                System.Linq.Expressions.Expression.Constant(value)
                            )
                        ),
                        newParam
                    )
                ),
                "blank" => query.Where(
                    System.Linq.Expressions.Expression.Lambda<System.Func<StoreRetailPrice, bool>>(
                        System.Linq.Expressions.Expression.Call(
                            typeof(string),
                            "IsNullOrEmpty",
                            null,
                            member
                        ),
                        newParam
                    )
                ),
                "notblank" => query.Where(
                    System.Linq.Expressions.Expression.Lambda<System.Func<StoreRetailPrice, bool>>(
                        System.Linq.Expressions.Expression.Not(
                            System.Linq.Expressions.Expression.Call(
                                typeof(string),
                                "IsNullOrEmpty",
                                null,
                                member
                            )
                        ),
                        newParam
                    )
                ),
                _ => query,
            };
        }

        private bool ApplyTextEval(string? fieldValue, string operation, string value)
        {
            switch (operation)
            {
                case "equals":
                    return fieldValue == value;
                case "notequal":
                    return fieldValue != value;
                case "contains":
                    return fieldValue?.Contains(value) ?? false;
                case "notcontains":
                    return !(fieldValue?.Contains(value) ?? false);
                case "startswith":
                    return fieldValue?.StartsWith(value) ?? false;
                case "endswith":
                    return fieldValue?.EndsWith(value) ?? false;
                case "blank":
                    return string.IsNullOrEmpty(fieldValue);
                case "notblank":
                    return !string.IsNullOrEmpty(fieldValue);
                default:
                    return true;
            }
        }

        private sealed class ParamReplaceVisitor : System.Linq.Expressions.ExpressionVisitor
        {
            private readonly System.Linq.Expressions.ParameterExpression _source;
            private readonly System.Linq.Expressions.ParameterExpression _target;

            public ParamReplaceVisitor(
                System.Linq.Expressions.ParameterExpression source,
                System.Linq.Expressions.ParameterExpression target
            )
            {
                _source = source;
                _target = target;
            }

            protected override System.Linq.Expressions.Expression VisitParameter(
                System.Linq.Expressions.ParameterExpression node
            )
            {
                return node == _source ? _target : base.VisitParameter(node);
            }
        }

        private ISugarQueryable<StoreRetailPrice, Product, HBLocalSupplier, Store> ApplyNumber(
            ISugarQueryable<StoreRetailPrice, Product, HBLocalSupplier, Store> query,
            string? operation,
            System.Linq.Expressions.Expression<System.Func<StoreRetailPrice, decimal?>> selector,
            decimal value,
            object? filterTo
        )
        {
            var oldParam = selector.Parameters[0];
            var newParam = System.Linq.Expressions.Expression.Parameter(
                typeof(StoreRetailPrice),
                "p"
            );
            var member = new ParamReplaceVisitor(oldParam, newParam).Visit(selector.Body);
            var constantValue = System.Linq.Expressions.Expression.Convert(
                System.Linq.Expressions.Expression.Constant(value),
                typeof(decimal?)
            );
            System.Linq.Expressions.Expression? condition = operation switch
            {
                "equals" => System.Linq.Expressions.Expression.Equal(member, constantValue),
                "notequal" => System.Linq.Expressions.Expression.NotEqual(member, constantValue),
                "lessthan" => System.Linq.Expressions.Expression.LessThan(member, constantValue),
                "lessthanorequal" => System.Linq.Expressions.Expression.LessThanOrEqual(
                    member,
                    constantValue
                ),
                "greaterthan" => System.Linq.Expressions.Expression.GreaterThan(
                    member,
                    constantValue
                ),
                "greaterthanorequal" => System.Linq.Expressions.Expression.GreaterThanOrEqual(
                    member,
                    constantValue
                ),
                "inrange" => filterTo != null
                && decimal.TryParse(filterTo.ToString(), out var toValue)
                    ? System.Linq.Expressions.Expression.AndAlso(
                        System.Linq.Expressions.Expression.GreaterThanOrEqual(
                            member,
                            constantValue
                        ),
                        System.Linq.Expressions.Expression.LessThanOrEqual(
                            member,
                            System.Linq.Expressions.Expression.Convert(
                                System.Linq.Expressions.Expression.Constant(toValue),
                                typeof(decimal?)
                            )
                        )
                    )
                    : null,
                _ => null,
            };
            if (condition == null)
                return query;
            var lambda = System.Linq.Expressions.Expression.Lambda<System.Func<
                StoreRetailPrice,
                bool
            >>(condition, newParam);
            return query.Where(lambda);
        }

        private ISugarQueryable<
            StoreRetailPrice,
            Product,
            HBLocalSupplier,
            ChinaSupplier,
            Store
        > ApplyNumber5(
            ISugarQueryable<StoreRetailPrice, Product, HBLocalSupplier, ChinaSupplier, Store> query,
            string? operation,
            System.Linq.Expressions.Expression<System.Func<StoreRetailPrice, decimal?>> selector,
            decimal value,
            object? filterTo
        )
        {
            var parameter = selector.Parameters[0];
            var member = selector.Body;
            var constantValue = System.Linq.Expressions.Expression.Convert(
                System.Linq.Expressions.Expression.Constant(value),
                typeof(decimal?)
            );
            System.Linq.Expressions.Expression? condition = operation switch
            {
                "equals" => System.Linq.Expressions.Expression.Equal(member, constantValue),
                "notequal" => System.Linq.Expressions.Expression.NotEqual(member, constantValue),
                "lessthan" => System.Linq.Expressions.Expression.LessThan(member, constantValue),
                "lessthanorequal" => System.Linq.Expressions.Expression.LessThanOrEqual(
                    member,
                    constantValue
                ),
                "greaterthan" => System.Linq.Expressions.Expression.GreaterThan(
                    member,
                    constantValue
                ),
                "greaterthanorequal" => System.Linq.Expressions.Expression.GreaterThanOrEqual(
                    member,
                    constantValue
                ),
                "inrange" => filterTo != null
                && decimal.TryParse(filterTo.ToString(), out var toValue)
                    ? System.Linq.Expressions.Expression.AndAlso(
                        System.Linq.Expressions.Expression.GreaterThanOrEqual(
                            member,
                            constantValue
                        ),
                        System.Linq.Expressions.Expression.LessThanOrEqual(
                            member,
                            System.Linq.Expressions.Expression.Convert(
                                System.Linq.Expressions.Expression.Constant(toValue),
                                typeof(decimal?)
                            )
                        )
                    )
                    : null,
                _ => null,
            };
            if (condition == null)
                return query;
            var lambda = System.Linq.Expressions.Expression.Lambda<System.Func<
                StoreRetailPrice,
                bool
            >>(condition, parameter);
            return query.Where(lambda);
        }

        public async Task<ApiResponse<BatchResultDto>> UpsertForActiveStoresAsync(
            List<StoreRetailPriceUpsertForActiveStoresItemDto> items,
            string updatedBy
        )
        {
            try
            {
                var stores = await _storeRetailPriceRepository.GetActiveStoreCodesAsync();
                if (stores == null)
                    stores = new List<string>();
                var upserts = new List<StoreRetailPriceUpsertItemDto>();
                foreach (var it in items)
                {
                    foreach (var sc in stores)
                    {
                        upserts.Add(
                            new StoreRetailPriceUpsertItemDto
                            {
                                StoreCode = sc,
                                ProductCode = it.ProductCode,
                                SupplierCode = "200",
                                PurchasePrice = it.PurchasePrice,
                                StoreRetailPriceValue = it.StoreRetailPriceValue,
                                DiscountRate = it.DiscountRate,
                                IsActive = it.IsActive ?? true,
                                IsAutoPricing = it.IsAutoPricing ?? false,
                            }
                        );
                    }
                }
                return await BatchUpsertAsync(upserts, updatedBy);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启用分店批量上架失败");
                return ApiResponse<BatchResultDto>.Error(
                    "批量保存失败",
                    "UPSERT_ACTIVE_STORES_ERROR"
                );
            }
        }

        private ISugarQueryable<
            StoreRetailPrice,
            Product,
            HBLocalSupplier,
            ChinaSupplier,
            Store
        > ApplyText5(
            ISugarQueryable<StoreRetailPrice, Product, HBLocalSupplier, ChinaSupplier, Store> query,
            string operation,
            string value,
            System.Linq.Expressions.Expression<System.Func<StoreRetailPrice, string?>> selector
        )
        {
            var oldParam = selector.Parameters[0];
            var newParam = System.Linq.Expressions.Expression.Parameter(
                typeof(StoreRetailPrice),
                "p"
            );
            var member = new ParamReplaceVisitor(oldParam, newParam).Visit(selector.Body);
            return operation switch
            {
                "equals" => query.Where(
                    System.Linq.Expressions.Expression.Lambda<System.Func<StoreRetailPrice, bool>>(
                        System.Linq.Expressions.Expression.Equal(
                            member,
                            System.Linq.Expressions.Expression.Constant(value, typeof(string))
                        ),
                        newParam
                    )
                ),
                "notequal" => query.Where(
                    System.Linq.Expressions.Expression.Lambda<System.Func<StoreRetailPrice, bool>>(
                        System.Linq.Expressions.Expression.NotEqual(
                            member,
                            System.Linq.Expressions.Expression.Constant(value, typeof(string))
                        ),
                        newParam
                    )
                ),
                "contains" => query.Where(
                    System.Linq.Expressions.Expression.Lambda<System.Func<StoreRetailPrice, bool>>(
                        System.Linq.Expressions.Expression.AndAlso(
                            System.Linq.Expressions.Expression.NotEqual(
                                member,
                                System.Linq.Expressions.Expression.Constant(null, typeof(string))
                            ),
                            System.Linq.Expressions.Expression.Call(
                                member,
                                typeof(string).GetMethod("Contains", new[] { typeof(string) })!,
                                System.Linq.Expressions.Expression.Constant(value)
                            )
                        ),
                        newParam
                    )
                ),
                "notcontains" => query.Where(
                    System.Linq.Expressions.Expression.Lambda<System.Func<StoreRetailPrice, bool>>(
                        System.Linq.Expressions.Expression.OrElse(
                            System.Linq.Expressions.Expression.Equal(
                                member,
                                System.Linq.Expressions.Expression.Constant(null, typeof(string))
                            ),
                            System.Linq.Expressions.Expression.Not(
                                System.Linq.Expressions.Expression.Call(
                                    member,
                                    typeof(string).GetMethod("Contains", new[] { typeof(string) })!,
                                    System.Linq.Expressions.Expression.Constant(value)
                                )
                            )
                        ),
                        newParam
                    )
                ),
                "startswith" => query.Where(
                    System.Linq.Expressions.Expression.Lambda<System.Func<StoreRetailPrice, bool>>(
                        System.Linq.Expressions.Expression.AndAlso(
                            System.Linq.Expressions.Expression.NotEqual(
                                member,
                                System.Linq.Expressions.Expression.Constant(null, typeof(string))
                            ),
                            System.Linq.Expressions.Expression.Call(
                                member,
                                typeof(string).GetMethod("StartsWith", new[] { typeof(string) })!,
                                System.Linq.Expressions.Expression.Constant(value)
                            )
                        ),
                        newParam
                    )
                ),
                "endswith" => query.Where(
                    System.Linq.Expressions.Expression.Lambda<System.Func<StoreRetailPrice, bool>>(
                        System.Linq.Expressions.Expression.AndAlso(
                            System.Linq.Expressions.Expression.NotEqual(
                                member,
                                System.Linq.Expressions.Expression.Constant(null, typeof(string))
                            ),
                            System.Linq.Expressions.Expression.Call(
                                member,
                                typeof(string).GetMethod("EndsWith", new[] { typeof(string) })!,
                                System.Linq.Expressions.Expression.Constant(value)
                            )
                        ),
                        newParam
                    )
                ),
                "blank" => query.Where(
                    System.Linq.Expressions.Expression.Lambda<System.Func<StoreRetailPrice, bool>>(
                        System.Linq.Expressions.Expression.Call(
                            typeof(string),
                            "IsNullOrEmpty",
                            null,
                            member
                        ),
                        newParam
                    )
                ),
                "notblank" => query.Where(
                    System.Linq.Expressions.Expression.Lambda<System.Func<StoreRetailPrice, bool>>(
                        System.Linq.Expressions.Expression.Not(
                            System.Linq.Expressions.Expression.Call(
                                typeof(string),
                                "IsNullOrEmpty",
                                null,
                                member
                            )
                        ),
                        newParam
                    )
                ),
                _ => query,
            };
        }

        public async Task<ApiResponse<SyncRetailPriceFromHqResult>> SyncFromHqAsync(
            List<string>? storeCodes,
            DateTime? startDate,
            DateTime? endDate = null
        )
        {
            return await _hqSyncService.SyncForPageAsync(storeCodes, startDate, endDate);
        }

        private static StoreRetailPrice MapHqToLocal(DIC_商品零售价表 hq, DateTime now)
        {
            return new StoreRetailPrice
            {
                UUID = hq.HGUID ?? UuidHelper.GenerateUuid7(),
                StoreCode = hq.H分店代码,
                ProductCode = hq.H商品编码,
                StoreProductCode = hq.H分店商品编码,
                SupplierCode = hq.H供应商编码,
                PurchasePrice = hq.H进货价,
                StoreRetailPriceValue = hq.H分店零售价,
                DiscountRate = StoreDiscountRate.Normalize(hq.H折扣率),
                IsActive = hq.H使用状态,
                IsAutoPricing = hq.H是否自动定价,
                IsSpecialProduct = hq.H是否特殊商品,
                CreatedAt = now,
                UpdatedAt = now,
                IsDeleted = false,
            };
        }
    }
}
