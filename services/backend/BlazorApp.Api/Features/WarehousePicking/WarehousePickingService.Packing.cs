using BlazorApp.Api.Features.Packing;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.WarehousePicking;

/// <summary>
/// 拣货与装箱联动（二期）：拣货端用已验明的拣货人调打包服务，不让客户端直接调打包接口。
/// 箱的写入都以「总仓拣货人」身份进行（只能操作总仓，不是经理 / 管理员），箱上的创建人、操作记录里的操作人都是拣货人姓名。
/// 不改库存，不回写订货单状态。
/// </summary>
internal sealed partial class WarehousePickingService
{
    private PackingOperator PackingOperatorOf(WarehousePickerContext picker) =>
        packingAccessService.WarehousePickerOperator(picker.UserGuid, picker.Name);

    // ---- 边拣边装：同步到箱 ----

    /// <summary>把这次拣货的件数变化同步到箱（正加负减）；在拣货事务里调用，打包服务加入同一事务。</summary>
    private Task<PackingResult<PackingCartonDetailDto>> SyncCartonAsync(
        string orderGuid,
        PickLine line,
        WarehousePickerContext picker,
        string cartonGuid,
        int delta
    )
    {
        if (string.IsNullOrWhiteSpace(line.ProductCode))
        {
            return Task.FromResult(PackingResult<PackingCartonDetailDto>.Fail(
                400,
                PackingErrorCodes.InvalidRequest,
                "该订单行没有商品编码，不能装箱"
            ));
        }

        return packingService.AdjustOrderCartonItemAsync(PackingOperatorOf(picker), orderGuid, cartonGuid, line.ProductCode, delta);
    }

    private static WarehousePickingCartonSyncDto? ToCartonSync(PackingCartonDetailDto? carton, string? productCode)
    {
        if (carton == null)
        {
            return null;
        }

        return new WarehousePickingCartonSyncDto
        {
            CartonGuid = carton.CartonGuid,
            CartonNo = carton.CartonNo,
            SkuCount = carton.SkuCount,
            PieceCount = carton.PieceCount,
            ItemPieces = carton.Items
                .Where(item => string.Equals(item.ProductCode, productCode?.Trim(), StringComparison.OrdinalIgnoreCase))
                .Sum(item => item.Quantity),
        };
    }

    private static WarehousePickingResult<TTarget> FromPackingFailure<TSource, TTarget>(PackingResult<TSource> failure) =>
        WarehousePickingResult<TTarget>.Fail(failure.StatusCode, failure.ErrorCode!, failure.Message!);

    // ---- 提交对账 ----

    /// <summary>提交对账的结果：Failure 不为空表示不能提交；没有任何箱时三项都为空，提交照旧。</summary>
    private sealed record ReconcileOutcome(
        WarehousePickingResult<WarehousePickingSubmitResultDto>? Failure,
        int? CartonCount,
        int? PackedPieces
    );

