using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HqEntities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Features.ProductWarehouse;

internal sealed class WarehouseProductTableQueryStore : ProductWarehouseSliceBase
{
    private readonly WarehouseProductTableQueryBuilder _queryBuilder;

    internal WarehouseProductTableQueryStore(ProductWarehouseSliceContext context)
        : base(context)
    {
        _queryBuilder = new WarehouseProductTableQueryBuilder(context);
    }

    internal ISugarQueryable<ProductWarehouseTableCodeSearchCandidate> BuildWarehouseTextSearchCandidateQuery(
        string keyword
    ) => _queryBuilder.BuildWarehouseTextSearchCandidateQuery(keyword);

    internal ISugarQueryable<ProductWarehouseTableCodeSearchCandidate> BuildWarehouseCodeSearchCandidateQuery(
        string keyword
    ) => _queryBuilder.BuildWarehouseCodeSearchCandidateQuery(keyword);

    /// <summary>
    /// 获取仓库商品列表（Antd Table 格式）
    /// 支持分类筛选、关键词搜索、分页
    /// 关联查询：仓库商品 + 国内商品 + 中国供应商 + 商品 + 仓库分类
    /// </summary>
    /// <param name="request">表格请求参数</param>
    /// <returns>分页后的仓库商品列表</returns>
    internal async Task<WarehouseProductTableQueryOutcome> QueryAsync(
        ReactTableRequestDto request
    )
    {
        var totalStopwatch = Stopwatch.StartNew();
        var timings = new ProductWarehouseTableTimings();
        var keyword = string.IsNullOrWhiteSpace(request.GlobalSearch)
            ? null
            : request.GlobalSearch.Trim();
        var isCodeLikeKeyword = keyword != null && WarehouseProductTableDiagnostics.IsWarehouseCodeLikeKeyword(keyword);
        var useTextSearchCandidates =
            keyword != null
            && !isCodeLikeKeyword
            && string.Equals(
                request.SortBy?.Trim(),
                "itemNumber",
                StringComparison.OrdinalIgnoreCase
            );
        var requestSnapshot = WarehouseProductTableDiagnostics.CreateWarehouseProductTableRequestSnapshot(
            request,
            keyword,
            isCodeLikeKeyword
        );

        var warehouseProductQuery = WarehouseProductTableDiagnostics.MeasureWarehouseProductTableStage(
            "candidate",
            totalStopwatch,
            timings,
            requestSnapshot,
            elapsedMs => timings.CandidateMs = elapsedMs,
            () =>
            {
                var baseQuery = _context.Db.Queryable<WarehouseProduct>();
                if (
                    keyword == null
                    || (!isCodeLikeKeyword && !useTextSearchCandidates)
                )
                {
                    return baseQuery;
                }

                // 货号排序存在行目标时，宽 OR 会让 SQL Server 沿货号索引逐行执行相关子查询；
                // 先收敛文本候选集，确保排序和分页只处理实际命中的商品。
                var candidateQuery = isCodeLikeKeyword
                    ? _queryBuilder.BuildWarehouseCodeSearchCandidateQuery(keyword)
                    : _queryBuilder.BuildWarehouseTextSearchCandidateQuery(keyword);
                return baseQuery
                    .InnerJoin(
                        candidateQuery,
                        (warehouseProduct, candidate) =>
                            warehouseProduct.ProductCode == candidate.ProductCode
                    )
                    .Select((warehouseProduct, candidate) => warehouseProduct)
                    .MergeTable();
            }
        );

        // 多表关联查询（使用 LeftJoin 避免 N+1 问题）
        var query = warehouseProductQuery
            .LeftJoin<DomesticProduct>(
                (w, dp) => dp.ProductCode == w.ProductCode && !dp.IsDeleted
            )
            .LeftJoin<ChinaSupplier>(
                (w, dp, s) => dp.SupplierCode == s.SupplierCode && !s.IsDeleted
            )
            .InnerJoin<Product>((w, dp, s, p) => p.ProductCode == w.ProductCode && !p.IsDeleted)
            .LeftJoin<WarehouseCategory>(
                (w, dp, s, p, c) => p.WarehouseCategoryGUID == c.CategoryGUID && !c.IsDeleted
            )
            .LeftJoin<HBLocalSupplier>(
                (w, dp, s, p, c, ls) =>
                    p.LocalSupplierCode == ls.LocalSupplierCode && !ls.IsDeleted
            )
            .Where(w => !w.IsDeleted);

        // 分类筛选互斥处理：先清洗具体分类，具体分类优先，未分类只在未选择具体分类时生效。
        var requestedCategoryGuids = request.CategoryGuids
            ?.Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g.Trim())
            .Distinct()
            .ToList() ?? new List<string>();
        if (requestedCategoryGuids.Any())
        {
            var guids = request.IncludeSubCategories
                ? GetCategoryAndSubCategories(requestedCategoryGuids)
                : requestedCategoryGuids;
            query = query.Where(
                (w, dp, s, p, c, ls) =>
                    p.WarehouseCategoryGUID != null && guids.Contains(p.WarehouseCategoryGUID)
            );
        }
        else if (request.UncategorizedOnly)
        {
            query = query.Where(
                (w, dp, s, p, c, ls) =>
                    p.WarehouseCategoryGUID == null || p.WarehouseCategoryGUID == string.Empty
            );
        }

