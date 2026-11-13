using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.WarehousePacking;

internal sealed partial class WarehousePackingService
{
    // ---- 托盘 ----

    public async Task<WarehousePackingResult<WarehousePackingPalletListDto>> ListPalletsAsync(string? filter, string? keyword)
    {
        var normalizedFilter = (filter ?? "all").Trim();
        var normalizedKeyword = keyword?.Trim();

        var buildingCount = await _db.Queryable<WarehousePackPallet>()
            .Where(pallet => pallet.Status == WarehousePackPalletStatuses.Building)
            .CountAsync();
        var sealedCount = await _db.Queryable<WarehousePackPallet>()
            .Where(pallet => pallet.Status == WarehousePackPalletStatuses.Sealed)
            .CountAsync();

        var query = _db.Queryable<WarehousePackPallet>()
            .Where(pallet => pallet.Status != WarehousePackPalletStatuses.Voided);
        if (normalizedFilter == "building")
        {
            query = query.Where(pallet => pallet.Status == WarehousePackPalletStatuses.Building);
        }
        else if (normalizedFilter == "sealed")
        {
            query = query.Where(pallet => pallet.Status == WarehousePackPalletStatuses.Sealed);
        }

        if (!string.IsNullOrEmpty(normalizedKeyword))
        {
            // 关键字可以是托盘号、箱号，或商品的条码 / 货号：后两者先换成托盘 GUID。
            var productCodes = (await FindProductsByCodeAsync(normalizedKeyword))
                .Select(product => product.ProductCode)
                .ToList();
            var cartonQuery = _db.Queryable<WarehousePackCarton>()
                .Where(carton => carton.PalletGUID != null && carton.Status != WarehousePackCartonStatuses.Voided);
            if (productCodes.Count == 0)
            {
                cartonQuery = cartonQuery.Where(carton => carton.CartonNo.Contains(normalizedKeyword));
            }
            else
            {
                var cartonGuids = await _db.Queryable<WarehousePackCartonItem>()
                    .Where(item => productCodes.Contains(item.ProductCode))
                    .Select(item => item.CartonGUID)
                    .Distinct()
                    .ToListAsync();
                cartonQuery = cartonQuery.Where(carton => carton.CartonNo.Contains(normalizedKeyword) || cartonGuids.Contains(carton.CartonGUID));
            }

            var palletGuids = (await cartonQuery.Select(carton => carton.PalletGUID).Distinct().ToListAsync())
                .Where(guid => guid != null)
                .Select(guid => guid!)
                .ToList();
            query = query.Where(pallet => pallet.PalletNo.Contains(normalizedKeyword) || palletGuids.Contains(pallet.PalletGUID));
        }

        var pallets = await query
            .OrderBy(pallet => pallet.UpdatedAtUtc, OrderByType.Desc)
            .Take(MaxListItems)
            .ToListAsync();

        return WarehousePackingResult<WarehousePackingPalletListDto>.Ok(new WarehousePackingPalletListDto
        {
            Items = await BuildPalletSummariesAsync(pallets),
            BuildingCount = buildingCount,
            SealedCount = sealedCount,
        });
    }

    public async Task<WarehousePackingResult<WarehousePackingPalletDetailDto>> CreatePalletAsync(
        WarehousePackingDestinationRequestDto request,
        WarehousePackingOperator op
    )
    {
        var destination = await ResolveDestinationAsync(request);
        if (destination.Error != null)
        {
            return PalletFail(400, WarehousePackingErrorCodes.DestinationInvalid, destination.Error);
        }

        return await ExecuteInTransactionAsync(async () =>
        {
            var now = UtcNow();
            var pallet = new WarehousePackPallet
            {
                PalletGUID = NewGuid(),
                PalletNo = await NextNumberAsync(PalletPrefix, 3),
                Status = WarehousePackPalletStatuses.Building,
                OriginName = ResolveOriginName(),
                DestinationType = destination.Type,
                DestinationStoreCode = destination.StoreCode,
                CarrierGUID = destination.CarrierGuid,
                CreatedAtUtc = now,
                CreatedByUserGuid = op.UserGuid,
                CreatedByName = op.Name,
                UpdatedAtUtc = now,
            };
            await _db.Insertable(pallet).ExecuteCommandAsync();
            await AddEventAsync(WarehousePackTargetTypes.Pallet, pallet.PalletGUID, WarehousePackEventActions.Created, null, op, now);
            return WarehousePackingResult<WarehousePackingPalletDetailDto>.Ok(await BuildPalletDetailAsync(pallet));
        });
    }

