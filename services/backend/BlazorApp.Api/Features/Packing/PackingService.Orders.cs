using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.Packing;

/// <summary>
/// 二期：按订货单装箱（总仓拣货端边拣边装 / 先拣后装）与分店按订单签收。
/// 这些方法是给拣货侧在「拣货事务」里调用的：已有外层事务时直接加入（见 ExecuteInTransactionAsync），
/// 锁序固定为「拣货会话行 → 箱」，本文件内部再取多把锁时按「托盘 → 箱」顺序，与整托签收一致。
/// 不改库存，不回写订货单。
/// </summary>
internal sealed partial class PackingService
{
    // ---- 按订货单装箱 ----

    public async Task<PackingResult<PackingCartonDetailDto>> AdjustOrderCartonItemAsync(
        PackingOperator op,
        string orderGuid,
        string cartonGuid,
        string productCode,
        int delta
    )
    {
        var code = productCode?.Trim();
        if (string.IsNullOrEmpty(code) || string.IsNullOrWhiteSpace(orderGuid) || string.IsNullOrWhiteSpace(cartonGuid))
        {
            return CartonFail(400, PackingErrorCodes.InvalidRequest, "缺少订货单、箱或商品");
        }

        return await ExecuteInTransactionAsync(async () =>
        {
            var (carton, denied) = await LockOrderCartonAsync(op, orderGuid, cartonGuid, "该箱已封箱，不能再改数量；需要先重新开箱");
            if (denied != null)
            {
                return denied;
            }

            var existing = await _db.Queryable<PackCartonItem>()
                .Where(item => item.CartonGUID == carton!.CartonGUID && item.ProductCode == code)
                .FirstAsync();
            var current = existing?.Quantity ?? 0;
            // long 运算避免溢出后绕过上下限。
            var total = (long)current + delta;
            if (total < 0)
            {
                // 箱里不够减：什么都不改，带回箱现状让界面提示「这只箱里只有 N 件」。
                return PackingResult<PackingCartonDetailDto>.Fail(
                    409,
                    PackingErrorCodes.CartonQtyInsufficient,
                    $"这只箱里该商品只有 {current} 件，不够减 {-(long)delta} 件",
                    await BuildCartonDetailAsync(carton!, code)
                );
            }

            if (total > MaxPiecesPerItem)
            {
                return CartonFail(400, PackingErrorCodes.QuantityOutOfRange, "单个商品数量超出上限");
            }

            if (delta != 0)
            {
                await WriteOrderCartonItemAsync(carton!, existing, code, (int)total, op);
            }

            return PackingResult<PackingCartonDetailDto>.Ok(await BuildCartonDetailAsync(carton!, total == 0 ? null : code));
        });
    }

    public async Task<PackingResult<PackingCartonDetailDto>> SetOrderCartonItemQuantityAsync(
        PackingOperator op,
        string orderGuid,
        string cartonGuid,
        string productCode,
        int quantity
    )
    {
        var code = productCode?.Trim();
        if (string.IsNullOrEmpty(code) || string.IsNullOrWhiteSpace(orderGuid) || string.IsNullOrWhiteSpace(cartonGuid))
        {
            return CartonFail(400, PackingErrorCodes.InvalidRequest, "缺少订货单、箱或商品");
        }

        if (quantity < 0 || quantity > MaxPiecesPerItem)
        {
            return CartonFail(400, PackingErrorCodes.QuantityOutOfRange, "数量超出范围");
        }

        return await ExecuteInTransactionAsync(async () =>
        {
            var (carton, denied) = await LockOrderCartonAsync(op, orderGuid, cartonGuid, "该箱已封箱，不能再改数量；需要先重新开箱");
            if (denied != null)
            {
                return denied;
            }

            var existing = await _db.Queryable<PackCartonItem>()
                .Where(item => item.CartonGUID == carton!.CartonGUID && item.ProductCode == code)
                .FirstAsync();
            if (existing == null && quantity > 0)
            {
                // 绝对数量可以直接把箱里没有的商品装进来：商品必须存在，免得手滑的编码写出一行没有名字的明细。
                var exists = await _db.Queryable<Product>().Where(item => !item.IsDeleted && item.ProductCode == code).AnyAsync();
                if (!exists)
                {
                    return CartonFail(404, PackingErrorCodes.CodeNotFound, "找不到该商品");
                }
            }

            // 已经是这个数量（重复提交 / 网络重试）：什么都不写，结果相同。
            if ((existing?.Quantity ?? 0) != quantity)
            {
                await WriteOrderCartonItemAsync(carton!, existing, code, quantity, op);
            }

            return PackingResult<PackingCartonDetailDto>.Ok(await BuildCartonDetailAsync(carton!, quantity == 0 ? null : code));
        });
    }

