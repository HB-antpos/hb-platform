using System.Data;
using System.Linq;
using System.Text.Json;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.HqEntities;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BlazorApp.Api.Features.LocalSupplierInvoices
{
    internal sealed class LocalSupplierInvoicesQueriesHandler
    {
        private readonly LocalSupplierInvoicesDependencies _dependencies;
        private SqlSugarContext _context => _dependencies.Context;
        private HqSqlSugarContext _hqContext => _dependencies.HqContext;
        private IMapper _mapper => _dependencies.Mapper;
        private ILogger _logger => _dependencies.Logger;
        private IAutoPricingService _autoPricingService => _dependencies.AutoPricingService;
        private IWarehouseProductChangeHistoryService _changeHistoryService => _dependencies.ChangeHistoryService;
        private ILocalSupplierInvoiceHqProductSyncService? _hqProductSyncService => _dependencies.HqProductSyncService;

        public LocalSupplierInvoicesQueriesHandler(LocalSupplierInvoicesDependencies dependencies)
        {
            _dependencies = dependencies;
        }

        public async Task<GridResponseDto<LocalSupplierInvoiceListDto>> GetGridDataAsync(
            GridRequestDto request
        ) => await GetGridDataAsync(request, null);

        public async Task<GridResponseDto<LocalSupplierInvoiceListDto>> GetGridDataAsync(
            GridRequestDto request,
            List<string>? allowedStoreCodes
        )
        {
            try
            {
                var db = _context.Db;
                var query = db.Queryable<StoreLocalSupplierInvoice>()
                    .LeftJoin<Store>((h, st) => h.StoreCode == st.StoreCode)
                    .LeftJoin<HBLocalSupplier>(
                        (h, st, sup) => h.SupplierCode == sup.LocalSupplierCode
                    )
                    .Where((h, st, sup) => h.IsDeleted == false);

                if (allowedStoreCodes != null)
                {
                    if (!allowedStoreCodes.Any())
                    {
                        query = query.Where((h, st, sup) => false);
                    }
                    else
                    {
                        query = query.Where((h, st, sup) =>
                            h.StoreCode != null && allowedStoreCodes.Contains(h.StoreCode)
                        );
                    }
                }

                string? productKeyword = null;
                string? selectedStoreCode = null;
                if (request.FilterModel != null && request.FilterModel.Any())
                {
                    foreach (var kv in request.FilterModel)
                    {
                        var col = NormalizeGridColumnId(kv.Key);
                        var f = kv.Value;
                        if (f == null || f.FilterType == null)
                            continue;
                        var type = f.FilterType.ToLower();

                        if (col == "ProductKeyword" && f.Filter != null)
                        {
                            productKeyword = f.Filter?.ToString()?.Trim();
                            continue;
                        }

                        if (col == "IsProductChecked")
                        {
                            // 口径与列表展示一致：至少一条有效明细，且有效明细的 ExistingProductCount 全部非空才算已检测。
                            // 无法解析的值直接忽略，不缩小结果。
                            if (bool.TryParse(f.Filter?.Trim(), out var productChecked))
                            {
                                query = productChecked
                                    ? query.Where((h, st, sup) =>
                                        SqlFunc.Subqueryable<StoreLocalSupplierInvoiceDetails>()
                                            .Where(d => d.IsDeleted == false && d.InvoiceGUID == h.InvoiceGUID)
                                            .Any()
                                        && SqlFunc.Subqueryable<StoreLocalSupplierInvoiceDetails>()
                                            .Where(d =>
                                                d.IsDeleted == false
                                                && d.InvoiceGUID == h.InvoiceGUID
                                                && d.ExistingProductCount == null
                                            )
                                            .NotAny()
                                    )
                                    : query.Where((h, st, sup) =>
                                        SqlFunc.Subqueryable<StoreLocalSupplierInvoiceDetails>()
                                            .Where(d => d.IsDeleted == false && d.InvoiceGUID == h.InvoiceGUID)
                                            .NotAny()
                                        || SqlFunc.Subqueryable<StoreLocalSupplierInvoiceDetails>()
                                            .Where(d =>
                                                d.IsDeleted == false
                                                && d.InvoiceGUID == h.InvoiceGUID
                                                && d.ExistingProductCount == null
                                            )
                                            .Any()
                                    );
                            }
                            continue;
                        }

                        if (type == "text" && f.Filter != null)
                        {
                            var v = f.Filter?.ToString()?.Trim();
                            if (string.IsNullOrEmpty(v))
                                continue;
                            var op = (f.Type ?? "contains").ToLower();
                            switch (col)
                            {
                                case "StoreCode":
                                    query = ApplyText(query, op, v, x => x.StoreCode);
                                    if (op == "equals")
                                    {
                                        selectedStoreCode = v;
                                    }
                                    break;
                                case "SupplierCode":
                                    query = ApplyText(query, op, v, x => x.SupplierCode);
                                    break;
                                case "InvoiceNo":
                                    query = ApplyText(query, op, v, x => x.InvoiceNo);
                                    break;
                                case "StoreName":
                                    query = query.Where((h, st, sup) => st.StoreName.Contains(v));
                                    break;
                                case "SupplierName":
                                    query = query.Where((h, st, sup) => sup.Name.Contains(v));
                                    break;
                                case "Remarks":
                                    query = ApplyText(query, op, v, x => x.Remarks);
                                    break;
                                case "CreatedBy":
                                    query = ApplyText(query, op, v, x => x.CreatedBy);
                                    break;
                            }
                        }
                        else if (type == "number" && f.Filter != null)
                        {
                            if (decimal.TryParse(f.Filter.ToString(), out var numValue))
                            {
                                var op = (f.Type ?? "equals").ToLower();
                                switch (col)
                                {
                                    case "TotalAmount":
                                        query = ApplyNumber(
                                            query,
                                            op,
                                            x => x.TotalAmount,
                                            numValue,
                                            f.FilterTo
                                        );
                                        break;
                                    case "ReceivedTotalAmount":
                                        query = ApplyNumber(
                                            query,
                                            op,
                                            x => x.ReceivedTotalAmount,
                                            numValue,
                                            f.FilterTo
                                        );
                                        break;
                                }
                            }
                        }
                        else if (type == "date" && f.Filter != null)
                        {
                            var op = (f.Type ?? "equals").ToLower();
                            switch (col)
                            {
                                case "OrderDate":
                                    query = ApplyDate(query, op, f.Filter, f.FilterTo, x => x.OrderDate);
                                    break;
                                case "InboundDate":
                                    query = ApplyDate(
                                        query,
                                        op,
                                        f.Filter,
                                        f.FilterTo,
                                        x => x.InboundDate
                                    );
                                    break;
                            }
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(productKeyword))
                {
                    var keyword = productKeyword;
                    var allowedProductStoreCodes = allowedStoreCodes?
                        .Select(code => code?.Trim())
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Select(code => code!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (!string.IsNullOrWhiteSpace(selectedStoreCode))
                    {
                        query = query.Where((h, st, sup) =>
                            SqlFunc.Subqueryable<StoreLocalSupplierInvoiceDetails>()
                                .Where(d =>
                                    d.IsDeleted == false
                                    && d.InvoiceGUID == h.InvoiceGUID
                                    && (
                                        d.StoreCode == null
                                        || (
                                            d.StoreCode == selectedStoreCode
                                            && d.StoreCode == h.StoreCode
                                        )
                                    )
                                    && (
                                        (d.ItemNumber != null && d.ItemNumber.Contains(keyword))
                                        || (d.Barcode != null && d.Barcode.Contains(keyword))
                                        || (
                                            d.StoreProductCode != null
                                            && d.StoreProductCode.Contains(keyword)
                                        )
                                        || (
                                            d.ProductName != null
                                            && d.ProductName.Contains(keyword)
                                        )
                                    )
                                )
                                .Any()
                        );
                    }
                    else if (allowedProductStoreCodes != null && allowedProductStoreCodes.Any())
                    {
                        query = query.Where((h, st, sup) =>
                            SqlFunc.Subqueryable<StoreLocalSupplierInvoiceDetails>()
                                .Where(d =>
                                    d.IsDeleted == false
                                    && d.InvoiceGUID == h.InvoiceGUID
                                    && (
                                        d.StoreCode == null
                                        || (
                                            allowedProductStoreCodes.Contains(d.StoreCode)
                                            && d.StoreCode == h.StoreCode
                                        )
                                    )
                                    && (
                                        (d.ItemNumber != null && d.ItemNumber.Contains(keyword))
                                        || (d.Barcode != null && d.Barcode.Contains(keyword))
                                        || (
                                            d.StoreProductCode != null
                                            && d.StoreProductCode.Contains(keyword)
                                        )
                                        || (
                                            d.ProductName != null
                                            && d.ProductName.Contains(keyword)
                                        )
                                    )
                                )
                                .Any()
                        );
                    }
                    else if (allowedProductStoreCodes != null)
                    {
                        query = query.Where((h, st, sup) => false);
                    }
                    else
                    {
                        query = query.Where((h, st, sup) =>
                            SqlFunc.Subqueryable<StoreLocalSupplierInvoiceDetails>()
                                .Where(d =>
                                    d.IsDeleted == false
                                    && d.InvoiceGUID == h.InvoiceGUID
                                    && (d.StoreCode == null || d.StoreCode == h.StoreCode)
                                    && (
                                        (d.ItemNumber != null && d.ItemNumber.Contains(keyword))
                                        || (d.Barcode != null && d.Barcode.Contains(keyword))
                                        || (
                                            d.StoreProductCode != null
                                            && d.StoreProductCode.Contains(keyword)
                                        )
                                        || (
                                            d.ProductName != null
                                            && d.ProductName.Contains(keyword)
                                        )
                                    )
                                )
                                .Any()
                        );
                    }
                }

                if (request.SortModel != null && request.SortModel.Any())
                {
                    var s = request.SortModel.First();
                    var asc = s.Sort?.ToLower() == "asc";
                    query = NormalizeGridColumnId(s.ColId) switch
                    {
                        "StoreName" => query.OrderBy(
                            (h, st, sup) => st.StoreName,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "SupplierName" => query.OrderBy(
                            (h, st, sup) => sup.Name,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "InvoiceNo" => query.OrderBy(
                            (h, st, sup) => h.InvoiceNo,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "OrderDate" => query.OrderBy(
                            (h, st, sup) => h.OrderDate,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "InboundDate" => query.OrderBy(
                            (h, st, sup) => h.InboundDate,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "TotalAmount" => query.OrderBy(
                            (h, st, sup) => h.TotalAmount,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "ReceivedTotalAmount" => query.OrderBy(
                            (h, st, sup) => h.ReceivedTotalAmount,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "CreatedAt" => query.OrderBy(
                            (h, st, sup) => h.CreatedAt,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        "UpdatedAt" => query.OrderBy(
                            (h, st, sup) => h.UpdatedAt,
                            asc ? OrderByType.Asc : OrderByType.Desc
                        ),
                        _ => query.OrderBy((h, st, sup) => h.OrderDate, OrderByType.Desc),
                    };
                }
                else
                {
                    query = query.OrderBy((h, st, sup) => h.OrderDate, OrderByType.Desc);
                }

                var total = await query.CountAsync();
                var pageSize = ClampGridPageSize(request.PageSize, 20, 20, 50, 100);
                var startRow = request.StartRow >= 0 ? request.StartRow : 0;
                var list = await query
                    .Select(
                        (h, st, sup) =>
                            new LocalSupplierInvoiceListDto
                            {
                                InvoiceGUID = h.InvoiceGUID,
                                StoreCode = h.StoreCode,
                                StoreName = st.StoreName,
                                SupplierCode = h.SupplierCode,
                                SupplierName = sup.Name,
                                InvoiceNo = h.InvoiceNo,
                                VoucherType = h.VoucherType,
                                OrderDate = h.OrderDate,
                                InboundDate = h.InboundDate,
                                TotalAmount = h.TotalAmount,
                                ReceivedTotalAmount = h.ReceivedTotalAmount,
                                FlowStatus = h.FlowStatus,
                                InboundStatus = h.InboundStatus,
                                Remarks = h.Remarks,
                                CreatedAt = h.CreatedAt,
                                CreatedBy = h.CreatedBy,
                                UpdatedAt = h.UpdatedAt,
                                UpdatedBy = h.UpdatedBy,
                            }
                    )
                    .Skip(startRow)
                    .Take(pageSize)
                    .ToListAsync();

                var invoiceGuids = list
                    .Select(item => item.InvoiceGUID)
                    .Where(guid => !string.IsNullOrWhiteSpace(guid))
                    .Distinct()
                    .ToList();
                if (invoiceGuids.Any())
                {
                    // 只统计当前页主表对应的明细，避免额外查询影响主表筛选和分页结果；上次价为空或 0 不参与涨跌价统计。
                    var priceChangedDetails = await db.Queryable<StoreLocalSupplierInvoiceDetails>()
                        .Where(d =>
                            d.IsDeleted == false
                            && d.InvoiceGUID != null
                            && invoiceGuids.Contains(d.InvoiceGUID)
                            && d.LastPurchasePrice > 0
                            && d.PurchasePrice.HasValue
                            && d.PurchasePrice != d.LastPurchasePrice
                        )
                        .Select(d => new
                        {
                            d.InvoiceGUID,
                            IsIncrease = d.PurchasePrice > d.LastPurchasePrice,
                        })
                        .ToListAsync();
                    var priceChangeCountsByInvoice = priceChangedDetails
                        .Where(item => !string.IsNullOrWhiteSpace(item.InvoiceGUID))
                        .GroupBy(item => item.InvoiceGUID!)
                        .ToDictionary(
                            group => group.Key,
                            group => new
                            {
                                Increase = group.Count(item => item.IsIncrease),
                                Decrease = group.Count(item => !item.IsIncrease),
                            }
                        );

                    // 仅按当前页批量聚合有效明细；至少一条明细且所有 ExistingProductCount 非空才算已检测。
                    // 0 是合法的“已检测但未匹配”，因此这里只统计 NULL，不把 0 当作未检测。
                    var productCheckAggregates = await db.Queryable<StoreLocalSupplierInvoiceDetails>()
                        .Where(d =>
                            d.IsDeleted == false
                            && d.InvoiceGUID != null
                            && invoiceGuids.Contains(d.InvoiceGUID)
                        )
                        .GroupBy(d => d.InvoiceGUID)
                        .Select(d => new
                        {
                            InvoiceGUID = d.InvoiceGUID,
                            DetailCount = SqlFunc.AggregateCount(d.DetailGUID),
                            NullProductCount = SqlFunc.AggregateSum(
                                SqlFunc.IIF(d.ExistingProductCount == null, 1, 0)
                            ),
                        })
                        .ToListAsync();
                    var checkedInvoices = productCheckAggregates
                        .Where(item =>
                            !string.IsNullOrWhiteSpace(item.InvoiceGUID)
                            && item.DetailCount > 0
                            && item.NullProductCount == 0
                        )
                        .Select(item => item.InvoiceGUID!)
                        .ToHashSet(StringComparer.Ordinal);

                    foreach (var item in list)
                    {
                        if (priceChangeCountsByInvoice.TryGetValue(item.InvoiceGUID, out var counts))
                        {
                            item.PriceIncreaseItemCount = counts.Increase;
                            item.PriceDecreaseItemCount = counts.Decrease;
                        }
                        item.IsProductChecked = checkedInvoices.Contains(item.InvoiceGUID);
                    }
                }

                return GridResponseDto<LocalSupplierInvoiceListDto>.OK(list, total);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LocalSupplierInvoice Grid 查询失败");
                return GridResponseDto<LocalSupplierInvoiceListDto>.Error("查询失败");
            }
        }

        public async Task<ApiResponse<LocalSupplierInvoiceFilterOptionsDto>> GetFilterOptionsAsync(
            List<string>? allowedStoreCodes,
            string? storeCode
        )
        {
            try
            {
                var normalizedStoreCode = storeCode?.Trim();
                if (string.IsNullOrWhiteSpace(normalizedStoreCode))
                {
                    normalizedStoreCode = null;
                }

                var normalizedAllowedStoreCodes = allowedStoreCodes?
                    .Select(code => code?.Trim())
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var response = new LocalSupplierInvoiceFilterOptionsDto();
                if (
                    normalizedStoreCode != null
                    && normalizedAllowedStoreCodes != null
                    && !normalizedAllowedStoreCodes.Contains(
                        normalizedStoreCode,
                        StringComparer.OrdinalIgnoreCase
                    )
                )
                {
                    // service 层再次做 scope 交集，避免未来复用时绕过 controller 的分店校验。
                    return ApiResponse<LocalSupplierInvoiceFilterOptionsDto>.OK(response);
                }

                var query = _context.Db.Queryable<StoreLocalSupplierInvoice>()
                    .LeftJoin<HBLocalSupplier>(
                        (invoice, supplier) =>
                            invoice.SupplierCode == supplier.LocalSupplierCode
                            && supplier.IsDeleted == false
                    )
                    .Where((invoice, supplier) =>
                        invoice.IsDeleted == false
                        && invoice.SupplierCode != null
                        && invoice.SupplierCode != ""
                    );

                if (normalizedStoreCode != null)
                {
                    query = query.Where((invoice, supplier) =>
                        invoice.StoreCode == normalizedStoreCode
                    );
                }
                else if (normalizedAllowedStoreCodes != null)
                {
                    query = normalizedAllowedStoreCodes.Any()
                        ? query.Where((invoice, supplier) =>
                            invoice.StoreCode != null
                            && normalizedAllowedStoreCodes.Contains(invoice.StoreCode)
                        )
                        : query.Where((invoice, supplier) => false);
                }

                var rows = await query
                    .Select(
                        (invoice, supplier) =>
                            new LocalSupplierInvoiceFilterOptionDto
                            {
                                Value = invoice.SupplierCode!,
                                Label = supplier.Name,
                            }
                    )
                    .Distinct()
                    .ToListAsync();

                response.Suppliers = rows
                    .Select(option =>
                    {
                        var value = option.Value.Trim();
                        var label = string.IsNullOrWhiteSpace(option.Label)
                            ? value
                            : option.Label.Trim();
                        return new LocalSupplierInvoiceFilterOptionDto
                        {
                            Value = value,
                            Label = label,
                        };
                    })
                    .Where(option => !string.IsNullOrWhiteSpace(option.Value))
                    .GroupBy(option => option.Value, StringComparer.OrdinalIgnoreCase)
                    .Select(group =>
                        group
                            .OrderBy(option => option.Label, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(option => option.Label, StringComparer.Ordinal)
                            .ThenBy(option => option.Value, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(option => option.Value, StringComparer.Ordinal)
                            .First()
                    )
                    .OrderBy(option => option.Label, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(option => option.Label, StringComparer.Ordinal)
                    .ThenBy(option => option.Value, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(option => option.Value, StringComparer.Ordinal)
                    .ToList();

                return ApiResponse<LocalSupplierInvoiceFilterOptionsDto>.OK(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "商城本地进货单供应商筛选项查询失败");
                return ApiResponse<LocalSupplierInvoiceFilterOptionsDto>.Error(
                    "供应商筛选项查询失败",
                    "QUERY_ERROR"
                );
            }
        }

        public async Task<ApiResponse<LocalSupplierInvoiceDetailDto>> GetInvoiceAsync(
            string invoiceGuid
        )
        {
            try
            {
                var db = _context.Db;
                var item = await db.Queryable<StoreLocalSupplierInvoice>()
                    .LeftJoin<Store>((h, st) => h.StoreCode == st.StoreCode)
                    .LeftJoin<HBLocalSupplier>(
                        (h, st, sup) => h.SupplierCode == sup.LocalSupplierCode
                    )
                    .Where((h, st, sup) => h.InvoiceGUID == invoiceGuid && h.IsDeleted == false)
                    .Select(
                        (h, st, sup) =>
                            new LocalSupplierInvoiceDetailDto
                            {
                                InvoiceGUID = h.InvoiceGUID,
                                AppGUID = h.AppGUID,
                                PcGUID = h.PcGUID,
                                StoreCode = h.StoreCode,
                                StoreName = st.StoreName,
                                SupplierCode = h.SupplierCode,
                                SupplierName = sup.Name,
                                InvoiceNo = h.InvoiceNo,
                                VoucherType = h.VoucherType,
                                OrderDate = h.OrderDate,
                                InboundDate = h.InboundDate,
                                TotalAmount = h.TotalAmount,
                                ReceivedTotalAmount = h.ReceivedTotalAmount,
                                VoucherImage = h.VoucherImage,
                                Remarks = h.Remarks,
                                ImportTemplate = h.ImportTemplate,
                                FlowStatus = h.FlowStatus,
                                InboundStatus = h.InboundStatus,
                                CreatedAt = h.CreatedAt,
                                UpdatedAt = h.UpdatedAt,
                            }
                    )
                    .FirstAsync();

                if (item == null)
                    return ApiResponse<LocalSupplierInvoiceDetailDto>.Error(
                        "数据不存在",
                        "NOT_FOUND"
                    );
                return ApiResponse<LocalSupplierInvoiceDetailDto>.OK(item);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取进货单详情失败");
                return ApiResponse<LocalSupplierInvoiceDetailDto>.Error("获取失败", "GET_ERROR");
            }
        }

        public async Task<ApiResponse<List<LocalSupplierInvoiceItemDto>>> GetDetailsAsync(
            string invoiceGuid
        )
        {
            try
            {
                var db = _context.Db;
                var list = await db.Queryable<StoreLocalSupplierInvoiceDetails>()
                    .LeftJoin<Product>((d, p) => d.ProductCode == p.ProductCode)
                    .Where((d, p) => d.InvoiceGUID == invoiceGuid && d.IsDeleted == false)
                    .Select(
                        (d, p) =>
                            new LocalSupplierInvoiceItemDto
                            {
                                DetailGUID = d.DetailGUID,
                                InvoiceGUID = d.InvoiceGUID,
                                StoreCode = d.StoreCode,
                                SupplierCode = d.SupplierCode,
                                ProductTagGUID = d.ProductTagGUID,
                                ProductCategoryGUID = d.ProductCategoryGUID,
                                StoreProductCode = d.StoreProductCode,
                                ProductCode = d.ProductCode,
                                ItemNumber = d.ItemNumber,
                                Barcode = d.Barcode,
                                AdditionalBarcodesJson = d.AdditionalBarcodesJson,
                                ProductName = d.ProductName,
                                Specification = d.Specification,
                                Unit = d.Unit,
                                Quantity = d.Quantity,
                                LastPurchasePrice = d.LastPurchasePrice,
                                PurchasePrice = d.PurchasePrice,
                                RetailPrice = d.RetailPrice,
                                Amount = d.Amount,
                                ExistingProductCount = d.ExistingProductCount,
                                BarcodeStatus = d.BarcodeStatus,
                                BarcodeMatchCount = d.BarcodeMatchCount,
                                ProductImage = p.ProductImage,
                                // 类型实时读取商品主档；保留历史明细，但不展示已删除主档的类型。
                                ProductType = p.IsDeleted == false ? p.ProductType : null,
                                ActivityType = d.ActivityType,
                                DiscountRate = d.DiscountRate,
                                AutoPricing = d.AutoPricing,
                                PricingFloatRate = d.PricingFloatRate,
                                NewAutoRetailPrice = d.NewAutoRetailPrice,
                                IsSpecialProduct = d.IsSpecialProduct,
                                OldStoreProductCode = d.OldStoreProductCode,
                            }
                    )
                    .ToListAsync();

                LocalSupplierInvoicesBarcodeRules.PopulateAdditionalBarcodes(list);
                return ApiResponse<List<LocalSupplierInvoiceItemDto>>.OK(list);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取进货单明细失败");
                return ApiResponse<List<LocalSupplierInvoiceItemDto>>.Error(
                    "获取失败",
                    "GET_ERROR"
                );
            }
        }

        public async Task<GridResponseDto<LocalSupplierInvoiceItemDto>> GetDetailsGridAsync(
            string invoiceGuid,
            GridRequestDto request
        )
        {
            try
            {
                var db = _context.Db;
                var query = db.Queryable<StoreLocalSupplierInvoiceDetails>()
                    .LeftJoin<Product>((d, p) => d.ProductCode == p.ProductCode)
                    .Where((d, p) => d.InvoiceGUID == invoiceGuid && d.IsDeleted == false)
                    .OrderBy((d, p) => d.CreatedAt, OrderByType.Desc)
                    .OrderBy((d, p) => d.DetailGUID, OrderByType.Asc);

                var priceChangeFilter = ResolveDetailPriceChangeFilter(request.FilterModel);
                if (priceChangeFilter == "up")
                {
                    // 明细涨价筛选必须排除上次价为空或 0 的行，避免把新商品误算为涨价。
                    query = query.Where((d, p) =>
                        d.LastPurchasePrice > 0
                        && d.PurchasePrice.HasValue
                        && d.PurchasePrice > d.LastPurchasePrice
                    );
                }
                else if (priceChangeFilter == "down")
                {
                    // 明细减价筛选同样只统计有有效上次价的行，保持和涨价口径一致。
                    query = query.Where((d, p) =>
                        d.LastPurchasePrice > 0
                        && d.PurchasePrice.HasValue
                        && d.PurchasePrice < d.LastPurchasePrice
                    );
                }

                var total = await query.CountAsync();
                var pageSize = ClampGridPageSize(request.PageSize, 50, 50, 100, 200);
                var startRow = request.StartRow >= 0 ? request.StartRow : 0;

                var list = await query
                    .Select(
                        (d, p) =>
                            new LocalSupplierInvoiceItemDto
                            {
                                DetailGUID = d.DetailGUID,
                                InvoiceGUID = d.InvoiceGUID,
                                StoreCode = d.StoreCode,
                                SupplierCode = d.SupplierCode,
                                ProductTagGUID = d.ProductTagGUID,
                                ProductCategoryGUID = d.ProductCategoryGUID,
                                StoreProductCode = d.StoreProductCode,
                                ProductCode = d.ProductCode,
                                ItemNumber = d.ItemNumber,
                                Barcode = d.Barcode,
                                AdditionalBarcodesJson = d.AdditionalBarcodesJson,
                                ProductName = d.ProductName,
                                Specification = d.Specification,
                                Unit = d.Unit,
                                Quantity = d.Quantity,
                                LastPurchasePrice = d.LastPurchasePrice,
                                PurchasePrice = d.PurchasePrice,
                                RetailPrice = d.RetailPrice,
                                Amount = d.Amount,
                                ExistingProductCount = d.ExistingProductCount,
                                BarcodeStatus = d.BarcodeStatus,
                                BarcodeMatchCount = d.BarcodeMatchCount,
                                ProductImage = p.ProductImage,
                                // 与非分页明细保持一致，不从副码或待执行操作推断商品类型。
                                ProductType = p.IsDeleted == false ? p.ProductType : null,
                                ActivityType = d.ActivityType,
                                DiscountRate = d.DiscountRate,
                                AutoPricing = d.AutoPricing,
                                PricingFloatRate = d.PricingFloatRate,
                                NewAutoRetailPrice = d.NewAutoRetailPrice,
                                IsSpecialProduct = d.IsSpecialProduct,
                                OldStoreProductCode = d.OldStoreProductCode,
                            }
                    )
                    .Skip(startRow)
                    .Take(pageSize)
                    .ToListAsync();

                LocalSupplierInvoicesBarcodeRules.PopulateAdditionalBarcodes(list);
                return GridResponseDto<LocalSupplierInvoiceItemDto>.OK(list, total);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取进货单分页明细失败");
                return GridResponseDto<LocalSupplierInvoiceItemDto>.Error("获取失败");
            }
        }


        private ISugarQueryable<StoreLocalSupplierInvoice, Store, HBLocalSupplier> ApplyText(
            ISugarQueryable<StoreLocalSupplierInvoice, Store, HBLocalSupplier> query,
            string operation,
            string value,
            System.Linq.Expressions.Expression<System.Func<
                StoreLocalSupplierInvoice,
                string?
            >> selector
        )
        {
            var oldParam = selector.Parameters[0];
            var newParam = System.Linq.Expressions.Expression.Parameter(
                typeof(StoreLocalSupplierInvoice),
                "h"
            );
            var member = new ParamReplaceVisitor(oldParam, newParam).Visit(selector.Body);
            return operation switch
            {
                "equals" => query.Where(
                    System.Linq.Expressions.Expression.Lambda<System.Func<
                        StoreLocalSupplierInvoice,
                        bool
                    >>(
                        System.Linq.Expressions.Expression.Equal(
                            member,
                            System.Linq.Expressions.Expression.Constant(value, typeof(string))
                        ),
                        newParam
                    )
                ),
                "notequal" => query.Where(
                    System.Linq.Expressions.Expression.Lambda<System.Func<
                        StoreLocalSupplierInvoice,
                        bool
                    >>(
                        System.Linq.Expressions.Expression.NotEqual(
                            member,
                            System.Linq.Expressions.Expression.Constant(value, typeof(string))
                        ),
                        newParam
                    )
                ),
                "contains" => query.Where(
                    System.Linq.Expressions.Expression.Lambda<System.Func<
                        StoreLocalSupplierInvoice,
                        bool
                    >>(
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
                    System.Linq.Expressions.Expression.Lambda<System.Func<
                        StoreLocalSupplierInvoice,
                        bool
                    >>(
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
                    System.Linq.Expressions.Expression.Lambda<System.Func<
                        StoreLocalSupplierInvoice,
                        bool
                    >>(
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
                    System.Linq.Expressions.Expression.Lambda<System.Func<
                        StoreLocalSupplierInvoice,
                        bool
                    >>(
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
                _ => query,
            };
        }

        private ISugarQueryable<StoreLocalSupplierInvoice, Store, HBLocalSupplier> ApplyNumber(
            ISugarQueryable<StoreLocalSupplierInvoice, Store, HBLocalSupplier> query,
            string? operation,
            System.Linq.Expressions.Expression<System.Func<
                StoreLocalSupplierInvoice,
                decimal?
            >> selector,
            decimal value,
            object? filterTo
        )
        {
            var oldParam = selector.Parameters[0];
            var newParam = System.Linq.Expressions.Expression.Parameter(
                typeof(StoreLocalSupplierInvoice),
                "h"
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
                _ => null,
            };

            if (condition != null)
            {
                var lambda = System.Linq.Expressions.Expression.Lambda<System.Func<
                    StoreLocalSupplierInvoice,
                    bool
                >>(condition, newParam);
                query = query.Where(lambda);
            }

            if (filterTo != null && operation == "inrange")
            {
                var constantValueTo = System.Linq.Expressions.Expression.Convert(
                    System.Linq.Expressions.Expression.Constant(System.Convert.ToDecimal(filterTo)),
                    typeof(decimal?)
                );
                var conditionTo = System.Linq.Expressions.Expression.LessThanOrEqual(
                    member,
                    constantValueTo
                );
                var lambdaTo = System.Linq.Expressions.Expression.Lambda<System.Func<
                    StoreLocalSupplierInvoice,
                    bool
                >>(conditionTo, newParam);
                query = query.Where(lambdaTo);
            }

            return query;
        }

        private ISugarQueryable<StoreLocalSupplierInvoice, Store, HBLocalSupplier> ApplyDate(
            ISugarQueryable<StoreLocalSupplierInvoice, Store, HBLocalSupplier> query,
            string? operation,
            string? filter,
            string? filterTo,
            System.Linq.Expressions.Expression<System.Func<
                StoreLocalSupplierInvoice,
                DateTime?
            >> selector
        )
        {
            if (!TryParseGridDate(filter, out var value))
            {
                return query;
            }

            var oldParam = selector.Parameters[0];
            var newParam = System.Linq.Expressions.Expression.Parameter(
                typeof(StoreLocalSupplierInvoice),
                "h"
            );
            var member = new ParamReplaceVisitor(oldParam, newParam).Visit(selector.Body);
            var startValue = System.Linq.Expressions.Expression.Convert(
                System.Linq.Expressions.Expression.Constant(value.Date),
                typeof(DateTime?)
            );
            var endValue = System.Linq.Expressions.Expression.Convert(
                System.Linq.Expressions.Expression.Constant(ToInclusiveEndOfDay(value)),
                typeof(DateTime?)
            );

            System.Linq.Expressions.Expression? condition = operation switch
            {
                "equals" => System.Linq.Expressions.Expression.AndAlso(
                    System.Linq.Expressions.Expression.GreaterThanOrEqual(member, startValue),
                    System.Linq.Expressions.Expression.LessThanOrEqual(member, endValue)
                ),
                "notequal" => System.Linq.Expressions.Expression.OrElse(
                    System.Linq.Expressions.Expression.LessThan(member, startValue),
                    System.Linq.Expressions.Expression.GreaterThan(member, endValue)
                ),
                "lessthan" => System.Linq.Expressions.Expression.LessThan(member, startValue),
                "lessthanorequal" => System.Linq.Expressions.Expression.LessThanOrEqual(
                    member,
                    endValue
                ),
                "greaterthan" => System.Linq.Expressions.Expression.GreaterThan(member, endValue),
                "greaterthanorequal" => System.Linq.Expressions.Expression.GreaterThanOrEqual(
                    member,
                    startValue
                ),
                "inrange" when TryParseGridDate(filterTo, out var toValue) =>
                    System.Linq.Expressions.Expression.AndAlso(
                        System.Linq.Expressions.Expression.GreaterThanOrEqual(member, startValue),
                        System.Linq.Expressions.Expression.LessThanOrEqual(
                            member,
                            System.Linq.Expressions.Expression.Convert(
                                System.Linq.Expressions.Expression.Constant(
                                    ToInclusiveEndOfDay(toValue)
                                ),
                                typeof(DateTime?)
                            )
                        )
                    ),
                _ => null,
            };

            if (condition == null)
            {
                return query;
            }

            var lambda = System.Linq.Expressions.Expression.Lambda<System.Func<
                StoreLocalSupplierInvoice,
                bool
            >>(condition, newParam);
            return query.Where(lambda);
        }

        private static int ClampGridPageSize(int requested, int fallback, params int[] allowed)
        {
            return allowed.Contains(requested) ? requested : fallback;
        }

        private static string? ResolveDetailPriceChangeFilter(
            Dictionary<string, FilterModelDto>? filterModel
        )
        {
            if (filterModel == null || filterModel.Count == 0)
                return null;

            foreach (var kv in filterModel)
            {
                if (NormalizeGridColumnId(kv.Key) != "PriceChange")
                    continue;

                var value = kv.Value.Filter;
                if (string.IsNullOrWhiteSpace(value) && kv.Value.Values?.Count > 0)
                    value = kv.Value.Values[0];

                var normalized = value?.Trim().ToLowerInvariant();
                if (normalized is "up" or "down")
                    return normalized;
            }

            return null;
        }

        private static string NormalizeGridColumnId(string? columnId)
        {
            return columnId?.Trim().ToLowerInvariant() switch
            {
                "storecode" => "StoreCode",
                "suppliercode" => "SupplierCode",
                "invoiceno" => "InvoiceNo",
                "storename" => "StoreName",
                "suppliername" => "SupplierName",
                "remarks" => "Remarks",
                "createdby" => "CreatedBy",
                "productkeyword" => "ProductKeyword",
                "isproductchecked" => "IsProductChecked",
                "totalamount" => "TotalAmount",
                "receivedtotalamount" => "ReceivedTotalAmount",
                "pricechange" => "PriceChange",
                "orderdate" => "OrderDate",
                "inbounddate" => "InboundDate",
                "createdat" => "CreatedAt",
                "updatedat" => "UpdatedAt",
                _ => columnId?.Trim() ?? string.Empty,
            };
        }

        private static bool TryParseGridDate(string? value, out DateTime date)
        {
            return DateTime.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal,
                out date
            );
        }

        private static DateTime ToInclusiveEndOfDay(DateTime value)
        {
            return value.Date.AddDays(1).AddTicks(-1);
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

    }
}
