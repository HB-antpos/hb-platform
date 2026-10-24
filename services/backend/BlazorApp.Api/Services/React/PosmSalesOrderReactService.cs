using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.PosmSalesOrders;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    public class PosmSalesOrderReactService : IPosmSalesOrderReactService
    {
        private readonly POSMSqlSugarContext _posmContext;
        private readonly SqlSugarContext _context;
        private readonly IMapper _mapper;
        private readonly ILogger<PosmSalesOrderReactService> _logger;

        /// <summary>列表总耗时达到该值时记 Warning，进入中心日志表，便于盯住 3 秒目标。</summary>
        private const long SlowListWarningMilliseconds = 2000;

        public PosmSalesOrderReactService(
            POSMSqlSugarContext posmContext,
            SqlSugarContext context,
            IMapper mapper,
            ILogger<PosmSalesOrderReactService> logger
        )
        {
            _posmContext = posmContext;
            _context = context;
            _mapper = mapper;
            _logger = logger;
        }

        public async Task<PosmSalesOrderListResultDto> GetSalesOrderListAsync(
            PosmSalesOrderQueryParams queryParams
        )
        {
            var stopwatch = Stopwatch.StartNew();
            var pageNumber = Math.Max(1, queryParams.PageNumber);
            // 限制单页上限，避免异常请求生成过大的 SQL Take 和响应体。
            var pageSize = queryParams.PageSize > 0 ? Math.Min(queryParams.PageSize, 1000) : 20;
            // 先用 long 计算再限制到 int 上限，避免极大页码乘法溢出为负数。
            var requestedSkip = ((long)pageNumber - 1L) * pageSize;
            var safeSkip = (int)Math.Min(requestedSkip, int.MaxValue);

            var keyword = queryParams.Keyword?.Trim();
            if (string.IsNullOrEmpty(keyword))
            {
                keyword = null;
            }
            // 关键词先在商品主档解析成商品编码；过宽时抛出拒绝异常，由控制器提示用户收窄。
            var keywordCodes = keyword == null ? null : await ResolveKeywordProductCodesAsync(keyword);

            // POS 写入的 OrderTime 是门店本地墙钟时间（非 UTC），日期与时段边界一律按墙钟直接比较。
            PosmSalesOrderListPage page;
            string path;
            if (_posmContext.Db.CurrentConnectionConfig.DbType == DbType.SqlServer)
            {
                var command = PosmSalesOrderSqlServerListQuery.Build(
                    queryParams,
                    keyword,
                    keywordCodes,
                    safeSkip,
                    pageSize
                );
                page = await PosmSalesOrderSqlServerListQuery.ExecuteAsync(
                    (DbConnection)_posmContext.Db.Ado.Connection,
                    command
                );
                path = command.Path;
            }
            else
            {
                // 非 SQL Server（测试用 SQLite）走 SqlSugar 表达式，筛选与关键词口径与上面一致。
                page = await QueryWithSugarAsync(queryParams, keyword, keywordCodes, safeSkip, pageSize);
                path = "sugar";
            }
            var queryMilliseconds = stopwatch.ElapsedMilliseconds;

            var items = page.Rows;
            await EnrichPageAsync(items, keywordCodes);
            await FillStoreInfoAsync(items);

            int? statusFilter = queryParams.OrderType.HasValue && queryParams.OrderType.Value != OrderType.All
                ? (int)queryParams.OrderType.Value
                : null;
            // 汇总不受状态筛选影响；分页总数只算当前状态。
            var total = page.Summary
                .Where(row => statusFilter == null || row.Status == statusFilter)
                .Sum(row => row.OrderCount);

            stopwatch.Stop();
            if (stopwatch.ElapsedMilliseconds >= SlowListWarningMilliseconds)
            {
                _logger.LogWarning(
                    "[posm-sales-order-list-perf] path={Path} days={Days} branch={Branch} keyword={HasKeyword} codes={CodeCount} total={Total} queryMs={QueryMs} totalMs={TotalMs}",
                    path,
                    queryParams.StartDate.HasValue && queryParams.EndDate.HasValue
                        ? PosmSalesOrderListRules.CountDays(queryParams.StartDate.Value, queryParams.EndDate.Value)
                        : 0,
                    PosmSalesOrderListRules.IsSingleBranch(queryParams),
                    keyword != null,
                    keywordCodes?.Count ?? 0,
                    total,
                    queryMilliseconds,
                    stopwatch.ElapsedMilliseconds
                );
            }

            return new PosmSalesOrderListResultDto
            {
                Items = items,
                Total = total,
                PageNumber = pageNumber,
                PageSize = pageSize,
                Summary = page.Summary.OrderBy(row => row.Status).ToList(),
            };
        }

        /// <summary>
        /// SqlSugar 表达式版本（测试用 SQLite）：订单级条件 → 关键词 → 明细聚合 → 汇总与分页。
        /// 状态筛选只作用于分页，汇总保留全部状态，与 SQL Server 批处理一致。
        /// </summary>
        private async Task<PosmSalesOrderListPage> QueryWithSugarAsync(
            PosmSalesOrderQueryParams queryParams,
            string? keyword,
            List<string>? keywordCodes,
            int skip,
            int pageSize
        )
        {
            var baseQuery = _posmContext
                .Db.Queryable<SalesOrder>()
                .LeftJoin<SalesOrderDetail>((o, d) => o.OrderGuid == d.OrderGuid);

            if (queryParams.StartDate.HasValue)
            {
                var start = queryParams.StartDate.Value.Date;
                baseQuery = baseQuery.Where(o => o.OrderTime >= start);
            }
            if (queryParams.EndDate.HasValue)
            {
                var endExclusive = queryParams.EndDate.Value.Date.AddDays(1);
                baseQuery = baseQuery.Where(o => o.OrderTime < endExclusive);
            }
            if (queryParams.TimeStart.HasValue)
            {
                var startSeconds = (int)queryParams.TimeStart.Value.TotalSeconds;
                baseQuery = baseQuery.Where(o =>
                    o.OrderTime.HasValue
                    && o.OrderTime.Value.Hour * 3600
                        + o.OrderTime.Value.Minute * 60
                        + o.OrderTime.Value.Second
                        >= startSeconds
                );
            }
            if (queryParams.TimeEnd.HasValue)
            {
                var endSeconds = (int)queryParams.TimeEnd.Value.TotalSeconds;
                baseQuery = baseQuery.Where(o =>
                    o.OrderTime.HasValue
                    && o.OrderTime.Value.Hour * 3600
                        + o.OrderTime.Value.Minute * 60
                        + o.OrderTime.Value.Second
                        <= endSeconds
                );
            }
            if (!string.IsNullOrWhiteSpace(queryParams.BranchCode))
            {
                baseQuery = baseQuery.Where(o => o.BranchCode == queryParams.BranchCode);
            }
            if (queryParams.BranchCodes != null && queryParams.BranchCodes.Any())
            {
                baseQuery = baseQuery.Where(o =>
                    o.BranchCode != null && queryParams.BranchCodes.Contains(o.BranchCode)
                );
            }
            if (!string.IsNullOrWhiteSpace(queryParams.DeviceCode))
            {
                baseQuery = baseQuery.Where(o => o.DeviceCode == queryParams.DeviceCode);
            }
            var refundFilter = PosmSalesOrderListRules.NormalizeRefundFilter(queryParams.RefundFilter);
            if (refundFilter != null)
            {
                // 与 SQL Server 路径同口径：退款单（Status=3）或订单里夹带了退货记录；退货记录表很小，先取出订单号集合。
                var returnOrderGuids = await LoadReturnOrderGuidsAsync(null);
                baseQuery = baseQuery.Where(o =>
                    o.Status == PosmSalesOrderListRules.RefundOrderStatus
                    || (o.OrderGuid != null && returnOrderGuids.Contains(o.OrderGuid))
                );
                if (refundFilter == PosmSalesOrderListRules.RefundFilterPartial)
                {
                    baseQuery = baseQuery.Where(o =>
                        SqlFunc.IsNull(SqlFunc.IIF(o.ActualAmount != null && o.ActualAmount != 0, o.ActualAmount, o.TotalAmount), 0) >= 0
                    );
                }
                else if (refundFilter == PosmSalesOrderListRules.RefundFilterFull)
                {
                    baseQuery = baseQuery.Where(o =>
                        SqlFunc.IIF(o.ActualAmount != null && o.ActualAmount != 0, o.ActualAmount, o.TotalAmount) < 0
                    );
                }
            }
            if (!string.IsNullOrWhiteSpace(queryParams.OrderGuidKeyword))
            {
                var orderGuidKeyword = queryParams.OrderGuidKeyword.Trim();
                baseQuery = baseQuery.Where(o =>
                    o.OrderGuid != null && o.OrderGuid.Contains(orderGuidKeyword)
                );
            }
            if (!string.IsNullOrWhiteSpace(queryParams.DeviceCodeKeyword))
            {
                var deviceCodeKeyword = queryParams.DeviceCodeKeyword.Trim();
                baseQuery = baseQuery.Where(o =>
                    o.DeviceCode != null && o.DeviceCode.Contains(deviceCodeKeyword)
                );
            }
            if (queryParams.ItemCountMin.HasValue)
                baseQuery = baseQuery.Where(o => o.ItemCount >= queryParams.ItemCountMin.Value);
            if (queryParams.ItemCountMax.HasValue)
                baseQuery = baseQuery.Where(o => o.ItemCount <= queryParams.ItemCountMax.Value);
            if (queryParams.TotalAmountMin.HasValue)
                baseQuery = baseQuery.Where(o => o.TotalAmount >= queryParams.TotalAmountMin.Value);
            if (queryParams.TotalAmountMax.HasValue)
                baseQuery = baseQuery.Where(o => o.TotalAmount <= queryParams.TotalAmountMax.Value);
            if (queryParams.DiscountAmountMin.HasValue)
                baseQuery = baseQuery.Where(o => o.DiscountAmount >= queryParams.DiscountAmountMin.Value);
            if (queryParams.DiscountAmountMax.HasValue)
                baseQuery = baseQuery.Where(o => o.DiscountAmount <= queryParams.DiscountAmountMax.Value);
            if (queryParams.ActualPayMin.HasValue)
            {
                // SQLite 的 decimal 表达式参数会按文本绑定，这里转 double 比较；SQL Server 路径保持 decimal 精度。
                var actualPayMin = (double)queryParams.ActualPayMin.Value;
                baseQuery = baseQuery.Where(o =>
                    SqlFunc.ToDouble(o.TotalAmount) - SqlFunc.ToDouble(o.DiscountAmount) >= actualPayMin
                );
            }
            if (queryParams.ActualPayMax.HasValue)
            {
                var actualPayMax = (double)queryParams.ActualPayMax.Value;
                baseQuery = baseQuery.Where(o =>
                    SqlFunc.ToDouble(o.TotalAmount) - SqlFunc.ToDouble(o.DiscountAmount) <= actualPayMax
                );
            }

            if (keyword != null)
            {
                // 口径与 SQL Server 批处理一致：关键词像订单号片段时匹配订单号；商品按主档解析出的编码（含关键词本身）匹配明细。
                var codes = keywordCodes ?? new List<string> { keyword };
                if (PosmSalesOrderListRules.IsOrderNumberFragment(keyword))
                {
                    baseQuery = baseQuery.Where((o, d) =>
                        (o.OrderGuid != null && o.OrderGuid.Contains(keyword))
                        || SqlFunc
                            .Subqueryable<SalesOrderDetail>()
                            .Where(detail => detail.OrderGuid == o.OrderGuid && codes.Contains(detail.ProductCode))
                            .Any()
                    );
                }
                else
                {
                    baseQuery = baseQuery.Where((o, d) =>
                        SqlFunc
                            .Subqueryable<SalesOrderDetail>()
                            .Where(detail => detail.OrderGuid == o.OrderGuid && codes.Contains(detail.ProductCode))
                            .Any()
                    );
                }
            }

            var grouped = baseQuery
                .GroupBy(
                    (o, d) =>
                        new
                        {
                            o.OrderGuid,
                            o.OrderTime,
                            o.BranchCode,
                            o.DeviceCode,
                            o.TotalAmount,
                            o.DiscountAmount,
                            o.ActualAmount,
                            o.ItemCount,
                            o.Status,
                        }
                )
                .Select(
                    (o, d) =>
                        new PosmSalesOrderDto
                        {
                            OrderGuid = o.OrderGuid,
                            OrderTime = o.OrderTime,
                            BranchCode = o.BranchCode,
                            DeviceCode = o.DeviceCode,
                            TotalAmount = o.TotalAmount,
                            DiscountAmount = o.DiscountAmount,
                            ActualAmount = o.ActualAmount,
                            ItemCount = o.ItemCount,
                            Status = o.Status,
                            SkuCount = SqlFunc.AggregateDistinctCount(d.ProductCode),
                            QuantityTotal = SqlFunc.AggregateSum(d.Quantity),
                        }
                )
                .MergeTable();

            // 种数、件数是明细聚合值，必须在 GroupBy/Select 之后过滤。
            if (queryParams.SkuCountMin.HasValue)
                grouped = grouped.Where(o => o.SkuCount >= queryParams.SkuCountMin.Value);
            if (queryParams.SkuCountMax.HasValue)
                grouped = grouped.Where(o => o.SkuCount <= queryParams.SkuCountMax.Value);
            if (queryParams.QuantityMin.HasValue)
                grouped = grouped.Where(o => o.QuantityTotal >= queryParams.QuantityMin.Value);
            if (queryParams.QuantityMax.HasValue)
                grouped = grouped.Where(o => o.QuantityTotal <= queryParams.QuantityMax.Value);

            var summaryRows = await grouped
                .Clone()
                .GroupBy(o => o.Status)
                .Select(o => new SugarSummaryRow
                {
                    Status = o.Status,
                    OrderCount = SqlFunc.AggregateCount(o.OrderGuid),
                    TotalAmount = SqlFunc.AggregateSum(o.TotalAmount),
                    DiscountAmount = SqlFunc.AggregateSum(o.DiscountAmount),
                })
                .ToListAsync();
            var summary = summaryRows
                .Select(row => new PosmSalesOrderStatusSummaryDto
                {
                    Status = row.Status,
                    OrderCount = row.OrderCount,
                    TotalAmount = row.TotalAmount ?? 0m,
                    DiscountAmount = row.DiscountAmount ?? 0m,
                })
                .ToList();

            var pageQuery = grouped.Clone();
            if (queryParams.OrderType.HasValue && queryParams.OrderType.Value != OrderType.All)
            {
                var status = (int)queryParams.OrderType.Value;
                pageQuery = pageQuery.Where(o => o.Status == status);
            }

            var (sortField, descending) = PosmSalesOrderSqlServerListQuery.NormalizeSort(
                queryParams.SortField,
                queryParams.SortDirection
            );
            var orderByType = descending ? OrderByType.Desc : OrderByType.Asc;
            // 排序字段仅允许白名单，非法字段统一回退到下单时间升序，避免动态 SQL 注入。
            pageQuery = sortField switch
            {
                "orderguid" => pageQuery.OrderBy(o => o.OrderGuid, orderByType),
                "branchcode" => pageQuery.OrderBy(o => o.BranchCode, orderByType),
                "devicecode" => pageQuery.OrderBy(o => o.DeviceCode, orderByType),
                "skucount" => pageQuery.OrderBy(o => o.SkuCount, orderByType),
                "quantity" => pageQuery.OrderBy(o => o.QuantityTotal, orderByType),
                "itemcount" => pageQuery.OrderBy(o => o.ItemCount, orderByType),
                "totalamount" => pageQuery.OrderBy(o => o.TotalAmount, orderByType),
                "discountamount" => pageQuery.OrderBy(o => o.DiscountAmount, orderByType),
                "actualpay" => pageQuery.OrderBy(o => o.TotalAmount - o.DiscountAmount, orderByType),
                _ => pageQuery.OrderBy(o => o.OrderTime, orderByType),
            };
            pageQuery = pageQuery.OrderBy(o => o.OrderGuid, OrderByType.Asc);

            var rows = await pageQuery.Skip(skip).Take(pageSize).ToListAsync();
            return new PosmSalesOrderListPage(summary, rows);
        }

        private sealed class SugarSummaryRow
        {
            public int? Status { get; set; }
            public int OrderCount { get; set; }
            public decimal? TotalAmount { get; set; }
            public decimal? DiscountAmount { get; set; }
        }

        /// <summary>
        /// 只为当前页补齐种数、件数、命中商品与支付方式：两次按订单号的小查询，
        /// 替代原先对整个日期范围 JOIN 明细后 GROUP BY。
        /// </summary>
        private async Task EnrichPageAsync(List<PosmSalesOrderDto> items, List<string>? keywordCodes)
        {
            var guids = items
                .Select(item => item.OrderGuid)
                .Where(guid => !string.IsNullOrWhiteSpace(guid))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (guids.Count == 0)
            {
                return;
            }

            var detailRows = await LoadDetailRowsAsync(guids);
            var detailsByOrder = detailRows
                .GroupBy(row => row.OrderGuid ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

            Dictionary<string, List<PosmSalesOrderMatchedProductDto>>? matchedByOrder = null;
            if (keywordCodes != null)
            {
                matchedByOrder = await BuildMatchedProductsAsync(detailRows, keywordCodes);
            }

            foreach (var item in items)
            {
                var rows = item.OrderGuid != null && detailsByOrder.TryGetValue(item.OrderGuid, out var found)
                    ? found
                    : new List<DetailRow>();
                // 种数按商品编码去重（与 SQL Server 不区分大小写的 COUNT(DISTINCT) 一致）；件数是数量之和，不是明细行数。
                item.SkuCount = rows
                    .Select(row => row.ProductCode)
                    .Where(code => code != null)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                item.QuantityTotal = rows.Sum(row => row.Quantity ?? 0);
                if (matchedByOrder != null)
                {
                    item.MatchedProducts = item.OrderGuid != null && matchedByOrder.TryGetValue(item.OrderGuid, out var matched)
                        ? matched
                        : new List<PosmSalesOrderMatchedProductDto>();
                }
            }

            try
            {
                // 含退货商品：退款单本身，或订单里夹带了退货记录；只按当前页订单号查小表。
                var returnOrderGuids = await LoadReturnOrderGuidsAsync(guids);
                foreach (var item in items)
                {
                    item.HasRefundItems = item.Status == PosmSalesOrderListRules.RefundOrderStatus
                        || (item.OrderGuid != null && returnOrderGuids.Contains(item.OrderGuid));
                }
            }
            catch (Exception ex)
            {
                // 退货标记只是展示增强：查询失败时退款单仍按状态标出，其余订单不标，不能让列表失败。
                _logger.LogError(ex, "查询收银记录退货标记失败，列表仅按退款状态标记");
                foreach (var item in items)
                {
                    item.HasRefundItems = item.Status == PosmSalesOrderListRules.RefundOrderStatus;
                }
            }

            try
            {
                var payments = await _posmContext
                    .Db.Queryable<PaymentDetail>()
                    .Where(payment => payment.OrderGuid != null && guids.Contains(payment.OrderGuid))
                    .Select(payment => new { payment.OrderGuid, payment.PaymentMethod })
                    .ToListAsync();
                var methodsByOrder = payments
                    .GroupBy(payment => payment.OrderGuid ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Select(payment => payment.PaymentMethod).Distinct().OrderBy(method => method).ToList(),
                        StringComparer.OrdinalIgnoreCase
                    );
                foreach (var item in items)
                {
                    item.PaymentMethods = item.OrderGuid != null && methodsByOrder.TryGetValue(item.OrderGuid, out var methods)
                        ? methods
                        : new List<int>();
                }
            }
            catch (Exception ex)
            {
                // 支付方式只是展示增强，查询失败不能让整个列表失败。
                _logger.LogError(ex, "查询收银记录支付方式失败，列表将不显示支付方式");
            }
        }

        /// <summary>
        /// 取有退货记录的订单号集合（sales_return_record.ReturnOrderGuid，不区分大小写）。
        /// orderGuids 为 null 时取全表（表只有约 1.3 万行，仅测试用的 SQLite 路径使用）；否则只查给定订单。
        /// </summary>
        private async Task<HashSet<string>> LoadReturnOrderGuidsAsync(List<string>? orderGuids)
        {
            var query = _posmContext.Db.Queryable<SalesReturnRecord>()
                .Where(record => record.ReturnOrderGuid != null && record.ReturnOrderGuid != string.Empty);
            if (orderGuids != null)
            {
                query = query.Where(record => orderGuids.Contains(record.ReturnOrderGuid));
            }
            var found = await query.Select(record => record.ReturnOrderGuid).Distinct().ToListAsync();
            return new HashSet<string>(found, StringComparer.OrdinalIgnoreCase);
        }

        private sealed class DetailRow
        {
            public string? OrderDetailGuid { get; set; }
            public string? OrderGuid { get; set; }
            public string? ProductCode { get; set; }
            public int? Quantity { get; set; }
        }

        private sealed class DetailTextRow
        {
            public string? OrderDetailGuid { get; set; }
            public string? ProductName { get; set; }
            public string? Barcode { get; set; }
        }

        /// <summary>
        /// 只取订单号索引已覆盖的列（编码、数量、明细主键），不回聚簇索引：
        /// 生产冷缓存下带商品名、条码的同一查询要 1.8 秒（157 次物理读），覆盖列只读索引页。
        /// </summary>
        private Task<List<DetailRow>> LoadDetailRowsAsync(List<string> guids) =>
            _posmContext
                .Db.Queryable<SalesOrderDetail>()
                .Where(detail => guids.Contains(detail.OrderGuid))
                .Select(detail => new DetailRow
                {
                    OrderDetailGuid = detail.OrderDetailGuid,
                    OrderGuid = detail.OrderGuid,
                    ProductCode = detail.ProductCode,
                    Quantity = detail.Quantity,
                })
                .ToListAsync();

        /// <summary>明细行中编码属于关键词解析结果的即为命中商品；同一订单同一商品多行（不同折扣）合并并累计数量。</summary>
        private async Task<Dictionary<string, List<PosmSalesOrderMatchedProductDto>>> BuildMatchedProductsAsync(
            List<DetailRow> detailRows,
            IReadOnlyCollection<string> keywordCodes
        )
        {
            var codeSet = new HashSet<string>(keywordCodes, StringComparer.OrdinalIgnoreCase);
            var hits = detailRows
                .Where(row => row.OrderGuid != null && row.ProductCode != null && codeSet.Contains(row.ProductCode))
                .ToList();
            var result = new Dictionary<string, List<PosmSalesOrderMatchedProductDto>>(StringComparer.OrdinalIgnoreCase);
            if (hits.Count == 0)
            {
                return result;
            }

            var masters = await LookupProductMastersAsync(hits.Select(row => row.ProductCode!));
            // 商品名、条码只为命中的明细行按主键取，行数通常只有几十。
            var hitIds = hits
                .Select(row => row.OrderDetailGuid)
                .Where(id => !string.IsNullOrEmpty(id))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var texts = hitIds.Count == 0
                ? new Dictionary<string, DetailTextRow>(StringComparer.OrdinalIgnoreCase)
                : (await _posmContext
                    .Db.Queryable<SalesOrderDetail>()
                    .Where(detail => hitIds.Contains(detail.OrderDetailGuid))
                    .Select(detail => new DetailTextRow
                    {
                        OrderDetailGuid = detail.OrderDetailGuid,
                        ProductName = detail.ProductName,
                        Barcode = detail.Barcode,
                    })
                    .ToListAsync())
                    .Where(row => row.OrderDetailGuid != null)
                    .GroupBy(row => row.OrderDetailGuid!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            DetailTextRow? TextOf(DetailRow row) =>
                row.OrderDetailGuid != null && texts.TryGetValue(row.OrderDetailGuid, out var text) ? text : null;

            foreach (var orderGroup in hits.GroupBy(row => row.OrderGuid!, StringComparer.OrdinalIgnoreCase))
            {
                result[orderGroup.Key] = orderGroup
                    .GroupBy(row => row.ProductCode!, StringComparer.OrdinalIgnoreCase)
                    .Select(productGroup => new PosmSalesOrderMatchedProductDto
                    {
                        ProductCode = productGroup.Key,
                        ItemNumber = masters.TryGetValue(productGroup.Key, out var master) ? master.ItemNumber : null,
                        ProductName = productGroup
                            .Select(row => TextOf(row)?.ProductName)
                            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
                        Barcode = productGroup
                            .Select(row => TextOf(row)?.Barcode)
                            .FirstOrDefault(barcode => !string.IsNullOrWhiteSpace(barcode)),
                        Quantity = productGroup.Sum(row => row.Quantity ?? 0),
                    })
                    .ToList();
            }
            return result;
        }

        private async Task FillStoreInfoAsync(List<PosmSalesOrderDto> items)
        {
            try
            {
                var storeCodes = items
                    .Where(i => !string.IsNullOrEmpty(i.BranchCode))
                    .Select(i => i.BranchCode)
                    .Distinct()
                    .ToList();
                if (!storeCodes.Any())
                {
                    return;
                }

                var stores = await _context
                    .Db.Queryable<Store>()
                    .Where(s => storeCodes.Contains(s.StoreCode) && !s.IsDeleted)
                    .ToListAsync();
                var storeDict = stores.ToDictionary(
                    s => s.StoreCode,
                    s => new
                    {
                        s.StoreName,
                        s.ABN,
                        s.BrandName,
                    }
                );
                foreach (var item in items)
                {
                    if (
                        !string.IsNullOrEmpty(item.BranchCode)
                        && storeDict.TryGetValue(item.BranchCode, out var store)
                    )
                    {
                        item.BranchName = store.StoreName;
                        item.ABN = store.ABN;
                        item.BrandName = store.BrandName;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询分店信息失败，将只显示分店代码");
            }
        }

        public async Task<Dictionary<string, List<PosmSalesOrderMatchedProductDto>>> GetMatchedProductsAsync(
            IReadOnlyCollection<string> orderGuids,
            string keyword
        )
        {
            var normalizedKeyword = keyword?.Trim();
            var guids = orderGuids
                .Where(guid => !string.IsNullOrWhiteSpace(guid))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (string.IsNullOrEmpty(normalizedKeyword) || guids.Count == 0)
            {
                return new Dictionary<string, List<PosmSalesOrderMatchedProductDto>>(StringComparer.Ordinal);
            }

            // 命中条件必须与列表关键词过滤保持一致（同一套编码解析），否则会出现"被搜出来却没有命中商品"的订单。
            List<string> codes;
            try
            {
                codes = await ResolveKeywordProductCodesAsync(normalizedKeyword);
            }
            catch (PosmSalesOrderQueryRejectedException)
            {
                return new Dictionary<string, List<PosmSalesOrderMatchedProductDto>>(StringComparer.Ordinal);
            }

            var detailRows = await LoadDetailRowsAsync(guids);
            var matched = await BuildMatchedProductsAsync(detailRows, codes);
            return new Dictionary<string, List<PosmSalesOrderMatchedProductDto>>(matched, StringComparer.Ordinal);
        }

        /// <summary>
        /// 把关键词解析成 POSM 商品编码：在商品主档按编码、货号、条码、中英文名称包含匹配，再加上关键词本身
        /// （按完整编码匹配主档里没有的 POS 临时商品）。含已删除商品，历史订单里仍可能卖过。
        /// POSM 明细不做名称/条码模糊匹配：那需要逐条回表读取，全部分店长区间无法在 3 秒内完成；
        /// 2026-09-19 核对近两周 30,765 个明细商品编码，主档覆盖 99.96%。
        /// 主档不可用时退回只按关键词本身匹配，列表不能整体失败。
        /// </summary>
        private async Task<List<string>> ResolveKeywordProductCodesAsync(string keyword)
        {
            var limit = PosmSalesOrderListRules.MaxKeywordProductCodes + 1;
            List<string?> codes;
            try
            {
                if (_context.Db.CurrentConnectionConfig.DbType == DbType.SqlServer)
                {
                    // 商品主档约 17 万行，CI 排序规则下多列前导通配 LIKE 约 1.4 秒；UPPER + BIN2 逐字节比较约 0.2 秒。
                    codes = await _context
                        .Db.Queryable<Product>()
                        .Where(
                            LocalSupplierProductSalesAnalysisService.SqlServerKeywordPredicate,
                            new
                            {
                                lspaKeywordPattern = LocalSupplierProductSalesAnalysisService.BuildSqlServerLikePattern(
                                    keyword.ToUpperInvariant()
                                ),
                            }
                        )
                        .Where(p => p.ProductCode != null)
                        .Select(p => p.ProductCode)
                        .Take(limit)
                        .ToListAsync();
                }
                else
                {
                    var upper = keyword.ToUpper();
                    codes = await _context
                        .Db.Queryable<Product>()
                        .Where(p =>
                            p.ProductCode != null
                            && (
                                p.ProductCode.ToUpper().Contains(upper)
                                || (p.ItemNumber != null && p.ItemNumber.ToUpper().Contains(upper))
                                || (p.Barcode != null && p.Barcode.ToUpper().Contains(upper))
                                || (p.ProductName != null && p.ProductName.ToUpper().Contains(upper))
                                || (p.EnglishName != null && p.EnglishName.ToUpper().Contains(upper))
                            )
                        )
                        .Select(p => p.ProductCode)
                        .Take(limit)
                        .ToListAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "按商品主档解析关键词失败，关键词将只按完整商品编码、订单号和收银机号匹配");
                codes = new List<string?>();
            }

            if (codes.Count >= limit)
            {
                throw new PosmSalesOrderQueryRejectedException(
                    PosmSalesOrderListRules.ErrorKeywordTooBroad,
                    "关键词匹配到的商品过多，请输入更完整的货号、条码或商品名。"
                );
            }

            return codes
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!.Trim())
                .Append(keyword)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>按商品编码回查主档货号与图片，供命中提示与明细行展示。</summary>
        private async Task<Dictionary<string, (string? ItemNumber, string? Image)>> LookupProductMastersAsync(
            IEnumerable<string> productCodes
        )
        {
            var codes = productCodes
                .Select(code => code.Trim())
                .Where(code => code.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var result = new Dictionary<string, (string? ItemNumber, string? Image)>(StringComparer.OrdinalIgnoreCase);
            if (codes.Count == 0)
            {
                return result;
            }

            try
            {
                var products = await _context
                    .Db.Queryable<Product>()
                    .Where(p => p.ProductCode != null && codes.Contains(p.ProductCode))
                    .Select(p => new { p.ProductCode, p.ItemNumber, p.ProductImage })
                    .ToListAsync();
                foreach (var product in products)
                {
                    if (string.IsNullOrWhiteSpace(product.ProductCode))
                    {
                        continue;
                    }
                    result[product.ProductCode] = (
                        string.IsNullOrWhiteSpace(product.ItemNumber) ? null : product.ItemNumber.Trim(),
                        string.IsNullOrWhiteSpace(product.ProductImage) ? null : product.ProductImage.Trim()
                    );
                }
            }
            catch (Exception ex)
            {
                // 货号与图片只是展示增强，主档查询失败不能让列表或详情失败。
                _logger.LogError(ex, "查询商品主档货号失败，将只显示商品编码");
            }
            return result;
        }

        public async Task<ApiResponse<PosmSalesOrderDetailResponse>> GetSalesOrderDetailAsync(
            string orderGuid
        )
        {
            try
            {
                var order = await _posmContext.SalesOrderDb.GetFirstAsync(o =>
                    o.OrderGuid == orderGuid
                );

                if (order == null)
                {
                    return new ApiResponse<PosmSalesOrderDetailResponse>
                    {
                        Success = false,
                        Message = "Order not found",
                    };
                }

                var orderDetails = await _posmContext.SalesOrderDetailDb.GetListAsync(d =>
                    d.OrderGuid == orderGuid
                );

                var paymentDetails = await _posmContext.PaymentDetailDb.GetListAsync(p =>
                    p.OrderGuid == orderGuid
                );

                var orderDto = _mapper.Map<PosmSalesOrderDto>(order);
                orderDto.HasRefundItems = order.Status == PosmSalesOrderListRules.RefundOrderStatus;
                if (!orderDto.HasRefundItems)
                {
                    try
                    {
                        orderDto.HasRefundItems = (await LoadReturnOrderGuidsAsync(new List<string> { orderGuid })).Count > 0;
                    }
                    catch (Exception ex)
                    {
                        // 退货标记只是展示增强，查询失败不影响详情。
                        _logger.LogError(ex, "查询订单退货标记失败");
                    }
                }

                try
                {
                    if (!string.IsNullOrEmpty(order.BranchCode))
                    {
                        var store = await _context.StoreDb.GetFirstAsync(s =>
                            s.StoreCode == order.BranchCode && !s.IsDeleted
                        );
                        if (store != null)
                        {
                            orderDto.BranchName = store.StoreName;
                            orderDto.ABN = store.ABN;
                            orderDto.BrandName = store.BrandName;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "查询分店信息失败");
                }

                var detailDtos = _mapper.Map<List<PosmSalesOrderDetailDto>>(orderDetails);
                // 货号与商品图来自主档，POSM 明细本身不存；主档没有的商品只显示编码。
                var masters = await LookupProductMastersAsync(
                    detailDtos.Select(d => d.ProductCode).Where(code => !string.IsNullOrWhiteSpace(code))!
                );
                foreach (var detail in detailDtos)
                {
                    if (detail.ProductCode != null && masters.TryGetValue(detail.ProductCode, out var master))
                    {
                        detail.ItemNumber = master.ItemNumber;
                        detail.ProductImage = master.Image;
                    }
                }

                var response = new PosmSalesOrderDetailResponse
                {
                    Order = orderDto,
                    OrderDetails = detailDtos,
                    PaymentDetails = _mapper.Map<List<PosmPaymentDetailDto>>(paymentDetails),
                };

                return new ApiResponse<PosmSalesOrderDetailResponse>
                {
                    Success = true,
                    Data = response,
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetSalesOrderDetailAsync failed");
                return new ApiResponse<PosmSalesOrderDetailResponse>
                {
                    Success = false,
                    Message = ex.Message,
                };
            }
        }
    }
}