    public async Task<PackingResult<PackingCartonDetailDto>> SealOrderCartonAsync(
        PackingOperator op,
        string orderGuid,
        string cartonGuid,
        PackingSealRequestDto request,
        string? palletGuid
    )
    {
        if (string.IsNullOrWhiteSpace(orderGuid) || string.IsNullOrWhiteSpace(cartonGuid))
        {
            return CartonFail(400, PackingErrorCodes.InvalidRequest, "缺少订货单或箱");
        }

        var pallet = palletGuid?.Trim();
        return await ExecuteInTransactionAsync(async () =>
        {
            // 要上托盘时先锁托盘再锁箱：与「把箱放上托盘」的锁序一致，同一只箱被两台设备同时操作也不会互相等死。
            if (!string.IsNullOrEmpty(pallet))
            {
                await LockPalletAsync(pallet);
            }

            // 已封箱的重复提交不在这里拦：SealCartonAsync 对已封箱是幂等的，客户端可以照常拿到数据重新打印标签。
            var (carton, denied) = await LockOrderCartonAsync(op, orderGuid, cartonGuid, null);
            if (denied != null)
            {
                return denied;
            }

            // 下面两步都加入本事务：任何一步失败，本方法返回失败结果，由事务整体回滚（封箱也撤销）。
            var sealedResult = await SealCartonAsync(op, carton!.CartonGUID, request);
            if (!sealedResult.Success || string.IsNullOrEmpty(pallet))
            {
                return sealedResult;
            }

            var added = await AddCartonToPalletAsync(op, pallet, new PackingAddCartonRequestDto { Code = carton.CartonGUID });
            if (!added.Success)
            {
                // 托盘不存在 / 不是总仓的 / 已封托 / 去向不一致等：错误码、状态码、说明原样返回。
                return CartonFail(added.StatusCode, added.ErrorCode ?? PackingErrorCodes.InvalidRequest, added.Message ?? "放上托盘失败");
            }

            var refreshed = await _db.Queryable<PackCarton>().Where(item => item.CartonGUID == carton.CartonGUID).FirstAsync();
            return PackingResult<PackingCartonDetailDto>.Ok(await BuildCartonDetailAsync(refreshed ?? carton, null));
        });
    }

