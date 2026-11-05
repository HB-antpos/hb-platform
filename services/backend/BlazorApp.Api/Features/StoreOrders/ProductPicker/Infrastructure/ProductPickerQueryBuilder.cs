using BlazorApp.Api.Features.StoreOrders.ProductPicker.Domain;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using SqlSugar;

namespace BlazorApp.Api.Features.StoreOrders.ProductPicker.Infrastructure;

internal static class ProductPickerQueryBuilder
{
    internal static ISugarQueryable<Product, WarehouseProduct, WarehouseCategory, HBLocalSupplier>
        CreateDefaultWarehouseProductQuery(
            ISqlSugarClient db,
            bool includeInactiveWarehouseProducts = false
        )
    {
        return CreateDefaultWarehouseProductBaseQuery(db, includeInactiveWarehouseProducts)
            .LeftJoin<WarehouseCategory>(
                (product, warehouseProduct, category) =>
                    product.WarehouseCategoryGUID == category.CategoryGUID
            )
            .LeftJoin<HBLocalSupplier>(
                (product, warehouseProduct, category, supplier) =>
                    product.LocalSupplierCode == supplier.LocalSupplierCode
                    && !supplier.IsDeleted
            );
    }

    internal static ISugarQueryable<Product, WarehouseProduct>
        CreateDefaultWarehouseProductBaseQuery(
            ISqlSugarClient db,
            bool includeInactiveWarehouseProducts = false
        )
    {
        var query = db.Queryable<Product>()
            .InnerJoin<WarehouseProduct>(
                (product, warehouseProduct) =>
                    product.ProductCode == warehouseProduct.ProductCode
            )
            .Where(
                (product, warehouseProduct) =>
                    !product.IsDeleted && !warehouseProduct.IsDeleted
            );

        if (!includeInactiveWarehouseProducts)
        {
            query = query.Where(
                (product, warehouseProduct) =>
                    product.IsActive && warehouseProduct.IsActive
            );
        }

        return query;
    }