    public async Task<WarehousePackingResult<WarehousePackingPalletDetailDto>> GetPalletAsync(string palletGuid)
    {
        var pallet = await _db.Queryable<WarehousePackPallet>().Where(item => item.PalletGUID == palletGuid).FirstAsync();
        return pallet == null
            ? PalletFail(404, WarehousePackingErrorCodes.PalletNotFound, "找不到该托盘")
            : WarehousePackingResult<WarehousePackingPalletDetailDto>.Ok(await BuildPalletDetailAsync(pallet));
    }

    public async Task<WarehousePackingResult<WarehousePackingPalletDetailDto>> AddCartonToPalletAsync(
        string palletGuid,
        WarehousePackingAddCartonRequestDto request,
        WarehousePackingOperator op
    )
    {
        var code = request.Code?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            return PalletFail(400, WarehousePackingErrorCodes.InvalidRequest, "箱号不能为空");
        }

        return await ExecuteInTransactionAsync(async () =>
        {
            var pallet = await LockPalletAsync(palletGuid);
            if (pallet == null)
            {
                return PalletFail(404, WarehousePackingErrorCodes.PalletNotFound, "找不到该托盘");
            }

            if (pallet.Status != WarehousePackPalletStatuses.Building)
            {
                return PalletFail(409, WarehousePackingErrorCodes.PalletNotBuilding, "该托盘已封托，不能再加箱；需要先重新开托");
            }

            var upperCode = code.ToUpperInvariant();
            var cartonGuid = await _db.Queryable<WarehousePackCarton>()
                .Where(item => item.CartonNo == upperCode || item.CartonGUID == code)
                .Select(item => item.CartonGUID)
                .FirstAsync();
            var carton = string.IsNullOrEmpty(cartonGuid) ? null : await LockCartonAsync(cartonGuid);
            if (carton == null || carton.Status == WarehousePackCartonStatuses.Voided)
            {
                return PalletFail(404, WarehousePackingErrorCodes.CartonNotFound, "找不到该箱");
            }

            if (carton.PalletGUID == pallet.PalletGUID)
            {
                // 同一只箱重复扫：已在本托盘上，按成功处理。
                return WarehousePackingResult<WarehousePackingPalletDetailDto>.Ok(await BuildPalletDetailAsync(pallet));
            }

            if (carton.Status != WarehousePackCartonStatuses.Sealed)
            {
                return await PalletBlockedAsync(pallet, carton, WarehousePackingErrorCodes.CartonNotSealed, "这只箱还没封箱，封箱后才能上托盘");
            }

            if (carton.PalletGUID != null)
            {
                return await PalletBlockedAsync(pallet, carton, WarehousePackingErrorCodes.CartonOnOtherPallet, "这只箱已经在另一个托盘上");
            }

            // 只比较双方都已明确的收货分店：任一方没指定时不算不一致。
            var mismatch = !string.IsNullOrEmpty(carton.DestinationStoreCode)
                && !string.IsNullOrEmpty(pallet.DestinationStoreCode)
                && !SameCode(carton.DestinationStoreCode, pallet.DestinationStoreCode);
            if (mismatch && !(request.AllowDestinationMismatch && op.IsManager))
            {
                return await PalletBlockedAsync(
                    pallet,
                    carton,
                    WarehousePackingErrorCodes.DestinationMismatch,
                    request.AllowDestinationMismatch ? "去向不一致时只有仓库经理可以放行" : "这只箱的去向和托盘不一致"
                );
            }

            var now = UtcNow();
            carton.PalletGUID = pallet.PalletGUID;
            // 上托盘后位置以托盘为准，清掉箱自己的暂存货位，避免查询时出现两个位置。
            carton.LocationCode = null;
            carton.UpdatedAtUtc = now;
            await _db.Updateable(carton).ExecuteCommandAsync();
            await TouchPalletAsync(pallet, now);
            await AddEventAsync(WarehousePackTargetTypes.Carton, carton.CartonGUID, WarehousePackEventActions.AddedToPallet, pallet.PalletNo, op, now);
            await AddEventAsync(
                WarehousePackTargetTypes.Pallet,
                pallet.PalletGUID,
                WarehousePackEventActions.AddedToPallet,
                mismatch ? $"{carton.CartonNo}（去向不一致，经理放行）" : carton.CartonNo,
                op,
                now
            );
            return WarehousePackingResult<WarehousePackingPalletDetailDto>.Ok(await BuildPalletDetailAsync(pallet));
        });
    }

    public async Task<WarehousePackingResult<WarehousePackingPalletDetailDto>> RemoveCartonFromPalletAsync(
        string palletGuid,
        string cartonGuid,
        WarehousePackingOperator op
    )
    {
        return await ExecuteInTransactionAsync(async () =>
        {
            var pallet = await LockPalletAsync(palletGuid);
            if (pallet == null)
            {
                return PalletFail(404, WarehousePackingErrorCodes.PalletNotFound, "找不到该托盘");
            }

            if (pallet.Status != WarehousePackPalletStatuses.Building)
            {
                return PalletFail(409, WarehousePackingErrorCodes.PalletNotBuilding, "该托盘已封托，不能移出箱；需要先重新开托");
            }

            var carton = await LockCartonAsync(cartonGuid);
            if (carton == null || carton.PalletGUID != pallet.PalletGUID)
            {
                return PalletFail(404, WarehousePackingErrorCodes.CartonNotFound, "托盘上没有这只箱");
            }

            var now = UtcNow();
            carton.PalletGUID = null;
            carton.UpdatedAtUtc = now;
            await _db.Updateable(carton).ExecuteCommandAsync();
            await TouchPalletAsync(pallet, now);
            await AddEventAsync(WarehousePackTargetTypes.Carton, carton.CartonGUID, WarehousePackEventActions.RemovedFromPallet, pallet.PalletNo, op, now);
            await AddEventAsync(WarehousePackTargetTypes.Pallet, pallet.PalletGUID, WarehousePackEventActions.RemovedFromPallet, carton.CartonNo, op, now);
            return WarehousePackingResult<WarehousePackingPalletDetailDto>.Ok(await BuildPalletDetailAsync(pallet));
        });
    }

    public async Task<WarehousePackingResult<WarehousePackingPalletDetailDto>> UpdatePalletHeaderAsync(
        string palletGuid,
        WarehousePackingPalletHeaderRequestDto request,
        WarehousePackingOperator op
    )
    {
        var destination = await ResolveDestinationAsync(request);
        if (destination.Error != null)
        {
            return PalletFail(400, WarehousePackingErrorCodes.DestinationInvalid, destination.Error);
        }

        if (!IsValidWeight(request.WeightKg))
        {
            return PalletFail(400, WarehousePackingErrorCodes.InvalidRequest, "重量超出范围");
        }

        return await ExecuteInTransactionAsync(async () =>
        {
            var pallet = await LockPalletAsync(palletGuid);
            if (pallet == null || pallet.Status == WarehousePackPalletStatuses.Voided)
            {
                return PalletFail(404, WarehousePackingErrorCodes.PalletNotFound, "找不到该托盘");
            }

            // 去向允许封托后再补（发运前填上即可），所以这里不限制状态。
            var destinationChanged = pallet.DestinationType != destination.Type
                || !SameCode(pallet.DestinationStoreCode, destination.StoreCode)
                || !SameCode(pallet.CarrierGUID, destination.CarrierGuid);
            var now = UtcNow();
            pallet.DestinationType = destination.Type;
            pallet.DestinationStoreCode = destination.StoreCode;
            pallet.CarrierGUID = destination.CarrierGuid;
            pallet.ConsignmentNo = Truncate(request.ConsignmentNo, 100);
            pallet.WeightKg = request.WeightKg;
            pallet.Remark = Truncate(request.Remark, 200);
            pallet.UpdatedAtUtc = now;
            await _db.Updateable(pallet).ExecuteCommandAsync();
            if (destinationChanged)
            {
                await AddEventAsync(WarehousePackTargetTypes.Pallet, pallet.PalletGUID, WarehousePackEventActions.HeaderChanged, "去向已更改", op, now);
            }

            return WarehousePackingResult<WarehousePackingPalletDetailDto>.Ok(await BuildPalletDetailAsync(pallet));
        });
    }

    public async Task<WarehousePackingResult<WarehousePackingPalletDetailDto>> SealPalletAsync(
        string palletGuid,
        WarehousePackingSealRequestDto request,
        WarehousePackingOperator op
    )
    {
        if (!IsValidWeight(request.WeightKg))
        {
            return PalletFail(400, WarehousePackingErrorCodes.InvalidRequest, "重量超出范围");
        }

        return await ExecuteInTransactionAsync(async () =>
        {
            var pallet = await LockPalletAsync(palletGuid);
            if (pallet == null)
            {
                return PalletFail(404, WarehousePackingErrorCodes.PalletNotFound, "找不到该托盘");
            }

            if (pallet.Status == WarehousePackPalletStatuses.Sealed)
            {
                // 重复点「封托」或网络重试：已封托直接返回，客户端可以照常打印标签。
                return WarehousePackingResult<WarehousePackingPalletDetailDto>.Ok(await BuildPalletDetailAsync(pallet));
            }

            if (pallet.Status != WarehousePackPalletStatuses.Building)
            {
                return PalletFail(409, WarehousePackingErrorCodes.PalletNotBuilding, "该托盘当前不能封托");
            }

            var hasCartons = await _db.Queryable<WarehousePackCarton>().Where(carton => carton.PalletGUID == palletGuid).AnyAsync();
            if (!hasCartons)
            {
                return PalletFail(409, WarehousePackingErrorCodes.PalletEmpty, "空托盘不能封托");
            }

            var now = UtcNow();
            pallet.Status = WarehousePackPalletStatuses.Sealed;
            pallet.SealedAtUtc = now;
            pallet.SealedByUserGuid = op.UserGuid;
            pallet.SealedByName = op.Name;
            if (request.WeightKg != null)
            {
                pallet.WeightKg = request.WeightKg;
            }

            pallet.UpdatedAtUtc = now;
            await _db.Updateable(pallet).ExecuteCommandAsync();
            await AddEventAsync(WarehousePackTargetTypes.Pallet, pallet.PalletGUID, WarehousePackEventActions.Sealed, null, op, now);
            return WarehousePackingResult<WarehousePackingPalletDetailDto>.Ok(await BuildPalletDetailAsync(pallet));
        });
    }

    public async Task<WarehousePackingResult<WarehousePackingPalletDetailDto>> ReopenPalletAsync(
        string palletGuid,
        WarehousePackingOperator op
    )
    {
        return await ExecuteInTransactionAsync(async () =>
        {
            var pallet = await LockPalletAsync(palletGuid);
            if (pallet == null)
            {
                return PalletFail(404, WarehousePackingErrorCodes.PalletNotFound, "找不到该托盘");
            }

            if (pallet.Status != WarehousePackPalletStatuses.Sealed)
            {
                return PalletFail(409, WarehousePackingErrorCodes.PalletNotSealed, "只有已封托的托盘可以重新开托");
            }

            var now = UtcNow();
            pallet.Status = WarehousePackPalletStatuses.Building;
            pallet.SealedAtUtc = null;
            pallet.SealedByUserGuid = null;
            pallet.SealedByName = null;
            pallet.UpdatedAtUtc = now;
            await _db.Updateable(pallet).ExecuteCommandAsync();
            await AddEventAsync(WarehousePackTargetTypes.Pallet, pallet.PalletGUID, WarehousePackEventActions.Reopened, null, op, now);
            return WarehousePackingResult<WarehousePackingPalletDetailDto>.Ok(await BuildPalletDetailAsync(pallet));
        });
    }

    public async Task<WarehousePackingResult<WarehousePackingPalletDetailDto>> VoidPalletAsync(
        string palletGuid,
        WarehousePackingOperator op
    )
    {
        return await ExecuteInTransactionAsync(async () =>
        {
            var pallet = await LockPalletAsync(palletGuid);
            if (pallet == null)
            {
                return PalletFail(404, WarehousePackingErrorCodes.PalletNotFound, "找不到该托盘");
            }

            if (pallet.Status != WarehousePackPalletStatuses.Building)
            {
                return PalletFail(409, WarehousePackingErrorCodes.PalletNotBuilding, "只有组托中的托盘可以作废；已封托的请先重新开托");
            }

            var hasCartons = await _db.Queryable<WarehousePackCarton>().Where(carton => carton.PalletGUID == palletGuid).AnyAsync();
            if (hasCartons)
            {
                return PalletFail(409, WarehousePackingErrorCodes.PalletNotEmpty, "托盘上还有箱，请先把箱移出");
            }

            var now = UtcNow();
            pallet.Status = WarehousePackPalletStatuses.Voided;
            pallet.UpdatedAtUtc = now;
            await _db.Updateable(pallet).ExecuteCommandAsync();
            await AddEventAsync(WarehousePackTargetTypes.Pallet, pallet.PalletGUID, WarehousePackEventActions.Voided, null, op, now);
            return WarehousePackingResult<WarehousePackingPalletDetailDto>.Ok(await BuildPalletDetailAsync(pallet));
        });
    }

    public async Task<WarehousePackingResult<WarehousePackingPalletDetailDto>> SetPalletLocationAsync(
        string palletGuid,
        string? locationCode,
        WarehousePackingOperator op
    )
    {
        var location = await ResolveLocationAsync(locationCode);
        if (location.Error != null)
        {
            return PalletFail(404, WarehousePackingErrorCodes.LocationNotFound, location.Error);
        }

        return await ExecuteInTransactionAsync(async () =>
        {
            var pallet = await LockPalletAsync(palletGuid);
            if (pallet == null || pallet.Status == WarehousePackPalletStatuses.Voided)
            {
                return PalletFail(404, WarehousePackingErrorCodes.PalletNotFound, "找不到该托盘");
            }

            var now = UtcNow();
            pallet.LocationCode = location.Code;
            pallet.UpdatedAtUtc = now;
            await _db.Updateable(pallet).ExecuteCommandAsync();
            await AddEventAsync(WarehousePackTargetTypes.Pallet, pallet.PalletGUID, WarehousePackEventActions.LocationChanged, location.Code ?? "清除货位", op, now);
            return WarehousePackingResult<WarehousePackingPalletDetailDto>.Ok(await BuildPalletDetailAsync(pallet));
        });
    }

    // ---- 扫码识别、位置查询、操作记录 ----

    public async Task<WarehousePackingResult<WarehousePackingResolveDto>> ResolveCodeAsync(string? code)
    {
        var normalized = code?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(normalized))
        {
            return WarehousePackingResult<WarehousePackingResolveDto>.Fail(400, WarehousePackingErrorCodes.InvalidRequest, "条码不能为空");
        }

        // 固定前缀区分箱和托盘：不是这两种前缀的码交回客户端按商品处理。
        if (normalized.StartsWith(CartonPrefix, StringComparison.Ordinal))
        {
            var carton = await _db.Queryable<WarehousePackCarton>().Where(item => item.CartonNo == normalized).FirstAsync();
            if (carton != null)
            {
                return WarehousePackingResult<WarehousePackingResolveDto>.Ok(
                    new WarehousePackingResolveDto { Kind = "carton", Guid = carton.CartonGUID, No = carton.CartonNo }
                );
            }
        }
        else if (normalized.StartsWith(PalletPrefix, StringComparison.Ordinal))
        {
            var pallet = await _db.Queryable<WarehousePackPallet>().Where(item => item.PalletNo == normalized).FirstAsync();
            if (pallet != null)
            {
                return WarehousePackingResult<WarehousePackingResolveDto>.Ok(
                    new WarehousePackingResolveDto { Kind = "pallet", Guid = pallet.PalletGUID, No = pallet.PalletNo }
                );
            }
        }

        return WarehousePackingResult<WarehousePackingResolveDto>.Fail(404, WarehousePackingErrorCodes.CodeNotFound, "不是箱号或托盘号");
    }

    public async Task<WarehousePackingResult<WarehousePackingLocateDto>> LocateProductAsync(string? code)
    {
        var normalized = code?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return WarehousePackingResult<WarehousePackingLocateDto>.Fail(400, WarehousePackingErrorCodes.InvalidRequest, "请输入条码或货号");
        }

        var matches = await FindProductsByCodeAsync(normalized);
        if (matches.Count == 0)
        {
            return WarehousePackingResult<WarehousePackingLocateDto>.Fail(404, WarehousePackingErrorCodes.CodeNotFound, "找不到该条码对应的商品");
        }

        // 位置查询是只读的：一码多品时取第一个，不打断查询。
        var product = matches[0];
        var items = await _db.Queryable<WarehousePackCartonItem>()
            .Where(item => item.ProductCode == product.ProductCode)
            .ToListAsync();
        var cartonGuids = items.Select(item => item.CartonGUID).Distinct().ToList();
        var cartons = cartonGuids.Count == 0
            ? new List<WarehousePackCarton>()
            : await _db.Queryable<WarehousePackCarton>()
                .Where(carton => cartonGuids.Contains(carton.CartonGUID) && carton.Status != WarehousePackCartonStatuses.Voided)
                .ToListAsync();
        var summaries = (await BuildCartonSummariesAsync(cartons)).ToDictionary(summary => summary.CartonGuid);
        var palletLocations = await LoadPalletLocationsAsync(cartons);
        var warehouseProduct = await _db.Queryable<WarehouseProduct>()
            .Where(item => item.ProductCode == product.ProductCode)
            .Select(item => new { item.StockQuantity, item.PackingQuantity })
            .FirstAsync();

        var rows = items
            .Where(item => summaries.ContainsKey(item.CartonGUID))
            .Select(item =>
            {
                var summary = summaries[item.CartonGUID];
                return new WarehousePackingLocateRowDto
                {
                    CartonGuid = summary.CartonGuid,
                    CartonNo = summary.CartonNo,
                    CartonStatus = summary.Status,
                    PalletGuid = summary.PalletGuid,
                    PalletNo = summary.PalletNo,
                    LocationCode = summary.PalletGuid != null
                        ? palletLocations.GetValueOrDefault(summary.PalletGuid)
                        : summary.LocationCode,
                    OriginName = summary.OriginName,
                    Destination = summary.Destination,
                    Quantity = item.Quantity,
                    OrderNo = summary.OrderNo,
                    UpdatedAtUtc = summary.UpdatedAtUtc,
                };
            })
            .OrderBy(row => row.LocationCode ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.PalletNo ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(row => row.CartonNo, StringComparer.Ordinal)
            .ToList();

        return WarehousePackingResult<WarehousePackingLocateDto>.Ok(new WarehousePackingLocateDto
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

    public async Task<WarehousePackingResult<List<WarehousePackingEventDto>>> ListEventsAsync(int targetType, string targetGuid)
    {
        var events = await _db.Queryable<WarehousePackEvent>()
            .Where(item => item.TargetType == targetType && item.TargetGUID == targetGuid)
            .OrderBy(item => item.CreatedAtUtc, OrderByType.Desc)
            // 同一时刻的记录再按 UUIDv7 排，保证先后顺序稳定。
            .OrderBy(item => item.EventGUID, OrderByType.Desc)
            .Take(MaxListItems)
            .ToListAsync();
        return WarehousePackingResult<List<WarehousePackingEventDto>>.Ok(events.Select(item => new WarehousePackingEventDto
        {
            Action = item.Action,
            Detail = item.Detail,
            OperatorName = item.OperatorName,
            CreatedAtUtc = item.CreatedAtUtc,
        }).ToList());
    }

    // ---- 运输公司、订货单选择 ----

    public async Task<WarehousePackingResult<List<WarehousePackingCarrierDto>>> ListCarriersAsync(bool includeInactive)
    {
        var query = _db.Queryable<WarehousePackCarrier>();
        if (!includeInactive)
        {
            query = query.Where(carrier => carrier.IsActive);
        }

        var carriers = await query.OrderBy(carrier => carrier.Name).ToListAsync();
        return WarehousePackingResult<List<WarehousePackingCarrierDto>>.Ok(carriers.Select(ToCarrierDto).ToList());
    }

    public async Task<WarehousePackingResult<WarehousePackingCarrierDto>> SaveCarrierAsync(
        string? carrierGuid,
        WarehousePackingCarrierSaveRequestDto request
    )
    {
        var name = Truncate(request.Name, 100);
        if (name == null)
        {
            return WarehousePackingResult<WarehousePackingCarrierDto>.Fail(400, WarehousePackingErrorCodes.InvalidRequest, "运输公司名称不能为空");
        }

        var normalizedGuid = string.IsNullOrWhiteSpace(carrierGuid) ? null : carrierGuid.Trim();
        var nameTaken = await _db.Queryable<WarehousePackCarrier>()
            .Where(carrier => carrier.Name == name && carrier.CarrierGUID != (normalizedGuid ?? string.Empty))
            .AnyAsync();
        if (nameTaken)
        {
            return WarehousePackingResult<WarehousePackingCarrierDto>.Fail(409, WarehousePackingErrorCodes.CarrierNameTaken, "已有同名的运输公司");
        }

        var now = UtcNow();
        if (normalizedGuid == null)
        {
            var created = new WarehousePackCarrier
            {
                CarrierGUID = NewGuid(),
                Name = name,
                Remark = Truncate(request.Remark, 200),
                IsActive = request.IsActive,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            await _db.Insertable(created).ExecuteCommandAsync();
            return WarehousePackingResult<WarehousePackingCarrierDto>.Ok(ToCarrierDto(created));
        }

        var carrier = await _db.Queryable<WarehousePackCarrier>().Where(item => item.CarrierGUID == normalizedGuid).FirstAsync();
        if (carrier == null)
        {
            return WarehousePackingResult<WarehousePackingCarrierDto>.Fail(404, WarehousePackingErrorCodes.CarrierNotFound, "找不到该运输公司");
        }

        // 停用只影响新选择：已经指向它的箱和托盘继续显示原名称。
        carrier.Name = name;
        carrier.Remark = Truncate(request.Remark, 200);
        carrier.IsActive = request.IsActive;
        carrier.UpdatedAtUtc = now;
        await _db.Updateable(carrier).ExecuteCommandAsync();
        return WarehousePackingResult<WarehousePackingCarrierDto>.Ok(ToCarrierDto(carrier));
    }

    public async Task<WarehousePackingResult<List<WarehousePackingOrderOptionDto>>> SearchOrdersAsync(string? keyword)
    {
        var normalized = keyword?.Trim();
        // 只列还在仓库手里的订货单：已提交（1）和配货中（3）。
        var query = _db.Queryable<WareHouseOrder>()
            .Where(order => !order.IsDeleted && (order.FlowStatus == 1 || order.FlowStatus == 3));
        if (!string.IsNullOrEmpty(normalized))
        {
            query = query.Where(order =>
                (order.OrderNo != null && order.OrderNo.Contains(normalized))
                || (order.StoreCode != null && order.StoreCode.Contains(normalized)));
        }

        var orders = await query
            .OrderBy(order => order.OrderDate, OrderByType.Desc)
            .Take(50)
            .Select(order => new WarehousePackingOrderOptionDto
            {
                OrderGuid = order.OrderGUID,
                OrderNo = order.OrderNo,
                StoreCode = order.StoreCode,
                FlowStatus = order.FlowStatus ?? 0,
                OrderDate = order.OrderDate,
            })
            .ToListAsync();
        var storeNames = await LoadStoreNamesAsync(orders.Select(order => order.StoreCode));
        foreach (var order in orders)
        {
            order.StoreName = order.StoreCode == null ? null : storeNames.GetValueOrDefault(order.StoreCode);
        }

        return WarehousePackingResult<List<WarehousePackingOrderOptionDto>>.Ok(orders);
    }

    private async Task<WarehousePackingResult<WarehousePackingPalletDetailDto>> PalletBlockedAsync(
        WarehousePackPallet pallet,
        WarehousePackCarton carton,
        string errorCode,
        string message
    )
    {
        var blocked = (await BuildCartonSummariesAsync(new List<WarehousePackCarton> { carton })).FirstOrDefault();
        return WarehousePackingResult<WarehousePackingPalletDetailDto>.Fail(
            409,
            errorCode,
            message,
            new WarehousePackingPalletDetailDto { PalletGuid = pallet.PalletGUID, PalletNo = pallet.PalletNo, BlockedCarton = blocked }
        );
    }

    private async Task TouchPalletAsync(WarehousePackPallet pallet, DateTime now)
    {
        pallet.UpdatedAtUtc = now;
        await _db.Updateable<WarehousePackPallet>()
            .SetColumns(item => item.UpdatedAtUtc == now)
            .Where(item => item.PalletGUID == pallet.PalletGUID)
            .ExecuteCommandAsync();
    }

    private static WarehousePackingCarrierDto ToCarrierDto(WarehousePackCarrier carrier) => new()
    {
        CarrierGuid = carrier.CarrierGUID,
        Name = carrier.Name,
        Remark = carrier.Remark,
        IsActive = carrier.IsActive,
    };

    private static WarehousePackingResult<WarehousePackingPalletDetailDto> PalletFail(int statusCode, string errorCode, string message) =>
        WarehousePackingResult<WarehousePackingPalletDetailDto>.Fail(statusCode, errorCode, message);
}