    /// <summary>
    /// 订单名下有非作废的箱时的提交对账：
    /// (a) 先把「装箱中且没有商品」的空箱自动作废；(b) 仍有装箱中的箱 → 409 PACKING_OPEN_CARTONS；
    /// (c) 按商品比较「拣货合计」与「箱内合计」，对不上 → 409 PACKING_NOT_RECONCILED 并带回每个差异商品。
    /// 没有任何非作废的箱（包括空箱被自动作废后一只不剩）时完全保持旧行为。
    /// </summary>
    private async Task<ReconcileOutcome> ReconcilePackingAsync(
        string orderGuid,
        WarehousePickerContext picker,
        List<WareHouseOrderDetails> details
    )
    {
        var noCartons = new ReconcileOutcome(null, null, null);
        var snapshot = await packingService.GetOrderSnapshotAsync(orderGuid);
        if (snapshot.Cartons.Count == 0)
        {
            return noCartons;
        }

        var voided = await packingService.VoidEmptyOrderCartonsAsync(PackingOperatorOf(picker), orderGuid);
        if (!voided.Success)
        {
            return new ReconcileOutcome(
                FromPackingFailure<int, WarehousePickingSubmitResultDto>(voided),
                null,
                null
            );
        }

        if (voided.Data > 0)
        {
            snapshot = await packingService.GetOrderSnapshotAsync(orderGuid);
            if (snapshot.Cartons.Count == 0)
            {
                return noCartons;
            }
        }

        var open = snapshot.Cartons.Where(carton => carton.Status == PackCartonStatuses.Packing).ToList();
        if (open.Count > 0)
        {
            return new ReconcileOutcome(
                WarehousePickingResult<WarehousePickingSubmitResultDto>.Fail(
                    409,
                    WarehousePickingErrorCodes.PackingOpenCartons,
                    $"还有 {open.Count} 只箱没有封箱：{string.Join("、", open.Select(carton => carton.CartonNo))}，请先封箱",
                    new WarehousePickingSubmitResultDto
                    {
                        OpenCartons = open.Select(carton => new WarehousePickingOpenCartonDto
                        {
                            CartonGuid = carton.CartonGuid,
                            CartonNo = carton.CartonNo,
                            SkuCount = carton.SkuCount,
                            PieceCount = carton.PieceCount,
                        }).ToList(),
                    }
                ),
                null,
                null
            );
        }

        var picked = await LoadPickedByProductAsync(orderGuid);
        var mismatchCodes = picked.Keys
            .Concat(snapshot.PackedByProduct.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(code => (picked.GetValueOrDefault(code)?.Picked ?? 0) != snapshot.PackedByProduct.GetValueOrDefault(code))
            .ToList();
        if (mismatchCodes.Count > 0)
        {
            var names = await LoadProductNamesAsync(mismatchCodes);
            var mismatches = mismatchCodes
                .Select(code =>
                {
                    var pickedQuantity = picked.GetValueOrDefault(code)?.Picked ?? 0;
                    var packedQuantity = snapshot.PackedByProduct.GetValueOrDefault(code);
                    var detailGuids = details
                        .Where(detail => string.Equals(detail.ProductCode?.Trim(), code, StringComparison.OrdinalIgnoreCase))
                        .Select(detail => detail.DetailGUID)
                        .Concat(picked.GetValueOrDefault(code)?.DetailGuids ?? new HashSet<string>())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    names.TryGetValue(code, out var name);
                    return new WarehousePickingReconcileMismatchDto
                    {
                        ProductCode = code,
                        ProductName = name?.ProductName,
                        ItemNumber = name?.ItemNumber,
                        DetailGuids = detailGuids,
                        Picked = pickedQuantity,
                        Packed = packedQuantity,
                        Diff = pickedQuantity - packedQuantity,
                    };
                })
                .OrderBy(mismatch => mismatch.ItemNumber ?? mismatch.ProductCode, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new ReconcileOutcome(
                WarehousePickingResult<WarehousePickingSubmitResultDto>.Fail(
                    409,
                    WarehousePickingErrorCodes.PackingNotReconciled,
                    $"有 {mismatches.Count} 个商品的拣货数量与装箱数量对不上，请先核对",
                    new WarehousePickingSubmitResultDto { Mismatches = mismatches }
                ),
                null,
                null
            );
        }

        return new ReconcileOutcome(null, snapshot.Cartons.Count, snapshot.PackedByProduct.Values.Sum());
    }

    private sealed class PickedProduct
    {
        public int Picked { get; set; }
        public HashSet<string> DetailGuids { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class PickedRow
    {
        public string DetailGUID { get; set; } = string.Empty;
        public string ProductCode { get; set; } = string.Empty;
        public int Quantity { get; set; }
    }

    private sealed class ProductNameRow
    {
        public string? ProductCode { get; set; }
        public string? ProductName { get; set; }
        public string? ItemNumber { get; set; }
    }

    /// <summary>
    /// 按商品汇总拣货记录：每个订单行先按记录求和并下限取 0（与提交时写入配货数的口径一致），再按商品相加。
    /// 会话已提交时同样按记录合计。
    /// </summary>
    private async Task<Dictionary<string, PickedProduct>> LoadPickedByProductAsync(string orderGuid)
    {
        var rows = await _db.Queryable<WarehouseOrderPickRecord>()
            .Where(record => record.OrderGUID == orderGuid)
            .GroupBy(record => new { record.DetailGUID, record.ProductCode })
            .Select(record => new PickedRow
            {
                DetailGUID = record.DetailGUID,
                ProductCode = record.ProductCode,
                Quantity = SqlFunc.AggregateSum(record.QuantityDelta),
            })
            .ToListAsync();
        var result = new Dictionary<string, PickedProduct>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Where(row => !string.IsNullOrWhiteSpace(row.ProductCode)))
        {
            var code = row.ProductCode.Trim();
            if (!result.TryGetValue(code, out var product))
            {
                product = new PickedProduct();
                result[code] = product;
            }

            product.Picked += Math.Max(0, row.Quantity);
            product.DetailGuids.Add(row.DetailGUID);
        }

        return result;
    }

    private async Task<Dictionary<string, ProductNameRow>> LoadProductNamesAsync(List<string> productCodes)
    {
        if (productCodes.Count == 0)
        {
            return new Dictionary<string, ProductNameRow>(StringComparer.OrdinalIgnoreCase);
        }

        var rows = await _db.Queryable<Product>()
            .Where(product => !product.IsDeleted && product.ProductCode != null && productCodes.Contains(product.ProductCode))
            .Select(product => new ProductNameRow
            {
                ProductCode = product.ProductCode,
                ProductName = product.ProductName,
                ItemNumber = product.ItemNumber,
            })
            .ToListAsync();
        // Product.ProductCode 没有唯一约束，历史重复商品只取第一条。
        return rows
            .Where(row => !string.IsNullOrWhiteSpace(row.ProductCode))
            .GroupBy(row => row.ProductCode!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    // ---- 装箱进度 ----

    public async Task<WarehousePickingResult<WarehousePickingPackProgressDto>> GetPackProgressAsync(
        string orderGuid,
        WarehousePickerContext? picker
    )
    {
        var order = await WarehousePickingQueries.LoadOrderAsync(_db, orderGuid);
        if (order == null)
        {
            return WarehousePickingResult<WarehousePickingPackProgressDto>.Fail(404, WarehousePickingErrorCodes.OrderNotFound, "找不到该订单");
        }

        var session = await WarehousePickingQueries.LoadSessionAsync(_db, orderGuid);
        var lineRows = await WarehousePickingQueries.LoadLinesAsync(_db, orderGuid);
        var picked = await LoadPickedByProductAsync(orderGuid);
        var snapshot = await packingService.GetOrderSnapshotAsync(orderGuid);

        // 订货件数按商品聚合（同一商品可能有多个订单行）。
        var ordered = lineRows
            .Where(row => !string.IsNullOrWhiteSpace(row.ProductCode))
            .GroupBy(row => row.ProductCode!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (
                    Ordered: group.Sum(row => row.Quantity ?? 0),
                    Name: group.Select(row => row.ProductName).FirstOrDefault(name => name != null),
                    ItemNumber: group.Select(row => row.ItemNumber).FirstOrDefault(item => item != null)
                ),
                StringComparer.OrdinalIgnoreCase
            );
        var codes = ordered.Keys
            .Concat(picked.Keys)
            .Concat(snapshot.PackedByProduct.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // 只在箱里（订单上没有这行）的商品，名称单独查。
        var extraNames = await LoadProductNamesAsync(codes.Where(code => !ordered.ContainsKey(code)).ToList());

        var lines = codes
            .Select(code =>
            {
                var line = ordered.GetValueOrDefault(code);
                extraNames.TryGetValue(code, out var extra);
                return new WarehousePickingPackProgressLineDto
                {
                    ProductCode = code,
                    ProductName = line.Name ?? extra?.ProductName,
                    ItemNumber = line.ItemNumber ?? extra?.ItemNumber,
                    OrderedPieces = line.Ordered,
                    PickedPieces = picked.GetValueOrDefault(code)?.Picked ?? 0,
                    PackedPieces = snapshot.PackedByProduct.GetValueOrDefault(code),
                };
            })
            .OrderBy(line => line.ItemNumber ?? line.ProductCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var openCartons = snapshot.Cartons.Where(carton => carton.Status == PackCartonStatuses.Packing).ToList();
        var nonEmptyOpen = openCartons.Count(carton => carton.SkuCount > 0);
        return WarehousePickingResult<WarehousePickingPackProgressDto>.Ok(new WarehousePickingPackProgressDto
        {
            OrderGuid = order.OrderGUID,
            OrderNo = order.OrderNo,
            StoreCode = order.StoreCode,
            StoreName = order.StoreName,
            OrderDate = order.OrderDate,
            FlowStatus = order.FlowStatus ?? 0,
            SessionStatus = session?.Status,
            Lines = lines,
            Cartons = snapshot.Cartons.Select(carton => new WarehousePickingPackProgressCartonDto
            {
                CartonGuid = carton.CartonGuid,
                CartonNo = carton.CartonNo,
                Status = carton.Status,
                SkuCount = carton.SkuCount,
                PieceCount = carton.PieceCount,
                PalletNo = carton.PalletNo,
                CreatedByName = carton.CreatedByName,
                IsMine = picker != null && string.Equals(carton.CreatedByUserGuid, picker.UserGuid, StringComparison.OrdinalIgnoreCase),
            }).ToList(),
            OpenCartonCount = openCartons.Count,
            EmptyCartonCount = openCartons.Count - nonEmptyOpen,
            SealedCartonCount = snapshot.Cartons.Count(carton => carton.Status != PackCartonStatuses.Packing),
            Reconciled = nonEmptyOpen == 0 && lines.All(line => line.PickedPieces == line.PackedPieces),
        });
    }

    // ---- 箱：新建、封箱、先拣后装 ----

    public async Task<WarehousePickingResult<PackingCartonDetailDto>> CreateCartonAsync(
        string orderGuid,
        WarehousePickingCreateCartonRequestDto request,
        WarehousePickerContext picker
    )
    {
        var order = await WarehousePickingQueries.LoadOrderAsync(_db, orderGuid);
        if (order == null)
        {
            return WarehousePickingResult<PackingCartonDetailDto>.Fail(404, WarehousePickingErrorCodes.OrderNotFound, "找不到该订单");
        }

        // 购物车（草稿）里的订货单还没提交，不能装箱；已提交 / 配货中 / 已完成都可以（提交拣货后仍能「先拣后装」）。
        if ((order.FlowStatus ?? 0) == 0)
        {
            return WarehousePickingResult<PackingCartonDetailDto>.Fail(409, WarehousePickingErrorCodes.OrderNotPickable, "该订单还没有提交，不能装箱");
        }

        var created = await packingService.CreateCartonAsync(
            PackingOperatorOf(picker),
            new PackingCreateCartonRequestDto
            {
                OwnerCode = PackOwners.Warehouse,
                OrderGuid = order.OrderGUID,
                // 不传去向（DestinationType=0）时，打包服务默认发往订货单的分店。
                DestinationType = request.DestinationType,
                DestinationCode = request.DestinationCode,
                CarrierGuid = request.CarrierGuid,
            }
        );
        return ToPickingResult(created);
    }

    public async Task<WarehousePickingResult<PackingCartonDetailDto>> SealCartonAsync(
        string orderGuid,
        string cartonGuid,
        WarehousePickingSealCartonRequestDto request,
        WarehousePickerContext picker
    )
    {
        var sealedResult = await packingService.SealOrderCartonAsync(
            PackingOperatorOf(picker),
            orderGuid,
            cartonGuid,
            new PackingSealRequestDto { WeightKg = request.WeightKg },
            request.PalletGuid
        );
        return ToPickingResult(sealedResult);
    }

    public async Task<WarehousePickingResult<WarehousePickingSetCartonItemResultDto>> SetCartonItemAsync(
        string orderGuid,
        string cartonGuid,
        string productCode,
        WarehousePickingSetCartonItemRequestDto request,
        WarehousePickerContext picker
    )
    {
        var code = productCode?.Trim();
        var result = await packingService.SetOrderCartonItemQuantityAsync(
            PackingOperatorOf(picker),
            orderGuid,
            cartonGuid,
            code ?? string.Empty,
            request.Quantity
        );
        if (!result.Success)
        {
            return FromPackingFailure<PackingCartonDetailDto, WarehousePickingSetCartonItemResultDto>(result);
        }

        // 超装只提示不拦截：该商品全订单装箱合计超过拣货合计时告诉界面多了几件。
        var snapshot = await packingService.GetOrderSnapshotAsync(orderGuid);
        var picked = await LoadPickedByProductAsync(orderGuid);
        var packedTotal = snapshot.PackedByProduct.GetValueOrDefault(code ?? string.Empty);
        var pickedTotal = picked.GetValueOrDefault(code ?? string.Empty)?.Picked ?? 0;
        var overBy = Math.Max(0, packedTotal - pickedTotal);
        return WarehousePickingResult<WarehousePickingSetCartonItemResultDto>.Ok(new WarehousePickingSetCartonItemResultDto
        {
            Carton = result.Data!,
            OverPacked = overBy > 0,
            OverPackedBy = overBy,
        });
    }

    private static WarehousePickingResult<PackingCartonDetailDto> ToPickingResult(PackingResult<PackingCartonDetailDto> result) =>
        result.Success
            ? WarehousePickingResult<PackingCartonDetailDto>.Ok(result.Data!)
            : WarehousePickingResult<PackingCartonDetailDto>.Fail(result.StatusCode, result.ErrorCode!, result.Message!, result.Data);
}
