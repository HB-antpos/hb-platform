using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// 只读仓库商品档案查询服务。
    /// 摘要读取 WarehouseProduct + Product；货柜读取 ContainerDetail join Container；
    /// 配货读取 WareHouseOrderDetails join WareHouseOrder。全部走 SQL 投影，避免 N+1 与全表分页。
    /// </summary>
    public sealed class WarehouseProductRecordQueryService : IWarehouseProductRecordQueryService
    {
        private const int CancelledContainerStatus = 7;
        private const int MaxAllocationDayCount = 366;

        private readonly SqlSugarContext _context;
        private readonly ILogger<WarehouseProductRecordQueryService> _logger;
        private readonly TimeProvider _timeProvider;

        public WarehouseProductRecordQueryService(
            SqlSugarContext context,
            ILogger<WarehouseProductRecordQueryService> logger,
            TimeProvider? timeProvider = null
        )
        {
            _context = context;
            _logger = logger;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        public async Task<WarehouseProductRecordSummaryDto?> GetSummaryAsync(string productCode)
        {
            var normalized = NormalizeCode(productCode);
            if (string.IsNullOrEmpty(normalized))
                throw new ArgumentException("商品编码不能为空。", nameof(productCode));

            // 仅返回有效仓库商品；Product 辅助档缺失或已删除时以空字段回退。
            return await _context
                .Db.Queryable<WarehouseProduct>()
                .LeftJoin<Product>((wp, p) =>
                    p.ProductCode != null
                    && SqlFunc.ToUpper(wp.ProductCode.Trim()) == SqlFunc.ToUpper(p.ProductCode.Trim())
                    && !p.IsDeleted
                )
                .Where((wp, p) =>
                    !wp.IsDeleted
                    && SqlFunc.ToUpper(wp.ProductCode.Trim()) == normalized
                )
                .Select((wp, p) => new WarehouseProductRecordSummaryDto
                {
                    ProductCode = wp.ProductCode,
                    ItemNumber = p.ItemNumber,
                    Barcode = p.Barcode,
                    ProductName = p.ProductName,
                    EnglishName = p.EnglishName,
                    ImageUrl = p.ProductImage,
                    IsActive = wp.IsActive,
                })
                .FirstAsync();
        }

        public async Task<WarehouseProductRecordContainerQueryResultDto> QueryContainersAsync(
            string productCode,
            WarehouseProductRecordContainerQueryRequest request
        )
        {
            var normalized = NormalizeCode(productCode);
            if (string.IsNullOrEmpty(normalized))
                throw new ArgumentException("商品编码不能为空。", nameof(productCode));

            await EnsureWarehouseProductExistsAsync(normalized);

            request ??= new WarehouseProductRecordContainerQueryRequest();
            if (request.PageNumber < 1)
                throw new ArgumentException("页码必须大于等于 1。");
            if (request.PageSize < 1 || request.PageSize > 100)
                throw new ArgumentException("每页数量必须在 1 到 100 之间。");
            if (
                request.ArrivalStartDate.HasValue
                && request.ArrivalEndDate.HasValue
                && request.ArrivalStartDate.Value.Date > request.ArrivalEndDate.Value.Date
            )
                throw new ArgumentException("到港开始日期不能晚于结束日期。");

            var query = BuildContainerQuery(normalized, request);

            // 全量过滤结果：明细行总数用于分页，汇总按去重货柜与三列求和计算，不随分页变化。
            var totalCount = await query.Clone().CountAsync();
            var summary = new WarehouseProductRecordContainerSummaryDto
            {
                LoadingPieces = await query.Clone().SumAsync((d, c) => d.LoadingPieces ?? 0m),
                LoadingQuantity = await query.Clone().SumAsync((d, c) => d.LoadingQuantity ?? 0m),
                TotalAmount = await query.Clone().SumAsync((d, c) => d.TotalAmount ?? 0m),
            };
            var containerCodes = await query
                .Clone()
                .GroupBy((d, c) => c.ContainerCode)
                .Select((d, c) => new ContainerCodeRow { ContainerCode = c.ContainerCode })
                .ToListAsync();
            summary.ContainerCount = containerCodes.Count;

            var ordered = ApplyContainerSort(query, request.SortBy, request.SortDirection);
            var items = await ordered
                .Select((d, c) => new WarehouseProductRecordContainerItemDto
                {
                    DetailCode = d.DetailCode,
                    ContainerCode = c.ContainerCode,
                    ContainerNumber = c.ContainerNumber,
                    LoadingDate = c.LoadingDate,
                    EstimatedArrivalDate = c.EstimatedArrivalDate,
                    ActualArrivalDate = c.ActualArrivalDate,
                    EffectiveArrivalDate = SqlFunc.IsNull(c.ActualArrivalDate, c.EstimatedArrivalDate),
                    Status = c.Status,
                    LoadingPieces = d.LoadingPieces,
                    LoadingQuantity = d.LoadingQuantity,
                    DomesticPrice = d.DomesticPrice,
                    ImportPrice = d.ImportPrice,
                    TotalAmount = d.TotalAmount,
                })
                .Skip((request.PageNumber - 1) * request.PageSize)
                .Take(request.PageSize)
                .ToListAsync();

            return new WarehouseProductRecordContainerQueryResultDto
            {
                TotalCount = totalCount,
                PageNumber = request.PageNumber,
                PageSize = request.PageSize,
                Summary = summary,
                Items = items,
            };
        }

        public async Task<WarehouseProductRecordAllocationQueryResultDto> QueryAllocationsAsync(
            string productCode,
            WarehouseProductRecordAllocationQueryRequest request
        )
        {
            var normalized = NormalizeCode(productCode);
            if (string.IsNullOrEmpty(normalized))
                throw new ArgumentException("商品编码不能为空。", nameof(productCode));

            await EnsureWarehouseProductExistsAsync(normalized);

            if (request == null)
                throw new ArgumentException("请求参数不能为空。");
            if (!request.StartDate.HasValue || !request.EndDate.HasValue)
                throw new ArgumentException("开始日期和结束日期不能为空。");

            var startDate = request.StartDate.Value.Date;
            var endDate = request.EndDate.Value.Date;
            var brisbaneToday = GetBrisbaneToday();
            if (endDate > brisbaneToday)
                throw new ArgumentException("结束日期不能晚于布里斯班今天。");

            var dayCount = (endDate - startDate).Days + 1;
            if (dayCount < 1 || dayCount > MaxAllocationDayCount)
                throw new ArgumentException($"日期范围必须在 1 到 {MaxAllocationDayCount} 天之间。");

            // 业务日 = OutboundDate ?? OrderDate，布里斯班闭区间；仅投影汇总所需列，按商品过滤。
            var rows = await _context
                .Db.Queryable<WareHouseOrderDetails>()
                .InnerJoin<WareHouseOrder>((d, o) => d.OrderGUID == o.OrderGUID)
                .Where((d, o) =>
                    !d.IsDeleted
                    && !o.IsDeleted
                    && d.ProductCode != null
                    && SqlFunc.ToUpper(d.ProductCode.Trim()) == normalized
                    && SqlFunc.IsNull(o.OutboundDate, o.OrderDate) >= startDate
                    && SqlFunc.IsNull(o.OutboundDate, o.OrderDate) < endDate.AddDays(1)
                )
                .Select((d, o) => new AllocationQueryRow
                {
                    OrderGuid = o.OrderGUID,
                    DetailStoreCode = d.StoreCode,
                    OrderStoreCode = o.StoreCode,
                    AllocQuantity = d.AllocQuantity,
                    ImportPrice = d.ImportPrice,
                    BusinessDate = SqlFunc.IsNull(o.OutboundDate, o.OrderDate),
                })
                .ToListAsync();

            var storeByCode = await LoadStoreByCodeAsync();

            var summary = new WarehouseProductRecordAllocationSummaryDto
            {
                AllocationQuantity = rows.Sum(r => r.AllocQuantity ?? 0m),
                AllocationAmount = rows.Sum(r => (r.AllocQuantity ?? 0m) * (r.ImportPrice ?? 0m)),
                OrderCount = rows
                    .Select(r => r.OrderGuid)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
            };

            var branches = rows
                .GroupBy(ResolveEffectiveStoreCode, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var code = group.Key;
                    var list = group.ToList();
                    var quantity = list.Sum(r => r.AllocQuantity ?? 0m);
                    var amount = list.Sum(r => (r.AllocQuantity ?? 0m) * (r.ImportPrice ?? 0m));
                    var orderCount = list
                        .Select(r => r.OrderGuid)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count();

                    var (storeName, isActive) = ResolveStoreDisplay(code, storeByCode);

                    return new WarehouseProductRecordAllocationBranchDto
                    {
                        StoreCode = code,
                        StoreName = storeName,
                        IsActive = isActive,
                        AllocationQuantity = quantity,
                        AllocationAmount = amount,
                        OrderCount = orderCount,
                        FirstAllocationDate = list.Min(r => r.BusinessDate),
                        LastAllocationDate = list.Max(r => r.BusinessDate),
                    };
                })
                .OrderByDescending(b => b.AllocationQuantity)
                .ThenBy(b => b.StoreCode, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new WarehouseProductRecordAllocationQueryResultDto
            {
                Summary = summary,
                Branches = branches,
            };
        }

        private async Task EnsureWarehouseProductExistsAsync(string normalizedCode)
        {
            var exists = await _context
                .Db.Queryable<WarehouseProduct>()
                .AnyAsync(x =>
                    !x.IsDeleted
                    && SqlFunc.ToUpper(x.ProductCode.Trim()) == normalizedCode
                );
            if (!exists)
                throw new KeyNotFoundException("商品不存在。");
        }

        private ISugarQueryable<ContainerDetail, Container> BuildContainerQuery(
            string normalizedProductCode,
            WarehouseProductRecordContainerQueryRequest request
        )
        {
            var query = _context
                .Db.Queryable<ContainerDetail>()
                .InnerJoin<Container>((d, c) => d.ContainerCode == c.ContainerCode)
                .Where((d, c) =>
                    !d.IsDeleted
                    && !c.IsDeleted
                    && d.ProductCode != null
                    && SqlFunc.ToUpper(d.ProductCode.Trim()) == normalizedProductCode
                );

            if (request.Statuses is { Count: > 0 })
            {
                var statuses = request.Statuses.Distinct().ToList();
                // 不能写 c.Status.HasValue：SqlSugar 会生成 [Status]<>''，SQL Server 里 '' 转 int 为 0，会把状态码 0（已装柜）排除掉。
                query = query.Where((d, c) => statuses.Contains(c.Status!.Value));
            }
            else
            {
                // 缺省排除已取消状态 7，null 状态不属于已取消，仍需保留。
                query = query.Where((d, c) => c.Status == null || c.Status != CancelledContainerStatus);
            }

            if (!string.IsNullOrWhiteSpace(request.ContainerKeyword))
            {
                var keyword = request.ContainerKeyword.Trim();
                // 关键字匹配货柜编号或货柜编码。
                query = query.Where((d, c) =>
                    (c.ContainerNumber != null && c.ContainerNumber.Contains(keyword))
                    || c.ContainerCode.Contains(keyword)
                );
            }

            if (request.ArrivalStartDate.HasValue)
            {
                var start = request.ArrivalStartDate.Value.Date;
                // 有效到货日 = 实际到货日 ?? 预计到岸日；应用日期过滤时 null 不命中。
                query = query.Where((d, c) =>
                    SqlFunc.IsNull(c.ActualArrivalDate, c.EstimatedArrivalDate) >= start
                );
            }

            if (request.ArrivalEndDate.HasValue)
            {
                var endExclusive = request.ArrivalEndDate.Value.Date.AddDays(1);
                query = query.Where((d, c) =>
                    SqlFunc.IsNull(c.ActualArrivalDate, c.EstimatedArrivalDate) < endExclusive
                );
            }

            return query;
        }

        private static ISugarQueryable<ContainerDetail, Container> ApplyContainerSort(
            ISugarQueryable<ContainerDetail, Container> query,
            string? sortBy,
            string? sortDirection
        )
        {
            var ascending = string.Equals(sortDirection, "asc", StringComparison.OrdinalIgnoreCase);
            var orderType = ascending ? OrderByType.Asc : OrderByType.Desc;
            var key = sortBy?.Trim().ToLowerInvariant();

            ISugarQueryable<ContainerDetail, Container> ordered = key switch
            {
                "loadingdate" => query.OrderBy((d, c) => c.LoadingDate, orderType),
                "containernumber" => query.OrderBy((d, c) => c.ContainerNumber, orderType),
                "status" => query.OrderBy((d, c) => c.Status, orderType),
                "loadingquantity" => query.OrderBy((d, c) => d.LoadingQuantity, orderType),
                "effectivearrivaldate" => query.OrderBy(
                    (d, c) => SqlFunc.IsNull(c.ActualArrivalDate, c.EstimatedArrivalDate),
                    orderType
                ),
                // 白名单之外的字段回退默认：有效到货日 desc。
                _ => query.OrderBy(
                    (d, c) => SqlFunc.IsNull(c.ActualArrivalDate, c.EstimatedArrivalDate),
                    OrderByType.Desc
                ),
            };

            // 稳定分页：主排序相同后按货柜编号/编码固定顺序，避免翻页时记录漂移。
            return key == "containernumber"
                ? ordered
                    .OrderBy((d, c) => c.ContainerCode, OrderByType.Asc)
                    .OrderBy((d, c) => d.DetailCode, OrderByType.Asc)
                : ordered
                    .OrderBy((d, c) => c.ContainerNumber, OrderByType.Asc)
                    .OrderBy((d, c) => c.ContainerCode, OrderByType.Asc)
                    .OrderBy((d, c) => d.DetailCode, OrderByType.Asc);
        }

        private async Task<Dictionary<string, Store>> LoadStoreByCodeAsync()
        {
            var stores = await _context.Db.Queryable<Store>().Where(x => !x.IsDeleted).ToListAsync();
            return stores
                .Where(x => !string.IsNullOrWhiteSpace(x.StoreCode))
                .GroupBy(x => NormalizeCode(x.StoreCode), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        }

        private DateTime GetBrisbaneToday()
        {
            var timeZone = ResolveBrisbaneTimeZone();
            return TimeZoneInfo.ConvertTimeFromUtc(_timeProvider.GetUtcNow().UtcDateTime, timeZone).Date;
        }

        private static TimeZoneInfo ResolveBrisbaneTimeZone()
        {
            foreach (var id in new[] { "Australia/Brisbane", "E. Australia Standard Time" })
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(id);
                }
                catch (TimeZoneNotFoundException) { }
                catch (InvalidTimeZoneException) { }
            }

            return TimeZoneInfo.Local;
        }

        private static string ResolveEffectiveStoreCode(AllocationQueryRow row)
        {
            var raw = string.IsNullOrWhiteSpace(row.DetailStoreCode)
                ? row.OrderStoreCode
                : row.DetailStoreCode;
            return NormalizeCode(raw);
        }

        private static (string StoreName, bool IsActive) ResolveStoreDisplay(
            string code,
            IReadOnlyDictionary<string, Store> storeByCode
        )
        {
            if (string.IsNullOrEmpty(code))
                return ("未匹配分店（无编码）", false);

            if (storeByCode.TryGetValue(code, out var store))
                return (store.StoreName, store.IsActive);

            return ($"未匹配分店（{code}）", false);
        }

        private static string NormalizeCode(string? value) =>
            value?.Trim().ToUpperInvariant() ?? string.Empty;

        private sealed class AllocationQueryRow
        {
            public string OrderGuid { get; set; } = string.Empty;
            public string? DetailStoreCode { get; set; }
            public string? OrderStoreCode { get; set; }
            public decimal? AllocQuantity { get; set; }
            public decimal? ImportPrice { get; set; }
            public DateTime? BusinessDate { get; set; }
        }

        private sealed class ContainerCodeRow
        {
            public string ContainerCode { get; set; } = string.Empty;
        }
    }
}
