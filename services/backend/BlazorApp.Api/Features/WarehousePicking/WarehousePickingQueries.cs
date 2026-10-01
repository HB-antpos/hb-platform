using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.WarehousePicking;

/// <summary>
/// 拣货读取：订单头、行、码表、拣货合计与参与人。只读，不开事务；写入路径在 <see cref="WarehousePickingService"/>。
/// 商品一律按 WareHouseOrderDetails.ProductCode 关联（与订单详情、配货单同口径），不使用分店商品编码。
/// </summary>
internal static class WarehousePickingQueries
{
    internal sealed class OrderHeaderRow
    {
        public string OrderGUID { get; set; } = string.Empty;
        public string? OrderNo { get; set; }
        public string? StoreCode { get; set; }
        public string? StoreName { get; set; }
        public DateTime? OrderDate { get; set; }
        public int? FlowStatus { get; set; }
    }

    internal sealed class LineRow
    {
        public string DetailGUID { get; set; } = string.Empty;
        public string? ProductCode { get; set; }
        public decimal? Quantity { get; set; }
        public string? ItemNumber { get; set; }
        public string? Barcode { get; set; }
        public string? ProductName { get; set; }
        public string? ProductImage { get; set; }
        public int? ProductType { get; set; }
        public int? MinOrderQuantity { get; set; }
    }

    internal sealed class PickTotalRow
    {
        public string DetailGUID { get; set; } = string.Empty;
        public string PickerUserGuid { get; set; } = string.Empty;
        public string? PickerName { get; set; }
        public int Quantity { get; set; }
    }

    private sealed class LocationRow
    {
        public string ProductCode { get; set; } = string.Empty;
        public string LocationCode { get; set; } = string.Empty;
        public string? LocationBarcode { get; set; }
    }

    private sealed class SetCodeRow
    {
        public string ProductCode { get; set; } = string.Empty;
        public string SetProductCode { get; set; } = string.Empty;
        public string SetItemNumber { get; set; } = string.Empty;
        public string? SetBarcode { get; set; }
        public int SetType { get; set; }
    }

    private sealed class MultiCodeRow
    {
        public string? ProductCode { get; set; }
        public string? MultiBarcode { get; set; }
        public string? MultiCodeProductCode { get; set; }
        public string? StoreMultiCodeProductCode { get; set; }
    }

    private sealed class ProductNameRow
    {
        public string? ProductCode { get; set; }
        public string? ProductName { get; set; }
        public string? ItemNumber { get; set; }
        public string? Barcode { get; set; }
    }

    internal static Task<OrderHeaderRow?> LoadOrderAsync(ISqlSugarClient db, string orderGuid)
    {
        return db.Queryable<WareHouseOrder>()
            .Where(order => order.OrderGUID == orderGuid && !order.IsDeleted)
            .Select(order => new OrderHeaderRow
            {
                OrderGUID = order.OrderGUID,
                OrderNo = order.OrderNo,
                StoreCode = order.StoreCode,
                OrderDate = order.OrderDate,
                FlowStatus = order.FlowStatus,
                StoreName = SqlFunc.Subqueryable<Store>()
                    .Where(store => store.StoreCode == order.StoreCode || store.StoreGUID == order.StoreCode)
                    .Select(store => store.StoreName),
            })
            .FirstAsync()!;
    }