    internal static ISugarQueryable<Product, WarehouseProduct, WarehouseCategory, HBLocalSupplier>
        ApplyWarehouseProductColumnFilters(
            ISugarQueryable<
                Product,
                WarehouseProduct,
                WarehouseCategory,
                HBLocalSupplier
            > query,
            StoreOrderProductColumnFiltersDto? filters
        )
    {
        if (filters == null)
        {
            return query;
        }

        var itemNumber = NormalizeColumnFilterText(filters.ItemNumber);
        if (itemNumber != null)
        {
            query = query.Where(
                (product, warehouseProduct, category, supplier) =>
                    product.ItemNumber != null
                    && product.ItemNumber.ToLower().Contains(itemNumber)
            );
        }

        var productName = NormalizeColumnFilterText(filters.ProductName);
        if (productName != null)
        {
            query = query.Where(
                (product, warehouseProduct, category, supplier) =>
                    product.ProductName != null
                    && product.ProductName.ToLower().Contains(productName)
            );
        }

        var barcode = NormalizeColumnFilterText(filters.Barcode);
        if (barcode != null)
        {
            query = query.Where(
                (product, warehouseProduct, category, supplier) =>
                    product.Barcode != null && product.Barcode.ToLower().Contains(barcode)
            );
        }

        var supplierKeyword = NormalizeColumnFilterText(filters.SupplierKeyword);
        if (supplierKeyword != null)
        {
            // 没有「查看国内供应商名称」权限的账号只能按供应商代码筛选：名称、店铺号两支换成匹配不到的占位关键词。
            var supplierNameKeyword = DomesticSupplierInfoMasking.GuardNameKeyword(supplierKeyword);
            query = query.Where(
                (product, warehouseProduct, category, supplier) =>
                    SqlFunc.Subqueryable<DomesticProduct>()
                        .Where(domesticProduct =>
                            !domesticProduct.IsDeleted
                            && SqlFunc.Subqueryable<ChinaSupplier>()
                                .Where(chinaSupplier =>
                                    chinaSupplier.SupplierCode
                                        == domesticProduct.SupplierCode
                                    && !chinaSupplier.IsDeleted
                                    && chinaSupplier.Status == 1
                                    && (
                                        (
                                            chinaSupplier.SupplierCode != null
                                            && chinaSupplier.SupplierCode
                                                .ToLower()
                                                .Contains(supplierKeyword)
                                        )
                                        || (
                                            chinaSupplier.SupplierName != null
                                            && chinaSupplier.SupplierName
                                                .ToLower()
                                                .Contains(supplierNameKeyword)
                                        )
                                        || (
                                            chinaSupplier.ShopNumber != null
                                            && chinaSupplier.ShopNumber
                                                .ToLower()
                                                .Contains(supplierNameKeyword)
                                        )
                                    )
                                )
                                .Any()
                            && (
                                domesticProduct.ProductCode == product.ProductCode
                                || (
                                    domesticProduct.HBProductNo != null
                                    && product.ItemNumber != null
                                    && domesticProduct.HBProductNo == product.ItemNumber
                                )
                                || (
                                    domesticProduct.Barcode != null
                                    && product.Barcode != null
                                    && domesticProduct.Barcode == product.Barcode
                                )
                            )
                        )
                        .Any()
            );
        }

        if (filters.StockQuantityMin.HasValue)
        {
            query = query.Where(
                (product, warehouseProduct, category, supplier) =>
                    (warehouseProduct.StockQuantity ?? 0) >= filters.StockQuantityMin.Value
            );
        }

        if (filters.StockQuantityMax.HasValue)
        {
            query = query.Where(
                (product, warehouseProduct, category, supplier) =>
                    (warehouseProduct.StockQuantity ?? 0) <= filters.StockQuantityMax.Value
            );
        }

        if (filters.MinOrderQuantityMin.HasValue)
        {
            query = query.Where(
                (product, warehouseProduct, category, supplier) =>
                    (warehouseProduct.MinOrderQuantity ?? 1)
                    >= filters.MinOrderQuantityMin.Value
            );
        }

        if (filters.MinOrderQuantityMax.HasValue)
        {
            query = query.Where(
                (product, warehouseProduct, category, supplier) =>
                    (warehouseProduct.MinOrderQuantity ?? 1)
                    <= filters.MinOrderQuantityMax.Value
            );
        }

        if (filters.ImportPriceMin.HasValue)
        {
            query = query.Where(
                (product, warehouseProduct, category, supplier) =>
                    warehouseProduct.ImportPrice.HasValue
                    && warehouseProduct.ImportPrice.Value >= filters.ImportPriceMin.Value
            );
        }

        if (filters.ImportPriceMax.HasValue)
        {
            query = query.Where(
                (product, warehouseProduct, category, supplier) =>
                    warehouseProduct.ImportPrice.HasValue
                    && warehouseProduct.ImportPrice.Value <= filters.ImportPriceMax.Value
            );
        }

        return query;
    }

