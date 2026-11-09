using BlazorApp.Api.Data;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.HqEntities;
using SqlSugar;

namespace BlazorApp.Api.Features.StoreOrders.Orders.Infrastructure;

internal sealed class StoreOrderLookupQueryStore(
    SqlSugarContext context,
    StoreOrderStoreIdentityReader storeIdentityReader,
    IStoreOrderOrdersHqConnectionFactory hqConnectionFactory,
    ILogger<StoreOrderLookupQueryStore> logger
)
{
    private readonly ISqlSugarClient _db = context.Db;

    internal async Task<List<BranchDto>> GetUsedBranchesAsync()
    {
        var usedStoreCodes = await _db.Queryable<WareHouseOrder>()
            .Where(order => !order.IsDeleted && !string.IsNullOrEmpty(order.StoreCode))
            .Select(order => order.StoreCode)
            .Distinct()
            .ToListAsync();
        if (usedStoreCodes.Count == 0)
        {
            return new List<BranchDto>();
        }

        var guidCodes = usedStoreCodes.Where(code => Guid.TryParse(code, out _)).ToList();
        var normalCodes = usedStoreCodes
            .Where(code => !Guid.TryParse(code, out _))
            .ToList();
        logger.LogInformation(
            "分店订货筛选分店解析开始：订单标识 {Total} 个，数字分店代码 {StoreCodeCount} 个，外购客户 HGUID {ExternalCustomerCount} 个",
            usedStoreCodes.Count,
            normalCodes.Count,
            guidCodes.Count
        );
        var branches = await _db.Queryable<Store>()
            .Where(store => normalCodes.Contains(store.StoreCode))
            .Select(store => new
            {
                Guid = store.StoreGUID,
                Code = store.StoreCode,
                Name = store.StoreName,
            })
            .ToListAsync();

        var externalCustomers = new List<BranchDto>();
        if (!LegacyDatabaseGate.HqEnabled)
        {
            externalCustomers = guidCodes
                .Where(StoreOrderExternalCustomerSnapshot.NamesByGuid.ContainsKey)
                .Select(code => new BranchDto
                {
                    Guid = code,
                    Code = code,
                    Name = StoreOrderExternalCustomerSnapshot.NamesByGuid[code],
                })
                .ToList();
        }
        else if (guidCodes.Count > 0)
        {
            try
            {
                using var hqDb = hqConnectionFactory.Create();
                externalCustomers = await hqDb.Queryable<CPT_DIC_外购客户信息表>()
                    .Where(customer =>
                        SqlFunc.HasValue(customer.HGUID)
                        && guidCodes.Contains(customer.HGUID!)
                    )
                    .Select(customer => new BranchDto
                    {
                        Guid = customer.HGUID!,
                        Code = customer.HGUID!,
                        Name = customer.客户名称 ?? customer.HGUID!,
                    })
                    .ToListAsync();
            }
            catch (Exception ex) when (IsRemoteConnectionFailure(ex))
            {
                // HQ 短暂不可达时只降级外购客户，不能让本地分店筛选一并失败。
                logger.LogWarning(
                    ex,
                    "外购客户分店筛选查询失败，已降级为仅返回本地分店。外购客户标识 {Count} 个，示例: {Codes}",
                    guidCodes.Count,
                    string.Join(", ", guidCodes.Take(5))
                );
            }
        }

        var result = new List<BranchDto>();
        var missingCodes = new List<string>();
        foreach (var code in usedStoreCodes)
        {
            var branch = branches.FirstOrDefault(candidate => candidate.Code == code);
            if (branch != null)
            {
                result.Add(
                    new BranchDto
                    {
                        Guid = branch.Guid,
                        Code = branch.Code,
                        Name = branch.Name,
                    }
                );
                continue;
            }

            var externalCustomer = externalCustomers.FirstOrDefault(candidate =>
                candidate.Code == code
            );
            if (externalCustomer != null)
            {
                result.Add(externalCustomer);
            }
            else
            {
                missingCodes.Add(code ?? string.Empty);
            }
        }

        if (missingCodes.Count > 0)
        {
            logger.LogWarning(
                "订单中存在 {Count} 个无法匹配分店表的分店标识，已从筛选列表忽略。示例: {Codes}",
                missingCodes.Count,
                string.Join(", ", missingCodes.Take(5))
            );
        }
        logger.LogInformation(
            "分店订货筛选分店解析完成：本地分店匹配 {StoreMatchedCount}/{StoreCodeCount}，外购客户匹配 {ExternalMatchedCount}/{ExternalCustomerCount}，未匹配 {MissingCount}",
            branches.Count,
            normalCodes.Count,
            externalCustomers.Count,
            guidCodes.Count,
            missingCodes.Count
        );
        return result
            .OrderBy(branch => branch.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(branch => branch.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal async Task<List<UnmatchedStoreOrderGroupDto>> GetUnmatchedGroupsAsync()
    {
        var unmatchedCodes = await storeIdentityReader.GetUnmatchedOrderStoreCodesAsync();
        if (unmatchedCodes.Count == 0)
        {
            return new List<UnmatchedStoreOrderGroupDto>();
        }

        var groupRows = await _db.Queryable<WareHouseOrder>()
            .Where(order =>
                !order.IsDeleted
                && order.StoreCode != null
                && unmatchedCodes.Contains(order.StoreCode)
            )
            .GroupBy(order => order.StoreCode)
            .Select(order => new UnmatchedStoreOrderGroupRow
            {
                SourceStoreCode = order.StoreCode!,
                OrderCount = SqlFunc.AggregateCount(order.OrderGUID),
                LatestOrderDate = SqlFunc.AggregateMax(order.OrderDate),
            })
            .ToListAsync();
        var sourceNameMap = await LoadExternalCustomerNameMapAsync(unmatchedCodes);
        return groupRows
            .Where(row => !string.IsNullOrWhiteSpace(row.SourceStoreCode))
            .Select(row =>
            {
                sourceNameMap.TryGetValue(row.SourceStoreCode, out var sourceName);
                return new UnmatchedStoreOrderGroupDto
                {
                    SourceStoreCode = row.SourceStoreCode,
                    SourceStoreName = sourceName,
                    OrderCount = row.OrderCount,
                    LatestOrderDate = row.LatestOrderDate,
                };
            })
            .OrderByDescending(group => group.OrderCount)
            .ThenByDescending(group => group.LatestOrderDate)
            .ThenBy(group => group.SourceStoreCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<Dictionary<string, string>> LoadExternalCustomerNameMapAsync(
        HashSet<string> sourceCodes
    )
    {
        var hqGuids = sourceCodes.Where(code => Guid.TryParse(code, out _)).ToList();
        if (hqGuids.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        if (!LegacyDatabaseGate.HqEnabled)
        {
            return hqGuids
                .Where(StoreOrderExternalCustomerSnapshot.NamesByGuid.ContainsKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    code => code,
                    code => StoreOrderExternalCustomerSnapshot.NamesByGuid[code],
                    StringComparer.OrdinalIgnoreCase
                );
        }

        using var hqDb = hqConnectionFactory.Create();
        var customers = await hqDb.Queryable<CPT_DIC_外购客户信息表>()
            .Where(customer =>
                customer.HGUID != null && hqGuids.Contains(customer.HGUID)
            )
            .Select(customer => new { customer.HGUID, customer.客户名称 })
            .ToListAsync();
        return customers
            .Where(customer => !string.IsNullOrWhiteSpace(customer.HGUID))
            .GroupBy(customer => customer.HGUID!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().客户名称 ?? group.Key,
                StringComparer.OrdinalIgnoreCase
            );
    }

    private static bool IsRemoteConnectionFailure(Exception exception)
    {
        var message = exception.ToString();
        return message.Contains("Connection open error", StringComparison.OrdinalIgnoreCase)
            || message.Contains(
                "连接数据库过程中发生错误",
                StringComparison.OrdinalIgnoreCase
            )
            || message.Contains(
                "network-related or instance-specific error",
                StringComparison.OrdinalIgnoreCase
            )
            || message.Contains("server was not found", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class UnmatchedStoreOrderGroupRow
    {
        public string SourceStoreCode { get; set; } = string.Empty;

        public int OrderCount { get; set; }

        public DateTime? LatestOrderDate { get; set; }
    }
}