        if (keyword != null && !isCodeLikeKeyword && !useTextSearchCandidates)
        {
            // 没有「查看国内供应商名称」权限的账号不能按名称搜出商品：名称这一支换成匹配不到的占位关键词。
            var supplierNameKeyword = DomesticSupplierInfoMasking.GuardNameKeyword(keyword);
            var globalSearchExpression = Expressionable.Create<
                WarehouseProduct,
                DomesticProduct,
                ChinaSupplier,
                Product,
                WarehouseCategory,
                HBLocalSupplier
            >();

            // SQL Server 默认不区分大小写；列侧不包 ToLower，保留索引可用性。
            globalSearchExpression = globalSearchExpression.Or(
                (w, dp, s, p, c, ls) =>
                    (p.ProductName != null && p.ProductName.Contains(keyword))
                    || (p.EnglishName != null && p.EnglishName.Contains(keyword))
                    || (p.ItemNumber != null && p.ItemNumber.Contains(keyword))
                    || (p.Barcode != null && p.Barcode.Contains(keyword))
                    || (c.CategoryName != null && c.CategoryName.Contains(keyword))
                    || (s.SupplierName != null && s.SupplierName.Contains(supplierNameKeyword))
                    || (p.LocalSupplierCode != null && p.LocalSupplierCode.Contains(keyword))
                    || (ls.Name != null && ls.Name.Contains(keyword))
            );

            query = query.Where(
                globalSearchExpression
                    .Or(WarehouseProductTableFilters.BuildPickingLocationCodePredicate("contains", keyword))
                    .Or(WarehouseProductTableFilters.BuildPickingLocationBarcodePredicate("contains", keyword))
                    .ToExpression()
            );
        }

