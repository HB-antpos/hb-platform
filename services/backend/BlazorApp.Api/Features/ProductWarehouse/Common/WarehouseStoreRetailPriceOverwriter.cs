using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Features.ProductWarehouse;

/// <summary>
/// 「同步分店零售价」勾选后的统一落地：把本次改了仓库零售价的商品，直接改写各启用分店的零售价，
/// 并按「生成价格任务」开关登记待换标签任务，或在「同步 + 不生成任务」时于改价当下同事务入队 HQ。
/// 所有改仓库零售价的入口（批量修改、货柜明细）共用本类，保证跳过规则与任务 / HQ 语义完全一致。
/// 调用方必须已在事务内（<paramref name="db"/> 是带事务的连接）：入队失败要让改价一起回滚。
/// </summary>
internal sealed class WarehouseStoreRetailPriceOverwriter
{
    // 价格任务的发起来源，与仓库自动下发共用同一标识（任务页按它显示「仓库下发」）。
    private const string TaskSource = "WarehouseAutoSync";
    private const string SystemUpdatedBy = "System";

    private readonly ISqlSugarClient _db;
    private readonly IStorePriceUpdateTaskService? _priceTaskService;
    private readonly IWarehouseStorePriceHqSyncEnqueuer? _hqPriceSyncEnqueuer;
    private readonly ILogger _logger;

    internal WarehouseStoreRetailPriceOverwriter(
        ISqlSugarClient db,
        IStorePriceUpdateTaskService? priceTaskService,
        IWarehouseStorePriceHqSyncEnqueuer? hqPriceSyncEnqueuer,
        ILogger logger
    )
    {
        _db = db;
        _priceTaskService = priceTaskService;
        _hqPriceSyncEnqueuer = hqPriceSyncEnqueuer;
        _logger = logger;
    }

    /// <summary>
    /// 把本次改了零售价的商品的各启用分店零售价直接改成新价，并按开关登记待换标签任务 / 当下同步 HQ。
    /// 特殊商品（分店刻意定价）与自动定价行（零售价由进价策略推导，改了也会被立刻重算）不覆盖；
    /// 新价与分店现价相同的行不动，也就不会产生无意义的任务。
    /// </summary>
    /// <returns>实际被改写的分店价行数。</returns>
    internal async Task<int> OverwriteAsync(
        IReadOnlyDictionary<string, decimal> retailByCode,
        WarehousePriceSyncPolicy policy,
        string updatedBy,
        string hqSource
    )
    {
        if (retailByCode.Count == 0)
        {
            return 0;
        }

        var activeStoreCodes = (
            await _db
                .Queryable<Store>()
                .Where(store => store.IsActive && !store.IsDeleted)
                .Select(store => store.StoreCode)
                .ToListAsync()
        )
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct()
            .ToList();
        if (activeStoreCodes.Count == 0)
        {
            return 0;
        }

        var rows = new List<StoreRetailPrice>();
        foreach (var codeBatch in retailByCode.Keys.Chunk(500))
        {
            var batch = codeBatch.ToList();
            rows.AddRange(
                await _db
                    .Queryable<StoreRetailPrice>()
                    .Where(price =>
                        price.ProductCode != null
                        && batch.Contains(price.ProductCode)
                        && price.StoreCode != null
                        && activeStoreCodes.Contains(price.StoreCode)
                        && !price.IsDeleted
                        && !price.IsSpecialProduct
                        && !price.IsAutoPricing
                    )
                    .ToListAsync()
            );
        }

        var now = DateTime.Now;
        var overwrites = new List<StorePriceOverwrite>();
        var changed = new List<StoreRetailPrice>();
        foreach (var row in rows)
        {
            if (!retailByCode.TryGetValue(row.ProductCode!, out var newPrice))
            {
                continue;
            }
            if (row.StoreRetailPriceValue == newPrice)
            {
                continue;
            }

            // 覆盖前的分店零售价就是货架标签上的价格，必须在就地改写前留存，否则无法登记「待换标签」。
            overwrites.Add(
                new StorePriceOverwrite(
                    row.StoreCode!,
                    row.ProductCode!,
                    row.StoreRetailPriceValue,
                    row.DiscountRate
                )
            );
            row.StoreRetailPriceValue = newPrice;
            row.UpdatedAt = now;
            row.UpdatedBy = updatedBy;
            changed.Add(row);
        }
        if (changed.Count == 0)
        {
            return 0;
        }

        foreach (var chunk in changed.Chunk(200))
        {
            await _db
                .Updateable(chunk.ToList())
                .UpdateColumns(price => new
                {
                    price.StoreRetailPriceValue,
                    price.UpdatedAt,
                    price.UpdatedBy,
                })
                .ExecuteCommandAsync();
        }
        await RecordOverwritesAsync(overwrites, updatedBy, policy.GenerateTasks);

        if (policy.ShouldSyncHqImmediately && _hqPriceSyncEnqueuer != null)
        {
            // 直接改了分店价又不生成任务：没有「任务执行」可等，改价当下同事务入队同步 HQ。
            // 与待换标签通知不同，这里不吞异常：入队失败必须让整笔改价一起回滚，否则 HBweb 与 HQ 会悄悄分叉。
            foreach (var group in changed.GroupBy(price => price.ProductCode!))
            {
                await _hqPriceSyncEnqueuer.EnqueueAsync(
                    _db,
                    group.Key,
                    group.Select(price => price.StoreCode),
                    new[] { ProductMaintenanceHqFieldMasks.StoreRetailPrice },
                    updatedBy,
                    hqSource
                );
            }
        }

        return changed.Count;
    }

    /// <summary>
    /// 为价格确实发生变化的分店登记「待换标签」通知。
    /// 通知失败不能影响改价本身，因此只记录日志；移动端列表与定时对账会兜底。
    /// </summary>
    private async Task RecordOverwritesAsync(
        IReadOnlyCollection<StorePriceOverwrite> overwrites,
        string updatedBy,
        bool generateTasks
    )
    {
        if (!generateTasks || _priceTaskService == null || overwrites.Count == 0)
        {
            return;
        }

        try
        {
            await _priceTaskService.RecordStoreOverwritesAsync(
                overwrites,
                new PriceTaskInitiator(
                    string.IsNullOrWhiteSpace(updatedBy) ? SystemUpdatedBy : updatedBy,
                    TaskSource
                )
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "登记分店待换标签通知失败: Count={Count}", overwrites.Count);
        }
    }
}
