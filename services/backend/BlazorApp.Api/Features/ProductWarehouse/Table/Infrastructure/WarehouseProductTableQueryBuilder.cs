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

internal sealed class WarehouseProductTableQueryBuilder : ProductWarehouseSliceBase
{
    internal WarehouseProductTableQueryBuilder(ProductWarehouseSliceContext context)
        : base(context) { }

    internal ISugarQueryable<ProductWarehouseTableCodeSearchCandidate> BuildWarehouseTextSearchCandidateQuery(
        string keyword
    )
    {
        var isSqlServer = _context.Db.CurrentConnectionConfig.DbType == DbType.SqlServer;

        var productTextQuery = _context
            .Db.Queryable<Product>()
            .Where(p =>
                !p.IsDeleted
                && p.ProductCode != null
                && (
                    (p.ProductName != null && p.ProductName.Contains(keyword))
                    || (p.EnglishName != null && p.EnglishName.Contains(keyword))
                    || (p.ItemNumber != null && p.ItemNumber.Contains(keyword))
                    || (p.Barcode != null && p.Barcode.Contains(keyword))
                    || (
                        p.LocalSupplierCode != null
                        && p.LocalSupplierCode.Contains(keyword)
                    )
                )
            )
            .Select(p => new ProductWarehouseTableCodeSearchCandidate
            {
                ProductCode = p.ProductCode!,
            });

        var categoryNameQuery = _context
            .Db.Queryable<Product>()
            .InnerJoin<WarehouseCategory>(
                (product, category) =>
                    product.WarehouseCategoryGUID == category.CategoryGUID
                    && !category.IsDeleted
            )
            .Where(
                (product, category) =>
                    !product.IsDeleted
                    && product.ProductCode != null
                    && category.CategoryName != null
                    && category.CategoryName.Contains(keyword)
            )
            .Select(
                (product, category) =>
                    new ProductWarehouseTableCodeSearchCandidate
                    {
                        ProductCode = product.ProductCode!,
                    }
            );

        // 没有「查看国内供应商名称」权限的账号不能按名称搜出商品：名称这一支换成匹配不到的占位关键词。
        var supplierNameKeyword = DomesticSupplierInfoMasking.GuardNameKeyword(keyword);
        var domesticSupplierNameQuery = _context
            .Db.Queryable<DomesticProduct>()
            .InnerJoin<ChinaSupplier>(
                (domesticProduct, supplier) =>
                    domesticProduct.SupplierCode == supplier.SupplierCode
                    && !supplier.IsDeleted
            )
            .Where(
                (domesticProduct, supplier) =>
                    !domesticProduct.IsDeleted
                    && domesticProduct.ProductCode != null
                    && supplier.SupplierName != null
                    && supplier.SupplierName.Contains(supplierNameKeyword)
            )
            .Select(
                (domesticProduct, supplier) =>
                    new ProductWarehouseTableCodeSearchCandidate
                    {
                        ProductCode = domesticProduct.ProductCode!,
                    }
            );

        var localSupplierNameQuery = _context
            .Db.Queryable<Product>()
            .InnerJoin<HBLocalSupplier>(
                (product, supplier) =>
                    product.LocalSupplierCode == supplier.LocalSupplierCode
                    && !supplier.IsDeleted
            )
            .Where(
                (product, supplier) =>
                    !product.IsDeleted
                    && product.ProductCode != null
                    && supplier.Name != null
                    && supplier.Name.Contains(keyword)
            )
            .Select(
                (product, supplier) =>
                    new ProductWarehouseTableCodeSearchCandidate
                    {
                        ProductCode = product.ProductCode!,
                }
            );

        var pickingLocationSource = _context
            .Db.Queryable<Location>()
            .InnerJoin<ProductLocation>(
                (location, productLocation) =>
                    location.LocationGuid == productLocation.LocationGuid
                    && !productLocation.IsDeleted
            )
            .WhereIF(
                isSqlServer,
                (location, productLocation) =>
                    !location.IsDeleted
                    && location.LocationType == PickingLocationType
                    && productLocation.ProductCode != null
                    && (
                        (
                            location.LocationCode != null
                            && location.LocationCode.Contains(SqlFunc.ToVarchar(keyword))
                        )
                        || (
                            location.LocationBarcode != null
                            && location.LocationBarcode.Contains(SqlFunc.ToVarchar(keyword))
                        )
                    )
            )
            .WhereIF(
                !isSqlServer,
                (location, productLocation) =>
                    !location.IsDeleted
                    && location.LocationType == PickingLocationType
                    && productLocation.ProductCode != null
                    && (
                        (
                            location.LocationCode != null
                            && location.LocationCode.Contains(keyword)
                        )
                        || (
                            location.LocationBarcode != null
                            && location.LocationBarcode.Contains(keyword)
                        )
                    )
            );
        var pickingLocationQuery = pickingLocationSource.Select(
            (location, productLocation) =>
                new ProductWarehouseTableCodeSearchCandidate
                {
                    ProductCode = productLocation.ProductCode!,
                }
        );

        var unionQuery = _context
            .Db.Union(
                productTextQuery,
                categoryNameQuery,
                domesticSupplierNameQuery,
                localSupplierNameQuery,
                pickingLocationQuery
            )
            .MergeTable();

        // Product 等主数据表使用 nvarchar 商品编码，仓库与货位表使用 varchar；
        // 最外层统一成 varchar，避免连接时转换 WarehouseProduct 的索引列。
        return isSqlServer
            ? unionQuery
                .Select(candidate => new ProductWarehouseTableCodeSearchCandidate
                {
                    ProductCode = SqlFunc.ToVarchar(candidate.ProductCode),
                })
                .MergeTable()
            : unionQuery;
    }

