using BlazorApp.Api.Data;
using BlazorApp.Api.Features.StoreOrders.Common;
using BlazorApp.Api.Features.StoreOrders.Orders.Domain;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using SqlSugar;

namespace BlazorApp.Api.Features.StoreOrders.Orders.Infrastructure;

internal sealed class StoreOrderDetailQueryStore(
    SqlSugarContext context,
    IStoreOrderAccessScope accessScope
)
{
    private readonly ISqlSugarClient _db = context.Db;

    internal async Task<ApiResponse<List<string>>> GetProductCodesAsync(string orderGuid)
    {
        var accessibleStoreCodes = await accessScope.GetAccessibleStoreCodesAsync();
        var order = await _db.Queryable<WareHouseOrder>()
            .Where(candidate => candidate.OrderGUID == orderGuid && !candidate.IsDeleted)
            .FirstAsync();
        if (order == null)
        {
            return new ApiResponse<List<string>>
            {
                Success = false,
                Message = "Order not found",
                Data = new List<string>(),
            };
        }

        if (
            accessibleStoreCodes != null
            && !string.IsNullOrWhiteSpace(order.StoreCode)
            && !accessibleStoreCodes.Contains(
                order.StoreCode,
                StringComparer.OrdinalIgnoreCase
            )
        )
        {
            return new ApiResponse<List<string>>
            {
                Success = false,
                Message = "You do not have access to this order",
                Data = new List<string>(),
            };
        }

        var productCodes = await _db.Queryable<WareHouseOrderDetails>()
            .Where(detail =>
                detail.OrderGUID == orderGuid
                && !detail.IsDeleted
                && detail.ProductCode != null
            )
            .Select(detail => detail.ProductCode!)
            .Distinct()
            .ToListAsync();
        return new ApiResponse<List<string>>
        {
            Success = true,
            Data = productCodes
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    internal async Task<ApiResponse<StoreOrderDetailDto?>> GetAsync(
        StoreOrderDetailInput input
    )
    {
        var query = input.Query;
        var accessibleStoreCodes = await accessScope.GetAccessibleStoreCodesAsync();
        var order = await _db.Queryable<WareHouseOrder>()
            .LeftJoin<Store>((candidate, store) =>
                candidate.StoreCode == store.StoreCode
                || candidate.StoreCode == store.StoreGUID
            )
            .Where(candidate =>
                candidate.OrderGUID == input.OrderGuid && !candidate.IsDeleted
            )
            .Select((candidate, store) => new
            {
                Order = candidate,
                StoreName = store.StoreName,
                StoreAddress = store.Address,
                StoreContactEmail = store.ContactEmail,
            })
            .FirstAsync();
        if (order == null)
        {
            return new ApiResponse<StoreOrderDetailDto?>
            {
                Success = false,
                Message = "Order not found",
            };
        }

        if (
            accessibleStoreCodes != null
            && !string.IsNullOrWhiteSpace(order.Order.StoreCode)
            && !accessibleStoreCodes.Contains(
                order.Order.StoreCode,
                StringComparer.OrdinalIgnoreCase
            )
        )
        {
            return new ApiResponse<StoreOrderDetailDto?>
            {
                Success = false,
                Message = "You do not have access to this order",
            };
        }

        var detailQuery = _db.Queryable<WareHouseOrderDetails>()
            .LeftJoin<Product>((detail, product) =>
                detail.ProductCode == product.ProductCode
            )
            .LeftJoin<WarehouseProduct>((detail, product, warehouseProduct) =>
                detail.ProductCode == warehouseProduct.ProductCode
            )
            .LeftJoin<DomesticProduct>(
                (detail, product, warehouseProduct, domesticProduct) =>
                    warehouseProduct.ProductCode == domesticProduct.ProductCode
            )
            .Where(detail =>
                detail.OrderGUID == order.Order.OrderGUID && !detail.IsDeleted
            );

        var keyword = query.Keyword?.Trim();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            detailQuery = detailQuery.Where(
                (detail, product, warehouseProduct, domesticProduct) =>
                    (product.ItemNumber != null && product.ItemNumber.Contains(keyword))
                    || (
                        product.ProductName != null
                        && product.ProductName.Contains(keyword)
                    )
                    || (product.Barcode != null && product.Barcode.Contains(keyword))
                    || (
                        detail.ProductCode != null
                        && detail.ProductCode.Contains(keyword)
                    )
            );
        }
        if (!string.IsNullOrWhiteSpace(query.ItemNumber))
        {
            var filter = query.ItemNumber;
            detailQuery = detailQuery.Where(
                (detail, product, warehouseProduct, domesticProduct) =>
                    product.ItemNumber != null && product.ItemNumber.Contains(filter)
            );
        }
        if (!string.IsNullOrWhiteSpace(query.ProductName))
        {
            var filter = query.ProductName;
            detailQuery = detailQuery.Where(
                (detail, product, warehouseProduct, domesticProduct) =>
                    product.ProductName != null && product.ProductName.Contains(filter)
            );
        }
        if (!string.IsNullOrWhiteSpace(query.Barcode))
        {
            var filter = query.Barcode;
            detailQuery = detailQuery.Where(
                (detail, product, warehouseProduct, domesticProduct) =>
                    product.Barcode != null && product.Barcode.Contains(filter)
            );
        }
        if (!string.IsNullOrWhiteSpace(query.LocationCode))
        {
            var filter = query.LocationCode;
            detailQuery = detailQuery.Where(
                (detail, product, warehouseProduct, domesticProduct) =>
                    detail.ProductCode != null
                    && SqlFunc.Subqueryable<ProductLocation>()
                        .InnerJoin<Location>((productLocation, location) =>
                            productLocation.LocationGuid == location.LocationGuid
                        )
                        .Where((productLocation, location) =>
                            productLocation.ProductCode == detail.ProductCode
                            && !productLocation.IsDeleted
                            && !location.IsDeleted
                            && location.LocationType == 1
                            && location.LocationCode != null
                            && location.LocationCode.Contains(filter)
                        )
                        .Any()
            );
        }
        if (query.QuantityMin.HasValue)
        {
            var min = query.QuantityMin.Value;
            detailQuery = detailQuery.Where(
                (detail, product, warehouseProduct, domesticProduct) =>
                    detail.Quantity != null && detail.Quantity >= min
            );
        }
        if (query.QuantityMax.HasValue)
        {
            var max = query.QuantityMax.Value;
            detailQuery = detailQuery.Where(
                (detail, product, warehouseProduct, domesticProduct) =>
                    detail.Quantity != null && detail.Quantity <= max
            );
        }
        if (query.AllocQuantityMin.HasValue)
        {
            var min = query.AllocQuantityMin.Value;
            detailQuery = detailQuery.Where(
                (detail, product, warehouseProduct, domesticProduct) =>
                    detail.AllocQuantity != null && detail.AllocQuantity >= min
            );
        }
        if (query.AllocQuantityMax.HasValue)
        {
            var max = query.AllocQuantityMax.Value;
            detailQuery = detailQuery.Where(
                (detail, product, warehouseProduct, domesticProduct) =>
                    detail.AllocQuantity != null && detail.AllocQuantity <= max
            );
        }
        if (query.ImportPriceMin.HasValue)
        {
            var min = query.ImportPriceMin.Value;
            detailQuery = detailQuery.Where(
                (detail, product, warehouseProduct, domesticProduct) =>
                    (detail.ImportPrice != null && detail.ImportPrice >= min)
                    || (
                        detail.ImportPrice == null
                        && warehouseProduct.ImportPrice != null
                        && warehouseProduct.ImportPrice >= min
                    )
            );
        }
        if (query.ImportPriceMax.HasValue)
        {
            var max = query.ImportPriceMax.Value;
            detailQuery = detailQuery.Where(
                (detail, product, warehouseProduct, domesticProduct) =>
                    (detail.ImportPrice != null && detail.ImportPrice <= max)
                    || (
                        detail.ImportPrice == null
                        && warehouseProduct.ImportPrice != null
                        && warehouseProduct.ImportPrice <= max
                    )
            );
        }
        if (query.IsActive.HasValue)
        {
            detailQuery = query.IsActive.Value
                ? detailQuery.Where(
                    (detail, product, warehouseProduct, domesticProduct) =>
                        warehouseProduct.IsActive
                )
                : detailQuery.Where(
                    (detail, product, warehouseProduct, domesticProduct) =>
                        !warehouseProduct.IsActive
                );
        }

        var statFilter = query.StatFilter?.Trim().ToLowerInvariant();
        if (statFilter == "orderednotshipped")
        {
            detailQuery = detailQuery.Where(
                (detail, product, warehouseProduct, domesticProduct) =>
                    (detail.Quantity ?? 0) > 0 && (detail.AllocQuantity ?? 0) == 0
            );
        }
        else if (statFilter == "shippedwithoutorder")
        {
            detailQuery = detailQuery.Where(
                (detail, product, warehouseProduct, domesticProduct) =>
                    (detail.Quantity ?? 0) <= 0 && (detail.AllocQuantity ?? 0) > 0
            );
        }
        else if (statFilter == "return")
        {
            // 退货行：发货数量为负（订货数量恒为 0）。直接比较，NULL 发货数量按 SQL 三值逻辑不命中。
            detailQuery = detailQuery.Where(
                (detail, product, warehouseProduct, domesticProduct) =>
                    detail.AllocQuantity < 0
            );
        }
        else if (statFilter is "active" or "1" or "true")
        {
            detailQuery = detailQuery.Where(
                (detail, product, warehouseProduct, domesticProduct) =>
                    warehouseProduct.IsActive
            );
        }
        else if (statFilter is "inactive" or "0" or "false")
        {
            detailQuery = detailQuery.Where(
                (detail, product, warehouseProduct, domesticProduct) =>
                    !warehouseProduct.IsActive
            );
        }

        var itemsTotal = await detailQuery.CountAsync();
        var sortBy = (query.SortBy ?? string.Empty).Trim().ToLowerInvariant();
        var isLocationCodeSort = sortBy == "locationcode";
        var orderType = query.SortDescending ? OrderByType.Desc : OrderByType.Asc;
        if (!isLocationCodeSort)
        {
            detailQuery = sortBy switch
            {
                "itemnumber" => detailQuery.OrderBy(
                    (detail, product, warehouseProduct, domesticProduct) =>
                        product.ItemNumber,
                    orderType
                ),
                "productcode" => detailQuery.OrderBy(
                    (detail, product, warehouseProduct, domesticProduct) =>
                        detail.ProductCode,
                    orderType
                ),
                "barcode" => detailQuery.OrderBy(
                    (detail, product, warehouseProduct, domesticProduct) =>
                        product.Barcode,
                    orderType
                ),
                "productname" => detailQuery.OrderBy(
                    (detail, product, warehouseProduct, domesticProduct) =>
                        product.ProductName,
                    orderType
                ),
                "quantity" => detailQuery.OrderBy(
                    (detail, product, warehouseProduct, domesticProduct) =>
                        detail.Quantity,
                    orderType
                ),
                "allocquantity" => detailQuery.OrderBy(
                    (detail, product, warehouseProduct, domesticProduct) =>
                        detail.AllocQuantity,
                    orderType
                ),
                "price" => detailQuery.OrderBy(
                    (detail, product, warehouseProduct, domesticProduct) =>
                        detail.OEMPrice,
                    orderType
                ),
                "amount" => detailQuery.OrderBy(
                    (detail, product, warehouseProduct, domesticProduct) =>
                        detail.OEMAmount,
                    orderType
                ),
                "importprice" => detailQuery.OrderBy(
                    (detail, product, warehouseProduct, domesticProduct) =>
                        detail.ImportPrice ?? warehouseProduct.ImportPrice,
                    orderType
                ),
                "importamount" => detailQuery.OrderBy(
                    (detail, product, warehouseProduct, domesticProduct) =>
                        detail.ImportAmount
                        ?? (
                            (detail.ImportPrice ?? (warehouseProduct.ImportPrice ?? 0))
                            * (detail.Quantity ?? 0)
                        ),
                    orderType
                ),
                "allocatedimportamount" => detailQuery.OrderBy(
                    (detail, product, warehouseProduct, domesticProduct) =>
                        (detail.ImportPrice ?? (warehouseProduct.ImportPrice ?? 0))
                        * (detail.AllocQuantity ?? 0),
                    orderType
                ),
                "isactive" => detailQuery.OrderBy(
                    (detail, product, warehouseProduct, domesticProduct) =>
                        warehouseProduct.IsActive,
                    orderType
                ),
                _ => detailQuery.OrderBy(
                    (detail, product, warehouseProduct, domesticProduct) =>
                        product.ItemNumber,
                    orderType
                ),
            };
            detailQuery = detailQuery.OrderBy(
                (detail, product, warehouseProduct, domesticProduct) =>
                    detail.DetailGUID,
                OrderByType.Asc
            );
        }

        var pageQuery = detailQuery.Select(
            (detail, product, warehouseProduct, domesticProduct) =>
                new StoreOrderCartItemDto
                {
                    DetailGUID = detail.DetailGUID,
                    ProductCode = detail.ProductCode ?? string.Empty,
                    ItemNumber = product.ItemNumber,
                    Barcode = product.Barcode,
                    Grade = SqlFunc.Subqueryable<ProductGrade>()
                        .Where(grade =>
                            grade.ProductCode == detail.ProductCode && !grade.IsDeleted
                        )
                        .OrderBy(grade => grade.Grade)
                        .Select(grade => grade.Grade),
                    ProductName = product.ProductName,
                    ProductImage = product.ProductImage,
                    Price = detail.OEMPrice ?? 0,
                    Quantity = detail.Quantity ?? 0,
                    AllocQuantity = detail.AllocQuantity,
                    ReturnReason = detail.ReturnReason,
                    ReturnDisposition = detail.ReturnDisposition,
                    ReturnNote = detail.ReturnNote,
                    Amount = detail.OEMAmount ?? 0,
                    ImportPrice = detail.ImportPrice ?? (warehouseProduct.ImportPrice ?? 0),
                    // 订货金额与发货金额是两个独立业务量，不能互相覆盖。
                    ImportAmount = detail.ImportAmount
                        ?? (
                            (detail.ImportPrice ?? (warehouseProduct.ImportPrice ?? 0))
                            * (detail.Quantity ?? 0)
                        ),
                    AllocatedImportAmount = (
                        detail.ImportPrice ?? (warehouseProduct.ImportPrice ?? 0)
                    ) * (detail.AllocQuantity ?? 0),
                    Volume = domesticProduct.PackingQuantity > 0
                        ? domesticProduct.UnitVolume / domesticProduct.PackingQuantity
                        : domesticProduct.UnitVolume,
                    MinOrderQuantity = warehouseProduct.MinOrderQuantity ?? 1,
                    IsActive = warehouseProduct.IsActive,
                    RRP = product.RetailPrice,
                }
        );

        List<StoreOrderCartItemDto> pageDetails;
        if (isLocationCodeSort)
        {
            var locationSortQuery = _db.Queryable<ProductLocation>()
                .InnerJoin<Location>((productLocation, location) =>
                    productLocation.LocationGuid == location.LocationGuid
                )
                .Where((productLocation, location) =>
                    productLocation.ProductCode != null
                    && !productLocation.IsDeleted
                    && !location.IsDeleted
                    && location.LocationType == 1
                    && location.LocationCode != null
                )
                .GroupBy((productLocation, location) => productLocation.ProductCode)
                .Select((productLocation, location) => new DetailLocationSortRow
                {
                    ProductCode = productLocation.ProductCode ?? string.Empty,
                    LocationSortCode = SqlFunc.AggregateMin(location.LocationCode),
                })
                .MergeTable();
            var locationPageQuery = detailQuery
                .LeftJoin<DetailLocationSortRow>(
                    locationSortQuery,
                    (detail, product, warehouseProduct, domesticProduct, locationSort) =>
                        detail.ProductCode == locationSort.ProductCode
                )
                .OrderBy(
                    (detail, product, warehouseProduct, domesticProduct, locationSort) =>
                        SqlFunc.IIF(
                            locationSort.LocationSortCode == null
                                || locationSort.LocationSortCode == string.Empty,
                            0,
                            1
                        ),
                    OrderByType.Asc
                )
                .OrderBy(
                    (detail, product, warehouseProduct, domesticProduct, locationSort) =>
                        locationSort.LocationSortCode,
                    query.SortDescending ? OrderByType.Desc : OrderByType.Asc
                )
                .OrderBy(
                    (detail, product, warehouseProduct, domesticProduct, locationSort) =>
                        product.ItemNumber,
                    OrderByType.Asc
                )
                .OrderBy(
                    (detail, product, warehouseProduct, domesticProduct, locationSort) =>
                        detail.DetailGUID,
                    OrderByType.Asc
                )
                .Select(
                    (detail, product, warehouseProduct, domesticProduct, locationSort) =>
                        new StoreOrderCartItemDto
                        {
                            DetailGUID = detail.DetailGUID,
                            ProductCode = detail.ProductCode ?? string.Empty,
                            ItemNumber = product.ItemNumber,
                            Barcode = product.Barcode,
                            Grade = SqlFunc.Subqueryable<ProductGrade>()
                                .Where(grade =>
                                    grade.ProductCode == detail.ProductCode
                                    && !grade.IsDeleted
                                )
                                .OrderBy(grade => grade.Grade)
                                .Select(grade => grade.Grade),
                            ProductName = product.ProductName,
                            ProductImage = product.ProductImage,
                            Price = detail.OEMPrice ?? 0,
                            Quantity = detail.Quantity ?? 0,
                            AllocQuantity = detail.AllocQuantity,
                            ReturnReason = detail.ReturnReason,
                            ReturnDisposition = detail.ReturnDisposition,
                            ReturnNote = detail.ReturnNote,
                            Amount = detail.OEMAmount ?? 0,
                            ImportPrice = detail.ImportPrice
                                ?? (warehouseProduct.ImportPrice ?? 0),
                            ImportAmount = detail.ImportAmount
                                ?? (
                                    (
                                        detail.ImportPrice
                                        ?? (warehouseProduct.ImportPrice ?? 0)
                                    ) * (detail.Quantity ?? 0)
                                ),
                            AllocatedImportAmount = (
                                detail.ImportPrice ?? (warehouseProduct.ImportPrice ?? 0)
                            ) * (detail.AllocQuantity ?? 0),
                            Volume = domesticProduct.PackingQuantity > 0
                                ? domesticProduct.UnitVolume
                                    / domesticProduct.PackingQuantity
                                : domesticProduct.UnitVolume,
                            MinOrderQuantity = warehouseProduct.MinOrderQuantity ?? 1,
                            IsActive = warehouseProduct.IsActive,
                            RRP = product.RetailPrice,
                        }
                );
            if (!input.LoadAllItems)
            {
                locationPageQuery = locationPageQuery
                    .Skip((query.PageNumber - 1) * query.PageSize)
                    .Take(query.PageSize);
            }

            pageDetails = await locationPageQuery.ToListAsync();
        }
        else
        {
            if (!input.LoadAllItems)
            {
                pageQuery = pageQuery
                    .Skip((query.PageNumber - 1) * query.PageSize)
                    .Take(query.PageSize);
            }

            pageDetails = await pageQuery.ToListAsync();
        }

        await FillLocationCodesAsync(pageDetails);
        FillVolumeFields(pageDetails);

        // 汇总按整单计算，不受当前页和筛选条件影响。
        var summary = await BuildSummaryQuery(_db, order.Order.OrderGUID).FirstAsync();
        var latestInvoiceEmailSentRecord = await _db
            .Queryable<StoreOrderInvoiceEmailSendRecord>()
            .Where(record => record.StoreOrderUuid == order.Order.OrderGUID)
            .OrderBy(record => record.SentAtUtc, OrderByType.Desc)
            .OrderBy(record => record.CreatedAtUtc, OrderByType.Desc)
            .FirstAsync();
        var dto = new StoreOrderDetailDto
        {
            OrderGUID = order.Order.OrderGUID,
            OrderNo = order.Order.OrderNo,
            StoreCode = order.Order.StoreCode,
            StoreName = order.StoreName,
            OrderDate = order.Order.OrderDate,
            OutboundDate = order.Order.OutboundDate,
            TotalAmount = order.Order.OEMTotalAmount ?? 0,
            TotalQuantity = (int)(summary?.TotalQuantity ?? 0),
            TotalAllocQuantity = (int)(summary?.TotalAllocQuantity ?? 0),
            TotalSKU = summary?.TotalSKU ?? 0,
            TotalImportAmount = summary?.TotalImportAmount ?? 0,
            TotalAllocatedImportAmount = summary?.TotalAllocatedImportAmount ?? 0,
            TotalShipImportAmount = summary?.TotalShipImportAmount ?? 0,
            TotalReturnImportAmount = summary?.TotalReturnImportAmount ?? 0,
            ReturnLineCount = summary?.ReturnLineCount ?? 0,
            TotalReturnQuantity = summary?.TotalReturnQuantity ?? 0,
            // 单据类型按净额判定：与发票接口、邮件、附件共用 StoreOrderReturnRules.ResolveDocumentType。
            DocumentType = StoreOrderReturnRules.ResolveDocumentType(
                summary?.TotalAllocatedImportAmount ?? 0
            ),
            TotalVolume = summary?.TotalOrderVolume ?? 0,
            TotalOrderVolume = summary?.TotalOrderVolume ?? 0,
            TotalAllocVolume = summary?.TotalAllocVolume ?? 0,
            Remarks = order.Order.Remarks,
            StoreAddress = order.StoreAddress,
            StoreContactEmail = order.StoreContactEmail,
            ShippingFee = order.Order.ShippingFee,
            FlowStatus = order.Order.FlowStatus,
            InvoiceEmailSentInfo = latestInvoiceEmailSentRecord == null
                ? new StoreOrderInvoiceEmailSentInfoDto()
                : new StoreOrderInvoiceEmailSentInfoDto
                {
                    HasSent = true,
                    SentAt = DateTime.SpecifyKind(
                        latestInvoiceEmailSentRecord.SentAtUtc,
                        DateTimeKind.Utc
                    ),
                    ToEmail = latestInvoiceEmailSentRecord.ToEmail,
                    JobId = latestInvoiceEmailSentRecord.JobId,
                },
            Items = pageDetails,
            Total = input.LoadAllItems ? pageDetails.Count : itemsTotal,
            ItemsTotal = input.LoadAllItems ? pageDetails.Count : itemsTotal,
            PageNumber = input.LoadAllItems ? 1 : query.PageNumber,
            PageSize = input.LoadAllItems ? pageDetails.Count : query.PageSize,
            OrderedNotShippedCount = summary?.OrderedNotShippedCount ?? 0,
            ShippedWithoutOrderCount = summary?.ShippedWithoutOrderCount ?? 0,
        };
        return new ApiResponse<StoreOrderDetailDto?> { Success = true, Data = dto };
    }

    /// <summary>
    /// 整单汇总查询（单独成方法，便于做 SQL Server 方言 SQL 文本断言）。
    /// 发货金额口径：进口价 × 发货数量。TotalAllocatedImportAmount 是净额，
    /// = TotalShipImportAmount（发货数量 &gt; 0 的行）+ TotalReturnImportAmount（发货数量 &lt; 0 的退货行，&lt;= 0）。
    /// 分行用 SUM(CASE WHEN 发货数量 &gt; 0 …)：条件里发货数量一律 (AllocQuantity ?? 0)，不写 HasValue 之类会被翻成 &lt;&gt;'' 的写法。
    /// </summary>
    internal static ISugarQueryable<StoreOrderDetailSummaryRow> BuildSummaryQuery(
        ISqlSugarClient db,
        string orderGuid
    )
    {
        return db.Queryable<WareHouseOrderDetails>()
            .LeftJoin<Product>((detail, product) =>
                detail.ProductCode == product.ProductCode
            )
            .LeftJoin<WarehouseProduct>((detail, product, warehouseProduct) =>
                detail.ProductCode == warehouseProduct.ProductCode
            )
            .LeftJoin<DomesticProduct>(
                (detail, product, warehouseProduct, domesticProduct) =>
                    warehouseProduct.ProductCode == domesticProduct.ProductCode
            )
            .Where(detail => detail.OrderGUID == orderGuid && !detail.IsDeleted)
            .Select((detail, product, warehouseProduct, domesticProduct) =>
                new StoreOrderDetailSummaryRow
                {
                    TotalQuantity = SqlFunc.AggregateSum(detail.Quantity ?? 0),
                    TotalAllocQuantity = SqlFunc.AggregateSum(detail.AllocQuantity ?? 0),
                    TotalSKU = SqlFunc.AggregateDistinctCount(detail.ProductCode),
                    TotalImportAmount = SqlFunc.AggregateSum(
                        detail.ImportAmount
                            ?? (
                                (detail.ImportPrice ?? (warehouseProduct.ImportPrice ?? 0))
                                * (detail.Quantity ?? 0)
                            )
                    ),
                    TotalAllocatedImportAmount = SqlFunc.AggregateSum(
                        (detail.ImportPrice ?? (warehouseProduct.ImportPrice ?? 0))
                        * (detail.AllocQuantity ?? 0)
                    ),
                    TotalShipImportAmount = SqlFunc.AggregateSum(
                        SqlFunc.IIF(
                            detail.AllocQuantity > 0,
                            (detail.ImportPrice ?? (warehouseProduct.ImportPrice ?? 0))
                                * (detail.AllocQuantity ?? 0),
                            0m
                        )
                    ),
                    TotalReturnImportAmount = SqlFunc.AggregateSum(
                        SqlFunc.IIF(
                            detail.AllocQuantity < 0,
                            (detail.ImportPrice ?? (warehouseProduct.ImportPrice ?? 0))
                                * (detail.AllocQuantity ?? 0),
                            0m
                        )
                    ),
                    ReturnLineCount = SqlFunc.AggregateCount(
                        SqlFunc.IIF(
                            detail.AllocQuantity < 0,
                            detail.DetailGUID,
                            null
                        )
                    ),
                    // 退回件数以正数表示（allocQuantity 为负，取反）。
                    TotalReturnQuantity = SqlFunc.AggregateSum(
                        SqlFunc.IIF(
                            detail.AllocQuantity < 0,
                            0m - (detail.AllocQuantity ?? 0),
                            0m
                        )
                    ),
                    TotalOrderVolume = SqlFunc.AggregateSum(
                        (
                            domesticProduct.PackingQuantity > 0
                                ? domesticProduct.UnitVolume
                                    / domesticProduct.PackingQuantity
                                : domesticProduct.UnitVolume
                        ) * (detail.Quantity ?? 0)
                    ),
                    TotalAllocVolume = SqlFunc.AggregateSum(
                        (
                            domesticProduct.PackingQuantity > 0
                                ? domesticProduct.UnitVolume
                                    / domesticProduct.PackingQuantity
                                : domesticProduct.UnitVolume
                        ) * (detail.AllocQuantity ?? 0)
                    ),
                    OrderedNotShippedCount = SqlFunc.AggregateCount(
                        SqlFunc.IIF(
                            (detail.Quantity ?? 0) > 0
                                && (detail.AllocQuantity ?? 0) == 0,
                            detail.DetailGUID,
                            null
                        )
                    ),
                    ShippedWithoutOrderCount = SqlFunc.AggregateCount(
                        SqlFunc.IIF(
                            (detail.Quantity ?? 0) <= 0
                                && (detail.AllocQuantity ?? 0) > 0,
                            detail.DetailGUID,
                            null
                        )
                    ),
                }
            );
    }

    internal sealed class StoreOrderDetailSummaryRow
    {
        public decimal TotalQuantity { get; set; }
        public decimal TotalAllocQuantity { get; set; }
        public int TotalSKU { get; set; }
        public decimal TotalImportAmount { get; set; }
        public decimal TotalAllocatedImportAmount { get; set; }
        public decimal TotalShipImportAmount { get; set; }
        public decimal TotalReturnImportAmount { get; set; }
        public int ReturnLineCount { get; set; }
        public decimal TotalReturnQuantity { get; set; }
        public decimal? TotalOrderVolume { get; set; }
        public decimal? TotalAllocVolume { get; set; }
        public int OrderedNotShippedCount { get; set; }
        public int ShippedWithoutOrderCount { get; set; }
    }

    private async Task FillLocationCodesAsync(List<StoreOrderCartItemDto> items)
    {
        var productCodes = items
            .Select(item => item.ProductCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (productCodes.Count == 0)
        {
            return;
        }

        var locations = await _db.Queryable<ProductLocation>()
            .InnerJoin<Location>((productLocation, location) =>
                productLocation.LocationGuid == location.LocationGuid
            )
            .Where((productLocation, location) =>
                productLocation.ProductCode != null
                && productCodes.Contains(productLocation.ProductCode)
                && !productLocation.IsDeleted
                && !location.IsDeleted
                && location.LocationType == 1
                && location.LocationCode != null
            )
            .Select((productLocation, location) => new
            {
                ProductCode = productLocation.ProductCode!,
                LocationCode = location.LocationCode!,
            })
            .ToListAsync();
        var locationMap = locations
            .GroupBy(location => location.ProductCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => string.Join(
                    ", ",
                    group.Select(location => location.LocationCode)
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(code => code)
                ),
                StringComparer.OrdinalIgnoreCase
            );
        foreach (var item in items)
        {
            if (locationMap.TryGetValue(item.ProductCode, out var locationCode))
            {
                item.LocationCode = locationCode;
            }
        }
    }

    private static void FillVolumeFields(List<StoreOrderCartItemDto> items)
    {
        foreach (var item in items)
        {
            if (!item.Volume.HasValue)
            {
                continue;
            }

            item.OrderVolume = item.Volume.Value * item.Quantity;
            item.AllocVolume = item.Volume.Value * (item.AllocQuantity ?? 0);
            item.TotalVolume = item.OrderVolume;
        }
    }

    private sealed class DetailLocationSortRow
    {
        public string ProductCode { get; set; } = string.Empty;

        public string? LocationSortCode { get; set; }
    }
}