    internal static ISugarQueryable<Product, WarehouseProduct, WarehouseCategory>
        ApplyWarehouseProductColumnFilters(
            ISugarQueryable<Product, WarehouseProduct, WarehouseCategory> query,
            StoreOrderProductColumnFiltersDto? filters
        )
    {
        if (filters == null)
        {
            return query;
        }

        var itemNumber = NormalizeColumnFilterText(filters.ItemNumber);
        if (itemNumber != null)
        {
            query = query.Where(
                (product, warehouseProduct, category) =>
                    product.ItemNumber != null
                    && product.ItemNumber.ToLower().Contains(itemNumber)
            );
        }

        var productName = NormalizeColumnFilterText(filters.ProductName);
        if (productName != null)
        {
            query = query.Where(
                (product, warehouseProduct, category) =>
                    product.ProductName != null
                    && product.ProductName.ToLower().Contains(productName)
            );
        }

        var barcode = NormalizeColumnFilterText(filters.Barcode);
        if (barcode != null)
        {
            query = query.Where(
                (product, warehouseProduct, category) =>
                    product.Barcode != null && product.Barcode.ToLower().Contains(barcode)
            );
        }

        var supplierKeyword = NormalizeColumnFilterText(filters.SupplierKeyword);
        if (supplierKeyword != null)
        {
            // 没有「查看国内供应商名称」权限的账号只能按供应商代码筛选：名称、店铺号两支换成匹配不到的占位关键词。
            var supplierNameKeyword = DomesticSupplierInfoMasking.GuardNameKeyword(supplierKeyword);
            query = query.Where(
                (product, warehouseProduct, category) =>
                    SqlFunc.Subqueryable<DomesticProduct>()
                        .Where(domesticProduct =>
                            !domesticProduct.IsDeleted
                            && SqlFunc.Subqueryable<ChinaSupplier>()
                                .Where(chinaSupplier =>
                                    chinaSupplier.SupplierCode
                                        == domesticProduct.SupplierCode
                                    && !chinaSupplier.IsDeleted
                                    && chinaSupplier.Status == 1
                                    && (
                                        (
                                            chinaSupplier.SupplierCode != null
                                            && chinaSupplier.SupplierCode
                                                .ToLower()
                                                .Contains(supplierKeyword)
                                        )
                                        || (
                                            chinaSupplier.SupplierName != null
                                            && chinaSupplier.SupplierName
                                                .ToLower()
                                                .Contains(supplierNameKeyword)
                                        )
                                        || (
                                            chinaSupplier.ShopNumber != null
                                            && chinaSupplier.ShopNumber
                                                .ToLower()
                                                .Contains(supplierNameKeyword)
                                        )
                                    )
                                )
                                .Any()
                            && (
                                domesticProduct.ProductCode == product.ProductCode
                                || (
                                    domesticProduct.HBProductNo != null
                                    && product.ItemNumber != null
                                    && domesticProduct.HBProductNo == product.ItemNumber
                                )
                                || (
                                    domesticProduct.Barcode != null
                                    && product.Barcode != null
                                    && domesticProduct.Barcode == product.Barcode
                                )
                            )
                        )
                        .Any()
            );
        }

        if (filters.StockQuantityMin.HasValue)
        {
            query = query.Where(
                (product, warehouseProduct, category) =>
                    (warehouseProduct.StockQuantity ?? 0) >= filters.StockQuantityMin.Value
            );
        }

        if (filters.StockQuantityMax.HasValue)
        {
            query = query.Where(
                (product, warehouseProduct, category) =>
                    (warehouseProduct.StockQuantity ?? 0) <= filters.StockQuantityMax.Value
            );
        }

        if (filters.MinOrderQuantityMin.HasValue)
        {
            query = query.Where(
                (product, warehouseProduct, category) =>
                    (warehouseProduct.MinOrderQuantity ?? 1)
                    >= filters.MinOrderQuantityMin.Value
            );
        }

        if (filters.MinOrderQuantityMax.HasValue)
        {
            query = query.Where(
                (product, warehouseProduct, category) =>
                    (warehouseProduct.MinOrderQuantity ?? 1)
                    <= filters.MinOrderQuantityMax.Value
            );
        }

        if (filters.ImportPriceMin.HasValue)
        {
            query = query.Where(
                (product, warehouseProduct, category) =>
                    warehouseProduct.ImportPrice.HasValue
                    && warehouseProduct.ImportPrice.Value >= filters.ImportPriceMin.Value
            );
        }

        if (filters.ImportPriceMax.HasValue)
        {
            query = query.Where(
                (product, warehouseProduct, category) =>
                    warehouseProduct.ImportPrice.HasValue
                    && warehouseProduct.ImportPrice.Value <= filters.ImportPriceMax.Value
            );
        }

        return query;
    }