        if (request.Filters != null && request.Filters.Any())
        {
            foreach (var kv in request.Filters)
            {
                var key = kv.Key?.ToLower();
                var values =
                    kv.Value?.Where(v => !string.IsNullOrWhiteSpace(v)).ToList()
                    ?? new List<string>();
                if (!values.Any())
                    continue;

                // 列头筛选 token 约定：旧裸文本保持 contains；新匹配模式使用 __filter: 前缀，避免误伤旧值。
                switch (key)
                {
                    case "productname":
                    case "name":
                        query = WarehouseProductTableFilters.ApplyWarehouseTextMatchFilter(
                            query,
                            values,
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    p.ProductName != null
                                    && p.ProductName.Contains(value),
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    p.ProductName != null && p.ProductName == value,
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    p.ProductName != null
                                    && p.ProductName.StartsWith(value),
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    p.ProductName != null
                                    && p.ProductName.EndsWith(value)
                        );
                        break;
                    case "nameen":
                        query = WarehouseProductTableFilters.ApplyWarehouseTextMatchFilter(
                            query,
                            values,
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    p.EnglishName != null
                                    && p.EnglishName.Contains(value),
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    p.EnglishName != null && p.EnglishName == value,
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    p.EnglishName != null
                                    && p.EnglishName.StartsWith(value),
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    p.EnglishName != null
                                    && p.EnglishName.EndsWith(value)
                        );
                        break;
                    case "itemnumber":
                        query = WarehouseProductTableFilters.ApplyWarehouseTextMatchFilter(
                            query,
                            values,
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    p.ItemNumber != null
                                    && p.ItemNumber.Contains(value),
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    p.ItemNumber != null && p.ItemNumber == value,
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    p.ItemNumber != null
                                    && p.ItemNumber.StartsWith(value),
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    p.ItemNumber != null
                                    && p.ItemNumber.EndsWith(value)
                        );
                        break;
                    case "barcode":
                        query = WarehouseProductTableFilters.ApplyWarehouseTextMatchFilter(
                            query,
                            values,
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    p.Barcode != null && p.Barcode.Contains(value),
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    p.Barcode != null && p.Barcode == value,
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    p.Barcode != null && p.Barcode.StartsWith(value),
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    p.Barcode != null && p.Barcode.EndsWith(value)
                        );
                        break;
                    case "locationcodes":
                        query = WarehouseProductTableFilters.ApplyPickingLocationTextMatchFilter(query, values);
                        break;
                    case "categoryname":
                        // 兼容旧客户端的分类名称文本筛选；新仓库商品页分类筛选走顶层 CategoryGuids/UncategorizedOnly。
                        query = WarehouseProductTableFilters.ApplyWarehouseTextMatchFilter(
                            query,
                            values,
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    c.CategoryName != null
                                    && c.CategoryName.Contains(value),
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    c.CategoryName != null && c.CategoryName == value,
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    c.CategoryName != null
                                    && c.CategoryName.StartsWith(value),
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    c.CategoryName != null
                                    && c.CategoryName.EndsWith(value)
                        );
                        break;
                    case "suppliername":
                    case "domesticsuppliername":
                        // 没有名称权限的账号按名称列筛选时一律筛不出结果，不能借列头筛选试探名称。
                        if (!DomesticSupplierInfoMasking.IsNameSearchAllowed)
                        {
                            values = new List<string> { DomesticSupplierInfoMasking.BlockedNameKeyword };
                        }
                        query = WarehouseProductTableFilters.ApplyWarehouseTextMatchFilter(
                            query,
                            values,
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    s.SupplierName != null
                                    && s.SupplierName.Contains(value),
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    s.SupplierName != null && s.SupplierName == value,
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    s.SupplierName != null
                                    && s.SupplierName.StartsWith(value),
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    s.SupplierName != null
                                    && s.SupplierName.EndsWith(value)
                        );
                        break;
                    case "suppliercode":
                    case "domesticsuppliercode":
                        var supplierCodes = WarehouseProductTableFilters.NormalizeWarehouseExactTextFilterValues(values);
                        if (supplierCodes.Any())
                        {
                            query = query.Where(
                                (w, dp, s, p, c, ls) =>
                                    s.SupplierCode != null && supplierCodes.Contains(s.SupplierCode)
                            );
                        }
                        break;
                    case "localsuppliercode":
                        var localSupplierCodes = WarehouseProductTableFilters.NormalizeWarehouseExactTextFilterValues(values);
                        if (localSupplierCodes.Any())
                        {
                            query = query.Where(
                                (w, dp, s, p, c, ls) =>
                                    p.LocalSupplierCode != null
                                    && localSupplierCodes.Contains(p.LocalSupplierCode)
                            );
                        }
                        break;
                    case "localsuppliername":
                        query = WarehouseProductTableFilters.ApplyWarehouseTextMatchFilter(
                            query,
                            values,
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    ls.Name != null && ls.Name.Contains(value),
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    ls.Name != null && ls.Name == value,
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    ls.Name != null && ls.Name.StartsWith(value),
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    ls.Name != null && ls.Name.EndsWith(value)
                        );
                        break;
                    case "domesticprice":
                        query = WarehouseProductTableFilters.ApplyWarehouseDecimalRangeFilter(
                            query,
                            values,
                            // 显式分支保持数值列比较语义：仓库值优先，仅 null 时使用国内商品值。
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    (
                                        w.DomesticPrice.HasValue
                                        && w.DomesticPrice.Value >= value
                                    )
                                    || (
                                        w.DomesticPrice == null
                                        && dp.DomesticPrice != null
                                        && dp.DomesticPrice.Value >= value
                                    ),
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    (
                                        w.DomesticPrice.HasValue
                                        && w.DomesticPrice.Value <= value
                                    )
                                    || (
                                        w.DomesticPrice == null
                                        && dp.DomesticPrice != null
                                        && dp.DomesticPrice.Value <= value
                                    ),
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    (
                                        w.DomesticPrice.HasValue
                                        && w.DomesticPrice.Value == value
                                    )
                                    || (
                                        w.DomesticPrice == null
                                        && dp.DomesticPrice != null
                                        && dp.DomesticPrice.Value == value
                                    )
                        );
                        break;
                    case "oemprice":
                    case "labelprice":
                        query = WarehouseProductTableFilters.ApplyWarehouseDecimalRangeFilter(
                            query,
                            values,
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    w.OEMPrice.HasValue && w.OEMPrice.Value >= value,
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    w.OEMPrice.HasValue && w.OEMPrice.Value <= value,
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    w.OEMPrice.HasValue && w.OEMPrice.Value == value
                        );
                        break;
                    case "importprice":
                        query = WarehouseProductTableFilters.ApplyWarehouseDecimalRangeFilter(
                            query,
                            values,
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    w.ImportPrice.HasValue && w.ImportPrice.Value >= value,
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    w.ImportPrice.HasValue && w.ImportPrice.Value <= value,
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    w.ImportPrice.HasValue && w.ImportPrice.Value == value
                        );
                        break;
                    case "packingqty":
                    case "packingquantity":
                        query = WarehouseProductTableFilters.ApplyWarehouseIntRangeFilter(
                            query,
                            values,
                            // 展示值优先取国内商品，缺失时回退仓库商品；过滤条件必须保持相同语义。
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    (
                                        dp.PackingQuantity != null
                                        && dp.PackingQuantity.Value >= value
                                    )
                                    || (
                                        dp.PackingQuantity == null
                                        && w.PackingQuantity.HasValue
                                        && w.PackingQuantity.Value >= value
                                    ),
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    (
                                        dp.PackingQuantity != null
                                        && dp.PackingQuantity.Value <= value
                                    )
                                    || (
                                        dp.PackingQuantity == null
                                        && w.PackingQuantity.HasValue
                                        && w.PackingQuantity.Value <= value
                                    ),
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    (
                                        dp.PackingQuantity != null
                                        && dp.PackingQuantity.Value == value
                                    )
                                    || (
                                        dp.PackingQuantity == null
                                        && w.PackingQuantity.HasValue
                                        && w.PackingQuantity.Value == value
                                    )
                        );
                        break;
                    case "volume":
                        query = WarehouseProductTableFilters.ApplyWarehouseDecimalRangeFilter(
                            query,
                            values,
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    (w.Volume.HasValue && w.Volume.Value >= value)
                                    || (
                                        w.Volume == null
                                        && dp.UnitVolume != null
                                        && dp.UnitVolume.Value >= value
                                    ),
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    (w.Volume.HasValue && w.Volume.Value <= value)
                                    || (
                                        w.Volume == null
                                        && dp.UnitVolume != null
                                        && dp.UnitVolume.Value <= value
                                    ),
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    (w.Volume.HasValue && w.Volume.Value == value)
                                    || (
                                        w.Volume == null
                                        && dp.UnitVolume != null
                                        && dp.UnitVolume.Value == value
                                    )
                        );
                        break;
                    case "minorderquantity":
                        query = WarehouseProductTableFilters.ApplyWarehouseIntRangeFilter(
                            query,
                            values,
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    w.MinOrderQuantity.HasValue
                                    && w.MinOrderQuantity.Value >= value,
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    w.MinOrderQuantity.HasValue
                                    && w.MinOrderQuantity.Value <= value,
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    w.MinOrderQuantity.HasValue
                                    && w.MinOrderQuantity.Value == value
                        );
                        break;
                    case "isactive":
                        var flags = WarehouseProductTableFilters.ParseBooleanFilterValues(values);
                        if (flags.Count == 1)
                        {
                            var isActive = flags[0];
                            query = query.Where(w => w.IsActive == isActive);
                        }
                        break;
                    case "producttype":
                        var productTypes = WarehouseProductTableFilters.ParseIntFilterValues(values);
                        if (productTypes.Any())
                        {
                            query = query.Where(
                                (w, dp, s, p, c, ls) =>
                                    productTypes.Contains(p.ProductType ?? dp.ProductType)
                            );
                        }
                        break;
                    case "updatedat":
                        query = WarehouseProductTableFilters.ApplyWarehouseDateRangeFilter(
                            query,
                            values,
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    w.UpdatedAt.HasValue && w.UpdatedAt.Value >= value,
                            value =>
                                (w, dp, s, p, c, ls) =>
                                    w.UpdatedAt.HasValue && w.UpdatedAt.Value <= value
                        );
                        break;
                    case "createdat":
                        query = WarehouseProductTableFilters.ApplyWarehouseDateRangeFilter(
                            query,
                            values,
                            value => (w, dp, s, p, c, ls) => w.CreatedAt >= value,
                            value => (w, dp, s, p, c, ls) => w.CreatedAt <= value
                        );
                        break;
                }
            }
        }

