using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services.Background;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBSalesRecord;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;

namespace BlazorApp.Api.Services
{
    /// <summary>销售统计垂直切片：SalesStatisticsSupplierSlice。</summary>
    internal sealed class SalesStatisticsSupplierSlice : SalesStatisticsSliceBase
    {
        public SalesStatisticsSupplierSlice(SalesStatisticsSliceContext shared)
            : base(shared) { }

    public async Task UpdateSupplierStatistics(
        DateTime? startDate = null,
        DateTime? endDate = null,
        List<string>? supplierCodes = null
    )
    {
        try
        {
            var targetStartDate = startDate ?? SalesStatisticsBusinessDate.Today();
            var targetEndDate = endDate ?? targetStartDate;
            var targetEndExclusive = targetEndDate.AddDays(1);

            if (targetStartDate > targetEndDate)
            {
                throw new ArgumentException("开始日期不能大于结束日期");
            }

            var targetSupplierCodes = SalesStatisticsCodeRules.NormalizeSupplierCodes(
                supplierCodes
            );

            _logger.LogInformation(
                "开始更新指定供应商统计数据: {StartDate} 至 {EndDate}, Suppliers: {Suppliers}",
                targetStartDate.ToString("yyyy-MM-dd"),
                targetEndDate.ToString("yyyy-MM-dd"),
                supplierCodes != null ? string.Join(", ", supplierCodes) : "All"
            );

            // 构建查询，关联销售订单、订单明细和产品供应商映射表
            var query = _posmContext
                .Db.Queryable<SalesOrder>()
                .LeftJoin<SalesOrderDetail>((o, d) => o.OrderGuid == d.OrderGuid)
                .LeftJoin<PosmProductSupplierMapping>(
                    (o, d, m) => d.ProductCode == m.ProductCode
                )
                // 金额、数量按净额并入退款单（Status=3，负数）；门店数、订单数只数 1/4。
                .Where(o =>
                    o.Status != null
                    && SalesOrderStatusRules.NetSalesStatuses.Contains(o.Status)
                    && o.OrderTime != null
                );

            // 使用半开区间过滤，避免不同数据库对 DateTime.Date 的翻译差异。
            query = query.Where(o =>
                o.OrderTime >= targetStartDate
                && o.OrderTime < targetEndExclusive
            );

            // 设置供应商代码过滤条件
            if (targetSupplierCodes.Any())
            {
                var includesUnknownSupplier = targetSupplierCodes.Contains(UnknownSupplierCode);
                query = query.Where(
                    (o, d, m) =>
                        (m.LocalSupplierCode != null
                            && targetSupplierCodes.Contains(m.LocalSupplierCode.Trim()))
                        || (m.ChinaSupplierCode != null
                            && targetSupplierCodes.Contains(m.ChinaSupplierCode.Trim()))
                        || (d.SupplierCode != null
                            && targetSupplierCodes.Contains(d.SupplierCode.Trim()))
                        || (
                            includesUnknownSupplier
                            && (m.LocalSupplierCode == null || m.LocalSupplierCode.Trim() == "")
                            && (d.SupplierCode == null || d.SupplierCode.Trim() == "")
                        )
                );
            }

            // 获取原始销售数据
            var rawData = await query
                .Select(
                    (o, d, m) =>
                        new
                        {
                            Date = o.OrderTime!.Value.Date,
                            BranchCode = o.BranchCode,
                            DetailSupplierCode = d.SupplierCode,
                            LocalSupplierCode = m.LocalSupplierCode,
                            ChinaSupplierCode = m.ChinaSupplierCode,
                            TotalAmount = d.ActualAmount,
                            Quantity = d.Quantity ?? 0m,
                            OrderGuid = o.OrderGuid,
                            OrderStatus = o.Status,
                        }
                )
                .ToListAsync();
            var orderAmountMaps = await SalesStatisticsProductStoreDailySourceQueries
                .LoadOrderAmountMapsAsync(
                _posmContext,
                targetStartDate,
                targetEndExclusive,
                rawData,
                row => row.OrderGuid,
                row => row.TotalAmount ?? 0m
            );

            var resolvedRows = rawData
                .Select(x => new
                {
                    x.Date,
                    BranchCode = SalesStatisticsCodeRules.Normalize(x.BranchCode),
                    LocalSupplierCode = SalesStatisticsProductStoreDailyDomainRules
                        .ResolveStatisticSupplierCode(
                        x.LocalSupplierCode,
                        x.DetailSupplierCode
                    ),
                    ChinaSupplierCode = SalesStatisticsCodeRules.Normalize(x.ChinaSupplierCode),
                    TotalAmount = SalesStatisticsProductStoreDailyDomainRules
                        .ResolveStatisticAmount(
                        x.OrderGuid,
                        x.TotalAmount ?? 0m,
                        orderAmountMaps.PaymentAmounts,
                        orderAmountMaps.DetailAmounts
                    ),
                    x.Quantity,
                    x.OrderGuid,
                    x.OrderStatus,
                })
                .ToList();

            var shouldRefreshAllSuppliers = !targetSupplierCodes.Any();
            var refreshesLocalMasterSupplier = targetSupplierCodes.Contains("200");
            var localRowsForStats = shouldRefreshAllSuppliers
                ? resolvedRows
                : resolvedRows.Where(x => targetSupplierCodes.Contains(x.LocalSupplierCode)).ToList();
            var chinaRowsForStats = resolvedRows
                .Where(x =>
                    x.LocalSupplierCode == "200"
                    && !string.IsNullOrEmpty(x.ChinaSupplierCode)
                    && (
                        shouldRefreshAllSuppliers
                        || refreshesLocalMasterSupplier
                        || targetSupplierCodes.Contains(x.ChinaSupplierCode)
                    )
                )
                .ToList();

            // 1. 本地供应商聚合：局部刷新国内子供应商时不重写本地 200 总计。
            var localStats = localRowsForStats
                .GroupBy(x => new { x.Date, x.LocalSupplierCode })
                .Select(g => new SupplierSalesStatistic
                {
                    Date = g.Key.Date,
                    SupplierCode = g.Key.LocalSupplierCode,
                    IsDomestic = false,
                    TotalAmount = g.Sum(x => x.TotalAmount),
                    TotalQuantity = (int)g.Sum(x => x.Quantity),
                    // 退款单只冲减金额/数量，不计门店数与订单数。
                    StoreCount = g.Where(x => !SalesOrderStatusRules.IsRefundOrder(x.OrderStatus))
                        .Select(x => x.BranchCode)
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Distinct()
                        .Count(),
                    OrderCount = g.Where(x => !SalesOrderStatusRules.IsRefundOrder(x.OrderStatus))
                        .Select(x => x.OrderGuid)
                        .Where(orderGuid => !string.IsNullOrWhiteSpace(orderGuid))
                        .Distinct()
                        .Count(),
                    UpdateTime = DateTime.Now,
                })
                .ToList();

            // 2. 国内供应商聚合
            // 仅针对 LocalSupplierCode == "200" 的记录进行二次聚合
            // 这些记录如果包含 ChinaSupplierCode，则按 ChinaSupplierCode 再统计一次
            // 这样可以得到每个具体的国内供应商的销量数据
            // 这些数据的 IsDomestic 标记为 true
            var chinaStats = chinaRowsForStats
                .GroupBy(x => new { x.Date, x.ChinaSupplierCode })
                .Select(g => new SupplierSalesStatistic
                {
                    Date = g.Key.Date,
                    SupplierCode = g.Key.ChinaSupplierCode ?? string.Empty,
                    IsDomestic = true,
                    TotalAmount = g.Sum(x => x.TotalAmount),
                    TotalQuantity = (int)g.Sum(x => x.Quantity),
                    // 退款单只冲减金额/数量，不计门店数与订单数。
                    StoreCount = g.Where(x => !SalesOrderStatusRules.IsRefundOrder(x.OrderStatus))
                        .Select(x => x.BranchCode)
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Distinct()
                        .Count(),
                    OrderCount = g.Where(x => !SalesOrderStatusRules.IsRefundOrder(x.OrderStatus))
                        .Select(x => x.OrderGuid)
                        .Where(orderGuid => !string.IsNullOrWhiteSpace(orderGuid))
                        .Distinct()
                        .Count(),
                    UpdateTime = DateTime.Now,
                })
                .ToList();

            // 合并本地供应商和国内供应商统计
            var allStats = localStats.Concat(chinaStats).ToList();

            // 获取供应商名称
            var allLocalCodes = localStats.Select(x => x.SupplierCode).Distinct().ToList();
            var allChinaCodes = chinaStats.Select(x => x.SupplierCode).Distinct().ToList();

            var supplierNameDict = new Dictionary<string, string>();

            // 查询本地供应商名称
            if (allLocalCodes.Any())
            {
                var localSuppliers = await _context.HBLocalSupplierDb.GetListAsync(s =>
                    s.LocalSupplierCode != null
                    && allLocalCodes.Contains(s.LocalSupplierCode)
                    && !s.IsDeleted
                );
                foreach (var s in localSuppliers)
                {
                    if (!string.IsNullOrEmpty(s.LocalSupplierCode))
                    {
                        supplierNameDict[s.LocalSupplierCode] = s.Name ?? s.LocalSupplierCode;
                    }
                }
            }

            // 查询国内供应商名称
            if (allChinaCodes.Any())
            {
                var chinaSuppliers = await _context.ChinaSupplierDb.GetListAsync(s =>
                    s.SupplierCode != null
                    && allChinaCodes.Contains(s.SupplierCode)
                    && !s.IsDeleted
                );
                foreach (var s in chinaSuppliers)
                {
                    if (!string.IsNullOrEmpty(s.SupplierCode))
                    {
                        supplierNameDict[s.SupplierCode] = s.SupplierName ?? s.SupplierCode;
                    }
                }
            }

            // 为统计记录填充供应商名称
            foreach (var stat in allStats)
            {
                if (stat.SupplierCode == UnknownSupplierCode)
                {
                    stat.SupplierName = UnknownSupplierName;
                }
                else if (supplierNameDict.TryGetValue(stat.SupplierCode, out var name))
                {
                    stat.SupplierName = name;
                }
                else
                {
                    stat.SupplierName = stat.SupplierCode;
                }
            }

            await SalesStatisticsTransactionExecutor.ExecuteAsync(
                beginAsync: () => _context.Db.Ado.BeginTranAsync(),
                workAsync: async () =>
                {
                    // 供应商统计按目标范围重建，避免旧空供应商或已无销售供应商残留。
                    var deleteable = _context.Db.Deleteable<SupplierSalesStatistic>()
                        .Where(s => s.Date >= targetStartDate && s.Date <= targetEndDate);
                    if (targetSupplierCodes.Any())
                    {
                        var deleteSupplierCodes = new List<string>();
                        if (refreshesLocalMasterSupplier)
                        {
                            var existingDomesticSupplierCodes = await _context
                                .Db.Queryable<SupplierSalesStatistic>()
                                .Where(s =>
                                    s.Date >= targetStartDate
                                    && s.Date <= targetEndDate
                                    && s.IsDomestic == true
                                )
                                .Select(s => s.SupplierCode)
                                .Distinct()
                                .ToListAsync();
                            deleteSupplierCodes.AddRange(existingDomesticSupplierCodes);
                        }

                        deleteSupplierCodes = deleteSupplierCodes
                            .Concat(targetSupplierCodes)
                            .Concat(allStats.Select(s => s.SupplierCode))
                            .Where(code => !string.IsNullOrWhiteSpace(code))
                            .Select(code => code.Trim())
                            .Distinct()
                            .ToList();
                        var includesUnknownSupplier = targetSupplierCodes.Contains(UnknownSupplierCode);
                        deleteable = deleteable.Where(s =>
                            deleteSupplierCodes.Contains(s.SupplierCode.Trim())
                            || (
                                includesUnknownSupplier
                                && (s.SupplierCode == null || s.SupplierCode.Trim() == "")
                            )
                        );
                    }

                    var deletedCount = await deleteable.ExecuteCommandAsync();
                    _logger.LogInformation("删除 {Count} 条供应商统计旧记录", deletedCount);

                    if (!allStats.Any())
                    {
                        _logger.LogInformation(
                            "没有找到供应商统计数据: {StartDate} 至 {EndDate}",
                            targetStartDate.ToString("yyyy-MM-dd"),
                            targetEndDate.ToString("yyyy-MM-dd")
                        );
                        return;
                    }

                    // 批量插入新记录
                    _context
                        .Db.Fastest<SupplierSalesStatistic>()
                        .PageSize(BatchSize)
                        .BulkCopy(allStats);
                },
                commitAsync: () => _context.Db.Ado.CommitTranAsync(),
                rollbackAsync: () => _context.Db.Ado.RollbackTranAsync(),
                logger: _logger,
                operationName: "指定供应商统计数据更新"
            );

            _logger.LogInformation(
                "指定供应商统计数据更新完成: {StartDate} 至 {EndDate}, 总记录: {Total}",
                targetStartDate.ToString("yyyy-MM-dd"),
                targetEndDate.ToString("yyyy-MM-dd"),
                allStats.Count
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "更新指定供应商统计数据失败: {StartDate} 至 {EndDate}",
                startDate?.ToString("yyyy-MM-dd"),
                endDate?.ToString("yyyy-MM-dd")
            );
            throw;
        }
    }

    }
}