    internal static ISugarQueryable<Product, WarehouseCategory, HBLocalSupplier>
        ApplyProductMasterColumnFilters(
            ISugarQueryable<Product, WarehouseCategory, HBLocalSupplier> query,
            StoreOrderProductColumnFiltersDto? filters
        )
    {
        if (filters == null)
        {
            return query;
        }

        var itemNumber = NormalizeColumnFilterText(filters.ItemNumber);
        if (itemNumber != null)
        {
            query = query.Where(
                (product, category, supplier) =>
                    product.ItemNumber != null
                    && product.ItemNumber.ToLower().Contains(itemNumber)
            );
        }

        var productName = NormalizeColumnFilterText(filters.ProductName);
        if (productName != null)
        {
            query = query.Where(
                (product, category, supplier) =>
                    product.ProductName != null
                    && product.ProductName.ToLower().Contains(productName)
            );
        }

        var barcode = NormalizeColumnFilterText(filters.Barcode);
        if (barcode != null)
        {
            query = query.Where(
                (product, category, supplier) =>
                    product.Barcode != null && product.Barcode.ToLower().Contains(barcode)
            );
        }

        var supplierKeyword = NormalizeColumnFilterText(filters.SupplierKeyword);
        if (supplierKeyword != null)
        {
            // 没有「查看国内供应商名称」权限的账号只能按供应商代码筛选：名称、店铺号两支换成匹配不到的占位关键词。
            var supplierNameKeyword = DomesticSupplierInfoMasking.GuardNameKeyword(supplierKeyword);
            query = query.Where(
                (product, category, supplier) =>
                    SqlFunc.Subqueryable<DomesticProduct>()
                        .Where(domesticProduct =>
                            !domesticProduct.IsDeleted
                            && SqlFunc.Subqueryable<ChinaSupplier>()
                                .Where(chinaSupplier =>
                                    chinaSupplier.SupplierCode
                                        == domesticProduct.SupplierCode
                                    && !chinaSupplier.IsDeleted
                                    && chinaSupplier.Status == 1
                                    && (
                                        (
                                            chinaSupplier.SupplierCode != null
                                            && chinaSupplier.SupplierCode
                                                .ToLower()
                                                .Contains(supplierKeyword)
                                        )
                                        || (
                                            chinaSupplier.SupplierName != null
                                            && chinaSupplier.SupplierName
                                                .ToLower()
                                                .Contains(supplierNameKeyword)
                                        )
                                        || (
                                            chinaSupplier.ShopNumber != null
                                            && chinaSupplier.ShopNumber
                                                .ToLower()
                                                .Contains(supplierNameKeyword)
                                        )
                                    )
                                )
                                .Any()
                            && (
                                domesticProduct.ProductCode == product.ProductCode
                                || (
                                    domesticProduct.HBProductNo != null
                                    && product.ItemNumber != null
                                    && domesticProduct.HBProductNo == product.ItemNumber
                                )
                                || (
                                    domesticProduct.Barcode != null
                                    && product.Barcode != null
                                    && domesticProduct.Barcode == product.Barcode
                                )
                            )
                        )
                        .Any()
            );
        }

        if (!NumberRangeAllowsValue(filters.StockQuantityMin, filters.StockQuantityMax, 0))
        {
            query = query.Where((product, category, supplier) => false);
        }

        if (
            !NumberRangeAllowsValue(
                filters.MinOrderQuantityMin,
                filters.MinOrderQuantityMax,
                1
            )
        )
        {
            query = query.Where((product, category, supplier) => false);
        }

        if (filters.ImportPriceMin.HasValue)
        {
            query = query.Where(
                (product, category, supplier) =>
                    product.PurchasePrice.HasValue
                    && product.PurchasePrice.Value >= filters.ImportPriceMin.Value
            );
        }

        if (filters.ImportPriceMax.HasValue)
        {
            query = query.Where(
                (product, category, supplier) =>
                    product.PurchasePrice.HasValue
                    && product.PurchasePrice.Value <= filters.ImportPriceMax.Value
            );
        }

        return query;
    }

