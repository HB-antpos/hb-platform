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
    /// <summary>销售统计垂直切片：SalesStatisticsStoreSupplierSlice。</summary>
    internal sealed class SalesStatisticsStoreSupplierSlice : SalesStatisticsSliceBase
    {
        public SalesStatisticsStoreSupplierSlice(SalesStatisticsSliceContext shared)
            : base(shared) { }

    internal static List<StoreSupplierSalesDetail> BuildStoreSupplierSalesDetails(
        IEnumerable<StoreSupplierSourceRow> storeSupplierData,
        IReadOnlyDictionary<string, HBLocalSupplier> localSupplierDict,
        IReadOnlyDictionary<string, ChinaSupplier> chinaSupplierDict,
        DateTime updateTime
    )
    {
        var resolvedRows = new List<StoreSupplierResolvedRow>();

        foreach (var data in storeSupplierData)
        {
            // 分店维度统计必须有有效分店编码，避免把空编码写入统计表。
            if (string.IsNullOrWhiteSpace(data.BranchCode))
                continue;

            var branchCode = data.BranchCode.Trim();
            var mappedLocalSupplierCode = data.LocalSupplierCode?.Trim() ?? string.Empty;
            var detailSupplierCode = data.DetailSupplierCode?.Trim() ?? string.Empty;
            var localSupplierCode = !string.IsNullOrWhiteSpace(mappedLocalSupplierCode)
                ? mappedLocalSupplierCode
                : detailSupplierCode;
            var chinaSupplierCode = data.ChinaSupplierCode?.Trim();

            var supplierCode = localSupplierCode;
            var supplierName = localSupplierCode;
            var isDomestic = false;

            // 映射缺失或本地供应商为空时，优先回退 POSM 明细供应商，仍为空才归入 UNKNOWN，避免空供应商主键冲突。
            if (string.IsNullOrWhiteSpace(supplierCode))
            {
                supplierCode = UnknownSupplierCode;
                supplierName = UnknownSupplierName;
            }
            else if (localSupplierCode == "200" && !string.IsNullOrWhiteSpace(chinaSupplierCode))
            {
                supplierCode = chinaSupplierCode;
                isDomestic = true;
                if (chinaSupplierDict.TryGetValue(chinaSupplierCode, out var cs))
                {
                    supplierName = cs.SupplierName ?? supplierCode;
                }
                else
                {
                    supplierName = supplierCode;
                }
            }
            else if (localSupplierDict.TryGetValue(localSupplierCode, out var ls))
            {
                supplierName = ls.Name ?? localSupplierCode;
            }

            resolvedRows.Add(new StoreSupplierResolvedRow
            {
                Date = data.Date.Date,
                BranchCode = branchCode,
                SupplierCode = supplierCode,
                SupplierName = supplierName,
                IsDomestic = isDomestic,
                OrderGuid = data.OrderGuid,
                OrderStatus = data.OrderStatus,
                TotalAmount = data.ActualAmount,
                TotalQuantity = (int)data.Quantity,
            });
        }

        // 最终供应商编码可能来自不同路径（映射、明细、UNKNOWN），写表前必须按真实主键再次合并。
        return resolvedRows
            .GroupBy(stat => new { stat.Date, stat.BranchCode, stat.SupplierCode })
            .Select(group =>
            {
                // 退款单只冲减金额/数量，不计订单数。
                var orderCount = group
                    .Where(x => !SalesOrderStatusRules.IsRefundOrder(x.OrderStatus))
                    .Select(x => x.OrderGuid)
                    .Where(orderGuid => !string.IsNullOrWhiteSpace(orderGuid))
                    .Distinct()
                    .Count();
                return new StoreSupplierSalesDetail
                {
                    Date = group.Key.Date,
                    BranchCode = group.Key.BranchCode,
                    SupplierCode = group.Key.SupplierCode,
                    SupplierName = group.Select(x => x.SupplierName)
                        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? group.Key.SupplierCode,
                    IsDomestic = group.Any(x => x.IsDomestic == true),
                    TotalAmount = group.Sum(x => x.TotalAmount),
                    TotalQuantity = group.Sum(x => x.TotalQuantity),
                    OrderCount = orderCount,
                    UpdateTime = updateTime,
                };
            })
            .ToList();
    }

    internal static List<AustralianSupplierStoreSalesDetail> BuildAustralianSupplierStoreSalesDetails(
        IEnumerable<StoreSupplierSourceRow> storeSupplierData,
        IReadOnlyDictionary<string, HBLocalSupplier> localSupplierDict,
        DateTime updateTime
    )
    {
        var resolvedRows = new List<StoreSupplierResolvedRow>();

        foreach (var data in storeSupplierData)
        {
            // 分店维度统计必须有有效分店编码，避免把空编码写入统计表。
            if (string.IsNullOrWhiteSpace(data.BranchCode))
                continue;

            var branchCode = data.BranchCode.Trim();
            var mappedLocalSupplierCode = data.LocalSupplierCode?.Trim() ?? string.Empty;
            var detailSupplierCode = data.DetailSupplierCode?.Trim() ?? string.Empty;
            var supplierCode = !string.IsNullOrWhiteSpace(mappedLocalSupplierCode)
                ? mappedLocalSupplierCode
                : detailSupplierCode;
            var supplierName = supplierCode;

            // 澳洲供应商统计只认本地供应商编码；映射为空时回退明细供应商，仍为空才归入 UNKNOWN。
            if (string.IsNullOrWhiteSpace(supplierCode))
            {
                supplierCode = UnknownSupplierCode;
                supplierName = UnknownSupplierName;
            }
            else if (localSupplierDict.TryGetValue(supplierCode, out var localSupplier))
            {
                supplierName = localSupplier.Name ?? supplierCode;
            }

            resolvedRows.Add(new StoreSupplierResolvedRow
            {
                Date = data.Date.Date,
                BranchCode = branchCode,
                SupplierCode = supplierCode,
                SupplierName = supplierName,
                OrderGuid = data.OrderGuid,
                OrderStatus = data.OrderStatus,
                TotalAmount = data.ActualAmount,
                TotalQuantity = (int)data.Quantity,
            });
        }

        // 最终供应商编码可能由映射、明细或 UNKNOWN 得到，写入前必须按真实主键二次聚合。
        return resolvedRows
            .GroupBy(stat => new { stat.Date, stat.BranchCode, stat.SupplierCode })
            .Select(group =>
            {
                // 退款单只冲减金额/数量，不计订单数。
                var orderCount = group
                    .Where(x => !SalesOrderStatusRules.IsRefundOrder(x.OrderStatus))
                    .Select(x => x.OrderGuid)
                    .Where(orderGuid => !string.IsNullOrWhiteSpace(orderGuid))
                    .Distinct()
                    .Count();
                return new AustralianSupplierStoreSalesDetail
                {
                    Date = group.Key.Date,
                    BranchCode = group.Key.BranchCode,
                    SupplierCode = group.Key.SupplierCode,
                    SupplierName = group.Select(x => x.SupplierName)
                        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? group.Key.SupplierCode,
                    TotalAmount = group.Sum(x => x.TotalAmount),
                    TotalQuantity = group.Sum(x => x.TotalQuantity),
                    OrderCount = orderCount,
                    UpdateTime = updateTime,
                };
            })
            .ToList();
    }

    /// <summary>
    /// 更新门店供应商统计数据
    /// 按门店和供应商维度聚合销售数据
    /// </summary>
    /// <param name="date">目标日期，为空则更新当天</param>
    /// <param name="branchCodes">分店代码列表，为空则更新所有分店</param>
    /// <param name="supplierCodes">供应商代码列表，为空则更新所有供应商</param>
    public async Task UpdateStoreSupplierStatistics(
        DateTime? date = null,
        List<string>? branchCodes = null,
        List<string>? supplierCodes = null
    )
    {
        try
        {
            var targetDate = (date ?? SalesStatisticsBusinessDate.Today()).Date;
            var nextDate = targetDate.AddDays(1);
            var targetBranchCodes = SalesStatisticsCodeRules.NormalizeBranchCodes(branchCodes);
            var targetSupplierCodes = SalesStatisticsCodeRules.NormalizeSupplierCodes(
                supplierCodes
            );

            _logger.LogInformation(
                "开始更新门店供应商统计数据: {Date}, 分店: {Branches}, 供应商: {Suppliers}",
                targetDate,
                branchCodes != null ? string.Join(", ", branchCodes) : "All",
                supplierCodes != null ? string.Join(", ", supplierCodes) : "All"
            );

            // 构建查询
            var query = _posmContext
                .Db.Queryable<SalesOrder>()
                .LeftJoin<SalesOrderDetail>((o, d) => o.OrderGuid == d.OrderGuid)
                .LeftJoin<PosmProductSupplierMapping>(
                    (o, d, m) => d.ProductCode == m.ProductCode
                )
                // 金额、数量按净额并入退款单（Status=3，负数）；订单数只数 1/4。
                .Where(o =>
                    o.Status != null
                    && SalesOrderStatusRules.NetSalesStatuses.Contains(o.Status)
                    && o.OrderTime != null
                    && o.OrderTime >= targetDate
                    && o.OrderTime < nextDate
                );

            // 设置分店过滤条件
            if (targetBranchCodes.Any())
            {
                query = query.Where(o =>
                    (o.BranchCode != null && targetBranchCodes.Contains(o.BranchCode.Trim()))
                    || o.BranchCode == null
                    || o.BranchCode.Trim() == ""
                );
            }

            // 设置供应商过滤条件
            if (targetSupplierCodes.Any())
            {
                var includesUnknownSupplier = targetSupplierCodes.Contains(UnknownSupplierCode);
                query = query.Where(
                    (o, d, m) =>
                        (
                            m.LocalSupplierCode != null
                            && targetSupplierCodes.Contains(m.LocalSupplierCode.Trim())
                        )
                        || (
                            m.ChinaSupplierCode != null
                            && targetSupplierCodes.Contains(m.ChinaSupplierCode.Trim())
                        )
                        || (d.SupplierCode != null && targetSupplierCodes.Contains(d.SupplierCode.Trim()))
                        || (
                            includesUnknownSupplier
                            && (m.LocalSupplierCode == null || m.LocalSupplierCode.Trim() == "")
                            && (d.SupplierCode == null || d.SupplierCode.Trim() == "")
                        )
                );
            }

            // 查询销售明细后按最终供应商编码聚合，确保订单数按订单去重。
            var rawStoreSupplierData = await query
                .Select(
                    (o, d, m) =>
                        new
                        {
                            Date = o.OrderTime!.Value.Date,
                            BranchCode = o.BranchCode,
                            DeviceCode = o.DeviceCode,
                            OrderGuid = o.OrderGuid,
                            OrderStatus = o.Status,
                            DetailSupplierCode = d.SupplierCode,
                            LocalSupplierCode = m.LocalSupplierCode,
                            ChinaSupplierCode = m.ChinaSupplierCode,
                            ActualAmount = d.ActualAmount ?? 0m,
                            Quantity = d.Quantity ?? 0m,
                        }
                )
                .ToListAsync();
            var orderAmountMaps = await SalesStatisticsProductStoreDailySourceQueries
                .LoadOrderAmountMapsAsync(
                _posmContext,
                targetDate,
                nextDate,
                rawStoreSupplierData,
                row => row.OrderGuid,
                row => row.ActualAmount
            );
            var deviceBranchMap = await SalesStatisticsProductStoreDailySourceQueries
                .LoadDeviceBranchMapAsync(
                _posmContext,
                rawStoreSupplierData
                    .Where(row => string.IsNullOrWhiteSpace(row.BranchCode))
                    .Select(row => row.DeviceCode)
            );
            var storeSupplierData = rawStoreSupplierData
                .Select(row => new StoreSupplierSourceRow
                {
                    Date = row.Date,
                    BranchCode = SalesStatisticsCodeRules.ResolveBranchCode(
                        row.BranchCode,
                        row.DeviceCode,
                        deviceBranchMap
                    ),
                    DeviceCode = row.DeviceCode,
                    OrderGuid = row.OrderGuid,
                    OrderStatus = row.OrderStatus,
                    DetailSupplierCode = row.DetailSupplierCode,
                    LocalSupplierCode = row.LocalSupplierCode,
                    ChinaSupplierCode = row.ChinaSupplierCode,
                    ActualAmount = SalesStatisticsProductStoreDailyDomainRules.ResolveStatisticAmount(
                        row.OrderGuid,
                        row.ActualAmount,
                        orderAmountMaps.PaymentAmounts,
                        orderAmountMaps.DetailAmounts
                    ),
                    Quantity = row.Quantity,
                })
                .Where(row =>
                    !targetBranchCodes.Any()
                    || targetBranchCodes.Contains(row.BranchCode ?? string.Empty)
                )
                .ToList();

            // 获取所有本地供应商代码
            var allLocalSupplierCodes = storeSupplierData
                .Select(d =>
                    !string.IsNullOrWhiteSpace(d.LocalSupplierCode)
                        ? d.LocalSupplierCode!.Trim()
                        : d.DetailSupplierCode?.Trim()
                )
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!)
                .Distinct()
                .ToList();

            // 查询本地供应商信息
            var localSupplierDict = new Dictionary<string, HBLocalSupplier>();
            if (allLocalSupplierCodes.Any())
            {
                var localSuppliers = await _context.HBLocalSupplierDb.GetListAsync(s =>
                    s.LocalSupplierCode != null
                    && allLocalSupplierCodes.Contains(s.LocalSupplierCode)
                    && !s.IsDeleted
                );
                localSupplierDict = localSuppliers.ToDictionary(
                    s => s.LocalSupplierCode!,
                    s => s
                );
            }

            // 获取所有国内供应商代码
            var allChinaSupplierCodes = storeSupplierData
                .Where(d => !string.IsNullOrEmpty(d.ChinaSupplierCode))
                .Select(d => d.ChinaSupplierCode!)
                .Distinct()
                .ToList();

            // 查询国内供应商信息
            var chinaSupplierDict = new Dictionary<string, ChinaSupplier>();
            if (allChinaSupplierCodes.Any())
            {
                var chinaSuppliers = await _context.ChinaSupplierDb.GetListAsync(cs =>
                    cs.SupplierCode != null
                    && allChinaSupplierCodes.Contains(cs.SupplierCode)
                    && !cs.IsDeleted
                );
                chinaSupplierDict = chinaSuppliers
                    .Where(cs => !string.IsNullOrEmpty(cs.SupplierCode))
                    .ToDictionary(cs => cs.SupplierCode!, cs => cs);
            }

            LogSkippedBranchCodeRows(
                "分店供应商销售统计",
                storeSupplierData,
                data => data.BranchCode,
                data => data.ActualAmount,
                data => data.Quantity
            );

            var statisticsList = BuildStoreSupplierSalesDetails(
                storeSupplierData,
                localSupplierDict,
                chinaSupplierDict,
                DateTime.Now
            );

            await SalesStatisticsTransactionExecutor.ExecuteAsync(
                beginAsync: () => _context.Db.Ado.BeginTranAsync(),
                workAsync: async () =>
                {
                    // 局部重算只删除本次范围，避免清掉同一天其它门店或供应商的统计。
                    var deleteable = _context
                        .Db.Deleteable<StoreSupplierSalesDetail>()
                        .Where(s => s.Date == targetDate);
                    if (targetBranchCodes.Any())
                    {
                        deleteable = deleteable.Where(s => targetBranchCodes.Contains(s.BranchCode));
                    }
                    if (targetSupplierCodes.Any())
                    {
                        // 国内供应商和 UNKNOWN 会在构建阶段改写成最终编码，删除时必须覆盖最终编码。
                        var deleteSupplierCodes = new List<string>();
                        if (targetSupplierCodes.Contains("200"))
                        {
                            var existingDomesticQuery = _context
                                .Db.Queryable<StoreSupplierSalesDetail>()
                                .Where(s => s.Date == targetDate && s.IsDomestic == true);
                            if (targetBranchCodes.Any())
                            {
                                existingDomesticQuery = existingDomesticQuery.Where(s =>
                                    targetBranchCodes.Contains(s.BranchCode)
                                );
                            }

                            var existingDomesticSupplierCodes = await existingDomesticQuery
                                .Select(s => s.SupplierCode)
                                .Distinct()
                                .ToListAsync();
                            deleteSupplierCodes.AddRange(existingDomesticSupplierCodes);
                        }

                        deleteSupplierCodes = deleteSupplierCodes
                            .Concat(targetSupplierCodes)
                            .Concat(statisticsList.Select(s => s.SupplierCode))
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

                    var deletedCount = await deleteable
                        .ExecuteCommandAsync();
                    _logger.LogInformation("删除 {Count} 条门店供应商统计旧记录", deletedCount);

                    if (!statisticsList.Any())
                    {
                        _logger.LogInformation("没有找到门店供应商统计数据: {Date}", targetDate);
                        return;
                    }

                    // 批量插入新记录
                    _context
                        .Db.Fastest<StoreSupplierSalesDetail>()
                        .PageSize(BatchSize)
                        .BulkCopy(statisticsList);
                },
                commitAsync: () => _context.Db.Ado.CommitTranAsync(),
                rollbackAsync: () => _context.Db.Ado.RollbackTranAsync(),
                logger: _logger,
                operationName: "门店供应商统计数据更新"
            );

            _logger.LogInformation(
                "门店供应商统计数据更新完成: {Date}, 总记录: {Total}",
                targetDate,
                statisticsList.Count
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新门店供应商统计数据失败: {Date}", date);
            throw;
        }
    }

    /// <summary>
    /// 批量更新门店供应商统计数据
    /// 逐日更新指定日期范围内的门店供应商统计
    /// </summary>
    /// <param name="startDate">开始日期</param>
    /// <param name="endDate">结束日期</param>
    /// <param name="branchCodes">分店代码列表，为空则更新所有分店</param>
    /// <param name="supplierCodes">供应商代码列表，为空则更新所有供应商</param>
    /// <returns>批量更新结果</returns>
    public async Task<BatchStatisticsUpdateResult> BatchUpdateStoreSupplierStatistics(
        DateTime startDate,
        DateTime endDate,
        List<string>? branchCodes = null,
        List<string>? supplierCodes = null
    )
    {
        var result = new BatchStatisticsUpdateResult();
        // 验证日期范围
        var validation = ValidateDateRange(startDate, endDate);
        if (!validation.Success)
        {
            return validation;
        }

        result.TotalDays = (int)(endDate - startDate).TotalDays + 1;
        _logger.LogInformation(
            "开始批量更新门店供应商统计数据: {StartDate} 至 {EndDate}, 分店: {Branches}, 供应商: {Suppliers}",
            startDate.ToString("yyyy-MM-dd"),
            endDate.ToString("yyyy-MM-dd"),
            branchCodes != null ? string.Join(", ", branchCodes) : "All",
            supplierCodes != null ? string.Join(", ", supplierCodes) : "All"
        );

        // 逐日更新统计数据
        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            try
            {
                await UpdateStoreSupplierStatistics(date, branchCodes, supplierCodes);
                result.ProcessedDays++;
            }
            catch (Exception ex)
            {
                result.FailedDates.Add(date.ToString("yyyy-MM-dd"));
                _logger.LogError(ex, "批量更新门店供应商统计失败: {Date}", date);
            }
        }

        result.Success = result.FailedDates.Count == 0;
        result.Message = result.Success
            ? $"批量更新门店供应商统计完成: {result.ProcessedDays}/{result.TotalDays} 天"
            : $"批量更新门店供应商统计部分完成: {result.ProcessedDays}/{result.TotalDays} 天, 失败 {result.FailedDates.Count} 天";

        _logger.LogInformation(result.Message);
        return result;
    }

    }
}