    public async Task<PackingOrderSnapshot> GetOrderSnapshotAsync(string orderGuid)
    {
        var snapshot = new PackingOrderSnapshot();
        // 排序明确为创建时间升序、再按箱号（按序数比较，不依赖数据库排序规则）。
        var cartons = (await _db.Queryable<PackCarton>()
                .Where(carton => carton.OrderGUID == orderGuid && carton.Status != PackCartonStatuses.Voided)
                .ToListAsync())
            .OrderBy(carton => carton.CreatedAtUtc)
            .ThenBy(carton => carton.CartonNo, StringComparer.Ordinal)
            .ToList();
        if (cartons.Count == 0)
        {
            return snapshot;
        }

        var cartonGuids = cartons.Select(carton => carton.CartonGUID).ToList();
        var items = await _db.Queryable<PackCartonItem>()
            .Where(item => cartonGuids.Contains(item.CartonGUID))
            .Select(item => new { item.CartonGUID, item.ProductCode, item.Quantity })
            .ToListAsync();
        var palletGuids = cartons.Where(carton => carton.PalletGUID != null).Select(carton => carton.PalletGUID!).Distinct().ToList();
        var palletNos = palletGuids.Count == 0
            ? new Dictionary<string, string>()
            : (await _db.Queryable<PackPallet>()
                    .Where(pallet => palletGuids.Contains(pallet.PalletGUID))
                    .Select(pallet => new { pallet.PalletGUID, pallet.PalletNo })
                    .ToListAsync())
                .ToDictionary(pallet => pallet.PalletGUID, pallet => pallet.PalletNo);

        var itemsByCarton = items.ToLookup(item => item.CartonGUID);
        snapshot.Cartons = cartons.Select(carton => new PackingOrderCartonRow
        {
            CartonGuid = carton.CartonGUID,
            CartonNo = carton.CartonNo,
            Status = carton.Status,
            SkuCount = itemsByCarton[carton.CartonGUID].Count(),
            PieceCount = itemsByCarton[carton.CartonGUID].Sum(item => item.Quantity),
            PalletNo = carton.PalletGUID == null ? null : palletNos.GetValueOrDefault(carton.PalletGUID),
            CreatedByUserGuid = carton.CreatedByUserGuid,
            CreatedByName = carton.CreatedByName,
            CreatedAtUtc = carton.CreatedAtUtc,
            Items = itemsByCarton[carton.CartonGUID]
                .OrderBy(item => item.ProductCode, StringComparer.Ordinal)
                .Select(item => new PackingOrderCartonItemRow { ProductCode = item.ProductCode, Quantity = item.Quantity })
                .ToList(),
        }).ToList();
        foreach (var group in items.GroupBy(item => item.ProductCode, StringComparer.OrdinalIgnoreCase))
        {
            snapshot.PackedByProduct[group.Key] = group.Sum(item => item.Quantity);
        }

        return snapshot;
    }

    public async Task<PackingResult<int>> VoidEmptyOrderCartonsAsync(PackingOperator op, string orderGuid)
    {
        if (!op.Has(PackOwners.Warehouse))
        {
            return PackingResult<int>.Fail(403, PackingErrorCodes.NotAllowed, "只有总仓可以处理订货单的箱");
        }

        return await ExecuteInTransactionAsync(async () =>
        {
            var candidates = await _db.Queryable<PackCarton>()
                .Where(carton =>
                    carton.OrderGUID == orderGuid
                    && carton.OwnerCode == PackOwners.Warehouse
                    && carton.Status == PackCartonStatuses.Packing
                )
                .OrderBy(carton => carton.CartonGUID)
                .Select(carton => carton.CartonGUID)
                .ToListAsync();
            var voided = 0;
            foreach (var cartonGuid in candidates)
            {
                // 加锁后再判一次：别的设备可能刚往这只「空箱」里装了商品，或已经把它封了。
                var carton = await LockCartonAsync(cartonGuid);
                if (carton == null || carton.Status != PackCartonStatuses.Packing)
                {
                    continue;
                }

                var hasItems = await _db.Queryable<PackCartonItem>().Where(item => item.CartonGUID == cartonGuid).AnyAsync();
                if (hasItems)
                {
                    continue;
                }

                var now = UtcNow();
                carton.Status = PackCartonStatuses.Voided;
                carton.UpdatedAtUtc = now;
                await _db.Updateable(carton).ExecuteCommandAsync();
                await AddEventAsync(PackTargetTypes.Carton, carton.CartonGUID, PackEventActions.Voided, "提交拣货时自动作废空箱", op, now);
                voided++;
            }

            return PackingResult<int>.Ok(voided);
        });
    }

    /// <summary>
    /// 锁箱并校验：必须是本订货单名下、总仓持有的箱，操作人要能操作总仓。
    /// 别的订单的箱、分店持有的箱一律按「找不到」处理，不暴露它们存在；requirePackingMessage 为空表示不要求仍在装箱中。
    /// </summary>
    private async Task<(PackCarton? Carton, PackingResult<PackingCartonDetailDto>? Denied)> LockOrderCartonAsync(
        PackingOperator op,
        string orderGuid,
        string cartonGuid,
        string? requirePackingMessage
    )
    {
        var carton = await LockCartonAsync(cartonGuid.Trim());
        if (carton != null && (!PackOwners.IsWarehouse(carton.OwnerCode) || !SameCode(carton.OrderGUID, orderGuid.Trim())))
        {
            carton = null;
        }

        var denied = CheckEditable(op, carton, requirePacking: requirePackingMessage != null, requirePackingMessage);
        return (carton, denied);
    }

