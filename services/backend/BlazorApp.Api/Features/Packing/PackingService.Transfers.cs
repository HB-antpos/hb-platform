using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.Packing;

/// <summary>
/// 店间调货：发出方「发出」→ 箱 / 托盘变成在路上（所属地点仍是发出方）→ 接收方扫码「签收」→ 所属地点换成接收方。
/// 整个过程只记箱的去向和位置，不改任何分店库存。
/// </summary>
internal sealed partial class PackingService
{
    // ---- 发出 ----

    public async Task<PackingResult<PackingCartonDetailDto>> DispatchCartonAsync(PackingOperator op, string cartonGuid)
    {
        return await ExecuteInTransactionAsync(async () =>
        {
            var carton = await LockCartonAsync(cartonGuid);
            var denied = CheckEditable(op, carton, requirePacking: false, null);
            if (denied != null)
            {
                return denied;
            }

            if (carton!.Status == PackCartonStatuses.Packing)
            {
                return CartonFail(409, PackingErrorCodes.CartonNotSealed, "还没封箱，封箱后才能发出");
            }

            if (carton.Status != PackCartonStatuses.Sealed)
            {
                return CartonFail(409, PackingErrorCodes.CartonNotSealed, "这只箱当前不能发出");
            }

            if (carton.PalletGUID != null)
            {
                return CartonFail(409, PackingErrorCodes.CartonOnPallet, "该箱在托盘上，请发出整个托盘；或先把箱移出托盘");
            }

            if (!PackDestinationTypes.IsShippable(carton.DestinationType) || string.IsNullOrEmpty(carton.DestinationCode))
            {
                return CartonFail(409, PackingErrorCodes.NotShippable, "请先选择收货分店或总仓");
            }

            var now = UtcNow();
            carton.Status = PackCartonStatuses.InTransit;
            // 标签上的 FROM 取发出时的所属地点：签收方再转发时，标签上就是转发方自己。
            carton.OriginCode = carton.OwnerCode;
            carton.OriginName = await OwnerNameAsync(carton.OwnerCode);
            carton.DispatchedAtUtc = now;
            carton.DispatchedByName = op.Name;
            carton.UpdatedAtUtc = now;
            await _db.Updateable(carton).ExecuteCommandAsync();
            await AddEventAsync(PackTargetTypes.Carton, carton.CartonGUID, PackEventActions.Dispatched, await DescribeDestinationAsync(carton.DestinationCode), op, now);
            return PackingResult<PackingCartonDetailDto>.Ok(await BuildCartonDetailAsync(carton, null));
        });
    }

    public async Task<PackingResult<PackingPalletDetailDto>> DispatchPalletAsync(PackingOperator op, string palletGuid)
    {
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
                return PalletFail(409, PackingErrorCodes.PalletNotSealed, "还没封托，封托后才能发出");
            }

            if (!PackDestinationTypes.IsShippable(pallet.DestinationType) || string.IsNullOrEmpty(pallet.DestinationCode))
            {
                return PalletFail(409, PackingErrorCodes.NotShippable, "请先选择收货分店或总仓");
            }

            var cartons = await _db.Queryable<PackCarton>()
                .Where(carton => carton.PalletGUID == palletGuid && carton.Status == PackCartonStatuses.Sealed)
                .ToListAsync();
            if (cartons.Count == 0)
            {
                return PalletFail(409, PackingErrorCodes.PalletEmpty, "空托盘不能发出");
            }

            var now = UtcNow();
            var originName = await OwnerNameAsync(pallet.OwnerCode);
            foreach (var carton in cartons)
            {
                // 托盘上的箱跟着托盘走：把托盘的去向、怎么送写进每只箱，签收时逐箱核对就不用再回头查托盘。
                carton.Status = PackCartonStatuses.InTransit;
                carton.OriginCode = pallet.OwnerCode;
                carton.OriginName = originName;
                carton.DestinationType = pallet.DestinationType;
                carton.DestinationCode = pallet.DestinationCode;
                carton.CarrierGUID = pallet.CarrierGUID;
                carton.DispatchedAtUtc = now;
                carton.DispatchedByName = op.Name;
                carton.UpdatedAtUtc = now;
                await _db.Updateable(carton).ExecuteCommandAsync();
            }

