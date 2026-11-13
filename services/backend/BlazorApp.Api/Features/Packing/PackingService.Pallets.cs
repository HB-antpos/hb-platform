using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.Packing;

internal sealed partial class PackingService
{
    // ---- 托盘 ----

    public async Task<PackingResult<PackingPalletListDto>> ListPalletsAsync(
        PackingOperator op,
        string? ownerCode,
        string? filter,
        string? keyword
    )
    {
        var owner = RequireOwner(op, ownerCode);
        if (owner.Error != null)
        {
            return PackingResult<PackingPalletListDto>.Fail(owner.StatusCode, owner.ErrorCode!, owner.Error);
        }

        var code = owner.Code!;
        var normalizedFilter = (filter ?? "all").Trim();
        var normalizedKeyword = keyword?.Trim();

        var buildingCount = await _db.Queryable<PackPallet>()
            .Where(pallet => pallet.OwnerCode == code && pallet.Status == PackPalletStatuses.Building)
            .CountAsync();
        var storedCount = await _db.Queryable<PackPallet>()
            .Where(pallet => pallet.OwnerCode == code && pallet.Status == PackPalletStatuses.Sealed)
            .CountAsync();

        var query = _db.Queryable<PackPallet>()
            .Where(pallet =>
                pallet.Status != PackPalletStatuses.Voided
                && (pallet.OwnerCode == code
                    || (pallet.OriginCode == code
                        && (pallet.Status == PackPalletStatuses.InTransit || pallet.OwnerCode != code)))
            );
        if (normalizedFilter == "building")
        {
            query = query.Where(pallet => pallet.OwnerCode == code && pallet.Status == PackPalletStatuses.Building);
        }
        else if (normalizedFilter == "stored")
        {
            query = query.Where(pallet => pallet.OwnerCode == code && pallet.Status == PackPalletStatuses.Sealed);
        }
        else if (normalizedFilter == "outgoing")
        {
            query = query.Where(pallet =>
                pallet.OriginCode == code
                && (pallet.Status == PackPalletStatuses.InTransit || pallet.OwnerCode != code)
            );
        }

        if (!string.IsNullOrEmpty(normalizedKeyword))
        {
            // 关键字可以是托盘号、箱号，或商品的条码 / 货号：后两者先换成托盘 GUID。
            var productCodes = (await FindProductsByCodeAsync(normalizedKeyword, code)).Select(product => product.ProductCode).ToList();
            var cartonQuery = _db.Queryable<PackCarton>()
                .Where(carton => carton.PalletGUID != null && carton.Status != PackCartonStatuses.Voided);
            if (productCodes.Count == 0)
            {
                cartonQuery = cartonQuery.Where(carton => carton.CartonNo.Contains(normalizedKeyword));
            }
            else
            {
                var cartonGuids = await _db.Queryable<PackCartonItem>()
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

        var pallets = await query.OrderBy(pallet => pallet.UpdatedAtUtc, OrderByType.Desc).Take(MaxListItems).ToListAsync();
        return PackingResult<PackingPalletListDto>.Ok(new PackingPalletListDto
        {
            Items = await BuildPalletSummariesAsync(pallets),
            BuildingCount = buildingCount,
            StoredCount = storedCount,
        });
    }

    public async Task<PackingResult<PackingPalletDetailDto>> CreatePalletAsync(PackingOperator op, PackingCreatePalletRequestDto request)
    {
        var owner = RequireOwner(op, request.OwnerCode);
        if (owner.Error != null)
        {
            return PalletFail(owner.StatusCode, owner.ErrorCode!, owner.Error);
        }

        var ownerCode = owner.Code!;
        var destination = await ResolveDestinationAsync(ownerCode, request);
        if (destination.Error != null)
        {
            return PalletFail(400, PackingErrorCodes.DestinationInvalid, destination.Error);
        }

        var originName = await OwnerNameAsync(ownerCode);
        return await ExecuteInTransactionAsync(async () =>
        {
            var now = UtcNow();
            var pallet = new PackPallet
            {
                PalletGUID = NewGuid(),
                PalletNo = await NextNumberAsync(PalletPrefix, 3, ownerCode),
                Status = PackPalletStatuses.Building,
                OwnerCode = ownerCode,
                OriginCode = ownerCode,
                OriginName = originName,
                DestinationType = destination.Type,
                DestinationCode = destination.Code,
                CarrierGUID = destination.CarrierGuid,
                CreatedAtUtc = now,
                CreatedByUserGuid = op.UserGuid,
                CreatedByName = op.Name,
                UpdatedAtUtc = now,
            };
            await _db.Insertable(pallet).ExecuteCommandAsync();
            await AddEventAsync(PackTargetTypes.Pallet, pallet.PalletGUID, PackEventActions.Created, null, op, now);
            return PackingResult<PackingPalletDetailDto>.Ok(await BuildPalletDetailAsync(pallet));
        });
    }

    public async Task<PackingResult<PackingPalletDetailDto>> GetPalletAsync(PackingOperator op, string palletGuid)
    {
        var pallet = await _db.Queryable<PackPallet>().Where(item => item.PalletGUID == palletGuid).FirstAsync();
        return pallet == null || !CanViewPallet(op, pallet)
            ? PalletFail(404, PackingErrorCodes.PalletNotFound, "找不到该托盘")
            : PackingResult<PackingPalletDetailDto>.Ok(await BuildPalletDetailAsync(pallet));
    }

    public async Task<PackingResult<PackingPalletDetailDto>> AddCartonToPalletAsync(
        PackingOperator op,
        string palletGuid,
        PackingAddCartonRequestDto request
    )
    {
        var code = request.Code?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            return PalletFail(400, PackingErrorCodes.InvalidRequest, "箱号不能为空");
        }

        return await ExecuteInTransactionAsync(async () =>
        {
            var pallet = await LockPalletAsync(palletGuid);
            var denied = CheckPalletEditable(op, pallet, requireBuilding: true, "该托盘已封托，不能再加箱；需要先重新开托");
            if (denied != null)
            {
                return denied;
            }

            var upperCode = code.ToUpperInvariant();
            var cartonGuid = await _db.Queryable<PackCarton>()
                .Where(item => item.CartonNo == upperCode || item.CartonGUID == code)
                .Select(item => item.CartonGUID)
                .FirstAsync();
            var carton = string.IsNullOrEmpty(cartonGuid) ? null : await LockCartonAsync(cartonGuid);
            // 只能加本地点自己的箱：别的地点的箱按「找不到」处理，不暴露它存在。
            if (carton == null || carton.Status == PackCartonStatuses.Voided || !op.Has(carton.OwnerCode) || !SameCode(carton.OwnerCode, pallet!.OwnerCode))
            {
                return PalletFail(404, PackingErrorCodes.CartonNotFound, "找不到该箱");
            }

            if (carton.PalletGUID == pallet.PalletGUID)
            {
                // 同一只箱重复扫：已在本托盘上，按成功处理。
                return PackingResult<PackingPalletDetailDto>.Ok(await BuildPalletDetailAsync(pallet));
            }

            if (carton.Status != PackCartonStatuses.Sealed)
            {
                return await PalletBlockedAsync(
                    pallet,
                    carton,
                    carton.Status == PackCartonStatuses.InTransit ? PackingErrorCodes.CartonInTransit : PackingErrorCodes.CartonNotSealed,
                    carton.Status == PackCartonStatuses.InTransit ? "这只箱已经发出，不能再上托盘" : "这只箱还没封箱，封箱后才能上托盘"
                );
            }

            if (carton.PalletGUID != null)
            {
                return await PalletBlockedAsync(pallet, carton, PackingErrorCodes.CartonOnOtherPallet, "这只箱已经在另一个托盘上");
            }

            // 只比较双方都已明确的收货地点：任一方没指定、或箱是本地存放时不算不一致。
            var mismatch = PackDestinationTypes.IsShippable(carton.DestinationType)
                && PackDestinationTypes.IsShippable(pallet.DestinationType)
                && !SameCode(carton.DestinationCode, pallet.DestinationCode);
            if (mismatch && !(request.AllowDestinationMismatch && op.IsManager))
            {
                return await PalletBlockedAsync(
                    pallet,
                    carton,
                    PackingErrorCodes.DestinationMismatch,
                    request.AllowDestinationMismatch ? "去向不一致时只有店长或仓库经理可以放行" : "这只箱的去向和托盘不一致"
                );
            }

            var now = UtcNow();
            carton.PalletGUID = pallet.PalletGUID;
            // 上托盘后位置以托盘为准，清掉箱自己的存放位置，避免查询时出现两个位置。
            carton.LocationCode = null;
            carton.UpdatedAtUtc = now;
            await _db.Updateable(carton).ExecuteCommandAsync();
            await TouchPalletAsync(pallet, now);
            await AddEventAsync(PackTargetTypes.Carton, carton.CartonGUID, PackEventActions.AddedToPallet, pallet.PalletNo, op, now);
            await AddEventAsync(
                PackTargetTypes.Pallet,
                pallet.PalletGUID,
                PackEventActions.AddedToPallet,
                mismatch ? $"{carton.CartonNo}（去向不一致，经理放行）" : carton.CartonNo,
                op,
                now
            );
            return PackingResult<PackingPalletDetailDto>.Ok(await BuildPalletDetailAsync(pallet));
        });
    }

    public async Task<PackingResult<PackingPalletDetailDto>> RemoveCartonFromPalletAsync(
        PackingOperator op,
        string palletGuid,
        string cartonGuid
    )
    {
        return await ExecuteInTransactionAsync(async () =>
        {
            var pallet = await LockPalletAsync(palletGuid);
            var denied = CheckPalletEditable(op, pallet, requireBuilding: true, "该托盘已封托，不能移出箱；需要先重新开托");
            if (denied != null)
            {
                return denied;
            }

            var carton = await LockCartonAsync(cartonGuid);
            if (carton == null || carton.PalletGUID != pallet!.PalletGUID)
            {
                return PalletFail(404, PackingErrorCodes.CartonNotFound, "托盘上没有这只箱");
            }

            var now = UtcNow();
            carton.PalletGUID = null;
            carton.UpdatedAtUtc = now;
            await _db.Updateable(carton).ExecuteCommandAsync();
            await TouchPalletAsync(pallet, now);
            await AddEventAsync(PackTargetTypes.Carton, carton.CartonGUID, PackEventActions.RemovedFromPallet, pallet.PalletNo, op, now);
            await AddEventAsync(PackTargetTypes.Pallet, pallet.PalletGUID, PackEventActions.RemovedFromPallet, carton.CartonNo, op, now);
            return PackingResult<PackingPalletDetailDto>.Ok(await BuildPalletDetailAsync(pallet));
        });
    }

    public async Task<PackingResult<PackingPalletDetailDto>> UpdatePalletHeaderAsync(
        PackingOperator op,
        string palletGuid,
        PackingPalletHeaderRequestDto request
    )
    {
        var ownerCode = await _db.Queryable<PackPallet>()
            .Where(item => item.PalletGUID == palletGuid)
            .Select(item => item.OwnerCode)
            .FirstAsync();
        if (string.IsNullOrEmpty(ownerCode) || !op.Has(ownerCode))
        {
            return PalletFail(404, PackingErrorCodes.PalletNotFound, "找不到该托盘");
        }

        var destination = await ResolveDestinationAsync(ownerCode, request);
        if (destination.Error != null)
        {
            return PalletFail(400, PackingErrorCodes.DestinationInvalid, destination.Error);
        }

        if (!IsValidWeight(request.WeightKg))
        {
            return PalletFail(400, PackingErrorCodes.InvalidRequest, "重量超出范围");
        }

        return await ExecuteInTransactionAsync(async () =>
        {
            var pallet = await LockPalletAsync(palletGuid);
            var denied = CheckPalletEditable(op, pallet, requireBuilding: false, null);
            if (denied != null)
            {
                return denied;
            }

            // 去向允许封托后再补（发出前填上即可），所以这里只拦已发出的和作废的。
            var destinationChanged = pallet!.DestinationType != destination.Type
                || !SameCode(pallet.DestinationCode, destination.Code)
                || !SameCode(pallet.CarrierGUID, destination.CarrierGuid);
            var now = UtcNow();
            pallet.DestinationType = destination.Type;
            pallet.DestinationCode = destination.Code;
            pallet.CarrierGUID = destination.CarrierGuid;
            pallet.ConsignmentNo = Truncate(request.ConsignmentNo, 100);
            pallet.WeightKg = request.WeightKg;
            pallet.Remark = Truncate(request.Remark, 200);
            pallet.UpdatedAtUtc = now;
            await _db.Updateable(pallet).ExecuteCommandAsync();
            if (destinationChanged)
            {
                await AddEventAsync(PackTargetTypes.Pallet, pallet.PalletGUID, PackEventActions.HeaderChanged, "去向已更改", op, now);
            }

            return PackingResult<PackingPalletDetailDto>.Ok(await BuildPalletDetailAsync(pallet));
        });
    }

    public async Task<PackingResult<PackingPalletDetailDto>> SealPalletAsync(
        PackingOperator op,
        string palletGuid,
        PackingSealRequestDto request
    )
    {
        if (!IsValidWeight(request.WeightKg))
        {
            return PalletFail(400, PackingErrorCodes.InvalidRequest, "重量超出范围");
        }

        return await ExecuteInTransactionAsync(async () =>
        {
            var pallet = await LockPalletAsync(palletGuid);
            if (pallet != null && op.Has(pallet.OwnerCode) && pallet.Status == PackPalletStatuses.Sealed)
            {
                // 重复点「封托」或网络重试：已封托直接返回，客户端可以照常打印标签。
                return PackingResult<PackingPalletDetailDto>.Ok(await BuildPalletDetailAsync(pallet));
            }

            var denied = CheckPalletEditable(op, pallet, requireBuilding: true, "该托盘当前不能封托");
            if (denied != null)
            {
                return denied;
            }

            var hasCartons = await _db.Queryable<PackCarton>().Where(carton => carton.PalletGUID == palletGuid).AnyAsync();
            if (!hasCartons)
            {
                return PalletFail(409, PackingErrorCodes.PalletEmpty, "空托盘不能封托");
            }

            var now = UtcNow();
            pallet!.Status = PackPalletStatuses.Sealed;
            pallet.SealedAtUtc = now;
            pallet.SealedByUserGuid = op.UserGuid;
            pallet.SealedByName = op.Name;
            if (request.WeightKg != null)
            {
                pallet.WeightKg = request.WeightKg;
            }

            pallet.UpdatedAtUtc = now;
            await _db.Updateable(pallet).ExecuteCommandAsync();
            await AddEventAsync(PackTargetTypes.Pallet, pallet.PalletGUID, PackEventActions.Sealed, null, op, now);
            return PackingResult<PackingPalletDetailDto>.Ok(await BuildPalletDetailAsync(pallet));
        });
    }

    public async Task<PackingResult<PackingPalletDetailDto>> ReopenPalletAsync(PackingOperator op, string palletGuid)
    {
        if (!op.IsManager)
        {
            return PalletFail(403, PackingErrorCodes.NotAllowed, "只有店长或仓库经理可以重新开托");
        }

        return await ExecuteInTransactionAsync(async () =>
        {
            var pallet = await LockPalletAsync(palletGuid);
            var denied = CheckPalletEditable(op, pallet, requireBuilding: false, null);
            if (denied != null)
            {
                return denied;
            }

            if (pallet!.Status != PackPalletStatuses.Sealed)
            {
                return PalletFail(409, PackingErrorCodes.PalletNotSealed, "只有已封托的托盘可以重新开托");
            }

            var now = UtcNow();
            pallet.Status = PackPalletStatuses.Building;
            pallet.SealedAtUtc = null;
            pallet.SealedByUserGuid = null;
            pallet.SealedByName = null;
            pallet.UpdatedAtUtc = now;
            await _db.Updateable(pallet).ExecuteCommandAsync();
            await AddEventAsync(PackTargetTypes.Pallet, pallet.PalletGUID, PackEventActions.Reopened, null, op, now);
            return PackingResult<PackingPalletDetailDto>.Ok(await BuildPalletDetailAsync(pallet));
        });
    }

    public async Task<PackingResult<PackingPalletDetailDto>> VoidPalletAsync(PackingOperator op, string palletGuid)
    {
        return await ExecuteInTransactionAsync(async () =>
        {
            var pallet = await LockPalletAsync(palletGuid);
            var denied = CheckPalletEditable(op, pallet, requireBuilding: true, "只有组托中的托盘可以作废；已封托的请先重新开托");
            if (denied != null)
            {
                return denied;
            }

            var hasCartons = await _db.Queryable<PackCarton>().Where(carton => carton.PalletGUID == palletGuid).AnyAsync();
            if (hasCartons)
            {
                return PalletFail(409, PackingErrorCodes.PalletNotEmpty, "托盘上还有箱，请先把箱移出");
            }

            var now = UtcNow();
            pallet!.Status = PackPalletStatuses.Voided;
            pallet.UpdatedAtUtc = now;
            await _db.Updateable(pallet).ExecuteCommandAsync();
            await AddEventAsync(PackTargetTypes.Pallet, pallet.PalletGUID, PackEventActions.Voided, null, op, now);
            return PackingResult<PackingPalletDetailDto>.Ok(await BuildPalletDetailAsync(pallet));
        });
    }

    public async Task<PackingResult<PackingPalletDetailDto>> SetPalletLocationAsync(PackingOperator op, string palletGuid, string? locationCode)
    {
        var ownerCode = await _db.Queryable<PackPallet>()
            .Where(item => item.PalletGUID == palletGuid)
            .Select(item => item.OwnerCode)
            .FirstAsync();
        if (string.IsNullOrEmpty(ownerCode) || !op.Has(ownerCode))
        {
            return PalletFail(404, PackingErrorCodes.PalletNotFound, "找不到该托盘");
        }

        var location = await ResolveLocationAsync(ownerCode, locationCode);
        if (location.Error != null)
        {
            return PalletFail(404, PackingErrorCodes.LocationNotFound, location.Error);
        }

        return await ExecuteInTransactionAsync(async () =>
        {
            var pallet = await LockPalletAsync(palletGuid);
            var denied = CheckPalletEditable(op, pallet, requireBuilding: false, null);
            if (denied != null)
            {
                return denied;
            }

            var now = UtcNow();
            pallet!.LocationCode = location.Code;
            pallet.UpdatedAtUtc = now;
            await _db.Updateable(pallet).ExecuteCommandAsync();
            await AddEventAsync(PackTargetTypes.Pallet, pallet.PalletGUID, PackEventActions.LocationChanged, location.Code ?? "清除位置", op, now);
            return PackingResult<PackingPalletDetailDto>.Ok(await BuildPalletDetailAsync(pallet));
        });
    }

    private async Task<PackingResult<PackingPalletDetailDto>> PalletBlockedAsync(
        PackPallet pallet,
        PackCarton carton,
        string errorCode,
        string message
    )
    {
        var blocked = (await BuildCartonSummariesAsync(new List<PackCarton> { carton })).FirstOrDefault();
        return PackingResult<PackingPalletDetailDto>.Fail(
            409,
            errorCode,
            message,
            new PackingPalletDetailDto { PalletGuid = pallet.PalletGUID, PalletNo = pallet.PalletNo, BlockedCarton = blocked }
        );
    }

    private async Task TouchPalletAsync(PackPallet pallet, DateTime now)
    {
        pallet.UpdatedAtUtc = now;
        await _db.Updateable<PackPallet>()
            .SetColumns(item => item.UpdatedAtUtc == now)
            .Where(item => item.PalletGUID == pallet.PalletGUID)
            .ExecuteCommandAsync();
    }
}