        var sortPlan = WarehouseProductTableSortPlan.Create(request);
        var orderDesc = sortPlan.Descending;
        if (sortPlan.HasRequestedSort)
        {
            var sort = sortPlan.Sort;
            // 按被遮掉的国内供应商名称排序会泄露相对顺序，无权限时改按供应商代码排。
            if (
                !DomesticSupplierInfoMasking.IsNameSearchAllowed
                && (sort == "suppliername" || sort == "domesticsuppliername")
            )
            {
                sort = "suppliercode";
            }
            if (sort == "productname" || sort == "name")
                query = orderDesc
                    ? query.OrderBy((w, dp, s, p, c, ls) => p.ProductName, OrderByType.Desc)
                    : query.OrderBy((w, dp, s, p, c, ls) => dp.ProductName, OrderByType.Asc);
            else if (sort == "nameen")
                query = orderDesc
                    ? query.OrderBy((w, dp, s, p, c, ls) => p.EnglishName, OrderByType.Desc)
                    : query.OrderBy((w, dp, s, p, c, ls) => p.EnglishName, OrderByType.Asc);
            else if (sort == "itemnumber")
                query = orderDesc
                    ? query.OrderBy((w, dp, s, p, c, ls) => p.ItemNumber, OrderByType.Desc)
                    : query.OrderBy((w, dp, s, p, c, ls) => p.ItemNumber, OrderByType.Asc);
            else if (sort == "barcode")
                query = orderDesc
                    ? query.OrderBy((w, dp, s, p, c, ls) => p.Barcode, OrderByType.Desc)
                    : query.OrderBy((w, dp, s, p, c, ls) => p.Barcode, OrderByType.Asc);
            else if (sort == "categoryname")
                query = orderDesc
                    ? query.OrderBy((w, dp, s, p, c, ls) => c.CategoryName, OrderByType.Desc)
                    : query.OrderBy((w, dp, s, p, c, ls) => c.CategoryName, OrderByType.Asc);
            else if (sort == "suppliername")
                query = orderDesc
                    ? query.OrderBy((w, dp, s, p, c, ls) => s.SupplierName, OrderByType.Desc)
                    : query.OrderBy((w, dp, s, p, c, ls) => s.SupplierName, OrderByType.Asc);
            else if (sort == "suppliercode")
                query = orderDesc
                    ? query.OrderBy((w, dp, s, p, c, ls) => s.SupplierCode, OrderByType.Desc)
                    : query.OrderBy((w, dp, s, p, c, ls) => s.SupplierCode, OrderByType.Asc);
            else if (sort == "domesticsuppliername")
                query = orderDesc
                    ? query.OrderBy((w, dp, s, p, c, ls) => s.SupplierName, OrderByType.Desc)
                    : query.OrderBy((w, dp, s, p, c, ls) => s.SupplierName, OrderByType.Asc);
            else if (sort == "domesticsuppliercode")
                query = orderDesc
                    ? query.OrderBy((w, dp, s, p, c, ls) => s.SupplierCode, OrderByType.Desc)
                    : query.OrderBy((w, dp, s, p, c, ls) => s.SupplierCode, OrderByType.Asc);
            else if (sort == "localsuppliercode")
                query = orderDesc
                    ? query.OrderBy(
                        (w, dp, s, p, c, ls) => p.LocalSupplierCode,
                        OrderByType.Desc
                    )
                    : query.OrderBy(
                        (w, dp, s, p, c, ls) => p.LocalSupplierCode,
                        OrderByType.Asc
                    );
            else if (sort == "localsuppliername")
                query = orderDesc
                    ? query.OrderBy((w, dp, s, p, c, ls) => ls.Name, OrderByType.Desc)
                    : query.OrderBy((w, dp, s, p, c, ls) => ls.Name, OrderByType.Asc);
            else if (sort == "domesticprice")
                query = orderDesc
                    ? query.OrderBy(
                        (w, dp, s, p, c, ls) =>
                            SqlFunc.IsNull(w.DomesticPrice, dp.DomesticPrice),
                        OrderByType.Desc
                    )
                        .OrderBy(
                            (w, dp, s, p, c, ls) => w.ProductCode,
                            OrderByType.Asc
                        )
                    : query.OrderBy(
                        (w, dp, s, p, c, ls) =>
                            SqlFunc.IsNull(w.DomesticPrice, dp.DomesticPrice),
                        OrderByType.Asc
                    )
                        .OrderBy(
                            (w, dp, s, p, c, ls) => w.ProductCode,
                            OrderByType.Asc
                        );
            else if (sort == "oemprice")
                query = orderDesc
                    ? query.OrderBy(w => w.OEMPrice, OrderByType.Desc)
                    : query.OrderBy(w => w.OEMPrice, OrderByType.Asc);
            else if (sort == "importprice")
                query = orderDesc
                    ? query.OrderBy(w => w.ImportPrice, OrderByType.Desc)
                    : query.OrderBy(w => w.ImportPrice, OrderByType.Asc);
            else if (sort == "volume")
                query = orderDesc
                    ? query.OrderBy(w => w.Volume, OrderByType.Desc)
                    : query.OrderBy(w => w.Volume, OrderByType.Asc);
            else if (sort == "minorderquantity")
                query = orderDesc
                    ? query.OrderBy(w => w.MinOrderQuantity, OrderByType.Desc)
                    : query.OrderBy(w => w.MinOrderQuantity, OrderByType.Asc);
            else if (sort == "createdat")
                query = orderDesc
                    ? query.OrderBy(w => w.CreatedAt, OrderByType.Desc)
                    : query.OrderBy(w => w.CreatedAt, OrderByType.Asc);
            else if (sort == "updatedat")
                query = orderDesc
                    ? query.OrderBy(w => w.UpdatedAt, OrderByType.Desc)
                    : query.OrderBy(w => w.UpdatedAt, OrderByType.Asc);
            else
                query = query.OrderBy(w => w.UpdatedAt, OrderByType.Desc);
        }
        else
        {
            query = query.OrderBy(w => w.UpdatedAt, OrderByType.Desc);
        }

