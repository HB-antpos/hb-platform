using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.ProductWarehouse;

/// <summary>
/// 导入候选列表的无事务 Query Store；保留原筛选、排序、分页及名称补全语义。
/// </summary>
internal sealed class WarehouseProductImportQueryStore : ProductWarehouseSliceBase
{
    internal WarehouseProductImportQueryStore(ProductWarehouseSliceContext context)
        : base(context) { }

    internal async Task<
        ReactTableResponseDto<DomesticProductNotInWarehouseDto>
    > GetDomesticProductsNotInWarehouseAsync(
        GetDomesticProductsNotInWarehouseRequestDto request
    )
    {
        var response = new ReactTableResponseDto<DomesticProductNotInWarehouseDto>();
        var query = _context
            .Db.Queryable<DomesticProduct>()
            .LeftJoin<ChinaSupplier>(
                (product, supplier) =>
                    product.SupplierCode == supplier.SupplierCode
                    && product.SupplierCode != null
            )
            .Where((product, supplier) => !product.IsDeleted && product.IsActive)
            .Where(
                (product, supplier) =>
                    !SqlFunc
                        .Subqueryable<WarehouseProduct>()
                        .Where(warehouse =>
                            warehouse.ProductCode == product.ProductCode
                            && !warehouse.IsDeleted
                        )
                        .Any()
            );

        if (request.SupplierId.HasValue)
        {
            query = query.Where(
                (product, supplier) =>
                    product.SupplierCode != null
                    && product.SupplierCode == request.SupplierId.ToString()
            );
        }
        if (request.ProductType.HasValue)
        {
            query = query.Where(
                (product, supplier) => product.ProductType == (int)request.ProductType.Value
            );
        }
        if (!string.IsNullOrWhiteSpace(request.GlobalSearch))
        {
            var keyword = request.GlobalSearch.Trim().ToLower();
            // 没有「查看国内供应商名称」权限的账号不能按名称搜出商品：名称这一支换成匹配不到的占位关键词。
            var supplierNameKeyword = DomesticSupplierInfoMasking.GuardNameKeyword(keyword);
            query = query.Where(
                (product, supplier) =>
                    (
                        product.ProductName != null
                        && product.ProductName.ToLower().Contains(keyword)
                    )
                    || (
                        product.EnglishProductName != null
                        && product.EnglishProductName.ToLower().Contains(keyword)
                    )
                    || (
                        product.HBProductNo != null
                        && product.HBProductNo.ToLower().Contains(keyword)
                    )
                    || (
                        product.Barcode != null
                        && product.Barcode.ToLower().Contains(keyword)
                    )
                    || (
                        supplier.SupplierName != null
                        && supplier.SupplierName.ToLower().Contains(supplierNameKeyword)
                    )
            );
        }

        if (request.Filters != null && request.Filters.Any())
        {
            foreach (var (filterName, rawValues) in request.Filters)
            {
                var values = rawValues
                    ?.Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToList() ?? new List<string>();
                if (values.Count == 0)
                    continue;

                var lowers = values.Select(value => value.ToLower()).ToList();
                switch (filterName?.ToLower())
                {
                    case "productname":
                    case "name":
                        query = query.Where(
                            (product, supplier) =>
                                product.ProductName != null
                                && lowers.Any(value =>
                                    product.ProductName.ToLower().Contains(value)
                                )
                        );
                        break;
                    case "nameen":
                        query = query.Where(
                            (product, supplier) =>
                                product.EnglishProductName != null
                                && lowers.Any(value =>
                                    product.EnglishProductName.ToLower().Contains(value)
                                )
                        );
                        break;
                    case "itemnumber":
                        query = query.Where(
                            (product, supplier) =>
                                product.HBProductNo != null
                                && lowers.Any(value =>
                                    product.HBProductNo.ToLower().Contains(value)
                                )
                        );
                        break;
                    case "barcode":
                        query = query.Where(
                            (product, supplier) =>
                                product.Barcode != null
                                && lowers.Any(value =>
                                    product.Barcode.ToLower().Contains(value)
                                )
                        );
                        break;
                    case "suppliername":
                        // 没有名称权限的账号按名称列筛选时一律筛不出结果，不能借列头筛选试探名称。
                        if (!DomesticSupplierInfoMasking.IsNameSearchAllowed)
                        {
                            lowers = new List<string> { DomesticSupplierInfoMasking.BlockedNameKeyword };
                        }
                        query = query.Where(
                            (product, supplier) =>
                                supplier.SupplierName != null
                                && lowers.Any(value =>
                                    supplier.SupplierName.ToLower().Contains(value)
                                )
                        );
                        break;
                }
            }
        }

        var orderDesc = string.Equals(
            request.SortOrder,
            "descend",
            StringComparison.OrdinalIgnoreCase
        );
        if (!string.IsNullOrWhiteSpace(request.SortBy))
        {
            var sort = request.SortBy.ToLower();
            // 按被遮掉的国内供应商名称排序会泄露相对顺序，无权限时改按货号排。
            if (sort == "suppliername" && !DomesticSupplierInfoMasking.IsNameSearchAllowed)
            {
                sort = "itemnumber";
            }
            if (sort is "productname" or "name")
                query = orderDesc
                    ? query.OrderBy((product, supplier) => product.ProductName, OrderByType.Desc)
                    : query.OrderBy((product, supplier) => product.ProductName, OrderByType.Asc);
            else if (sort == "nameen")
                query = orderDesc
                    ? query.OrderBy(
                        (product, supplier) => product.EnglishProductName,
                        OrderByType.Desc
                    )
                    : query.OrderBy(
                        (product, supplier) => product.EnglishProductName,
                        OrderByType.Asc
                    );
            else if (sort == "itemnumber")
                query = orderDesc
                    ? query.OrderBy((product, supplier) => product.HBProductNo, OrderByType.Desc)
                    : query.OrderBy((product, supplier) => product.HBProductNo, OrderByType.Asc);
            else if (sort == "barcode")
                query = orderDesc
                    ? query.OrderBy((product, supplier) => product.Barcode, OrderByType.Desc)
                    : query.OrderBy((product, supplier) => product.Barcode, OrderByType.Asc);
            else if (sort == "suppliername")
                query = orderDesc
                    ? query.OrderBy(
                        (product, supplier) => supplier.SupplierName,
                        OrderByType.Desc
                    )
                    : query.OrderBy(
                        (product, supplier) => supplier.SupplierName,
                        OrderByType.Asc
                    );
            else
                query = query.OrderBy(
                    (product, supplier) => product.UpdatedAt,
                    OrderByType.Desc
                );
        }
        else
        {
            query = query.OrderBy(
                (product, supplier) => product.UpdatedAt,
                OrderByType.Desc
            );
        }

        var total = await query.Clone().CountAsync();
        var items = await query
            .Select(
                (product, supplier) =>
                    new WarehouseProductDomesticImportCandidateRow
                    {
                        ProductCode = product.ProductCode,
                        ItemNumber = product.HBProductNo,
                        Barcode = product.Barcode,
                        ProductImage = product.ProductImage,
                        ProductName = product.ProductName,
                        EnglishName = product.EnglishProductName,
                        ProductType = (ProductTypeEnum)product.ProductType,
                        DomesticPrice = product.DomesticPrice,
                        OEMPrice = product.OEMPrice ?? 0m,
                        ImportPrice = product.ImportPrice ?? 0m,
                        Volume = product.UnitVolume,
                        SupplierName = supplier.SupplierName,
                        SupplierCode = product.SupplierCode,
                    }
            )
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        var displayNameSources = WarehouseProductImportQueryResultAssembler.CreateNameSources(
            items
        );
        var nameResolutions = await ResolveImportProductNamesAsync(displayNameSources);
        var translatedDisplayNames = displayNameSources
            .Where(source =>
                nameResolutions.TryGetValue(source.ProductCode, out var resolution)
                && resolution.WasTranslated
                && !string.IsNullOrWhiteSpace(resolution.EnglishName)
            )
            .ToList();
        if (translatedDisplayNames.Count > 0)
        {
            var now = DateTime.Now;
            foreach (var source in translatedDisplayNames)
            {
                source.EnglishProductName = nameResolutions[source.ProductCode].EnglishName;
                source.UpdatedAt = now;
            }
            await _context
                .Db.Updateable(translatedDisplayNames)
                .UpdateColumns(product => new
                {
                    product.EnglishProductName,
                    product.UpdatedAt,
                })
                .WhereColumns(product => new { product.ProductCode })
                .ExecuteCommandAsync();
        }

        var result = WarehouseProductImportQueryResultAssembler.MapDomesticCandidates(
            items,
            nameResolutions.ToDictionary(
                pair => pair.Key,
                pair =>
                    new WarehouseProductImportResolvedName(
                        pair.Value.DisplayName,
                        pair.Value.EnglishName
                    )
            )
        );
        var productCodes = result.Select(item => item.ProductCode).ToList();
        if (productCodes.Count > 0)
        {
            var setProductCodes = await _context
                .Db.Queryable<DomesticSetProduct>()
                .Where(product =>
                    productCodes.Contains(product.ProductCode) && !product.IsDeleted
                )
                .Select(product => product.ProductCode)
                .ToListAsync();
            var multiCodeProductCodes = await _context
                .Db.Queryable<StoreMultiCodeProduct>()
                .Where(product =>
                    product.ProductCode != null
                    && productCodes.Contains(product.ProductCode)
                    && !product.IsDeleted
                )
                .Select(product => product.ProductCode)
                .ToListAsync();
            WarehouseProductImportQueryResultAssembler.ApplyRelationFlags(
                result,
                setProductCodes,
                multiCodeProductCodes
            );
        }

        response.Items = result;
        response.Total = total;
        return response;
    }

