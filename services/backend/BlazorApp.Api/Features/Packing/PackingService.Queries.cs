using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.Packing;

internal sealed partial class PackingService
{
    // ---- 扫码识别、位置查询、操作记录 ----

    public async Task<PackingResult<PackingResolveDto>> ResolveCodeAsync(PackingOperator op, string? code)
    {
        var normalized = code?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(normalized))
        {
            return PackingResult<PackingResolveDto>.Fail(400, PackingErrorCodes.InvalidRequest, "条码不能为空");
        }

        // 固定前缀区分箱和托盘：不是这两种前缀的码交回客户端按商品处理。
        if (normalized.StartsWith(CartonPrefix, StringComparison.Ordinal))
        {
            var carton = await _db.Queryable<PackCarton>().Where(item => item.CartonNo == normalized).FirstAsync();
            if (carton != null && await CanViewCartonAsync(op, carton))
            {
                return PackingResult<PackingResolveDto>.Ok(
                    new PackingResolveDto { Kind = "carton", Guid = carton.CartonGUID, No = carton.CartonNo, PalletGuid = carton.PalletGUID }
                );
            }
        }
        else if (normalized.StartsWith(PalletPrefix, StringComparison.Ordinal))
        {
            var pallet = await _db.Queryable<PackPallet>().Where(item => item.PalletNo == normalized).FirstAsync();
            if (pallet != null && CanViewPallet(op, pallet))
            {
                return PackingResult<PackingResolveDto>.Ok(
                    new PackingResolveDto { Kind = "pallet", Guid = pallet.PalletGUID, No = pallet.PalletNo }
                );
            }
        }