            pallet.Status = PackPalletStatuses.InTransit;
            pallet.OriginCode = pallet.OwnerCode;
            pallet.OriginName = originName;
            pallet.DispatchedAtUtc = now;
            pallet.DispatchedByName = op.Name;
            pallet.UpdatedAtUtc = now;
            await _db.Updateable(pallet).ExecuteCommandAsync();
            await AddEventAsync(
                PackTargetTypes.Pallet,
                pallet.PalletGUID,
                PackEventActions.Dispatched,
                $"{await DescribeDestinationAsync(pallet.DestinationCode)} · {cartons.Count} 箱",
                op,
                now
            );
            return PackingResult<PackingPalletDetailDto>.Ok(await BuildPalletDetailAsync(pallet));
        });
    }

    // ---- 签收 ----

    public async Task<PackingResult<PackingCartonDetailDto>> ReceiveCartonAsync(
        PackingOperator op,
        string cartonGuid,
        PackingReceiveCartonRequestDto request
    )
    {
        return await ExecuteInTransactionAsync(async () =>
        {
            var carton = await LockCartonAsync(cartonGuid);
            var access = await CheckReceivableAsync(op, carton);
            if (access != null)
            {
                return access;
            }

            var location = await ResolveLocationAsync(carton!.DestinationCode!, request.LocationCode);
            if (location.Error != null)
            {
                return CartonFail(404, PackingErrorCodes.LocationNotFound, location.Error);
            }

            var now = UtcNow();
            var destinationCode = carton.DestinationCode!;
            var palletGuid = carton.PalletGUID;
            await ReceiveCartonCoreAsync(carton, destinationCode, palletGuid == null ? location.Code : null, op, now, null);
            if (palletGuid != null)
            {
                await FinishPalletIfAllReceivedAsync(palletGuid, destinationCode, location.Code, op, now);
            }

            return PackingResult<PackingCartonDetailDto>.Ok(await BuildCartonDetailAsync(carton, null));
        });
    }

    public async Task<PackingResult<PackingPalletDetailDto>> ReceivePalletAsync(
        PackingOperator op,
        string palletGuid,
        PackingReceivePalletRequestDto request
    )
    {
        return await ExecuteInTransactionAsync(async () =>
        {
            var pallet = await LockPalletAsync(palletGuid);
            if (pallet == null || !CanViewPallet(op, pallet))
            {
                return PalletFail(404, PackingErrorCodes.PalletNotFound, "找不到该托盘");
            }

            if (pallet.Status != PackPalletStatuses.InTransit)
            {
                return PalletFail(409, PackingErrorCodes.NotInTransit, "这个托盘不在路上，不能签收");
            }

            var destinationCode = pallet.DestinationCode ?? string.Empty;
            if (!op.Has(destinationCode))
            {
                return PalletFail(403, PackingErrorCodes.NotForYou, "这个托盘不是发给你所在地点的");
            }

            var inTransit = await _db.Queryable<PackCarton>()
                .Where(carton => carton.PalletGUID == palletGuid && carton.Status == PackCartonStatuses.InTransit)
                .ToListAsync();
            var selectedGuids = request.CartonGuids?.Where(guid => !string.IsNullOrWhiteSpace(guid)).Select(guid => guid.Trim()).ToList();
            List<PackCarton> toReceive;
            if (selectedGuids is { Count: > 0 })
            {
                // 逐箱核对：只签收勾选的箱，没到的箱继续留在待签收里。
                toReceive = inTransit.Where(carton => selectedGuids.Contains(carton.CartonGUID, StringComparer.OrdinalIgnoreCase)).ToList();
                if (toReceive.Count != selectedGuids.Distinct(StringComparer.OrdinalIgnoreCase).Count())
                {
                    return PalletFail(404, PackingErrorCodes.CartonNotFound, "有的箱不在这个托盘上，或已经签收");
                }
            }
            else
            {
                // 整托签收必须先数箱：数量和系统记录对不上就不能一次签掉，改为逐箱核对。
                if (request.ExpectedCartonCount == null || request.ExpectedCartonCount != inTransit.Count)
                {
                    var detail = await BuildPalletDetailAsync(pallet);
                    detail.ActualInTransitCartonCount = inTransit.Count;
                    return PackingResult<PackingPalletDetailDto>.Fail(
                        409,
                        PackingErrorCodes.CountMismatch,
                        request.ExpectedCartonCount == null
                            ? "整托签收前请先确认实到箱数"
                            : $"实到 {request.ExpectedCartonCount} 箱，系统记录是 {inTransit.Count} 箱，请逐箱扫码核对",
                        detail
                    );
                }

                toReceive = inTransit;
            }

            if (toReceive.Count == 0)
            {
                return PalletFail(409, PackingErrorCodes.NotInTransit, "托盘上没有待签收的箱");
            }

            var location = await ResolveLocationAsync(destinationCode, request.LocationCode);
            if (location.Error != null)
            {
                return PalletFail(404, PackingErrorCodes.LocationNotFound, location.Error);
            }

            var now = UtcNow();
            var via = string.IsNullOrWhiteSpace(request.ViaCartonNo) ? null : $"通过箱标签 {Truncate(request.ViaCartonNo, 30)} 签收";
            foreach (var carton in toReceive)
            {
                await ReceiveCartonCoreAsync(carton, destinationCode, null, op, now, via);
            }

            await FinishPalletIfAllReceivedAsync(palletGuid, destinationCode, location.Code, op, now, toReceive.Count, via);
            var refreshed = await _db.Queryable<PackPallet>().Where(item => item.PalletGUID == palletGuid).FirstAsync();
            return PackingResult<PackingPalletDetailDto>.Ok(await BuildPalletDetailAsync(refreshed ?? pallet));
        });
    }

    /// <summary>签收一只箱：所属地点换成接收方，状态回到已封箱（在接收方手里就是本地存放），去向重置为本地存放。</summary>
    private async Task ReceiveCartonCoreAsync(
        PackCarton carton,
        string destinationCode,
        string? locationCode,
        PackingOperator op,
        DateTime now,
        string? via
    )
    {
        carton.OwnerCode = destinationCode;
        carton.Status = PackCartonStatuses.Sealed;
        carton.DestinationType = PackDestinationTypes.Local;
        carton.DestinationCode = null;
        carton.CarrierGUID = null;
        carton.LocationCode = locationCode;
        carton.ReceivedAtUtc = now;
        carton.ReceivedByName = op.Name;
        carton.UpdatedAtUtc = now;
        await _db.Updateable(carton).ExecuteCommandAsync();
        await AddEventAsync(
            PackTargetTypes.Carton,
            carton.CartonGUID,
            PackEventActions.Received,
            via == null ? $"来自 {carton.OriginName}" : $"来自 {carton.OriginName}；{via}",
            op,
            now
        );
    }

    /// <summary>托盘上的箱都签收完，托盘本身才换所属地点；还有箱在路上时托盘保持在路上，显示「部分签收」。</summary>
    private async Task FinishPalletIfAllReceivedAsync(
        string palletGuid,
        string destinationCode,
        string? locationCode,
        PackingOperator op,
        DateTime now,
        int receivedNow = 1,
        string? via = null
    )
    {
        var remaining = await _db.Queryable<PackCarton>()
            .Where(carton => carton.PalletGUID == palletGuid && carton.Status == PackCartonStatuses.InTransit)
            .CountAsync();
        var pallet = await _db.Queryable<PackPallet>().Where(item => item.PalletGUID == palletGuid).FirstAsync();
        if (pallet == null)
        {
            return;
        }

        if (remaining > 0)
        {
            await AddEventAsync(PackTargetTypes.Pallet, palletGuid, PackEventActions.Received, $"部分签收 {receivedNow} 箱，还有 {remaining} 箱在路上{(via == null ? string.Empty : "；" + via)}", op, now);
            await TouchPalletAsync(pallet, now);
            return;
        }

        pallet.OwnerCode = destinationCode;
        pallet.Status = PackPalletStatuses.Sealed;
        pallet.DestinationType = PackDestinationTypes.Local;
        pallet.DestinationCode = null;
        pallet.CarrierGUID = null;
        pallet.LocationCode = locationCode;
        pallet.ReceivedAtUtc = now;
        pallet.ReceivedByName = op.Name;
        pallet.UpdatedAtUtc = now;
        await _db.Updateable(pallet).ExecuteCommandAsync();
        await AddEventAsync(PackTargetTypes.Pallet, palletGuid, PackEventActions.Received, $"全部签收 · 来自 {pallet.OriginName}{(via == null ? string.Empty : "；" + via)}", op, now);
    }

    /// <summary>签收前校验：箱存在且看得见、正在路上、是发给当前账号所在地点的。</summary>
    private async Task<PackingResult<PackingCartonDetailDto>?> CheckReceivableAsync(PackingOperator op, PackCarton? carton)
    {
        if (carton == null || !await CanViewCartonAsync(op, carton))
        {
            return CartonFail(404, PackingErrorCodes.CartonNotFound, "找不到该箱");
        }

        if (carton.Status != PackCartonStatuses.InTransit)
        {
            return CartonFail(409, PackingErrorCodes.NotInTransit, "这只箱不在路上，不能签收");
        }

        if (!op.Has(carton.DestinationCode))
        {
            return CartonFail(403, PackingErrorCodes.NotForYou, "这只箱不是发给你所在地点的");
        }

        return null;
    }

    // ---- 待签收列表与扫码查找 ----

    public async Task<PackingResult<PackingIncomingDto>> ListIncomingAsync(PackingOperator op, string? ownerCode)
    {
        var owner = RequireOwner(op, ownerCode);
        if (owner.Error != null)
        {
            return PackingResult<PackingIncomingDto>.Fail(owner.StatusCode, owner.ErrorCode!, owner.Error);
        }

        var code = owner.Code!;
        var pendingPallets = await _db.Queryable<PackPallet>()
            .Where(pallet => pallet.Status == PackPalletStatuses.InTransit && pallet.DestinationCode == code)
            .OrderBy(pallet => pallet.DispatchedAtUtc, OrderByType.Desc)
            .Take(MaxListItems)
            .ToListAsync();
        var pendingCartons = await _db.Queryable<PackCarton>()
            .Where(carton => carton.Status == PackCartonStatuses.InTransit && carton.DestinationCode == code && carton.PalletGUID == null)
            .OrderBy(carton => carton.DispatchedAtUtc, OrderByType.Desc)
            .Take(MaxListItems)
            .ToListAsync();
        var recentPallets = await _db.Queryable<PackPallet>()
            .Where(pallet => pallet.OwnerCode == code && pallet.ReceivedAtUtc != null && pallet.Status != PackPalletStatuses.Voided)
            .OrderBy(pallet => pallet.ReceivedAtUtc, OrderByType.Desc)
            .Take(10)
            .ToListAsync();
        var recentCartons = await _db.Queryable<PackCarton>()
            .Where(carton => carton.OwnerCode == code && carton.ReceivedAtUtc != null && carton.PalletGUID == null && carton.Status != PackCartonStatuses.Voided)
            .OrderBy(carton => carton.ReceivedAtUtc, OrderByType.Desc)
            .Take(10)
            .ToListAsync();

        var palletItems = await BuildIncomingPalletItemsAsync(pendingPallets.Concat(recentPallets).ToList());
        var cartonSummaries = (await BuildCartonSummariesAsync(pendingCartons.Concat(recentCartons).ToList())).ToDictionary(summary => summary.CartonGuid);
        var carrierNames = await LoadCarrierNamesAsync(pendingCartons.Concat(recentCartons).Select(carton => carton.CarrierGUID));

        PackingIncomingItemDto ToItem(PackCarton carton) => new()
        {
            Kind = "carton",
            Guid = carton.CartonGUID,
            No = carton.CartonNo,
            OriginCode = carton.OriginCode,
            OriginName = carton.OriginName,
            CarrierName = carton.CarrierGUID == null ? null : carrierNames.GetValueOrDefault(carton.CarrierGUID),
            CartonCount = 1,
            PieceCount = cartonSummaries[carton.CartonGUID].PieceCount,
            DispatchedAtUtc = carton.DispatchedAtUtc,
            DispatchedByName = carton.DispatchedByName,
            ReceivedAtUtc = carton.ReceivedAtUtc,
            ReceivedByName = carton.ReceivedByName,
        };

        return PackingResult<PackingIncomingDto>.Ok(new PackingIncomingDto
        {
            Pending = pendingPallets.Select(pallet => palletItems[pallet.PalletGUID])
                .Concat(pendingCartons.Select(ToItem))
                .OrderByDescending(item => item.DispatchedAtUtc)
                .ToList(),
            Recent = recentPallets.Select(pallet => palletItems[pallet.PalletGUID])
                .Concat(recentCartons.Select(ToItem))
                .OrderByDescending(item => item.ReceivedAtUtc)
                .Take(10)
                .ToList(),
        });
    }

    public async Task<PackingResult<PackingReceiveLookupDto>> LookupForReceiveAsync(PackingOperator op, string? code)
    {
        var normalized = code?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(normalized))
        {
            return PackingResult<PackingReceiveLookupDto>.Fail(400, PackingErrorCodes.InvalidRequest, "条码不能为空");
        }

        PackPallet? pallet = null;
        PackCarton? scanned = null;
        if (normalized.StartsWith(PalletPrefix, StringComparison.Ordinal))
        {
            pallet = await _db.Queryable<PackPallet>().Where(item => item.PalletNo == normalized).FirstAsync();
        }
        else if (normalized.StartsWith(CartonPrefix, StringComparison.Ordinal))
        {
            scanned = await _db.Queryable<PackCarton>().Where(item => item.CartonNo == normalized).FirstAsync();
            // 托盘标签丢了：箱在托盘上、而且托盘还在路上时，扫箱标签就找到整个托盘。
            if (scanned?.PalletGUID != null)
            {
                pallet = await _db.Queryable<PackPallet>().Where(item => item.PalletGUID == scanned.PalletGUID).FirstAsync();
                if (pallet is not { Status: PackPalletStatuses.InTransit })
                {
                    pallet = null;
                }
            }
        }

        if (pallet != null)
        {
            if (!CanViewPallet(op, pallet))
            {
                return PackingResult<PackingReceiveLookupDto>.Fail(404, PackingErrorCodes.PalletNotFound, "找不到该托盘");
            }

            var inTransitCount = await _db.Queryable<PackCarton>()
                .Where(carton => carton.PalletGUID == pallet.PalletGUID && carton.Status == PackCartonStatuses.InTransit)
                .CountAsync();
            var blockReason = pallet.Status != PackPalletStatuses.InTransit
                ? PackingErrorCodes.NotInTransit
                : !op.Has(pallet.DestinationCode) ? PackingErrorCodes.NotForYou : null;
            return PackingResult<PackingReceiveLookupDto>.Ok(new PackingReceiveLookupDto
            {
                Kind = "pallet",
                Pallet = await BuildPalletDetailAsync(pallet),
                ScannedCartonGuid = scanned?.CartonGUID,
                ScannedCartonNo = scanned?.CartonNo,
                ViaCartonLabel = scanned != null,
                InTransitCartonCount = inTransitCount,
                CanReceive = blockReason == null && inTransitCount > 0,
                BlockReason = blockReason,
            });
        }

        if (scanned == null || !await CanViewCartonAsync(op, scanned))
        {
            return PackingResult<PackingReceiveLookupDto>.Fail(404, PackingErrorCodes.CodeNotFound, "找不到这个箱或托盘");
        }

        var cartonBlock = scanned.Status != PackCartonStatuses.InTransit
            ? PackingErrorCodes.NotInTransit
            : !op.Has(scanned.DestinationCode) ? PackingErrorCodes.NotForYou : null;
        return PackingResult<PackingReceiveLookupDto>.Ok(new PackingReceiveLookupDto
        {
            Kind = "carton",
            Carton = await BuildCartonDetailAsync(scanned, null),
            ScannedCartonGuid = scanned.CartonGUID,
            ScannedCartonNo = scanned.CartonNo,
            InTransitCartonCount = scanned.Status == PackCartonStatuses.InTransit ? 1 : 0,
            CanReceive = cartonBlock == null,
            BlockReason = cartonBlock,
        });
    }

    private async Task<int> CountIncomingAsync(string ownerCode)
    {
        var pallets = await _db.Queryable<PackPallet>()
            .Where(pallet => pallet.Status == PackPalletStatuses.InTransit && pallet.DestinationCode == ownerCode)
            .CountAsync();
        var cartons = await _db.Queryable<PackCarton>()
            .Where(carton => carton.Status == PackCartonStatuses.InTransit && carton.DestinationCode == ownerCode && carton.PalletGUID == null)
            .CountAsync();
        return pallets + cartons;
    }

    private async Task<Dictionary<string, PackingIncomingItemDto>> BuildIncomingPalletItemsAsync(List<PackPallet> pallets)
    {
        var result = new Dictionary<string, PackingIncomingItemDto>();
        if (pallets.Count == 0)
        {
            return result;
        }

        var guids = pallets.Select(pallet => pallet.PalletGUID).Distinct().ToList();
        var cartons = await _db.Queryable<PackCarton>()
            .Where(carton => carton.PalletGUID != null && guids.Contains(carton.PalletGUID))
            .Select(carton => new { carton.CartonGUID, carton.PalletGUID, carton.Status })
            .ToListAsync();
        var cartonGuids = cartons.Select(carton => carton.CartonGUID).ToList();
        var pieces = cartonGuids.Count == 0
            ? new Dictionary<string, int>()
            : (await _db.Queryable<PackCartonItem>().Where(item => cartonGuids.Contains(item.CartonGUID)).Select(item => new { item.CartonGUID, item.Quantity }).ToListAsync())
                .GroupBy(item => item.CartonGUID)
                .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity));
        var carrierNames = await LoadCarrierNamesAsync(pallets.Select(pallet => pallet.CarrierGUID));
        foreach (var pallet in pallets)
        {
            var own = cartons.Where(carton => carton.PalletGUID == pallet.PalletGUID).ToList();
            result[pallet.PalletGUID] = new PackingIncomingItemDto
            {
                Kind = "pallet",
                Guid = pallet.PalletGUID,
                No = pallet.PalletNo,
                OriginCode = pallet.OriginCode,
                OriginName = pallet.OriginName,
                CarrierName = pallet.CarrierGUID == null ? null : carrierNames.GetValueOrDefault(pallet.CarrierGUID),
                ConsignmentNo = pallet.ConsignmentNo,
                CartonCount = own.Count,
                ReceivedCartonCount = own.Count(carton => carton.Status != PackCartonStatuses.InTransit),
                PieceCount = own.Sum(carton => pieces.GetValueOrDefault(carton.CartonGUID)),
                DispatchedAtUtc = pallet.DispatchedAtUtc,
                DispatchedByName = pallet.DispatchedByName,
                ReceivedAtUtc = pallet.ReceivedAtUtc,
                ReceivedByName = pallet.ReceivedByName,
            };
        }

        return result;
    }

    private async Task<string> DescribeDestinationAsync(string? destinationCode)
    {
        var name = string.IsNullOrEmpty(destinationCode) ? null : await OwnerNameAsync(destinationCode);
        return $"发往 {name ?? destinationCode ?? "--"}";
    }
}