    internal static ISugarQueryable<Product, WarehouseProduct, WarehouseCategory, HBLocalSupplier>
        ApplyWarehouseProductSort(
            ISugarQueryable<
                Product,
                WarehouseProduct,
                WarehouseCategory,
                HBLocalSupplier
            > query,
            StoreOrderFilterDto filter
        )
    {
        var sortBy = (filter.SortBy ?? "default").Trim().ToLower();
        var orderType = filter.SortDescending ? OrderByType.Desc : OrderByType.Asc;

        return sortBy switch
        {
            "priceasc" => query
                .OrderBy((product, warehouseProduct, category, supplier) => warehouseProduct.OEMPrice, OrderByType.Asc)
                .OrderBy((product, warehouseProduct, category, supplier) => product.ProductCode, OrderByType.Asc),
            "pricedesc" => query
                .OrderBy((product, warehouseProduct, category, supplier) => warehouseProduct.OEMPrice, OrderByType.Desc)
                .OrderBy((product, warehouseProduct, category, supplier) => product.ProductCode, OrderByType.Asc),
            "name" => query
                .OrderBy((product, warehouseProduct, category, supplier) => product.ProductName, OrderByType.Asc)
                .OrderBy((product, warehouseProduct, category, supplier) => product.ProductCode, OrderByType.Asc),
            "productname" => query
                .OrderBy((product, warehouseProduct, category, supplier) => product.ProductName, orderType)
                .OrderBy((product, warehouseProduct, category, supplier) => product.ProductCode, OrderByType.Asc),
            "barcode" => query
                .OrderBy((product, warehouseProduct, category, supplier) => product.Barcode, orderType)
                .OrderBy((product, warehouseProduct, category, supplier) => product.ProductCode, OrderByType.Asc),
            "stockquantity" => query
                .OrderBy((product, warehouseProduct, category, supplier) => warehouseProduct.StockQuantity ?? 0, orderType)
                .OrderBy((product, warehouseProduct, category, supplier) => product.ProductCode, OrderByType.Asc),
            "minorderquantity" => query
                .OrderBy((product, warehouseProduct, category, supplier) => warehouseProduct.MinOrderQuantity ?? 1, orderType)
                .OrderBy((product, warehouseProduct, category, supplier) => product.ProductCode, OrderByType.Asc),
            "importprice" => query
                .OrderBy((product, warehouseProduct, category, supplier) => warehouseProduct.ImportPrice ?? 0, orderType)
                .OrderBy((product, warehouseProduct, category, supplier) => product.ProductCode, OrderByType.Asc),
            "itemnumber" => query
                .OrderBy((product, warehouseProduct, category, supplier) => product.ItemNumber, orderType)
                .OrderBy((product, warehouseProduct, category, supplier) => product.ProductCode, OrderByType.Asc),
            _ => query
                .OrderBy((product, warehouseProduct, category, supplier) => product.ItemNumber, OrderByType.Asc)
                .OrderBy((product, warehouseProduct, category, supplier) => product.ProductCode, OrderByType.Asc),
        };
    }