    internal ISugarQueryable<ProductWarehouseTableCodeSearchCandidate> BuildWarehouseCodeSearchCandidateQuery(
        string keyword
    )
    {
        var hbPrefixedKeyword = keyword.StartsWith("HB", StringComparison.OrdinalIgnoreCase)
            ? keyword
            : $"HB{keyword}";
        var isSqlServer = _context.Db.CurrentConnectionConfig.DbType == DbType.SqlServer;

        // SQL Server 的 WarehouseProduct/Location/ProductLocation 为 varchar；参数显式转成 varchar，
        // 避免优化器把这些索引列隐式转成 nvarchar 后退化为扫描。
        var warehouseProductCodeQuery = _context
            .Db.Queryable<WarehouseProduct>()
            .WhereIF(
                isSqlServer,
                w =>
                    !w.IsDeleted
                    && w.ProductCode != null
                    && (
                        w.ProductCode == SqlFunc.ToVarchar(keyword)
                        || w.ProductCode.StartsWith(SqlFunc.ToVarchar(keyword))
                    )
            )
            .WhereIF(
                !isSqlServer,
                w =>
                    !w.IsDeleted
                    && w.ProductCode != null
                    && (w.ProductCode == keyword || w.ProductCode.StartsWith(keyword))
            )
            .Select(w => new ProductWarehouseTableCodeSearchCandidate
            {
                ProductCode = w.ProductCode,
            });

        var itemNumberQuery = _context
            .Db.Queryable<Product>()
            .Where(p =>
                !p.IsDeleted
                && p.ProductCode != null
                && p.ItemNumber != null
                && (p.ItemNumber == keyword || p.ItemNumber.StartsWith(keyword))
            )
            .Select(p => new ProductWarehouseTableCodeSearchCandidate
            {
                ProductCode = p.ProductCode!,
            });

        var hbItemNumberQuery = _context
            .Db.Queryable<Product>()
            .Where(p =>
                !p.IsDeleted
                && p.ProductCode != null
                && p.ItemNumber != null
                && (
                    p.ItemNumber == hbPrefixedKeyword
                    || p.ItemNumber.StartsWith(hbPrefixedKeyword)
                )
            )
            .Select(p => new ProductWarehouseTableCodeSearchCandidate
            {
                ProductCode = p.ProductCode!,
            });

        var barcodeQuery = _context
            .Db.Queryable<Product>()
            .Where(p =>
                !p.IsDeleted
                && p.ProductCode != null
                && p.Barcode != null
                && (p.Barcode == keyword || p.Barcode.StartsWith(keyword))
            )
            .Select(p => new ProductWarehouseTableCodeSearchCandidate
            {
                ProductCode = p.ProductCode!,
            });

        var localSupplierCodeQuery = _context
            .Db.Queryable<Product>()
            .Where(p =>
                !p.IsDeleted
                && p.ProductCode != null
                && p.LocalSupplierCode != null
                && (
                    p.LocalSupplierCode == keyword
                    || p.LocalSupplierCode.StartsWith(keyword)
                )
            )
            .Select(p => new ProductWarehouseTableCodeSearchCandidate
            {
                ProductCode = p.ProductCode!,
            });

        var domesticSupplierCodeQuery = _context
            .Db.Queryable<ChinaSupplier>()
            .InnerJoin<DomesticProduct>(
                (supplier, domesticProduct) =>
                    supplier.SupplierCode == domesticProduct.SupplierCode
                    && !domesticProduct.IsDeleted
            )
            .WhereIF(
                isSqlServer,
                (supplier, domesticProduct) =>
                    !supplier.IsDeleted
                    && supplier.SupplierCode != null
                    && domesticProduct.ProductCode != null
                    && (
                        supplier.SupplierCode == SqlFunc.ToVarchar(keyword)
                        || supplier.SupplierCode.StartsWith(SqlFunc.ToVarchar(keyword))
                    )
            )
            .WhereIF(
                !isSqlServer,
                (supplier, domesticProduct) =>
                    !supplier.IsDeleted
                    && supplier.SupplierCode != null
                    && domesticProduct.ProductCode != null
                    && (
                        supplier.SupplierCode == keyword
                        || supplier.SupplierCode.StartsWith(keyword)
                    )
            )
            .Select(
                (supplier, domesticProduct) =>
                    new ProductWarehouseTableCodeSearchCandidate
                    {
                        ProductCode = domesticProduct.ProductCode!,
                    }
            );

        var pickingLocationCodeSource = _context
            .Db.Queryable<Location>()
            .InnerJoin<ProductLocation>(
                (location, productLocation) =>
                    location.LocationGuid == productLocation.LocationGuid
                    && !productLocation.IsDeleted
            )
            .WhereIF(
                isSqlServer,
                (location, productLocation) =>
                    !location.IsDeleted
                    && location.LocationType == PickingLocationType
                    && location.LocationCode != null
                    && productLocation.ProductCode != null
                    && (
                        location.LocationCode == SqlFunc.ToVarchar(keyword)
                        || location.LocationCode.StartsWith(SqlFunc.ToVarchar(keyword))
                    )
            )
            .WhereIF(
                !isSqlServer,
                (location, productLocation) =>
                    !location.IsDeleted
                    && location.LocationType == PickingLocationType
                    && location.LocationCode != null
                    && productLocation.ProductCode != null
                    && (
                        location.LocationCode == keyword
                        || location.LocationCode.StartsWith(keyword)
                    )
            );
        var pickingLocationCodeQuery = pickingLocationCodeSource
            .Select(
                (location, productLocation) =>
                    new ProductWarehouseTableCodeSearchCandidate
                    {
                        ProductCode = productLocation.ProductCode!,
                    }
            );

        var pickingLocationBarcodeSource = _context
            .Db.Queryable<Location>()
            .InnerJoin<ProductLocation>(
                (location, productLocation) =>
                    location.LocationGuid == productLocation.LocationGuid
                    && !productLocation.IsDeleted
            )
            .WhereIF(
                isSqlServer,
                (location, productLocation) =>
                    !location.IsDeleted
                    && location.LocationType == PickingLocationType
                    && location.LocationBarcode != null
                    && productLocation.ProductCode != null
                    && (
                        location.LocationBarcode == SqlFunc.ToVarchar(keyword)
                        || location.LocationBarcode.StartsWith(SqlFunc.ToVarchar(keyword))
                    )
            )
            .WhereIF(
                !isSqlServer,
                (location, productLocation) =>
                    !location.IsDeleted
                    && location.LocationType == PickingLocationType
                    && location.LocationBarcode != null
                    && productLocation.ProductCode != null
                    && (
                        location.LocationBarcode == keyword
                        || location.LocationBarcode.StartsWith(keyword)
                    )
            );
        var pickingLocationBarcodeQuery = pickingLocationBarcodeSource
            .Select(
                (location, productLocation) =>
                    new ProductWarehouseTableCodeSearchCandidate
                    {
                        ProductCode = productLocation.ProductCode!,
                    }
            );

        var unionQuery = _context
            .Db.Union(
                warehouseProductCodeQuery,
                itemNumberQuery,
                hbItemNumberQuery,
                barcodeQuery,
                localSupplierCodeQuery,
                domesticSupplierCodeQuery,
                pickingLocationCodeQuery,
                pickingLocationBarcodeQuery
            )
            .MergeTable();

        // UNION 受 nvarchar 商品列影响会提升结果类型；SQL Server 最外层统一转回 varchar，
        // 让候选集与 WarehouseProduct.ProductCode 连接时不再转换仓库索引列。
        return isSqlServer
            ? unionQuery
                .Select(candidate => new ProductWarehouseTableCodeSearchCandidate
                {
                    ProductCode = SqlFunc.ToVarchar(candidate.ProductCode),
                })
                .MergeTable()
            : unionQuery;
    }
}