    /// <summary>把箱内某商品写成指定件数：0 删除该行，否则新增或更新；同时刷新箱的更新时间。</summary>
    private async Task WriteOrderCartonItemAsync(PackCarton carton, PackCartonItem? existing, string productCode, int quantity, PackingOperator op)
    {
        var now = UtcNow();
        if (quantity == 0)
        {
            if (existing != null)
            {
                await _db.Deleteable<PackCartonItem>()
                    .Where(item => item.CartonGUID == carton.CartonGUID && item.ProductCode == productCode)
                    .ExecuteCommandAsync();
            }

            await TouchCartonAsync(carton, now);
            return;
        }

        await UpsertItemAsync(carton, existing, productCode, quantity, op, now);
    }

    // ---- 分店按订单签收 ----

    public async Task<PackingResult<PackingOrderReceiveDto>> GetOrderReceiveAsync(PackingOperator op, string orderRef)
    {
        var scope = await ResolveReceiveOrderAsync(op, orderRef);
        return scope.Denied ?? PackingResult<PackingOrderReceiveDto>.Ok(await BuildOrderReceiveAsync(scope.Order!, scope.StoreCode!));
    }

    public async Task<PackingResult<PackingOrderReceiveDto>> ReceiveOrderAsync(PackingOperator op, string orderRef, int? expectedCartonCount)
    {
        var scope = await ResolveReceiveOrderAsync(op, orderRef);
        if (scope.Denied != null)
        {
            return scope.Denied;
        }

        var order = scope.Order!;
        var storeCode = scope.StoreCode!;
        return await ExecuteInTransactionAsync(async () =>
        {
            var candidates = await _db.Queryable<PackCarton>()
                .Where(carton =>
                    carton.OrderGUID == order.OrderGUID
                    && carton.Status == PackCartonStatuses.InTransit
                    && carton.DestinationCode == storeCode
                )
                .Select(carton => new { carton.CartonGUID, carton.PalletGUID })
                .ToListAsync();

            // 锁序与整托签收一致：先托盘、后箱，各自按编号排序，多台设备同时签收同一订单时不会互相等死。
            foreach (var palletGuid in candidates
                         .Where(carton => carton.PalletGUID != null)
                         .Select(carton => carton.PalletGUID!)
                         .Distinct()
                         .OrderBy(guid => guid, StringComparer.Ordinal))
            {
                await LockPalletAsync(palletGuid);
            }

            var inTransit = new List<PackCarton>();
            foreach (var cartonGuid in candidates.Select(carton => carton.CartonGUID).OrderBy(guid => guid, StringComparer.Ordinal))
            {
                var carton = await LockCartonAsync(cartonGuid);
                if (carton is { Status: PackCartonStatuses.InTransit } && SameCode(carton.DestinationCode, storeCode))
                {
                    inTransit.Add(carton);
                }
            }

            // 与整托签收同一规则：必须先数箱，数量和系统记录对不上就什么都不签，改为逐箱 / 逐托核对。
            if (expectedCartonCount == null || expectedCartonCount != inTransit.Count)
            {
                var detail = await BuildOrderReceiveAsync(order, storeCode);
                detail.ActualInTransitCartonCount = inTransit.Count;
                return PackingResult<PackingOrderReceiveDto>.Fail(
                    409,
                    PackingErrorCodes.CountMismatch,
                    expectedCartonCount == null
                        ? "签收前请先确认实到箱数"
                        : $"实到 {expectedCartonCount} 箱，系统记录是 {inTransit.Count} 箱，请逐箱扫码核对",
                    detail
                );
            }

            if (inTransit.Count == 0)
            {
                return PackingResult<PackingOrderReceiveDto>.Fail(409, PackingErrorCodes.NotInTransit, "这张订单没有发给本店、还在路上的箱");
            }

            var now = UtcNow();
            var via = $"按订单 {Truncate(order.OrderNo, 40) ?? order.OrderGUID} 签收";
            foreach (var carton in inTransit)
            {
                await ReceiveCartonCoreAsync(carton, storeCode, null, op, now, via);
            }

            // 托盘上的箱：沿用整托签收的收尾逻辑——托盘上的箱都签完托盘才换所属地点，否则托盘保持在路上并记「部分签收」。
            foreach (var group in inTransit.Where(carton => carton.PalletGUID != null).GroupBy(carton => carton.PalletGUID!))
            {
                await FinishPalletIfAllReceivedAsync(group.Key, storeCode, null, op, now, group.Count(), via);
            }

            return PackingResult<PackingOrderReceiveDto>.Ok(await BuildOrderReceiveAsync(order, storeCode));
        });
    }