    internal static ISugarQueryable<Product, WarehouseProduct, WarehouseCategory>
        ApplyWarehouseProductSort(
            ISugarQueryable<Product, WarehouseProduct, WarehouseCategory> query,
            StoreOrderFilterDto filter
        )
    {
        var sortBy = (filter.SortBy ?? "default").Trim().ToLower();
        var orderType = filter.SortDescending ? OrderByType.Desc : OrderByType.Asc;

        return sortBy switch
        {
            "priceasc" => query
                .OrderBy((product, warehouseProduct, category) => warehouseProduct.OEMPrice, OrderByType.Asc)
                .OrderBy((product, warehouseProduct, category) => product.ProductCode, OrderByType.Asc),
            "pricedesc" => query
                .OrderBy((product, warehouseProduct, category) => warehouseProduct.OEMPrice, OrderByType.Desc)
                .OrderBy((product, warehouseProduct, category) => product.ProductCode, OrderByType.Asc),
            "name" => query
                .OrderBy((product, warehouseProduct, category) => product.ProductName, OrderByType.Asc)
                .OrderBy((product, warehouseProduct, category) => product.ProductCode, OrderByType.Asc),
            "productname" => query
                .OrderBy((product, warehouseProduct, category) => product.ProductName, orderType)
                .OrderBy((product, warehouseProduct, category) => product.ProductCode, OrderByType.Asc),
            "barcode" => query
                .OrderBy((product, warehouseProduct, category) => product.Barcode, orderType)
                .OrderBy((product, warehouseProduct, category) => product.ProductCode, OrderByType.Asc),
            "stockquantity" => query
                .OrderBy((product, warehouseProduct, category) => warehouseProduct.StockQuantity ?? 0, orderType)
                .OrderBy((product, warehouseProduct, category) => product.ProductCode, OrderByType.Asc),
            "minorderquantity" => query
                .OrderBy((product, warehouseProduct, category) => warehouseProduct.MinOrderQuantity ?? 1, orderType)
                .OrderBy((product, warehouseProduct, category) => product.ProductCode, OrderByType.Asc),
            "importprice" => query
                .OrderBy((product, warehouseProduct, category) => warehouseProduct.ImportPrice ?? 0, orderType)
                .OrderBy((product, warehouseProduct, category) => product.ProductCode, OrderByType.Asc),
            "itemnumber" => query
                .OrderBy((product, warehouseProduct, category) => product.ItemNumber, orderType)
                .OrderBy((product, warehouseProduct, category) => product.ProductCode, OrderByType.Asc),
            _ => query
                .OrderBy((product, warehouseProduct, category) => product.ItemNumber, OrderByType.Asc)
                .OrderBy((product, warehouseProduct, category) => product.ProductCode, OrderByType.Asc),
        };
    }

    internal static ISugarQueryable<Product, WarehouseCategory, HBLocalSupplier>
        ApplyProductMasterSort(
            ISugarQueryable<Product, WarehouseCategory, HBLocalSupplier> query,
            StoreOrderFilterDto filter
        )
    {
        var sortBy = (filter.SortBy ?? "default").Trim().ToLower();
        var orderType = filter.SortDescending ? OrderByType.Desc : OrderByType.Asc;

        return sortBy switch
        {
            "priceasc" => query
                .OrderBy((product, category, supplier) => product.PurchasePrice, OrderByType.Asc)
                .OrderBy((product, category, supplier) => product.ProductCode, OrderByType.Asc),
            "pricedesc" => query
                .OrderBy((product, category, supplier) => product.PurchasePrice, OrderByType.Desc)
                .OrderBy((product, category, supplier) => product.ProductCode, OrderByType.Asc),
            "name" => query
                .OrderBy((product, category, supplier) => product.ProductName, OrderByType.Asc)
                .OrderBy((product, category, supplier) => product.ProductCode, OrderByType.Asc),
            "productname" => query
                .OrderBy((product, category, supplier) => product.ProductName, orderType)
                .OrderBy((product, category, supplier) => product.ProductCode, OrderByType.Asc),
            "barcode" => query
                .OrderBy((product, category, supplier) => product.Barcode, orderType)
                .OrderBy((product, category, supplier) => product.ProductCode, OrderByType.Asc),
            "importprice" => query
                .OrderBy((product, category, supplier) => product.PurchasePrice ?? 0, orderType)
                .OrderBy((product, category, supplier) => product.ProductCode, OrderByType.Asc),
            "itemnumber" => query
                .OrderBy((product, category, supplier) => product.ItemNumber, orderType)
                .OrderBy((product, category, supplier) => product.ProductCode, OrderByType.Asc),
            _ => query
                .OrderBy((product, category, supplier) => product.ItemNumber, OrderByType.Asc)
                .OrderBy((product, category, supplier) => product.ProductCode, OrderByType.Asc),
        };
    }