        return PackingResult<PackingResolveDto>.Fail(404, PackingErrorCodes.CodeNotFound, "不是箱号或托盘号");
    }

    public async Task<PackingResult<PackingLocateDto>> LocateProductAsync(
        PackingOperator op,
        string? ownerCode,
        string? code,
        bool allStores
    )
    {
        var owner = RequireOwner(op, ownerCode);
        if (owner.Error != null)
        {
            return PackingResult<PackingLocateDto>.Fail(owner.StatusCode, owner.ErrorCode!, owner.Error);
        }

        // 跨店查询只给店长 / 管理员：店员只看本店。
        if (allStores && !op.IsManager)
        {
            return PackingResult<PackingLocateDto>.Fail(403, PackingErrorCodes.NotAllowed, "只有店长可以查看其他分店");
        }

        var me = owner.Code!;
        var normalized = code?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return PackingResult<PackingLocateDto>.Fail(400, PackingErrorCodes.InvalidRequest, "请输入条码或货号");
        }

        var matches = await FindProductsByCodeAsync(normalized, me);
        if (matches.Count == 0)
        {
            return PackingResult<PackingLocateDto>.Fail(404, PackingErrorCodes.CodeNotFound, "找不到该条码对应的商品");
        }

        // 位置查询是只读的：一码多品时取第一个，不打断查询。
        var product = matches[0];
        var items = await _db.Queryable<PackCartonItem>().Where(item => item.ProductCode == product.ProductCode).ToListAsync();
        var cartonGuids = items.Select(item => item.CartonGUID).Distinct().ToList();
        var cartonQuery = _db.Queryable<PackCarton>()
            .Where(carton => cartonGuids.Contains(carton.CartonGUID) && carton.Status != PackCartonStatuses.Voided);
        if (!allStores)
        {
            // 本地点相关的箱：自己持有的、自己发出的、发给自己在路上的。
            cartonQuery = cartonQuery.Where(carton =>
                carton.OwnerCode == me
                || (carton.OriginCode == me && (carton.Status == PackCartonStatuses.InTransit || carton.OwnerCode != me))
                || (carton.Status == PackCartonStatuses.InTransit && carton.DestinationCode == me));
        }

        var cartons = cartonGuids.Count == 0 ? new List<PackCarton>() : await cartonQuery.ToListAsync();
        var summaries = (await BuildCartonSummariesAsync(cartons)).ToDictionary(summary => summary.CartonGuid);
        var palletLocations = await LoadPalletLocationsAsync(cartons);
        var cartonsByGuid = cartons.ToDictionary(carton => carton.CartonGUID);
        var warehouseProduct = PackOwners.IsWarehouse(me)
            ? await _db.Queryable<WarehouseProduct>()
                .Where(item => item.ProductCode == product.ProductCode)
                .Select(item => new { item.StockQuantity, item.PackingQuantity })
                .FirstAsync()
            : null;

        var rows = items
            .Where(item => summaries.ContainsKey(item.CartonGUID))
            .Select(item =>
            {
                var summary = summaries[item.CartonGUID];
                var carton = cartonsByGuid[item.CartonGUID];
                return new PackingLocateRowDto
                {
                    CartonGuid = summary.CartonGuid,
                    CartonNo = summary.CartonNo,
                    CartonStatus = summary.Status,
                    PalletGuid = summary.PalletGuid,
                    PalletNo = summary.PalletNo,
                    LocationCode = summary.PalletGuid != null ? palletLocations.GetValueOrDefault(summary.PalletGuid) : summary.LocationCode,
                    OwnerCode = summary.OwnerCode,
                    OwnerName = summary.OwnerName,
                    OriginName = summary.OriginName,
                    Destination = summary.Destination,
                    Quantity = item.Quantity,
                    OrderNo = summary.OrderNo,
                    UpdatedAtUtc = summary.UpdatedAtUtc,
                    Group = LocateGroup(carton, me),
                };
            })
            .OrderBy(row => row.Group == "stored" ? 0 : row.Group == "transit" ? 1 : row.Group == "elsewhere" ? 2 : 3)
            .ThenBy(row => row.OwnerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.LocationCode ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.PalletNo ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(row => row.CartonNo, StringComparer.Ordinal)
            .ToList();

        return PackingResult<PackingLocateDto>.Ok(new PackingLocateDto
        {
            Product = product.ToCandidate(),
            MiddlePackageQuantity = product.MiddlePackageQuantity,
            PackingQuantity = warehouseProduct?.PackingQuantity,
            StockQuantity = warehouseProduct?.StockQuantity,
            PackedQuantity = rows.Sum(row => row.Quantity),
            CartonCount = rows.Count,
            PalletCount = rows.Where(row => row.PalletGuid != null).Select(row => row.PalletGuid).Distinct().Count(),
            Rows = rows,
        });
    }

    private static string LocateGroup(PackCarton carton, string me)
    {
        if (SameCode(carton.OwnerCode, me) && carton.Status != PackCartonStatuses.InTransit)
        {
            return "stored";
        }

        if (carton.Status == PackCartonStatuses.InTransit && (SameCode(carton.OwnerCode, me) || SameCode(carton.DestinationCode, me)))
        {
            return "transit";
        }

        return SameCode(carton.OriginCode, me) && !SameCode(carton.OwnerCode, me) ? "elsewhere" : "other";
    }

    public async Task<PackingResult<List<PackingEventDto>>> ListEventsAsync(PackingOperator op, int targetType, string targetGuid)
    {
        var visible = targetType == PackTargetTypes.Carton
            ? await _db.Queryable<PackCarton>().Where(item => item.CartonGUID == targetGuid).FirstAsync() is { } carton && await CanViewCartonAsync(op, carton)
            : await _db.Queryable<PackPallet>().Where(item => item.PalletGUID == targetGuid).FirstAsync() is { } pallet && CanViewPallet(op, pallet);
        if (!visible)
        {
            return PackingResult<List<PackingEventDto>>.Fail(404, PackingErrorCodes.CodeNotFound, "找不到该箱或托盘");
        }

        var events = await _db.Queryable<PackEvent>()
            .Where(item => item.TargetType == targetType && item.TargetGUID == targetGuid)
            .OrderBy(item => item.CreatedAtUtc, OrderByType.Desc)
            // 同一时刻的记录再按 UUIDv7 排，保证先后顺序稳定。
            .OrderBy(item => item.EventGUID, OrderByType.Desc)
            .Take(MaxListItems)
            .ToListAsync();
        return PackingResult<List<PackingEventDto>>.Ok(events.Select(item => new PackingEventDto
        {
            Action = item.Action,
            Detail = item.Detail,
            OperatorName = item.OperatorName,
            CreatedAtUtc = item.CreatedAtUtc,
        }).ToList());
    }

    // ---- 运输公司（全公司共用）----

    public async Task<PackingResult<List<PackingCarrierDto>>> ListCarriersAsync(bool includeInactive)
    {
        var query = _db.Queryable<PackCarrier>();
        if (!includeInactive)
        {
            query = query.Where(carrier => carrier.IsActive);
        }

        var carriers = await query.OrderBy(carrier => carrier.Name).ToListAsync();
        return PackingResult<List<PackingCarrierDto>>.Ok(carriers.Select(ToCarrierDto).ToList());
    }

    public async Task<PackingResult<PackingCarrierDto>> SaveCarrierAsync(PackingOperator op, string? carrierGuid, PackingCarrierSaveRequestDto request)
    {
        var normalizedGuid = string.IsNullOrWhiteSpace(carrierGuid) ? null : carrierGuid.Trim();
        // 现场可以临时新增运输公司；改名、停用只有管理员。
        if (normalizedGuid != null && !op.IsAdmin)
        {
            return PackingResult<PackingCarrierDto>.Fail(403, PackingErrorCodes.NotAllowed, "只有管理员可以修改运输公司");
        }

        var name = Truncate(request.Name, 100);
        if (name == null)
        {
            return PackingResult<PackingCarrierDto>.Fail(400, PackingErrorCodes.InvalidRequest, "运输公司名称不能为空");
        }

        var nameTaken = await _db.Queryable<PackCarrier>()
            .Where(carrier => carrier.Name == name && carrier.CarrierGUID != (normalizedGuid ?? string.Empty))
            .AnyAsync();
        if (nameTaken)
        {
            return PackingResult<PackingCarrierDto>.Fail(409, PackingErrorCodes.CarrierNameTaken, "已有同名的运输公司");
        }

        var now = UtcNow();
        if (normalizedGuid == null)
        {
            var created = new PackCarrier
            {
                CarrierGUID = NewGuid(),
                Name = name,
                Remark = Truncate(request.Remark, 200),
                IsActive = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            await _db.Insertable(created).ExecuteCommandAsync();
            return PackingResult<PackingCarrierDto>.Ok(ToCarrierDto(created));
        }

        var carrier = await _db.Queryable<PackCarrier>().Where(item => item.CarrierGUID == normalizedGuid).FirstAsync();
        if (carrier == null)
        {
            return PackingResult<PackingCarrierDto>.Fail(404, PackingErrorCodes.CarrierNotFound, "找不到该运输公司");
        }

        // 停用只影响新选择：已经指向它的箱和托盘继续显示原名称。
        carrier.Name = name;
        carrier.Remark = Truncate(request.Remark, 200);
        carrier.IsActive = request.IsActive;
        carrier.UpdatedAtUtc = now;
        await _db.Updateable(carrier).ExecuteCommandAsync();
        return PackingResult<PackingCarrierDto>.Ok(ToCarrierDto(carrier));
    }

    // ---- 存放位置（分店自己维护；总仓用现有货位表）----

    public async Task<PackingResult<List<PackingLocationDto>>> ListLocationsAsync(PackingOperator op, string? ownerCode, bool includeInactive)
    {
        var owner = RequireOwner(op, ownerCode);
        if (owner.Error != null)
        {
            return PackingResult<List<PackingLocationDto>>.Fail(owner.StatusCode, owner.ErrorCode!, owner.Error);
        }

        var code = owner.Code!;
        if (PackOwners.IsWarehouse(code))
        {
            var warehouseLocations = await _db.Queryable<Location>()
                .Where(location => !location.IsDeleted && location.LocationCode != null)
                .OrderBy(location => location.LocationCode)
                .Take(500)
                .ToListAsync();
            return PackingResult<List<PackingLocationDto>>.Ok(warehouseLocations.Select(location => new PackingLocationDto
            {
                LocationGuid = location.LocationGuid,
                OwnerCode = code,
                Name = location.LocationCode ?? string.Empty,
                Barcode = location.LocationBarcode ?? string.Empty,
                IsActive = true,
            }).ToList());
        }

        var query = _db.Queryable<PackLocation>().Where(location => location.OwnerCode == code);
        if (!includeInactive)
        {
            query = query.Where(location => location.IsActive);
        }

        var locations = await query.OrderBy(location => location.Name).ToListAsync();
        return PackingResult<List<PackingLocationDto>>.Ok(locations.Select(ToLocationDto).ToList());
    }

    public async Task<PackingResult<PackingLocationDto>> SaveLocationAsync(PackingOperator op, string? locationGuid, PackingLocationSaveRequestDto request)
    {
        if (!op.IsManager)
        {
            return PackingResult<PackingLocationDto>.Fail(403, PackingErrorCodes.NotAllowed, "只有店长可以维护存放位置");
        }

        var name = Truncate(request.Name, 50);
        if (name == null)
        {
            return PackingResult<PackingLocationDto>.Fail(400, PackingErrorCodes.InvalidRequest, "位置名称不能为空");
        }

        var normalizedGuid = string.IsNullOrWhiteSpace(locationGuid) ? null : locationGuid.Trim();
        PackLocation? existing = null;
        string ownerCode;
        if (normalizedGuid == null)
        {
            var owner = RequireOwner(op, request.OwnerCode);
            if (owner.Error != null)
            {
                return PackingResult<PackingLocationDto>.Fail(owner.StatusCode, owner.ErrorCode!, owner.Error);
            }

            ownerCode = owner.Code!;
            if (PackOwners.IsWarehouse(ownerCode))
            {
                return PackingResult<PackingLocationDto>.Fail(400, PackingErrorCodes.InvalidRequest, "总仓的位置在「货位管理」里维护");
            }
        }
        else
        {
            existing = await _db.Queryable<PackLocation>().Where(item => item.LocationGUID == normalizedGuid).FirstAsync();
            if (existing == null || !op.Has(existing.OwnerCode))
            {
                return PackingResult<PackingLocationDto>.Fail(404, PackingErrorCodes.LocationNotFound, "找不到该位置");
            }

            ownerCode = existing.OwnerCode;
        }

        var nameTaken = await _db.Queryable<PackLocation>()
            .Where(location => location.OwnerCode == ownerCode && location.Name == name && location.LocationGUID != (normalizedGuid ?? string.Empty))
            .AnyAsync();
        if (nameTaken)
        {
            return PackingResult<PackingLocationDto>.Fail(409, PackingErrorCodes.LocationNameTaken, "本店已有同名的存放位置");
        }

        return await ExecuteInTransactionAsync(async () =>
        {
            var now = UtcNow();
            if (existing == null)
            {
                var created = new PackLocation
                {
                    LocationGUID = NewGuid(),
                    OwnerCode = ownerCode,
                    Name = name,
                    Barcode = await NextNumberAsync(LocationPrefix, 6, ownerCode, includeDate: false),
                    IsActive = true,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                await _db.Insertable(created).ExecuteCommandAsync();
                return PackingResult<PackingLocationDto>.Ok(ToLocationDto(created));
            }

            // 改名后，已经放在这个位置上的箱和托盘仍显示当时记下的名称；条码不变，标签继续有效。
            existing.Name = name;
            existing.IsActive = request.IsActive;
            existing.UpdatedAtUtc = now;
            await _db.Updateable(existing).ExecuteCommandAsync();
            return PackingResult<PackingLocationDto>.Ok(ToLocationDto(existing));
        });
    }

    // ---- 可发往的地点 ----

    /// <summary>
    /// 选「其他分店 / 总仓」时的候选：所有启用的分店（排除发出地点自己）加总仓。
    /// 店员没有「查看分店」权限，调不动通用的分店列表接口，所以单独给一个只读接口；只返回编码和名称。
    /// </summary>
    public async Task<PackingResult<List<PackingOwnerDto>>> ListDestinationsAsync(PackingOperator op, string? ownerCode)
    {
        var owner = RequireOwner(op, ownerCode);
        if (owner.Error != null)
        {
            return PackingResult<List<PackingOwnerDto>>.Fail(owner.StatusCode, owner.ErrorCode!, owner.Error);
        }

        var me = owner.Code!;
        var stores = await _db.Queryable<Store>()
            .Where(store => !store.IsDeleted && store.IsActive && store.StoreCode != me)
            .OrderBy(store => store.StoreName)
            .Select(store => new { store.StoreCode, store.StoreName })
            .ToListAsync();
        var destinations = stores
            .Select(store => new PackingOwnerDto { Code = store.StoreCode, Name = store.StoreName, IsWarehouse = false })
            .ToList();
        if (!PackOwners.IsWarehouse(me))
        {
            destinations.Insert(0, new PackingOwnerDto { Code = PackOwners.Warehouse, Name = PackingConfig.WarehouseName(configuration), IsWarehouse = true });
        }

        return PackingResult<List<PackingOwnerDto>>.Ok(destinations);
    }

    // ---- 订货单选择（总仓二期用）----

    public async Task<PackingResult<List<PackingOrderOptionDto>>> SearchOrdersAsync(PackingOperator op, string? keyword)
    {
        if (!op.Has(PackOwners.Warehouse))
        {
            return PackingResult<List<PackingOrderOptionDto>>.Ok(new List<PackingOrderOptionDto>());
        }

        var normalized = keyword?.Trim();
        // 只列还在仓库手里的订货单：已提交（1）和配货中（3）。
        var query = _db.Queryable<WareHouseOrder>().Where(order => !order.IsDeleted && (order.FlowStatus == 1 || order.FlowStatus == 3));
        if (!string.IsNullOrEmpty(normalized))
        {
            query = query.Where(order =>
                (order.OrderNo != null && order.OrderNo.Contains(normalized))
                || (order.StoreCode != null && order.StoreCode.Contains(normalized)));
        }

        var orders = await query
            .OrderBy(order => order.OrderDate, OrderByType.Desc)
            .Take(50)
            .Select(order => new PackingOrderOptionDto
            {
                OrderGuid = order.OrderGUID,
                OrderNo = order.OrderNo,
                StoreCode = order.StoreCode,
                FlowStatus = order.FlowStatus ?? 0,
                OrderDate = order.OrderDate,
            })
            .ToListAsync();
        var storeNames = await LoadOwnerNamesAsync(orders.Select(order => order.StoreCode));
        foreach (var order in orders)
        {
            order.StoreName = order.StoreCode == null ? null : storeNames.GetValueOrDefault(order.StoreCode);
        }

        return PackingResult<List<PackingOrderOptionDto>>.Ok(orders);
    }

    private static PackingCarrierDto ToCarrierDto(PackCarrier carrier) => new()
    {
        CarrierGuid = carrier.CarrierGUID,
        Name = carrier.Name,
        Remark = carrier.Remark,
        IsActive = carrier.IsActive,
    };

    private static PackingLocationDto ToLocationDto(PackLocation location) => new()
    {
        LocationGuid = location.LocationGUID,
        OwnerCode = location.OwnerCode,
        Name = location.Name,
        Barcode = location.Barcode,
        IsActive = location.IsActive,
    };
}