    /// <summary>
    /// 订单引用既可以是 OrderGUID，也可以是订单号（订单条码 HBSO: 后面是订单号）：先按 OrderGUID 精确匹配，没有再按订单号精确匹配
    /// （去首尾空格、忽略大小写）。订货单必须存在，且它的分店在操作人的地点范围内；否则统一按「找不到订货单」处理，
    /// 不暴露其它分店的订单是否存在。订单号不唯一时只在操作人的分店范围内取：
    /// 范围内仍有多张，优先取已经有装箱（非作废箱）关联的，再取下单时间最新的。
    /// </summary>
    private async Task<(WareHouseOrder? Order, string? StoreCode, PackingResult<PackingOrderReceiveDto>? Denied)> ResolveReceiveOrderAsync(
        PackingOperator op,
        string orderRef
    )
    {
        var notFound = (Order: (WareHouseOrder?)null, StoreCode: (string?)null, Denied: (PackingResult<PackingOrderReceiveDto>?)PackingResult<PackingOrderReceiveDto>.Fail(404, PackingErrorCodes.OrderNotFound, "找不到该订货单"));
        var reference = orderRef?.Trim();
        var storeOwners = op.Owners.Where(item => !PackOwners.IsWarehouse(item.Code)).ToList();
        // 只有总仓范围的账号没有任何分店订单可签，省掉查询。
        if (string.IsNullOrEmpty(reference) || storeOwners.Count == 0)
        {
            return notFound;
        }

        PackingOwner? OwnerOf(WareHouseOrder order) =>
            string.IsNullOrWhiteSpace(order.StoreCode) ? null : storeOwners.FirstOrDefault(item => SameCode(item.Code, order.StoreCode));

        var byGuid = await LoadOrderAsync(reference);
        if (byGuid != null)
        {
            var guidOwner = OwnerOf(byGuid);
            return guidOwner == null ? notFound : (byGuid, guidOwner.Code, null);
        }

        // SQL Server 默认排序规则不区分大小写，等值查询即可命中（并用上 OrderNo 的唯一索引）；
        // 区分大小写的库（如测试用 SQLite）等值查不到时，再按大写比较补一次；
        // SQL Server 上不补：UPPER(列) 用不上索引，店员扫到不存在的订单号会触发整表扫描，而它本来就不区分大小写，没命中就是真没有。
        var candidates = await _db.Queryable<WareHouseOrder>()
            .Where(order => !order.IsDeleted && order.OrderNo == reference)
            .ToListAsync();
        if (candidates.Count == 0 && !IsSqlServer)
        {
            var upper = reference.ToUpperInvariant();
            candidates = await _db.Queryable<WareHouseOrder>()
                .Where(order => !order.IsDeleted && order.OrderNo != null && order.OrderNo.ToUpper() == upper)
                .ToListAsync();
        }

        var inScope = candidates
            .Select(order => (Order: order, Owner: OwnerOf(order)))
            .Where(item => item.Owner != null)
            .ToList();
        if (inScope.Count == 0)
        {
            return notFound;
        }

        if (inScope.Count > 1)
        {
            // OrderNo 的唯一索引是启动时尽力创建的，不能保证没有重号：同号多张时先看谁有装箱关联。
            var guids = inScope.Select(item => item.Order.OrderGUID).ToList();
            var withCartons = (await _db.Queryable<PackCarton>()
                    .Where(carton => carton.OrderGUID != null && guids.Contains(carton.OrderGUID) && carton.Status != PackCartonStatuses.Voided)
                    .Select(carton => carton.OrderGUID)
                    .Distinct()
                    .ToListAsync())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (inScope.Any(item => withCartons.Contains(item.Order.OrderGUID)))
            {
                inScope = inScope.Where(item => withCartons.Contains(item.Order.OrderGUID)).ToList();
            }
        }

        var picked = inScope
            .OrderByDescending(item => item.Order.OrderDate ?? DateTime.MinValue)
            .ThenBy(item => item.Order.OrderGUID, StringComparer.Ordinal)
            .First();
        return (picked.Order, picked.Owner!.Code, null);
    }