    internal static async Task<List<LineRow>> LoadLinesAsync(ISqlSugarClient db, string orderGuid)
    {
        var rows = await db.Queryable<WareHouseOrderDetails>()
            .LeftJoin<Product>((detail, product) =>
                detail.ProductCode == product.ProductCode && !product.IsDeleted
            )
            .LeftJoin<WarehouseProduct>((detail, product, warehouseProduct) =>
                detail.ProductCode == warehouseProduct.ProductCode
            )
            .Where(detail => detail.OrderGUID == orderGuid && !detail.IsDeleted)
            .Select((detail, product, warehouseProduct) => new LineRow
            {
                DetailGUID = detail.DetailGUID,
                ProductCode = detail.ProductCode,
                Quantity = detail.Quantity,
                ItemNumber = product.ItemNumber,
                Barcode = product.Barcode,
                ProductName = product.ProductName,
                ProductImage = product.ProductImage,
                ProductType = product.ProductType,
                MinOrderQuantity = warehouseProduct.MinOrderQuantity,
            })
            .ToListAsync();

        // Product.ProductCode 没有唯一约束，历史重复商品会让同一行重复出现，按明细只保留一条。
        return rows
            .GroupBy(row => row.DetailGUID, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    internal static Task<List<PickTotalRow>> LoadPickTotalsAsync(
        ISqlSugarClient db,
        string orderGuid,
        string? detailGuid = null
    )
    {
        var query = db.Queryable<WarehouseOrderPickRecord>()
            .Where(record => record.OrderGUID == orderGuid);
        if (!string.IsNullOrWhiteSpace(detailGuid))
        {
            query = query.Where(record => record.DetailGUID == detailGuid);
        }

        return query
            .GroupBy(record => new { record.DetailGUID, record.PickerUserGuid })
            .Select(record => new PickTotalRow
            {
                DetailGUID = record.DetailGUID,
                PickerUserGuid = record.PickerUserGuid,
                PickerName = SqlFunc.AggregateMax(record.PickerName),
                Quantity = SqlFunc.AggregateSum(record.QuantityDelta),
            })
            .ToListAsync();
    }

    internal static Task<List<WarehouseOrderPickParticipant>> LoadParticipantsAsync(
        ISqlSugarClient db,
        string orderGuid
    )
    {
        return db.Queryable<WarehouseOrderPickParticipant>()
            .Where(participant => participant.OrderGUID == orderGuid)
            .OrderBy(participant => participant.JoinedAtUtc)
            .ToListAsync();
    }

    internal static Task<WarehouseOrderPickSession?> LoadSessionAsync(
        ISqlSugarClient db,
        string orderGuid
    )
    {
        return db.Queryable<WarehouseOrderPickSession>()
            .Where(session => session.OrderGUID == orderGuid)
            .FirstAsync()!;
    }

    internal static async Task<WarehousePickingSheetDto> BuildSheetAsync(
        ISqlSugarClient db,
        OrderHeaderRow order,
        WarehouseOrderPickSession? session
    )
    {
        var lines = await LoadLinesAsync(db, order.OrderGUID);
        var productCodes = lines
            .Select(line => line.ProductCode?.Trim())
            .Where(code => !string.IsNullOrEmpty(code))
            .Select(code => code!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var locations = await LoadPickLocationsAsync(db, productCodes);
        var setCodes = await LoadSetCodesAsync(db, productCodes);
        var multiCodes = await LoadStoreMultiCodesAsync(db, productCodes, order.StoreCode);
        var childNames = await LoadProductNamesAsync(
            db,
            setCodes.Where(code => code.SetType == 1).Select(code => code.SetProductCode)
        );
        var totals = await LoadPickTotalsAsync(db, order.OrderGUID);
        var participants = await LoadParticipantsAsync(db, order.OrderGUID);
        var stockouts = await LoadActiveStockoutsAsync(db, order.OrderGUID);
        var assignments = await LoadAssignmentsAsync(db, order.OrderGUID);

        var totalsByDetail = totals
            .GroupBy(total => total.DetailGUID, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var locationsByProduct = locations
            .GroupBy(location => location.ProductCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var setCodesByProduct = setCodes
            .GroupBy(code => code.ProductCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var multiCodesByProduct = multiCodes
            .Where(code => !string.IsNullOrWhiteSpace(code.ProductCode))
            .GroupBy(code => code.ProductCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var codeSources = new List<WarehousePickingCodeSource>();
        var lineDtos = new List<WarehousePickingLineDto>();
        foreach (var line in lines)
        {
            var productCode = line.ProductCode?.Trim() ?? string.Empty;
            locationsByProduct.TryGetValue(productCode, out var lineLocations);
            setCodesByProduct.TryGetValue(productCode, out var lineSetCodes);
            multiCodesByProduct.TryGetValue(productCode, out var lineMultiCodes);
            totalsByDetail.TryGetValue(line.DetailGUID, out var lineTotals);

            var children = (lineSetCodes ?? new List<SetCodeRow>())
                .Where(code => code.SetType == 1)
                .Select(code =>
                {
                    childNames.TryGetValue(code.SetProductCode, out var child);
                    return new WarehousePickingSetChildDto
                    {
                        ProductCode = code.SetProductCode,
                        ItemNumber = NullIfBlank(code.SetItemNumber) ?? child?.ItemNumber,
                        Barcode = NullIfBlank(code.SetBarcode) ?? child?.Barcode,
                        ProductName = child?.ProductName,
                    };
                })
                .ToList();

            lineDtos.Add(new WarehousePickingLineDto
            {
                DetailGuid = line.DetailGUID,
                ProductCode = productCode,
                ItemNumber = line.ItemNumber,
                Barcode = line.Barcode,
                ProductName = line.ProductName,
                ProductImage = line.ProductImage,
                LocationCode = lineLocations == null
                    ? null
                    : string.Join(
                        ", ",
                        lineLocations
                            .Select(location => location.LocationCode)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                    ),
                OrderedQuantity = line.Quantity ?? 0,
                MinOrderQuantity = line.MinOrderQuantity,
                IsSet = line.ProductType == 1 || children.Count > 0,
                SetChildren = children,
                PickedTotal = lineTotals?.Sum(total => total.Quantity) ?? 0,
                PickedBy = ToPickedBy(lineTotals),
                Stockout = stockouts.GetValueOrDefault(line.DetailGUID),
                AssigneeUserGuid = assignments.GetValueOrDefault(line.DetailGUID)?.PickerUserGuid,
                AssigneeName = assignments.GetValueOrDefault(line.DetailGUID)?.PickerName,
                AssignmentSegmentNo = assignments.GetValueOrDefault(line.DetailGUID)?.SegmentNo,
            });

            AddLineCodes(codeSources, line, productCode, lineSetCodes, lineMultiCodes, childNames);
            foreach (var location in lineLocations ?? new List<LocationRow>())
            {
                codeSources.Add(new(location.LocationCode, WarehousePickingCodeTargets.Location, line.DetailGUID, null, location.LocationCode));
                codeSources.Add(new(location.LocationBarcode, WarehousePickingCodeTargets.Location, line.DetailGUID, null, location.LocationCode));
            }
        }

        return new WarehousePickingSheetDto
        {
            OrderGuid = order.OrderGUID,
            OrderNo = order.OrderNo,
            StoreCode = order.StoreCode,
            StoreName = order.StoreName,
            OrderDate = order.OrderDate,
            FlowStatus = order.FlowStatus ?? 0,
            Session = ToSessionDto(session),
            Lines = lineDtos,
            Codes = WarehousePickingRules.BuildCodeMap(codeSources),
            Participants = participants.Select(ToParticipantDto).ToList(),
            ServerTimeUtc = DateTime.UtcNow,
        };
    }

    internal static async Task<WarehousePickingProgressDto> BuildProgressAsync(
        ISqlSugarClient db,
        string orderGuid,
        WarehouseOrderPickSession? session
    )
    {
        var lines = await db.Queryable<WareHouseOrderDetails>()
            .LeftJoin<WarehouseProduct>((detail, warehouseProduct) =>
                detail.ProductCode == warehouseProduct.ProductCode
            )
            .Where(detail => detail.OrderGUID == orderGuid && !detail.IsDeleted)
            .Select((detail, warehouseProduct) => new LineRow
            {
                DetailGUID = detail.DetailGUID,
                ProductCode = detail.ProductCode,
                MinOrderQuantity = warehouseProduct.MinOrderQuantity,
            })
            .ToListAsync();
        var totals = await LoadPickTotalsAsync(db, orderGuid);
        var participants = await LoadParticipantsAsync(db, orderGuid);
        var stockouts = await LoadActiveStockoutsAsync(db, orderGuid);
        var assignments = await LoadAssignmentsAsync(db, orderGuid);
        var totalsByDetail = totals
            .GroupBy(total => total.DetailGUID, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        return new WarehousePickingProgressDto
        {
            Session = ToSessionDto(session),
            Lines = lines
                .GroupBy(line => line.DetailGUID, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Select(line =>
                {
                    totalsByDetail.TryGetValue(line.DetailGUID, out var lineTotals);
                    return new WarehousePickingLineProgressDto
                    {
                        DetailGuid = line.DetailGUID,
                        PickedTotal = lineTotals?.Sum(total => total.Quantity) ?? 0,
                        PickedBy = ToPickedBy(lineTotals),
                        MinOrderQuantity = line.MinOrderQuantity,
                        Stockout = stockouts.GetValueOrDefault(line.DetailGUID),
                        AssigneeUserGuid = assignments.GetValueOrDefault(line.DetailGUID)?.PickerUserGuid,
                        AssigneeName = assignments.GetValueOrDefault(line.DetailGUID)?.PickerName,
                        AssignmentSegmentNo = assignments.GetValueOrDefault(line.DetailGUID)?.SegmentNo,
                    };
                })
                .ToList(),
            Participants = participants.Select(ToParticipantDto).ToList(),
            ServerTimeUtc = DateTime.UtcNow,
        };
    }

    internal static async Task<WarehousePickingLineProgressDto> BuildLineProgressAsync(
        ISqlSugarClient db,
        string orderGuid,
        string detailGuid,
        int? minOrderQuantity
    )
    {
        var totals = await LoadPickTotalsAsync(db, orderGuid, detailGuid);
        var stockouts = await LoadActiveStockoutsAsync(db, orderGuid, detailGuid);
        return new WarehousePickingLineProgressDto
        {
            DetailGuid = detailGuid,
            PickedTotal = totals.Sum(total => total.Quantity),
            PickedBy = ToPickedBy(totals),
            MinOrderQuantity = minOrderQuantity,
            Stockout = stockouts.GetValueOrDefault(detailGuid),
        };
    }

    /// <summary>仍有效（未撤销、未因又拣到货而失效）的“货位没货”标记，按订单行索引。</summary>
    internal static async Task<Dictionary<string, WarehousePickingStockoutDto>> LoadActiveStockoutsAsync(
        ISqlSugarClient db,
        string orderGuid,
        string? detailGuid = null
    )
    {
        var query = db.Queryable<WarehouseOrderPickStockout>()
            .Where(stockout => stockout.OrderGUID == orderGuid && stockout.ClearedAtUtc == null);
        if (!string.IsNullOrWhiteSpace(detailGuid))
        {
            query = query.Where(stockout => stockout.DetailGUID == detailGuid);
        }

        var rows = await query.ToListAsync();
        return rows.ToDictionary(
            row => row.DetailGUID,
            row => new WarehousePickingStockoutDto
            {
                Reason = row.Reason,
                MarkedByName = row.MarkedByName,
                MarkedAtUtc = row.MarkedAtUtc,
                PickedAtMark = row.PickedAtMark,
            },
            StringComparer.OrdinalIgnoreCase
        );
    }

    /// <summary>订单各行的负责人（经理派单），按订单行索引；没有分配时为空字典。</summary>
    internal static async Task<Dictionary<string, WarehouseOrderPickAssignment>> LoadAssignmentsAsync(
        ISqlSugarClient db,
        string orderGuid
    )
    {
        var rows = await db.Queryable<WarehouseOrderPickAssignment>()
            .Where(assignment => assignment.OrderGUID == orderGuid)
            .ToListAsync();
        return rows
            .GroupBy(row => row.DetailGUID, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>订单列表用：每张订单的负责人与各自行数，按段号排序。</summary>
    internal static async Task<Dictionary<string, List<WarehousePickingAssigneeDto>>> LoadAssigneesAsync(
        ISqlSugarClient db,
        List<string> orderGuids
    )
    {
        if (orderGuids.Count == 0)
        {
            return new Dictionary<string, List<WarehousePickingAssigneeDto>>(StringComparer.OrdinalIgnoreCase);
        }

        var rows = await db.Queryable<WarehouseOrderPickAssignment>()
            .Where(assignment => orderGuids.Contains(assignment.OrderGUID))
            .GroupBy(assignment => new { assignment.OrderGUID, assignment.PickerUserGuid, assignment.SegmentNo })
            .Select(assignment => new AssigneeCountRow
            {
                OrderGUID = assignment.OrderGUID,
                PickerUserGuid = assignment.PickerUserGuid,
                SegmentNo = assignment.SegmentNo,
                PickerName = SqlFunc.AggregateMax(assignment.PickerName),
                LineCount = SqlFunc.AggregateCount(assignment.DetailGUID),
            })
            .ToListAsync();
        return rows
            .GroupBy(row => row.OrderGUID, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(row => new WarehousePickingAssigneeDto
                    {
                        PickerUserGuid = row.PickerUserGuid,
                        PickerName = row.PickerName,
                        LineCount = row.LineCount,
                        SegmentNo = row.SegmentNo,
                    })
                    .OrderBy(row => row.SegmentNo)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase
            );
    }

    /// <summary>派单用：订单各行及其配货位文本（与拣货单同口径），供按走位顺序分段。</summary>
    internal static async Task<List<WarehouseRouteLine>> LoadRouteLinesAsync(ISqlSugarClient db, string orderGuid)
    {
        var lines = await LoadLinesAsync(db, orderGuid);
        var productCodes = lines
            .Select(line => line.ProductCode?.Trim())
            .Where(code => !string.IsNullOrEmpty(code))
            .Select(code => code!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var locationsByProduct = (await LoadPickLocationsAsync(db, productCodes))
            .GroupBy(location => location.ProductCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => string.Join(
                    ", ",
                    group
                        .Select(location => location.LocationCode)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                ),
                StringComparer.OrdinalIgnoreCase
            );
        return lines
            .Select(line =>
            {
                var productCode = line.ProductCode?.Trim() ?? string.Empty;
                return new WarehouseRouteLine(
                    line.DetailGUID,
                    locationsByProduct.GetValueOrDefault(productCode),
                    line.ItemNumber,
                    productCode,
                    line.Quantity ?? 0,
                    line.ProductName,
                    line.Barcode,
                    line.MinOrderQuantity
                );
            })
            .ToList();
    }

    private sealed class AssigneeCountRow
    {
        public string OrderGUID { get; set; } = string.Empty;
        public string? PickerUserGuid { get; set; }
        public string? PickerName { get; set; }
        public int SegmentNo { get; set; }
        public int LineCount { get; set; }
    }

    /// <summary>商品的配货位文本（与拣货单同口径，多个按编码排序后以逗号连接）；未绑定时为空。</summary>
    internal static async Task<string?> LoadPickLocationTextAsync(ISqlSugarClient db, string? productCode)
    {
        if (string.IsNullOrWhiteSpace(productCode))
        {
            return null;
        }

        var locations = await LoadPickLocationsAsync(db, new List<string> { productCode.Trim() });
        return locations.Count == 0
            ? null
            : string.Join(
                ", ",
                locations
                    .Select(location => location.LocationCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            );
    }

    /// <summary>扫到不在本单的码时，给界面显示“扫到的是什么”；只取第一个命中的商品，仅用于提示。</summary>
    internal static async Task<WarehousePickingCodeLookupDto?> LookupProductByCodeAsync(
        ISqlSugarClient db,
        string code
    )
    {
        var product = await db.Queryable<Product>()
            .Where(item =>
                !item.IsDeleted
                && (item.Barcode == code || item.ItemNumber == code || item.ProductCode == code)
            )
            .Select(item => new WarehousePickingCodeLookupDto
            {
                ProductCode = item.ProductCode ?? string.Empty,
                ProductName = item.ProductName,
                ItemNumber = item.ItemNumber,
                Barcode = item.Barcode,
                ProductImage = item.ProductImage,
            })
            .FirstAsync();
        if (product == null)
        {
            var parentCode = await db.Queryable<ProductSetCode>()
                .Where(item =>
                    !item.IsDeleted
                    && (item.SetBarcode == code || item.SetItemNumber == code || item.SetProductCode == code)
                )
                .Select(item => item.ProductCode)
                .FirstAsync();
            if (string.IsNullOrWhiteSpace(parentCode))
            {
                return null;
            }

            product = await db.Queryable<Product>()
                .Where(item => !item.IsDeleted && item.ProductCode == parentCode)
                .Select(item => new WarehousePickingCodeLookupDto
                {
                    ProductCode = item.ProductCode ?? string.Empty,
                    ProductName = item.ProductName,
                    ItemNumber = item.ItemNumber,
                    Barcode = item.Barcode,
                    ProductImage = item.ProductImage,
                })
                .FirstAsync();
            if (product == null)
            {
                return null;
            }
        }

        var locations = await LoadPickLocationsAsync(db, new List<string> { product.ProductCode });
        product.LocationCode = locations.Count == 0
            ? null
            : string.Join(", ", locations.Select(location => location.LocationCode).Distinct(StringComparer.OrdinalIgnoreCase));
        return product;
    }

    private static void AddLineCodes(
        List<WarehousePickingCodeSource> sources,
        LineRow line,
        string productCode,
        List<SetCodeRow>? setCodes,
        List<MultiCodeRow>? multiCodes,
        IReadOnlyDictionary<string, ProductNameRow> childNames
    )
    {
        var detailGuid = line.DetailGUID;
        sources.Add(new(line.Barcode, WarehousePickingCodeTargets.Line, detailGuid, WarehouseOrderPickMatchKinds.Barcode, null));
        sources.Add(new(line.ItemNumber, WarehousePickingCodeTargets.Line, detailGuid, WarehouseOrderPickMatchKinds.ItemNumber, null));
        sources.Add(new(productCode, WarehousePickingCodeTargets.Line, detailGuid, WarehouseOrderPickMatchKinds.ProductCode, null));

        foreach (var setCode in setCodes ?? new List<SetCodeRow>())
        {
            // 套装子项（SetType=1）按主码计；一品多码（SetType=2）本就是同一商品的别名码。
            var isChild = setCode.SetType == 1;
            var kind = isChild ? WarehouseOrderPickMatchKinds.SetChild : WarehouseOrderPickMatchKinds.MultiCode;
            string? label = null;
            if (isChild && childNames.TryGetValue(setCode.SetProductCode, out var child))
            {
                label = child.ProductName;
            }

            sources.Add(new(setCode.SetBarcode, WarehousePickingCodeTargets.Line, detailGuid, kind, label));
            sources.Add(new(setCode.SetItemNumber, WarehousePickingCodeTargets.Line, detailGuid, kind, label));
            sources.Add(new(setCode.SetProductCode, WarehousePickingCodeTargets.Line, detailGuid, kind, label));
        }

        foreach (var multiCode in multiCodes ?? new List<MultiCodeRow>())
        {
            sources.Add(new(multiCode.MultiBarcode, WarehousePickingCodeTargets.Line, detailGuid, WarehouseOrderPickMatchKinds.MultiCode, null));
            sources.Add(new(multiCode.MultiCodeProductCode, WarehousePickingCodeTargets.Line, detailGuid, WarehouseOrderPickMatchKinds.MultiCode, null));
            sources.Add(new(multiCode.StoreMultiCodeProductCode, WarehousePickingCodeTargets.Line, detailGuid, WarehouseOrderPickMatchKinds.MultiCode, null));
        }
    }

    private static Task<List<LocationRow>> LoadPickLocationsAsync(
        ISqlSugarClient db,
        List<string> productCodes
    )
    {
        if (productCodes.Count == 0)
        {
            return Task.FromResult(new List<LocationRow>());
        }

        // 与订单详情 FillLocationCodesAsync 同口径：只取配货位（LocationType=1）。
        return db.Queryable<ProductLocation>()
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
            .Select((productLocation, location) => new LocationRow
            {
                ProductCode = productLocation.ProductCode!,
                LocationCode = location.LocationCode!,
                LocationBarcode = location.LocationBarcode,
            })
            .ToListAsync();
    }

    private static Task<List<SetCodeRow>> LoadSetCodesAsync(
        ISqlSugarClient db,
        List<string> productCodes
    )
    {
        if (productCodes.Count == 0)
        {
            return Task.FromResult(new List<SetCodeRow>());
        }

        return db.Queryable<ProductSetCode>()
            .Where(code => productCodes.Contains(code.ProductCode) && !code.IsDeleted)
            .Select(code => new SetCodeRow
            {
                ProductCode = code.ProductCode,
                SetProductCode = code.SetProductCode,
                SetItemNumber = code.SetItemNumber,
                SetBarcode = code.SetBarcode,
                SetType = code.SetType,
            })
            .ToListAsync();
    }

    private static Task<List<MultiCodeRow>> LoadStoreMultiCodesAsync(
        ISqlSugarClient db,
        List<string> productCodes,
        string? storeCode
    )
    {
        if (productCodes.Count == 0 || string.IsNullOrWhiteSpace(storeCode))
        {
            return Task.FromResult(new List<MultiCodeRow>());
        }

        // 分店一品多码只在该分店生效，按订单分店过滤。
        return db.Queryable<StoreMultiCodeProduct>()
            .Where(code =>
                code.ProductCode != null
                && productCodes.Contains(code.ProductCode)
                && code.StoreCode == storeCode
                && !code.IsDeleted
            )
            .Select(code => new MultiCodeRow
            {
                ProductCode = code.ProductCode,
                MultiBarcode = code.MultiBarcode,
                MultiCodeProductCode = code.MultiCodeProductCode,
                StoreMultiCodeProductCode = code.StoreMultiCodeProductCode,
            })
            .ToListAsync();
    }

    private static async Task<Dictionary<string, ProductNameRow>> LoadProductNamesAsync(
        ISqlSugarClient db,
        IEnumerable<string> productCodes
    )
    {
        var codes = productCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (codes.Count == 0)
        {
            return new Dictionary<string, ProductNameRow>(StringComparer.OrdinalIgnoreCase);
        }

        var rows = await db.Queryable<Product>()
            .Where(product => product.ProductCode != null && codes.Contains(product.ProductCode) && !product.IsDeleted)
            .Select(product => new ProductNameRow
            {
                ProductCode = product.ProductCode,
                ProductName = product.ProductName,
                ItemNumber = product.ItemNumber,
                Barcode = product.Barcode,
            })
            .ToListAsync();
        return rows
            .Where(row => !string.IsNullOrWhiteSpace(row.ProductCode))
            .GroupBy(row => row.ProductCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    internal static WarehousePickingSessionDto ToSessionDto(WarehouseOrderPickSession? session)
    {
        if (session == null)
        {
            return new WarehousePickingSessionDto { Status = 0 };
        }

        return new WarehousePickingSessionDto
        {
            Status = session.Status,
            StartedAtUtc = session.StartedAtUtc,
            StartedByName = session.StartedByName,
            SubmittedAtUtc = session.SubmittedAtUtc,
            SubmittedByName = session.SubmittedByName,
        };
    }

    private static WarehousePickingParticipantDto ToParticipantDto(WarehouseOrderPickParticipant participant)
    {
        return new WarehousePickingParticipantDto
        {
            PickerUserGuid = participant.PickerUserGuid,
            PickerName = participant.PickerName,
            LastActiveAtUtc = participant.LastActiveAtUtc,
            LastDetailGuid = participant.LastDetailGUID,
        };
    }

    private static List<WarehousePickedByDto> ToPickedBy(IEnumerable<PickTotalRow>? totals)
    {
        return (totals ?? Enumerable.Empty<PickTotalRow>())
            .Where(total => total.Quantity != 0)
            .GroupBy(total => total.PickerUserGuid, StringComparer.OrdinalIgnoreCase)
            .Select(group => new WarehousePickedByDto
            {
                PickerUserGuid = group.Key,
                PickerName = group.Select(total => total.PickerName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? string.Empty,
                Quantity = group.Sum(total => total.Quantity),
            })
            .Where(entry => entry.Quantity != 0)
            .OrderByDescending(entry => entry.Quantity)
            .ToList();
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