    internal async Task<
        ReactTableResponseDto<NonHotbargainProductNotInWarehouseDto>
    > GetNonHotbargainProductsNotInWarehouseAsync(
        GetNonHotbargainProductsNotInWarehouseRequestDto request
    )
    {
        var query = _context
            .Db.Queryable<Product>()
            .LeftJoin<WarehouseProduct>(
                (product, warehouse) =>
                    product.ProductCode == warehouse.ProductCode && !warehouse.IsDeleted
            )
            .LeftJoin<HBLocalSupplier>(
                (product, warehouse, supplier) =>
                    product.LocalSupplierCode == supplier.LocalSupplierCode
                    && !supplier.IsDeleted
            )
            .Where(
                (product, warehouse, supplier) =>
                    !product.IsDeleted
                    && product.IsActive
                    && product.ProductCode != null
                    && warehouse.ProductCode == null
            );

        if (!string.IsNullOrWhiteSpace(request.GlobalSearch))
        {
            var keyword = request.GlobalSearch.Trim();
            query = query.Where(
                (product, warehouse, supplier) =>
                    (product.ItemNumber != null && product.ItemNumber.Contains(keyword))
                    || (product.Barcode != null && product.Barcode.Contains(keyword))
                    || (product.ProductCode != null && product.ProductCode.Contains(keyword))
                    || (product.ProductName != null && product.ProductName.Contains(keyword))
                    || (product.EnglishName != null && product.EnglishName.Contains(keyword))
                    || (
                        product.LocalSupplierCode != null
                        && product.LocalSupplierCode.Contains(keyword)
                    )
                    || (supplier.Name != null && supplier.Name.Contains(keyword))
            );
        }

        if (request.Filters != null && request.Filters.Any())
        {
            foreach (var (filterName, rawValues) in request.Filters)
            {
                var values = rawValues
                    ?.Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToList() ?? new List<string>();
                if (values.Count == 0)
                    continue;

                switch (filterName?.ToLower())
                {
                    case "itemnumber":
                        {
                            var filters = values.Select(value => value.Trim()).ToList();
                            query = query.Where(
                                (product, warehouse, supplier) =>
                                    product.ItemNumber != null
                                    && filters.Any(value => product.ItemNumber.Contains(value))
                            );
                        }
                        break;
                    case "localsuppliercode":
                    case "suppliercode":
                        {
                            var filters = values.Select(value => value.Trim()).ToList();
                            query = query.Where(
                                (product, warehouse, supplier) =>
                                    product.LocalSupplierCode != null
                                    && filters.Contains(product.LocalSupplierCode)
                            );
                        }
                        break;
                    case "localsuppliername":
                        {
                            var filters = values.Select(value => value.Trim()).ToList();
                            query = query.Where(
                                (product, warehouse, supplier) =>
                                    supplier.Name != null
                                    && filters.Any(value => supplier.Name.Contains(value))
                            );
                        }
                        break;
                }
            }
        }

        var total = await query.Clone().CountAsync();
        var rows = await query
            .OrderBy((product, warehouse, supplier) => product.ItemNumber, OrderByType.Asc)
            .OrderBy((product, warehouse, supplier) => product.ProductCode, OrderByType.Asc)
            .Select(
                (product, warehouse, supplier) =>
                    new WarehouseProductNonHotbargainImportCandidateRow
                    {
                        ProductCode = product.ProductCode!,
                        ItemNumber = product.ItemNumber ?? string.Empty,
                        Barcode = product.Barcode,
                        ProductName = product.ProductName,
                        EnglishName = product.EnglishName,
                        ProductType = (ProductTypeEnum)(product.ProductType ?? 0),
                        PurchasePrice = product.PurchasePrice,
                        RetailPrice = product.RetailPrice,
                        LocalSupplierCode = product.LocalSupplierCode,
                        LocalSupplierName = supplier.Name,
                        ProductImage = product.ProductImage,
                    }
            )
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return WarehouseProductImportQueryResultAssembler.MapNonHotbargainCandidates(
            rows,
            total
        );
    }
}

internal sealed class WarehouseProductDomesticImportCandidateRow
{
    public string ProductCode { get; set; } = string.Empty;
    public string? ItemNumber { get; set; }
    public string? Barcode { get; set; }
    public string? ProductImage { get; set; }
    public string? ProductName { get; set; }
    public string? EnglishName { get; set; }
    public ProductTypeEnum ProductType { get; set; }
    public decimal? DomesticPrice { get; set; }
    public decimal OEMPrice { get; set; }
    public decimal ImportPrice { get; set; }
    public decimal? Volume { get; set; }
    public string? SupplierName { get; set; }
    public string? SupplierCode { get; set; }
}

internal sealed class WarehouseProductNonHotbargainImportCandidateRow
{
    public string ProductCode { get; set; } = string.Empty;
    public string ItemNumber { get; set; } = string.Empty;
    public string? Barcode { get; set; }
    public string? ProductName { get; set; }
    public string? EnglishName { get; set; }
    public ProductTypeEnum ProductType { get; set; }
    public decimal? PurchasePrice { get; set; }
    public decimal? RetailPrice { get; set; }
    public string? LocalSupplierCode { get; set; }
    public string? LocalSupplierName { get; set; }
    public string? ProductImage { get; set; }
}