    /// <summary>
    /// 该订单发给本店的箱：在路上的（去向是本店），以及已经签收、现在归属本店的。
    /// 仓库里还在装 / 还没发出的箱不算（对分店来说还没有发货）。
    /// </summary>
    private async Task<PackingOrderReceiveDto> BuildOrderReceiveAsync(WareHouseOrder order, string storeCode)
    {
        var cartons = await _db.Queryable<PackCarton>()
            .Where(carton =>
                carton.OrderGUID == order.OrderGUID
                && carton.Status != PackCartonStatuses.Voided
                && ((carton.Status == PackCartonStatuses.InTransit && carton.DestinationCode == storeCode)
                    || (carton.OwnerCode == storeCode && carton.ReceivedAtUtc != null))
            )
            .OrderBy(carton => carton.CartonNo)
            .ToListAsync();
        var summaries = (await BuildCartonSummariesAsync(cartons)).ToDictionary(summary => summary.CartonGuid);

        var cartonGuids = cartons.Select(carton => carton.CartonGUID).ToList();
        var shipped = cartonGuids.Count == 0
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : (await _db.Queryable<PackCartonItem>()
                    .Where(item => cartonGuids.Contains(item.CartonGUID))
                    .Select(item => new { item.ProductCode, item.Quantity })
                    .ToListAsync())
                .GroupBy(item => item.ProductCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity), StringComparer.OrdinalIgnoreCase);

        // 订货 / 配货数：退货行（AllocQuantity 为负）不计。
        var details = await _db.Queryable<WareHouseOrderDetails>()
            .Where(detail =>
                detail.OrderGUID == order.OrderGUID
                && !detail.IsDeleted
                && detail.ProductCode != null
                && (detail.AllocQuantity == null || detail.AllocQuantity >= 0)
            )
            .Select(detail => new { detail.ProductCode, detail.Quantity, detail.AllocQuantity })
            .ToListAsync();
        var ordered = details
            .GroupBy(detail => detail.ProductCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (
                    Ordered: group.Sum(detail => detail.Quantity ?? 0),
                    Alloc: group.Any(detail => detail.AllocQuantity != null) ? group.Sum(detail => detail.AllocQuantity ?? 0) : (decimal?)null
                ),
                StringComparer.OrdinalIgnoreCase
            );

        var productCodes = ordered.Keys.Concat(shipped.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var products = (await LoadProductsAsync(productCodes))
            .GroupBy(product => product.ProductCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return new PackingOrderReceiveDto
        {
            OrderGuid = order.OrderGUID,
            OrderNo = order.OrderNo,
            StoreCode = storeCode,
            Cartons = cartons.Select(carton =>
            {
                var summary = summaries[carton.CartonGUID];
                return new PackingOrderReceiveCartonDto
                {
                    CartonGuid = carton.CartonGUID,
                    CartonNo = carton.CartonNo,
                    Status = carton.Status,
                    PalletNo = summary.PalletNo,
                    SkuCount = summary.SkuCount,
                    PieceCount = summary.PieceCount,
                };
            }).ToList(),
            InTransitCount = cartons.Count(carton => carton.Status == PackCartonStatuses.InTransit),
            ReceivedCount = cartons.Count(carton => carton.Status == PackCartonStatuses.Sealed && SameCode(carton.OwnerCode, storeCode)),
            Products = productCodes
                .Select(code =>
                {
                    var product = products.GetValueOrDefault(code);
                    var line = ordered.GetValueOrDefault(code);
                    return new PackingOrderReceiveProductDto
                    {
                        ProductCode = code,
                        ProductName = product?.ProductName,
                        ItemNumber = product?.ItemNumber,
                        OrderedPieces = line.Ordered,
                        AllocPieces = line.Alloc,
                        ShippedPieces = shipped.GetValueOrDefault(code),
                    };
                })
                .OrderBy(product => product.ItemNumber ?? product.ProductCode, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }
}