    internal static ISugarQueryable<Product, WarehouseProduct, WarehouseCategory, HBLocalSupplier>
        ApplyWarehouseProductSearch(
            ISugarQueryable<
                Product,
                WarehouseProduct,
                WarehouseCategory,
                HBLocalSupplier
            > query,
            ProductPickerSearchFilter search,
            List<string>? locationProductCodes = null
        )
    {
        if (locationProductCodes is { Count: > 0 })
        {
            if (!string.IsNullOrWhiteSpace(search.UnifiedKeyword))
            {
                var keyword = search.UnifiedKeyword;
                return query.Where(
                    (product, warehouseProduct, category, supplier) =>
                        (
                            product.ProductCode != null
                            && locationProductCodes.Contains(product.ProductCode)
                        )
                        || (
                            product.ItemNumber != null
                            && product.ItemNumber.ToLower().Contains(keyword)
                        )
                        || (
                            product.Barcode != null
                            && product.Barcode.ToLower().Contains(keyword)
                        )
                        || (
                            product.ProductName != null
                            && product.ProductName.ToLower().Contains(keyword)
                        )
                );
            }

            if (
                !string.IsNullOrWhiteSpace(search.ItemOrBarcodeKeyword)
                && !string.IsNullOrWhiteSpace(search.ProductNameKeyword)
            )
            {
                var itemOrBarcodeKeyword = search.ItemOrBarcodeKeyword;
                var productNameKeyword = search.ProductNameKeyword;
                return query.Where(
                    (product, warehouseProduct, category, supplier) =>
                        (
                            product.ProductCode != null
                            && locationProductCodes.Contains(product.ProductCode)
                        )
                        || (
                            (
                                (
                                    product.ItemNumber != null
                                    && product.ItemNumber
                                        .ToLower()
                                        .Contains(itemOrBarcodeKeyword)
                                )
                                || (
                                    product.Barcode != null
                                    && product.Barcode
                                        .ToLower()
                                        .Contains(itemOrBarcodeKeyword)
                                )
                            )
                            && product.ProductName != null
                            && product.ProductName.ToLower().Contains(productNameKeyword)
                        )
                );
            }

            if (!string.IsNullOrWhiteSpace(search.ItemOrBarcodeKeyword))
            {
                var keyword = search.ItemOrBarcodeKeyword;
                return query.Where(
                    (product, warehouseProduct, category, supplier) =>
                        (
                            product.ProductCode != null
                            && locationProductCodes.Contains(product.ProductCode)
                        )
                        || (
                            product.ItemNumber != null
                            && product.ItemNumber.ToLower().Contains(keyword)
                        )
                        || (
                            product.Barcode != null
                            && product.Barcode.ToLower().Contains(keyword)
                        )
                );
            }

            if (!string.IsNullOrWhiteSpace(search.ProductNameKeyword))
            {
                var keyword = search.ProductNameKeyword;
                return query.Where(
                    (product, warehouseProduct, category, supplier) =>
                        (
                            product.ProductCode != null
                            && locationProductCodes.Contains(product.ProductCode)
                        )
                        || (
                            product.ProductName != null
                            && product.ProductName.ToLower().Contains(keyword)
                        )
                );
            }

            return query.Where(
                (product, warehouseProduct, category, supplier) =>
                    product.ProductCode != null
                    && locationProductCodes.Contains(product.ProductCode)
            );
        }

        if (!string.IsNullOrWhiteSpace(search.UnifiedKeyword))
        {
            var keyword = search.UnifiedKeyword;
            return query.Where(
                (product, warehouseProduct, category, supplier) =>
                    (
                        product.ItemNumber != null
                        && product.ItemNumber.ToLower().Contains(keyword)
                    )
                    || (
                        product.Barcode != null
                        && product.Barcode.ToLower().Contains(keyword)
                    )
                    || (
                        product.ProductName != null
                        && product.ProductName.ToLower().Contains(keyword)
                    )
            );
        }

        if (!string.IsNullOrWhiteSpace(search.ItemOrBarcodeKeyword))
        {
            var keyword = search.ItemOrBarcodeKeyword;
            query = query.Where(
                (product, warehouseProduct, category, supplier) =>
                    (
                        product.ItemNumber != null
                        && product.ItemNumber.ToLower().Contains(keyword)
                    )
                    || (
                        product.Barcode != null
                        && product.Barcode.ToLower().Contains(keyword)
                    )
            );
        }

        if (!string.IsNullOrWhiteSpace(search.ProductNameKeyword))
        {
            var keyword = search.ProductNameKeyword;
            query = query.Where(
                (product, warehouseProduct, category, supplier) =>
                    product.ProductName != null
                    && product.ProductName.ToLower().Contains(keyword)
            );
        }

        return query;
    }