        var total = await WarehouseProductTableDiagnostics.MeasureWarehouseProductTableStageAsync(
            "count",
            totalStopwatch,
            timings,
            requestSnapshot,
            elapsedMs => timings.CountMs = elapsedMs,
            () => query.Clone().CountAsync()
        );

        var pageProductCodes = await WarehouseProductTableDiagnostics.MeasureWarehouseProductTableStageAsync(
            "page",
            totalStopwatch,
            timings,
            requestSnapshot,
            elapsedMs => timings.PageMs = elapsedMs,
            () =>
                query
                    .Clone()
                    .Select((w, dp, s, p, c, ls) => w.ProductCode)
                    .Skip((request.Page - 1) * request.PageSize)
                    .Take(request.PageSize)
                    .ToListAsync()
        );

        if (!pageProductCodes.Any())
        {
            return new WarehouseProductTableQueryOutcome(
                WarehouseProductTableResultAssembler.Empty(total),
                requestSnapshot,
                timings.Snapshot(totalStopwatch.ElapsedMilliseconds),
                total,
                0
            );
        }

        var pagePickingLocations = await WarehouseProductTableDiagnostics.MeasureWarehouseProductTableStageAsync(
            "location",
            totalStopwatch,
            timings,
            requestSnapshot,
            elapsedMs => timings.LocationMs = elapsedMs,
            () =>
                _context
                    .Db.Queryable<ProductLocation>()
                    .InnerJoin<Location>((pl, l) => pl.LocationGuid == l.LocationGuid)
                    .Where(
                        (pl, l) =>
                            pl.ProductCode != null
                            && pageProductCodes.Contains(pl.ProductCode)
                            && !pl.IsDeleted
                            && !l.IsDeleted
                            && l.LocationType == PickingLocationType
                    )
                    .Select(
                        (pl, l) =>
                            new WarehouseProductTableLocationRow
                            {
                                ProductCode = pl.ProductCode!,
                                LocationCode = l.LocationCode,
                                LocationBarcode = l.LocationBarcode,
                            }
                    )
                    .ToListAsync()
        );