    internal static ISugarQueryable<Product, WarehouseCategory, HBLocalSupplier>
        ApplyProductMasterSearch(
            ISugarQueryable<Product, WarehouseCategory, HBLocalSupplier> query,
            ProductPickerSearchFilter search
        )
    {
        if (!string.IsNullOrWhiteSpace(search.UnifiedKeyword))
        {
            var keyword = search.UnifiedKeyword;
            return query.Where(
                (product, category, supplier) =>
                    (
                        product.ItemNumber != null
                        && product.ItemNumber.ToLower().Contains(keyword)
                    )
                    || (
                        product.Barcode != null
                        && product.Barcode.ToLower().Contains(keyword)
                    )
                    || (
                        product.ProductName != null
                        && product.ProductName.ToLower().Contains(keyword)
                    )
            );
        }

        if (!string.IsNullOrWhiteSpace(search.ItemOrBarcodeKeyword))
        {
            var keyword = search.ItemOrBarcodeKeyword;
            query = query.Where(
                (product, category, supplier) =>
                    (
                        product.ItemNumber != null
                        && product.ItemNumber.ToLower().Contains(keyword)
                    )
                    || (
                        product.Barcode != null
                        && product.Barcode.ToLower().Contains(keyword)
                    )
            );
        }

        if (!string.IsNullOrWhiteSpace(search.ProductNameKeyword))
        {
            var keyword = search.ProductNameKeyword;
            query = query.Where(
                (product, category, supplier) =>
                    product.ProductName != null
                    && product.ProductName.ToLower().Contains(keyword)
            );
        }

        return query;
    }

    internal static ISugarQueryable<Product, WarehouseProduct, WarehouseCategory>
        ApplyOrderPickerProductSearch(
            ISugarQueryable<Product, WarehouseProduct, WarehouseCategory> query,
            ProductPickerSearchFilter search
        )
    {
        if (!string.IsNullOrWhiteSpace(search.UnifiedKeyword))
        {
            var keyword = search.UnifiedKeyword;
            return query.Where(
                (product, warehouseProduct, category) =>
                    (
                        product.ItemNumber != null
                        && product.ItemNumber.ToLower().Contains(keyword)
                    )
                    || (
                        product.Barcode != null
                        && product.Barcode.ToLower().Contains(keyword)
                    )
                    || (
                        product.ProductName != null
                        && product.ProductName.ToLower().Contains(keyword)
                    )
            );
        }

        if (!string.IsNullOrWhiteSpace(search.ItemOrBarcodeKeyword))
        {
            var keyword = search.ItemOrBarcodeKeyword;
            query = query.Where(
                (product, warehouseProduct, category) =>
                    (
                        product.ItemNumber != null
                        && product.ItemNumber.ToLower().Contains(keyword)
                    )
                    || (
                        product.Barcode != null
                        && product.Barcode.ToLower().Contains(keyword)
                    )
            );
        }

        if (!string.IsNullOrWhiteSpace(search.ProductNameKeyword))
        {
            var keyword = search.ProductNameKeyword;
            query = query.Where(
                (product, warehouseProduct, category) =>
                    product.ProductName != null
                    && product.ProductName.ToLower().Contains(keyword)
            );
        }

        return query;
    }

    private static string? NormalizeColumnFilterText(string? value)
    {
        var trimmed = value?.Trim().ToLower();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static bool NumberRangeAllowsValue(int? min, int? max, int value)
    {
        return (!min.HasValue || value >= min.Value)
            && (!max.HasValue || value <= max.Value);
    }
}