        var rows = await WarehouseProductTableDiagnostics.MeasureWarehouseProductTableStageAsync(
            "rows",
            totalStopwatch,
            timings,
            requestSnapshot,
            elapsedMs => timings.RowsMs = elapsedMs,
            () =>
                _context
                    .Db.Queryable<WarehouseProduct>()
                    .LeftJoin<DomesticProduct>(
                        (w, dp) => dp.ProductCode == w.ProductCode && !dp.IsDeleted
                    )
                    .LeftJoin<ChinaSupplier>(
                        (w, dp, s) => dp.SupplierCode == s.SupplierCode && !s.IsDeleted
                    )
                    .InnerJoin<Product>(
                        (w, dp, s, p) => p.ProductCode == w.ProductCode && !p.IsDeleted
                    )
                    .LeftJoin<WarehouseCategory>(
                        (w, dp, s, p, c) =>
                            p.WarehouseCategoryGUID == c.CategoryGUID && !c.IsDeleted
                    )
                    .LeftJoin<HBLocalSupplier>(
                        (w, dp, s, p, c, ls) =>
                            p.LocalSupplierCode == ls.LocalSupplierCode && !ls.IsDeleted
                    )
                    .Where(w => !w.IsDeleted && pageProductCodes.Contains(w.ProductCode))
                    .Select(
                        (w, dp, s, p, c, ls) =>
                            new WarehouseProductTableRow
                            {
                                ProductCode = w.ProductCode,
                                ProductName = p.ProductName,
                                EnglishName = p.EnglishName,
                                ItemNumber = p.ItemNumber,
                                Barcode = p.Barcode,
                                CategoryName = c.CategoryName,
                                SupplierName = s.SupplierName,
                                SupplierCode = s.SupplierCode,
                                DomesticSupplierName = s.SupplierName,
                                DomesticSupplierCode = s.SupplierCode,
                                LocalSupplierCode = p.LocalSupplierCode,
                                LocalSupplierName = ls.Name ?? p.LocalSupplierCode,
                                // 列表返回值与国内价筛选、排序保持同一兜底语义。
                                DomesticPrice = SqlFunc.IsNull(
                                    w.DomesticPrice,
                                    dp.DomesticPrice
                                ),
                                OEMPrice = w.OEMPrice,
                                ImportPrice = w.ImportPrice,
                                WarehouseVolume = w.Volume,
                                DomesticUnitVolume = dp.UnitVolume,
                                DomesticPackingQuantity = dp.PackingQuantity,
                                WarehousePackingQuantity = w.PackingQuantity,
                                MinOrderQuantity = w.MinOrderQuantity,
                                IsActive = w.IsActive,
                                CreatedAt = w.CreatedAt,
                                UpdatedAt = w.UpdatedAt,
                                UpdatedBy = w.UpdatedBy,
                                ProductImage = p.ProductImage,
                                ProductType = p.ProductType ?? dp.ProductType,
                            }
                    )
                    .ToListAsync()
        );

        var response = WarehouseProductTableDiagnostics.MeasureWarehouseProductTableStage(
            "map",
            totalStopwatch,
            timings,
            requestSnapshot,
            elapsedMs => timings.MapMs = elapsedMs,
            () => WarehouseProductTableResultAssembler.Assemble(
                pageProductCodes,
                pagePickingLocations,
                rows,
                total
            )
        );

        return new WarehouseProductTableQueryOutcome(
            response,
            requestSnapshot,
            timings.Snapshot(totalStopwatch.ElapsedMilliseconds),
            total,
            response.Items.Count
        );
    }

    private List<string> GetCategoryAndSubCategories(List<string> categoryGuids)
    {
        var all = _context.Db.Queryable<WarehouseCategory>().ToList();
        var result = new HashSet<string>(categoryGuids.Where(g => !string.IsNullOrEmpty(g)));
        var stack = new Stack<string>(result);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            var children = all.Where(c => c.ParentGUID == cur && c.IsActive)
                .Select(c => c.CategoryGUID)
                .Where(x => !string.IsNullOrEmpty(x))
                .ToList();
            foreach (var ch in children)
            {
                if (result.Add(ch))
                    stack.Push(ch);
            }
        }
        return result.ToList();
    }
}
