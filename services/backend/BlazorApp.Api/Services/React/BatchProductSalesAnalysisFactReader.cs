using System.Data;
using System.Data.Common;
using System.Text.Json;
using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBSalesRecord;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Data.SqlClient;
using SqlSugar;

namespace BlazorApp.Api.Services.React;

/// <summary>
/// 批量货号销量的事实读取边界。每个来源都在 SQL Server 端按日期、分店、商品和折扣类别聚合；
/// 不把交易明细搬回 API 进程，也不为每个商品或门店重新查询。
/// </summary>
internal sealed class BatchProductSalesAnalysisFactReader
{
    private readonly ISqlSugarClient _catalogDb;
    private readonly ISqlSugarClient _posmDb;
    private readonly ISqlSugarClient _hbSalesDb;

    internal BatchProductSalesAnalysisFactReader(
        ISqlSugarClient catalogDb,
        ISqlSugarClient posmDb,
        ISqlSugarClient hbSalesDb)
    {
        _catalogDb = catalogDb;
        _posmDb = posmDb;
        _hbSalesDb = hbSalesDb;
    }

    internal async Task<List<BatchProductSalesAggregateRow>> ReadAsync(
        IReadOnlyCollection<string> productCodes,
        DateTime startDate,
        DateTime endDate,
        IReadOnlyCollection<string> storeCodes,
        CancellationToken cancellationToken,
        IReadOnlyList<BatchProductSalesHBSalesAlias>? preparedHbsAliases = null)
    {
        var products = Normalize(productCodes);
        var stores = Normalize(storeCodes);
        if (products.Count == 0 || stores.Count == 0)
            return [];
        EnsureSqlServer(_posmDb, "POSM");
        EnsureSqlServer(_hbSalesDb, "HBSales");
        EnsureSqlServer(_catalogDb, "HBweb");

        var endExclusive = endDate.Date.AddDays(1);
        cancellationToken.ThrowIfCancellationRequested();
        var posmTask = ReadPosmAsync(products, stores, startDate.Date, endExclusive, cancellationToken);
        // HBSales 的可用范围并不等同于自然年份。与 canonical 统计使用同一个已核验历史窗口，
        // 使 2024-09-14 起的真实历史进入事实，同时不把 2026 误接入旧来源。
        var hbStart = Max(startDate.Date, SalesStatisticsHBSalesHistoryWindow.StartDate);
        var hbEndExclusive = Min(endExclusive, SalesStatisticsHBSalesHistoryWindow.EndExclusive);
        var hbTask = hbStart < hbEndExclusive
            ? ReadHBSalesAsync(products, stores, hbStart, hbEndExclusive, cancellationToken, preparedHbsAliases)
            : Task.FromResult(new List<SqlAggregateRow>());
        await Task.WhenAll(posmTask, hbTask);
        cancellationToken.ThrowIfCancellationRequested();

        // 两个数据库不能 UNION。先按 canonical 的日期/分店/供应商/商品粒度合并两个来源并落为四位，
        // 再合并为页面需要的折扣类别；不能让 POSM 与 HBSales 分别 round 后改变日统计金额。
        return CanonicalizeSupplierGroups(posmTask.Result.Concat(hbTask.Result));
    }

    private async Task<List<SqlAggregateRow>> ReadPosmAsync(
        IReadOnlyList<string> products, IReadOnlyList<string> stores, DateTime startDate, DateTime endExclusive,
        CancellationToken cancellationToken)
    {
        const string sql = """
SET NOCOUNT ON;

-- 全日快照的 ProductScope 可能有数千项。先物化范围和来源订单，避免 CTE 被优化器反复展开后
-- 对 OPENJSON 与成交明细作错误的基数估计；下游仍只在 SQL Server 内聚合。
-- 商品主数据及快照键均限定为 nvarchar(50)。OPENJSON 的 value 是 nvarchar(max)，
-- 需在物化时收窄，否则 SQL Server 不能为范围表创建索引。
SELECT DISTINCT CONVERT(nvarchar(50), LTRIM(RTRIM([value]))) AS ProductCode INTO #ProductScope FROM OPENJSON(@products);
CREATE UNIQUE CLUSTERED INDEX IX_ProductScope ON #ProductScope(ProductCode);
SELECT DISTINCT CONVERT(nvarchar(50), LTRIM(RTRIM([value]))) AS BranchCode INTO #StoreScope FROM OPENJSON(@stores);
CREATE UNIQUE CLUSTERED INDEX IX_StoreScope ON #StoreScope(BranchCode);

SELECT DeviceCode, BranchCode INTO #DeviceBranch FROM (
    -- 与 ProductStoreDaily 的 C# 分组一致：取同设备第一条非空分店，不能用 MAX 改变历史归属。
    SELECT UPPER(LTRIM(RTRIM([系统设备编号]))) AS DeviceCode,
           LTRIM(RTRIM([分店代码])) AS BranchCode,
           ROW_NUMBER() OVER (
               PARTITION BY UPPER(LTRIM(RTRIM([系统设备编号])))
               ORDER BY [ID]
           ) AS RowNumber
    FROM [POSM_设备注册信息表]
    WHERE [系统设备编号] IS NOT NULL
      AND NULLIF(LTRIM(RTRIM([分店代码])), '') IS NOT NULL
) mapped WHERE RowNumber = 1;
CREATE UNIQUE CLUSTERED INDEX IX_DeviceBranch ON #DeviceBranch(DeviceCode);

-- 与商品日统计同口径：按净额并入退款单（Status=3，负数明细/支付）；补充退货（ReturnFacts）只认 Status 1/4，
-- 否则会与退款单自带的负数明细重复冲减。
SELECT o.[OrderGuid], o.[OrderTime], o.[Status],
       COALESCE(NULLIF(LTRIM(RTRIM(o.[BranchCode])), ''), db.BranchCode) AS BranchCode,
       o.[DeviceCode]
INTO #EligibleOrders
    FROM [sales_order] o
    LEFT JOIN #DeviceBranch db ON db.DeviceCode = UPPER(LTRIM(RTRIM(o.[DeviceCode])))
    INNER JOIN #StoreScope ss ON ss.BranchCode = COALESCE(NULLIF(LTRIM(RTRIM(o.[BranchCode])), ''), db.BranchCode)
    WHERE o.[Status] IN (1, 3, 4) AND o.[OrderTime] >= @startDate AND o.[OrderTime] < @endExclusive
;
CREATE UNIQUE CLUSTERED INDEX IX_EligibleOrders ON #EligibleOrders(OrderGuid);

SELECT o.[OrderGuid], o.[OrderTime], o.[Status] AS OrderStatus, o.[BranchCode], o.[DeviceCode], d.[OrderDetailGuid],
           d.[ProductCode], d.[SupplierCode], d.[Quantity], d.[ActualAmount], d.[Price], d.[Subtotal], d.[DiscountAmount], d.[DiscountRate],
       CONVERT(nvarchar(50), LTRIM(RTRIM(d.[ProductCode]))) AS NormalizedProductCode
INTO #DayDetails
FROM #EligibleOrders o INNER JOIN [sales_order_detail] d ON d.[OrderGuid] = o.[OrderGuid];
CREATE INDEX IX_DayDetailsOrder ON #DayDetails(OrderGuid);
CREATE INDEX IX_DayDetailsProduct ON #DayDetails(NormalizedProductCode);

-- 先限定真正含目标商品的订单，随后仍带出该订单的所有明细作为支付分母。
SELECT DISTINCT d.[OrderGuid] INTO #TargetSaleOrders
FROM #DayDetails d INNER JOIN #ProductScope ps ON ps.ProductCode = d.NormalizedProductCode;
CREATE UNIQUE CLUSTERED INDEX IX_TargetSaleOrders ON #TargetSaleOrders(OrderGuid);

SELECT p.[OrderGuid], SUM(COALESCE(p.[Amount], 0)) AS PaymentAmount INTO #PaymentTotals
FROM [payment_detail] p INNER JOIN #TargetSaleOrders o ON o.[OrderGuid] = p.[OrderGuid]
GROUP BY p.[OrderGuid];
CREATE UNIQUE CLUSTERED INDEX IX_PaymentTotals ON #PaymentTotals(OrderGuid);

SELECT d.[OrderGuid], d.[OrderTime], d.[OrderStatus], d.[BranchCode], d.[DeviceCode], d.[OrderDetailGuid], d.[ProductCode], d.[SupplierCode],
       d.[Quantity], d.[ActualAmount], d.[Price], d.[Subtotal], d.[DiscountAmount], d.[DiscountRate],
       d.NormalizedProductCode,
       SUM(COALESCE(d.[ActualAmount], 0)) OVER (PARTITION BY d.[OrderGuid]) AS OrderDetailAmount
INTO #AllDetails
FROM #DayDetails d INNER JOIN #TargetSaleOrders tso ON tso.[OrderGuid] = d.[OrderGuid];
CREATE INDEX IX_AllDetailsProduct ON #AllDetails(NormalizedProductCode);

-- 退货去重是日/状态语义，不能受本次分店 scope 影响；显式从当天全店订单收集 GUID。
SELECT d.[OrderDetailGuid] INTO #CurrentDayDetailGuids
FROM [sales_order] o INNER JOIN [sales_order_detail] d ON d.[OrderGuid] = o.[OrderGuid]
WHERE o.[Status] IN (1, 3, 4) AND o.[OrderTime] >= @startDate AND o.[OrderTime] < @endExclusive;
CREATE UNIQUE CLUSTERED INDEX IX_CurrentDayDetailGuids ON #CurrentDayDetailGuids(OrderDetailGuid);

WITH ProductScope AS (SELECT ProductCode FROM #ProductScope),
StoreScope AS (SELECT BranchCode FROM #StoreScope),
SaleFacts AS (
    SELECT CONVERT(date, d.[OrderTime]) AS [Date],
           d.[BranchCode] AS BranchCode,
           d.NormalizedProductCode AS ProductCode,
           COALESCE(NULLIF(LTRIM(RTRIM(d.[SupplierCode])), ''), NULLIF(LTRIM(RTRIM(supplierMap.[LocalSupplierCode])), ''), 'UNKNOWN') AS SupplierCode,
           COALESCE(d.[Quantity], 0) AS Quantity,
           -- 先把 SUM 后的 decimal(38,*) 缩窄，避免 SQL Server 除法把有效分摊精度压到 6 位；
           -- POSM 的支付分摊原值可到六位；先缩窄 SUM 后的 decimal(38,*)，再以 19,6 / 26,12
           -- 保留付款、明细和分母的六位精度，避免 SQL Server 除法降成六位或在源侧截去小数。
           CASE WHEN pt.PaymentAmount IS NULL OR d.OrderDetailAmount = 0 THEN CAST(0 AS decimal(38,16))
                ELSE CAST(
                    CAST(CAST(pt.PaymentAmount AS decimal(19,6)) * CAST(COALESCE(d.[ActualAmount], 0) AS decimal(19,6)) AS decimal(26,12))
                    / NULLIF(CAST(d.OrderDetailAmount AS decimal(19,6)), 0)
                    AS decimal(38,16)) END AS SalesAmount,
           d.[Price], d.[ActualAmount], d.[Subtotal], d.[DiscountAmount], d.[DiscountRate],
           CAST(CASE WHEN COALESCE(d.[Quantity], 0) < 0 OR COALESCE(d.[ActualAmount], 0) < 0 THEN 1 ELSE 0 END AS bit) AS IsReturn,
           -- 退款单（Status=3）的负数行自带原价/小计/折扣字段，按行自身判定折扣，不因缺「原单数量证据」归为未知。
           CAST(CASE WHEN d.OrderStatus = 3 THEN 1 ELSE 0 END AS bit) AS IsRefundOrderLine
    FROM #AllDetails d
    LEFT JOIN #PaymentTotals pt ON pt.[OrderGuid] = d.[OrderGuid]
    LEFT JOIN [posm_product_supplier_mapping] supplierMap
        ON supplierMap.[ProductCode] = d.NormalizedProductCode
    INNER JOIN ProductScope ps ON ps.ProductCode = d.NormalizedProductCode
),
ReturnFacts AS (
    SELECT CONVERT(date, o.[OrderTime]) AS [Date],
           o.[BranchCode] AS BranchCode,
           LTRIM(RTRIM(COALESCE(NULLIF(LTRIM(RTRIM(r.[ProductCode])), ''), od.[ProductCode]))) AS ProductCode,
           COALESCE(NULLIF(LTRIM(RTRIM(od.[SupplierCode])), ''), NULLIF(LTRIM(RTRIM(returnSupplierMap.[LocalSupplierCode])), ''), 'UNKNOWN') AS SupplierCode,
           -ABS(COALESCE(r.[ReturnQuantity], 0)) AS Quantity,
           -ABS(COALESCE(r.[ReturnAmount], 0)) AS SalesAmount,
           CASE WHEN LTRIM(RTRIM(r.[OriginalOrderGuid])) = LTRIM(RTRIM(od.[OrderGuid]))
                     AND (NULLIF(LTRIM(RTRIM(r.[ProductCode])), '') IS NULL OR NULLIF(LTRIM(RTRIM(od.[ProductCode])), '') IS NULL OR LTRIM(RTRIM(r.[ProductCode])) = LTRIM(RTRIM(od.[ProductCode])))
                THEN od.[Price] END AS Price,
           CASE WHEN LTRIM(RTRIM(r.[OriginalOrderGuid])) = LTRIM(RTRIM(od.[OrderGuid]))
                     AND (NULLIF(LTRIM(RTRIM(r.[ProductCode])), '') IS NULL OR NULLIF(LTRIM(RTRIM(od.[ProductCode])), '') IS NULL OR LTRIM(RTRIM(r.[ProductCode])) = LTRIM(RTRIM(od.[ProductCode])))
                THEN od.[ActualAmount] END AS OriginalActualAmount,
           CASE WHEN LTRIM(RTRIM(r.[OriginalOrderGuid])) = LTRIM(RTRIM(od.[OrderGuid]))
                     AND (NULLIF(LTRIM(RTRIM(r.[ProductCode])), '') IS NULL OR NULLIF(LTRIM(RTRIM(od.[ProductCode])), '') IS NULL OR LTRIM(RTRIM(r.[ProductCode])) = LTRIM(RTRIM(od.[ProductCode])))
                THEN od.[Quantity] END AS OriginalQuantity,
           CASE WHEN LTRIM(RTRIM(r.[OriginalOrderGuid])) = LTRIM(RTRIM(od.[OrderGuid]))
                     AND (NULLIF(LTRIM(RTRIM(r.[ProductCode])), '') IS NULL OR NULLIF(LTRIM(RTRIM(od.[ProductCode])), '') IS NULL OR LTRIM(RTRIM(r.[ProductCode])) = LTRIM(RTRIM(od.[ProductCode])))
                THEN od.[Subtotal] END AS Subtotal,
           CASE WHEN LTRIM(RTRIM(r.[OriginalOrderGuid])) = LTRIM(RTRIM(od.[OrderGuid]))
                     AND (NULLIF(LTRIM(RTRIM(r.[ProductCode])), '') IS NULL OR NULLIF(LTRIM(RTRIM(od.[ProductCode])), '') IS NULL OR LTRIM(RTRIM(r.[ProductCode])) = LTRIM(RTRIM(od.[ProductCode])))
                THEN od.[DiscountAmount] END AS DiscountAmount,
           CASE WHEN LTRIM(RTRIM(r.[OriginalOrderGuid])) = LTRIM(RTRIM(od.[OrderGuid]))
                     AND (NULLIF(LTRIM(RTRIM(r.[ProductCode])), '') IS NULL OR NULLIF(LTRIM(RTRIM(od.[ProductCode])), '') IS NULL OR LTRIM(RTRIM(r.[ProductCode])) = LTRIM(RTRIM(od.[ProductCode])))
                THEN od.[DiscountRate] END AS DiscountRate,
           CAST(1 AS bit) AS IsReturn,
           CAST(0 AS bit) AS IsRefundOrderLine
    FROM [sales_return_record] r
    INNER JOIN #EligibleOrders o ON o.[OrderGuid] = r.[ReturnOrderGuid] AND o.[Status] IN (1, 4)
    LEFT JOIN [sales_order_detail] od ON od.[OrderDetailGuid] = r.[OriginalOrderDetailGuid]
    LEFT JOIN [posm_product_supplier_mapping] returnSupplierMap
        ON returnSupplierMap.[ProductCode] = LTRIM(RTRIM(COALESCE(NULLIF(LTRIM(RTRIM(r.[ProductCode])), ''), od.[ProductCode])))
    INNER JOIN ProductScope ps ON ps.ProductCode = LTRIM(RTRIM(COALESCE(NULLIF(LTRIM(RTRIM(r.[ProductCode])), ''), od.[ProductCode])))
    WHERE NULLIF(LTRIM(RTRIM(r.[ReturnDetailGuid])), '') IS NULL
       OR NOT EXISTS (SELECT 1 FROM #CurrentDayDetailGuids ad WHERE ad.[OrderDetailGuid] = r.[ReturnDetailGuid])
),
Facts AS (
    SELECT s.[Date], s.BranchCode, s.ProductCode, s.SupplierCode, s.Quantity, s.SalesAmount, s.[Price], s.[ActualAmount],
           NULL AS OriginalActualAmount, NULL AS OriginalQuantity, s.[Subtotal], s.[DiscountAmount], s.[DiscountRate], s.IsReturn, s.IsRefundOrderLine FROM SaleFacts s
    UNION ALL
    SELECT r.[Date], r.BranchCode, r.ProductCode, r.SupplierCode, r.Quantity, r.SalesAmount, r.[Price], NULL,
           r.OriginalActualAmount, r.OriginalQuantity, r.[Subtotal], r.[DiscountAmount], r.[DiscountRate], r.IsReturn, r.IsRefundOrderLine FROM ReturnFacts r
),
Classified AS (
    SELECT f.*,
      CASE
        WHEN f.IsReturn = 1 AND f.IsRefundOrderLine = 0 AND (f.[Price] IS NULL OR f.OriginalQuantity IS NULL OR f.OriginalQuantity = 0
             OR ABS(ABS(f.SalesAmount) / NULLIF(ABS(f.Quantity), 0) - ABS(f.OriginalActualAmount) / NULLIF(ABS(f.OriginalQuantity), 0)) > 0.0001) THEN 2
        WHEN f.[DiscountAmount] IS NULL AND f.[DiscountRate] IS NULL AND f.[Subtotal] IS NULL THEN 2
        WHEN COALESCE(f.[DiscountAmount], 0) <> 0 OR COALESCE(f.[DiscountRate], 0) <> 0
             OR (f.[Subtotal] IS NOT NULL AND ABS(COALESCE(f.[ActualAmount], f.OriginalActualAmount, 0)) < ABS(f.[Subtotal])) THEN 1
        ELSE 0 END AS DiscountKind
    FROM Facts f INNER JOIN StoreScope ss ON ss.BranchCode = f.BranchCode
)
SELECT [Date], BranchCode, ProductCode, SupplierCode, DiscountKind,
       SUM(Quantity) AS Quantity, SUM(SalesAmount) AS SalesAmount,
       SUM(CASE WHEN IsReturn = 1 THEN ABS(Quantity) ELSE 0 END) AS ReturnQuantity,
       SUM(CASE WHEN DiscountKind = 2 THEN 1 ELSE 0 END) AS UnknownRowCount,
       MIN(CASE WHEN DiscountKind <> 2 AND [Price] > 0 THEN [Price] END) AS OriginalPriceMin,
       MAX(CASE WHEN DiscountKind <> 2 AND [Price] > 0 THEN [Price] END) AS OriginalPriceMax,
       MIN(CASE WHEN DiscountKind = 1 AND Quantity <> 0 THEN ABS(SalesAmount / Quantity) END) AS DiscountPriceMin,
       MAX(CASE WHEN DiscountKind = 1 AND Quantity <> 0 THEN ABS(SalesAmount / Quantity) END) AS DiscountPriceMax
FROM Classified
WHERE BranchCode IS NOT NULL AND BranchCode <> '' AND ProductCode IS NOT NULL AND ProductCode <> ''
GROUP BY [Date], BranchCode, ProductCode, SupplierCode, DiscountKind;
""";
        var returnTableName = _posmDb.EntityMaintenance.GetTableName(typeof(SalesReturnRecord));
        var hasReturnTable = _posmDb.DbMaintenance.GetTableInfoList(false)
            .Any(table => string.Equals(table.Name, returnTableName, StringComparison.OrdinalIgnoreCase));
        var effectiveSql = hasReturnTable ? sql : sql.Replace("[sales_return_record] r", """
(SELECT CAST(NULL AS nvarchar(100)) [ReturnDetailGuid], CAST(NULL AS nvarchar(100)) [ReturnOrderGuid],
        CAST(NULL AS nvarchar(100)) [OriginalOrderGuid], CAST(NULL AS nvarchar(100)) [OriginalOrderDetailGuid],
        CAST(NULL AS nvarchar(100)) [ProductCode], CAST(NULL AS decimal(19,4)) [ReturnQuantity],
        CAST(NULL AS decimal(19,4)) [ReturnAmount] WHERE 1 = 0) r
""", StringComparison.Ordinal);
        return await ExecuteAggregateQueryAsync(_posmDb, effectiveSql,
            [new("@products", JsonSerializer.Serialize(products), SqlDbType.NVarChar),
             new("@stores", JsonSerializer.Serialize(stores), SqlDbType.NVarChar),
             new("@startDate", startDate, SqlDbType.DateTime2), new("@endExclusive", endExclusive, SqlDbType.DateTime2)],
            cancellationToken);
    }

    private async Task<List<SqlAggregateRow>> ReadHBSalesAsync(IReadOnlyList<string> products,
        IReadOnlyList<string> stores, DateTime startDate, DateTime endExclusive, CancellationToken cancellationToken,
        IReadOnlyList<BatchProductSalesHBSalesAlias>? preparedAliases)
    {
        // 定时日快照已在 Capture 阶段从当天实际缺码明细读取候选并固定进来源签名。
        // 直接复用它，避免全日商品数千时再次按“所有商品的全部历史 alias”扩展目录。
        // 旧的页面/服务调用仍沿用原有动态解析路径。
        var aliases = preparedAliases?.ToList() ?? await BuildHBSalesAliasesAsync(products, stores, cancellationToken);
        const string sql = """
SET NOCOUNT ON;

-- HBSales 的当天事实很窄，但退货原单证据可能落在整个历史详情表。先物化当天行及实际原单号，
-- 再只扫描一次候选原单详情，避免每条退货在 OUTER APPLY 中重复全表扫描。
SELECT DISTINCT CONVERT(nvarchar(100), LTRIM(RTRIM([value]))) ProductCode INTO #ProductScope
FROM OPENJSON(@products) WHERE NULLIF(LTRIM(RTRIM([value])), '') IS NOT NULL;
CREATE UNIQUE CLUSTERED INDEX IX_ProductScope ON #ProductScope(ProductCode);
SELECT DISTINCT CONVERT(nvarchar(100), LTRIM(RTRIM([value]))) BranchCode INTO #StoreScope
FROM OPENJSON(@stores) WHERE NULLIF(LTRIM(RTRIM([value])), '') IS NOT NULL;
CREATE UNIQUE CLUSTERED INDEX IX_StoreScope ON #StoreScope(BranchCode);
SELECT Alias, BranchCode, ProductCode, Scope INTO #Aliases FROM OPENJSON(@aliases)
    WITH (Alias nvarchar(100), BranchCode nvarchar(100), ProductCode nvarchar(100), Scope nvarchar(16));
CREATE INDEX IX_AliasesBranch ON #Aliases(Scope, BranchCode, Alias);
CREATE INDEX IX_AliasesGlobal ON #Aliases(Scope, Alias);

SELECT IDENTITY(bigint, 1, 1) AS RowId,
       CONVERT(date, d.[B结账日期]) [Date], LTRIM(RTRIM(d.[B分店代码])) BranchCode,
       LTRIM(RTRIM(d.[B产品编号])) RawProductCode, d.[B货号] ItemNumber, d.[B条形码] Barcode,
       LTRIM(RTRIM(d.[B供应商ID])) SupplierCode, d.[B数量] Quantity, d.[B合计金额] SalesAmount,
       d.[B单价] DetailPrice, d.[B原价合计金额] DetailOriginalAmount, d.[B合计金额] DetailSaleAmount,
       d.[B数量] DetailSaleQuantity, d.[B折扣率] DetailDiscountRate,
       LTRIM(RTRIM(m.[B单据类型])) DocumentType,
       LTRIM(RTRIM(m.[B原销售单号])) OriginalOrderNumber,
       LTRIM(RTRIM(d.[B退货码])) ReturnCode
INTO #DayFacts
FROM [B销售清单主表副本] m
INNER JOIN [B销售清单详情表副本] d ON d.[B销售单号] = m.[B销售单号]
INNER JOIN #StoreScope scope ON scope.BranchCode = LTRIM(RTRIM(d.[B分店代码]))
WHERE d.[B结账日期] >= @startDate AND d.[B结账日期] < @endExclusive
  AND m.[B结账日期] >= @mainWindowStart AND m.[B结账日期] < @mainWindowEnd
  AND (m.[B单据类型] IS NULL OR LTRIM(RTRIM(m.[B单据类型])) <> '2')
  AND (EXISTS (SELECT 1 FROM #ProductScope ps WHERE ps.ProductCode = LTRIM(RTRIM(d.[B产品编号])))
       OR (NULLIF(LTRIM(RTRIM(d.[B产品编号])), '') IS NULL
           AND EXISTS (SELECT 1 FROM #Aliases a WHERE a.Alias = LTRIM(RTRIM(d.[B货号])) OR a.Alias = LTRIM(RTRIM(d.[B条形码])))));
CREATE UNIQUE CLUSTERED INDEX IX_DayFactsRow ON #DayFacts(RowId);
CREATE INDEX IX_DayFactsReturn ON #DayFacts(DocumentType, OriginalOrderNumber);

SELECT DISTINCT OriginalOrderNumber INTO #OriginalOrderScope
FROM #DayFacts WHERE DocumentType IN ('3', '4') AND OriginalOrderNumber <> '';
CREATE UNIQUE CLUSTERED INDEX IX_OriginalOrderScope ON #OriginalOrderScope(OriginalOrderNumber);

SELECT LTRIM(RTRIM(o.[B销售单号])) OriginalOrderNumber, o.[ID] DetailId,
       LTRIM(RTRIM(o.[B退货码])) ReturnCode, LTRIM(RTRIM(o.[B产品编号])) ProductCode,
       LTRIM(RTRIM(o.[B条形码])) Barcode, o.[B单价], o.[B原价合计金额],
       o.[B合计金额], o.[B数量], o.[B折扣率]
INTO #OriginalEvidenceSource
FROM [B销售清单详情表副本] o
INNER JOIN #OriginalOrderScope scope ON scope.OriginalOrderNumber = LTRIM(RTRIM(o.[B销售单号]));
CREATE INDEX IX_OriginalEvidenceSource ON #OriginalEvidenceSource(OriginalOrderNumber, ReturnCode, ProductCode, Barcode);

SELECT r.RowId, COUNT(o.DetailId) CandidateCount, MIN(o.ProductCode) OriginalProductCode,
       MIN(o.[B单价]) [B单价], MIN(o.[B原价合计金额]) [B原价合计金额],
       MIN(o.[B合计金额]) [B合计金额], MIN(o.[B数量]) [B数量], MIN(o.[B折扣率]) [B折扣率]
INTO #OriginalEvidence
FROM #DayFacts r
LEFT JOIN #OriginalEvidenceSource o ON r.DocumentType IN ('3', '4')
    AND o.OriginalOrderNumber = r.OriginalOrderNumber
    AND ((NULLIF(r.ReturnCode, '') IS NOT NULL
          AND o.ReturnCode = r.ReturnCode
          AND (NULLIF(r.RawProductCode, '') IS NULL OR NULLIF(o.ProductCode, '') IS NULL OR o.ProductCode = r.RawProductCode))
         OR (NULLIF(r.ReturnCode, '') IS NULL
             AND (NULLIF(r.RawProductCode, '') IS NULL OR o.ProductCode = r.RawProductCode)
             AND (NULLIF(LTRIM(RTRIM(r.Barcode)), '') IS NULL OR o.Barcode = LTRIM(RTRIM(r.Barcode)))) )
WHERE r.DocumentType IN ('3', '4')
GROUP BY r.RowId;
CREATE UNIQUE CLUSTERED INDEX IX_OriginalEvidence ON #OriginalEvidence(RowId);

WITH Raw AS (
 SELECT f.[Date], f.BranchCode, f.RawProductCode, f.ItemNumber, f.Barcode, f.SupplierCode, f.Quantity, f.SalesAmount,
        CASE WHEN f.DocumentType IN ('3','4') THEN origEvidence.[B单价] ELSE f.DetailPrice END OriginalPrice,
        CASE WHEN f.DocumentType IN ('3','4') THEN origEvidence.[B原价合计金额] ELSE f.DetailOriginalAmount END OriginalAmount,
        CASE WHEN f.DocumentType IN ('3','4') THEN origEvidence.[B合计金额] ELSE f.DetailSaleAmount END OriginalSaleAmount,
        CASE WHEN f.DocumentType IN ('3','4') THEN origEvidence.[B数量] ELSE f.DetailSaleQuantity END OriginalSaleQuantity,
        CASE WHEN f.DocumentType IN ('3','4') THEN origEvidence.[B折扣率] ELSE f.DetailDiscountRate END DiscountRate,
        CASE WHEN f.DocumentType IN ('3','4') THEN origEvidence.CandidateCount ELSE 1 END OriginalCandidateCount,
        origEvidence.OriginalProductCode, f.DocumentType
 FROM #DayFacts f LEFT JOIN #OriginalEvidence origEvidence ON origEvidence.RowId = f.RowId
),
Resolved AS (
 SELECT r.*, CASE WHEN NULLIF(LTRIM(RTRIM(r.RawProductCode)), '') IS NOT NULL THEN ps.ProductCode
                  ELSE COALESCE(branchAlias.ProductCode, globalAlias.ProductCode, crossAlias.ProductCode) END ProductCode,
   CASE WHEN branchAlias.CandidateCount > 0 THEN branchAlias.CandidateCount
        WHEN globalAlias.CandidateCount > 0 THEN globalAlias.CandidateCount ELSE crossAlias.CandidateCount END CandidateCount
 FROM Raw r
 LEFT JOIN #ProductScope ps ON ps.ProductCode = LTRIM(RTRIM(r.RawProductCode))
 OUTER APPLY (SELECT COUNT(DISTINCT ProductCode) CandidateCount, MIN(ProductCode) ProductCode FROM #Aliases a
   WHERE a.Scope = 'branch' AND a.BranchCode = r.BranchCode AND a.Alias = LTRIM(RTRIM(r.Barcode))) branchAlias
 OUTER APPLY (SELECT COUNT(DISTINCT ProductCode) CandidateCount, MIN(ProductCode) ProductCode FROM #Aliases a
   WHERE a.Scope = 'global' AND (a.Alias = LTRIM(RTRIM(r.ItemNumber)) OR a.Alias = LTRIM(RTRIM(r.Barcode)))) globalAlias
 OUTER APPLY (SELECT COUNT(DISTINCT ProductCode) CandidateCount, MIN(ProductCode) ProductCode FROM #Aliases a
   WHERE a.Scope = 'cross' AND a.Alias = LTRIM(RTRIM(r.Barcode))) crossAlias
),
Classified AS (
 SELECT [Date], BranchCode, ProductCode, COALESCE(NULLIF(SupplierCode, ''), 'UNKNOWN') AS SupplierCode,
   CASE WHEN LTRIM(RTRIM(DocumentType)) IN ('3','4') THEN -COALESCE(Quantity,0) ELSE COALESCE(Quantity,0) END Quantity,
   CASE WHEN LTRIM(RTRIM(DocumentType)) IN ('3','4') THEN -COALESCE(SalesAmount,0) ELSE COALESCE(SalesAmount,0) END SalesAmount,
   CASE WHEN LTRIM(RTRIM(DocumentType)) IN ('3','4') THEN 1 ELSE 0 END IsReturn,
   OriginalPrice,
   CASE WHEN LTRIM(RTRIM(DocumentType)) IN ('3','4')
             AND (OriginalCandidateCount <> 1 OR OriginalSaleQuantity IS NULL OR OriginalSaleQuantity = 0
                  OR (NULLIF(LTRIM(RTRIM(OriginalProductCode)), '') IS NOT NULL
                      AND LTRIM(RTRIM(OriginalProductCode)) <> LTRIM(RTRIM(ProductCode)))
                  OR Quantity IS NULL OR Quantity = 0
                  OR ABS(ABS(COALESCE(SalesAmount, 0)) / ABS(Quantity)
                         - ABS(COALESCE(OriginalSaleAmount, 0)) / ABS(OriginalSaleQuantity)) > 0.0001) THEN 2
        WHEN OriginalAmount IS NULL AND DiscountRate IS NULL THEN 2
        WHEN COALESCE(DiscountRate,0) <> 0
             OR (OriginalAmount IS NOT NULL AND ABS(COALESCE(OriginalSaleAmount, SalesAmount,0)) < ABS(OriginalAmount)) THEN 1
        ELSE 0 END DiscountKind
 FROM Resolved
 WHERE ProductCode IS NOT NULL AND ProductCode <> ''
   -- 权威 B产品编号 只需精确命中目标商品；别名歧义只能限制缺产品编号行。
   AND (NULLIF(LTRIM(RTRIM(RawProductCode)), '') IS NOT NULL OR CandidateCount IS NULL OR CandidateCount <= 1)
)
SELECT c.[Date], c.BranchCode, c.ProductCode, c.SupplierCode, c.DiscountKind, SUM(Quantity) Quantity, SUM(SalesAmount) SalesAmount,
 SUM(CASE WHEN IsReturn=1 THEN ABS(Quantity) ELSE 0 END) ReturnQuantity,
 SUM(CASE WHEN DiscountKind=2 THEN 1 ELSE 0 END) UnknownRowCount,
 MIN(CASE WHEN DiscountKind<>2 AND OriginalPrice>0 THEN OriginalPrice END) OriginalPriceMin,
 MAX(CASE WHEN DiscountKind<>2 AND OriginalPrice>0 THEN OriginalPrice END) OriginalPriceMax,
 MIN(CASE WHEN DiscountKind=1 AND Quantity<>0 THEN ABS(SalesAmount/Quantity) END) DiscountPriceMin,
 MAX(CASE WHEN DiscountKind=1 AND Quantity<>0 THEN ABS(SalesAmount/Quantity) END) DiscountPriceMax
FROM Classified c INNER JOIN #StoreScope s ON s.BranchCode=c.BranchCode
GROUP BY c.[Date], c.BranchCode, c.ProductCode, c.SupplierCode, c.DiscountKind;
""";
        return await ExecuteAggregateQueryAsync(_hbSalesDb, sql,
            [new("@products", JsonSerializer.Serialize(products), SqlDbType.NVarChar), new("@stores", JsonSerializer.Serialize(stores), SqlDbType.NVarChar),
             new("@aliases", JsonSerializer.Serialize(aliases), SqlDbType.NVarChar), new("@startDate", startDate, SqlDbType.DateTime2),
             new("@endExclusive", endExclusive, SqlDbType.DateTime2), new("@mainWindowStart", startDate.AddDays(-7), SqlDbType.DateTime2),
             new("@mainWindowEnd", endExclusive.AddDays(7), SqlDbType.DateTime2)], cancellationToken);
    }

    private async Task<List<BatchProductSalesHBSalesAlias>> BuildHBSalesAliasesAsync(IReadOnlyList<string> products,
        IReadOnlyList<string> stores, CancellationToken cancellationToken)
    {
        var productScope = JsonSerializer.Serialize(products);
        var target = await ExecuteCatalogRowsAsync(_catalogDb, """
SELECT p.[ProductCode], p.[ItemNumber], p.[Barcode] FROM [Product] p
INNER JOIN OPENJSON(@codes) s ON s.[value] = p.[ProductCode] WHERE p.[IsDeleted] = 0;
""", productScope, cancellationToken, reader => new CatalogProductRow
        {
            ProductCode = GetNullableString(reader, 0), ItemNumber = GetNullableString(reader, 1), Barcode = GetNullableString(reader, 2),
        });
        // 正向读取目标商品的所有三类历史身份，再查同名候选做全局/分店歧义判定；
        // 这一集合与查询商品数绑定，避免为一个商品把全年所有缺码交易拉入内存。
        var targetSetAliases = await ExecuteCatalogRowsAsync(_catalogDb, """
SELECT p.[SetBarcode] FROM [ProductSetCode] p INNER JOIN OPENJSON(@codes) s ON s.[value] = p.[ProductCode]
WHERE p.[IsDeleted] = 0 AND p.[SetBarcode] IS NOT NULL;
""", productScope, cancellationToken, reader => GetNullableString(reader, 0));
        var targetMultiAliases = await ExecuteCatalogRowsAsync(_catalogDb, """
SELECT p.[MultiBarcode] FROM [StoreMultiCodeProduct] p INNER JOIN OPENJSON(@codes) s ON s.[value] = p.[ProductCode]
WHERE p.[IsDeleted] = 0 AND p.[MultiBarcode] IS NOT NULL;
""", productScope, cancellationToken, reader => GetNullableString(reader, 0));
        var aliases = target.SelectMany(p => new[] { p.ItemNumber, p.Barcode }).Concat(targetSetAliases).Concat(targetMultiAliases)
            .Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (aliases.Count == 0) return [];
        // 旧年份缺码可能跨越很多条码；按 500 个代码分批，避免生成无界 IN 参数列表。
        var allProducts = new List<CatalogProductRow>();
        var sets = new List<AliasCatalogRow>();
        var multi = new List<StoreAliasCatalogRow>();
        foreach (var chunk in aliases.Chunk(500))
        {
            var batch = JsonSerializer.Serialize(chunk);
            allProducts.AddRange(await ExecuteCatalogRowsAsync(_catalogDb, """
SELECT p.[ProductCode], p.[ItemNumber], p.[Barcode] FROM [Product] p INNER JOIN OPENJSON(@codes) s
ON s.[value] = p.[ItemNumber] OR s.[value] = p.[Barcode] WHERE p.[IsDeleted] = 0 AND p.[ProductCode] IS NOT NULL;
""", batch, cancellationToken, reader => new CatalogProductRow { ProductCode = GetNullableString(reader, 0), ItemNumber = GetNullableString(reader, 1), Barcode = GetNullableString(reader, 2) }));
            sets.AddRange(await ExecuteCatalogRowsAsync(_catalogDb, """
SELECT p.[SetBarcode], p.[ProductCode] FROM [ProductSetCode] p INNER JOIN OPENJSON(@codes) s ON s.[value] = p.[SetBarcode]
WHERE p.[IsDeleted] = 0 AND p.[ProductCode] IS NOT NULL;
""", batch, cancellationToken, reader => new AliasCatalogRow { Alias = GetNullableString(reader, 0), ProductCode = GetNullableString(reader, 1) }));
            multi.AddRange(await ExecuteCatalogRowsAsync(_catalogDb, """
SELECT p.[MultiBarcode], p.[StoreCode], p.[ProductCode] FROM [StoreMultiCodeProduct] p INNER JOIN OPENJSON(@codes) s ON s.[value] = p.[MultiBarcode]
WHERE p.[IsDeleted] = 0 AND p.[StoreCode] IS NOT NULL AND p.[ProductCode] IS NOT NULL;
""", batch, cancellationToken, reader => new StoreAliasCatalogRow { Alias = GetNullableString(reader, 0), BranchCode = GetNullableString(reader, 1), ProductCode = GetNullableString(reader, 2) }));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var targetSet = products.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<BatchProductSalesHBSalesAlias>();
        foreach (var value in aliases)
        {
            // 非目标商品也必须参加歧义判定，不能只看命中目标的行。
            var globalAll = allProducts.Where(p => Same(p.ItemNumber, value) || Same(p.Barcode, value)).Select(p => p.ProductCode!).Concat(sets.Where(s => Same(s.Alias, value)).Select(s => s.ProductCode!)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (globalAll.Count == 1 && targetSet.Contains(globalAll[0]))
                result.Add(new BatchProductSalesHBSalesAlias(value, null, globalAll[0], "global"));
            else if (globalAll.Count > 0)
                // 空值占位使 SQL 不会把已存在的全局歧义降级到跨店多码候选。
                result.Add(new BatchProductSalesHBSalesAlias(value, null, string.Empty, "global"));
            foreach (var branch in stores)
            {
                var candidates = multi.Where(m => Same(m.Alias, value) && Same(m.BranchCode, branch)).Select(m => m.ProductCode!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (candidates.Count == 1 && targetSet.Contains(candidates[0]))
                    result.Add(new BatchProductSalesHBSalesAlias(value, branch, candidates[0], "branch"));
                else if (candidates.Count > 0)
                    // 分店多码有候选但不唯一时，稳定口径不允许退回全局候选。
                    result.Add(new BatchProductSalesHBSalesAlias(value, branch, string.Empty, "branch"));
            }
            var cross = multi.Where(m => Same(m.Alias, value)).Select(m => m.ProductCode!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (globalAll.Count == 0 && cross.Count == 1 && targetSet.Contains(cross[0])) result.Add(new BatchProductSalesHBSalesAlias(value, null, cross[0], "cross"));
        }
        return result;
    }

    /// <summary>
    /// 对齐 ProductStoreDailySalesStatistic 的持久化边界：来源行先按供应商合并，金额仅在
    /// supplier 组末尾四舍五入。折扣类别不是 canonical 主键，因此类别金额的四位舍入残差
    /// 固定放入该组已有的最低类别，保证类别之和严格等于 canonical 总额而不改变事实分类。
    /// </summary>
    internal static List<BatchProductSalesAggregateRow> CanonicalizeSupplierGroups(IEnumerable<SqlAggregateRow> sourceRows)
    {
        var supplierRows = sourceRows
            .Where(row => !string.IsNullOrWhiteSpace(row.BranchCode)
                && !string.IsNullOrWhiteSpace(row.ProductCode))
            .GroupBy(row => new SupplierFactGroupKey(
                row.Date.Date,
                NormalizeFactCode(row.BranchCode),
                NormalizeFactCode(row.ProductCode),
                NormalizeSupplierCode(row.SupplierCode)))
            .SelectMany(group => CanonicalizeSupplierGroup(group.Key, group))
            .ToList();

        return supplierRows
            .GroupBy(row => new
            {
                row.Date,
                BranchCode = NormalizeFactCode(row.BranchCode),
                ProductCode = NormalizeFactCode(row.ProductCode),
                row.DiscountKind,
            })
            .Select(group => ToAggregateRow(
                group.Key.Date,
                group.Key.BranchCode,
                group.Key.ProductCode,
                group.Key.DiscountKind,
                group))
            .OrderBy(row => row.Date)
            .ThenBy(row => row.BranchCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ProductCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<SqlAggregateRow> CanonicalizeSupplierGroup(
        SupplierFactGroupKey key,
        IEnumerable<SqlAggregateRow> rows)
    {
        var categories = rows.GroupBy(row => row.DiscountKind)
            .Select(group => new SqlAggregateRow
            {
                Date = key.Date,
                BranchCode = key.BranchCode,
                ProductCode = key.ProductCode,
                SupplierCode = key.SupplierCode,
                DiscountKind = group.Key,
                Quantity = group.Sum(row => row.Quantity),
                SalesAmount = Math.Round(group.Sum(row => row.SalesAmount), 4, MidpointRounding.AwayFromZero),
                ReturnQuantity = group.Sum(row => row.ReturnQuantity),
                UnknownRowCount = group.Sum(row => row.UnknownRowCount),
                OriginalPriceMin = Min(group.Select(row => row.OriginalPriceMin)),
                OriginalPriceMax = Max(group.Select(row => row.OriginalPriceMax)),
                DiscountPriceMin = Min(group.Select(row => row.DiscountPriceMin)),
                DiscountPriceMax = Max(group.Select(row => row.DiscountPriceMax)),
            })
            .OrderBy(row => row.DiscountKind)
            .ToList();

        var canonicalAmount = Math.Round(rows.Sum(row => row.SalesAmount), 4, MidpointRounding.AwayFromZero);
        var amountResidual = canonicalAmount - categories.Sum(row => row.SalesAmount);
        if (amountResidual != 0m)
        {
            // 残差只来自把 canonical 无类别金额投影回类别；优先最低既有类别是稳定且可复算的选择。
            categories[0].SalesAmount += amountResidual;
        }

        var sourceQuantity = rows.Sum(row => row.Quantity);
        var canonicalQuantity = (decimal)(int)sourceQuantity;
        var quantityResidual = canonicalQuantity - categories.Sum(row => row.Quantity);
        if (quantityResidual != 0m)
        {
            // canonical 在 supplier 组强制截断总量，无法从类别事实判断被截去的分数属于哪一类。
            // 单独标为 unknown，既保留来源的已知类别，也不伪造正价/折扣数量。
            categories.Add(new SqlAggregateRow
            {
                Date = key.Date,
                BranchCode = key.BranchCode,
                ProductCode = key.ProductCode,
                SupplierCode = key.SupplierCode,
                DiscountKind = 2,
                Quantity = quantityResidual,
                UnknownRowCount = 1,
            });
        }

        return categories;
    }

    private static string NormalizeFactCode(string? value) => SalesStatisticsCodeRules.Normalize(value);

    private static string NormalizeSupplierCode(string? value)
    {
        var normalized = SalesStatisticsCodeRules.Normalize(value);
        return string.IsNullOrWhiteSpace(normalized)
            ? SalesStatisticsCodeRules.UnknownSupplierCode
            : normalized;
    }

    private sealed record SupplierFactGroupKey(DateTime Date, string BranchCode, string ProductCode, string SupplierCode);

    internal static BatchProductSalesAggregateRow ToAggregateRow(DateTime date, string branchCode, string productCode, int kind, IEnumerable<SqlAggregateRow> rows)
    {
        var list = rows.ToList(); var quantity = list.Sum(r => r.Quantity); var amount = list.Sum(r => r.SalesAmount);
        var row = new BatchProductSalesAggregateRow { Date = date.Date, BranchCode = branchCode.Trim(), ProductCode = productCode.Trim(), ReturnQuantity = list.Sum(r => r.ReturnQuantity), SalesAmount = amount,
            UnknownRowCount = list.Sum(r => r.UnknownRowCount), OriginalPriceMin = Min(list.Select(r => r.OriginalPriceMin)), OriginalPriceMax = Max(list.Select(r => r.OriginalPriceMax)), DiscountPriceMin = Min(list.Select(r => r.DiscountPriceMin)), DiscountPriceMax = Max(list.Select(r => r.DiscountPriceMax)) };
        if (kind == 0) row.RegularQuantity = quantity; else if (kind == 1) row.DiscountQuantity = quantity; else row.UnknownQuantity = quantity;
        row.Quantity = row.RegularQuantity + row.DiscountQuantity + row.UnknownQuantity;
        return row;
    }

    /// <summary>与 HBSales 日统计一致：类型 3/4 仅乘 -1，其它单据保留源数据符号。</summary>
    internal static decimal ApplyHBSalesDocumentSign(decimal value, string? documentType) =>
        documentType?.Trim() is "3" or "4" ? -value : value;

    private static async Task<List<SqlAggregateRow>> ExecuteAggregateQueryAsync(
        ISqlSugarClient db, string sql, IReadOnlyList<BatchSqlParameter> parameters, CancellationToken cancellationToken)
    {
        // 不使用 Ado.SqlQueryAsync：该调用不能接收请求取消令牌，且会继承 POSM 的长全局 timeout。
        await using var connection = new SqlConnection(db.CurrentConnectionConfig.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 60;
        foreach (var item in parameters)
        {
            var parameter = new SqlParameter(item.Name, item.Type) { Value = item.Value ?? DBNull.Value };
            if (item.Type == SqlDbType.NVarChar) parameter.Size = -1;
            command.Parameters.Add(parameter);
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<SqlAggregateRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SqlAggregateRow
            {
                Date = reader.GetDateTime(0), BranchCode = reader.GetString(1), ProductCode = reader.GetString(2),
                SupplierCode = reader.GetString(3), DiscountKind = reader.GetInt32(4), Quantity = reader.GetDecimal(5), SalesAmount = reader.GetDecimal(6),
                ReturnQuantity = reader.GetDecimal(7), UnknownRowCount = reader.GetInt32(8),
                OriginalPriceMin = GetNullableDecimal(reader, 9), OriginalPriceMax = GetNullableDecimal(reader, 10),
                DiscountPriceMin = GetNullableDecimal(reader, 11), DiscountPriceMax = GetNullableDecimal(reader, 12),
            });
        }
        return rows;
    }

    private static decimal? GetNullableDecimal(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
    private static string? GetNullableString(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static async Task<List<T>> ExecuteCatalogRowsAsync<T>(ISqlSugarClient db, string sql, string codesJson,
        CancellationToken cancellationToken, Func<DbDataReader, T> map)
    {
        // 目录别名也必须响应取消；不能在聚合 SQL 可取消后仍由 SqlSugar 的 ToListAsync 长时间占用请求。
        await using var connection = new SqlConnection(db.CurrentConnectionConfig.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 60;
        command.Parameters.Add(new SqlParameter("@codes", SqlDbType.NVarChar, -1) { Value = codesJson });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken)) rows.Add(map(reader));
        return rows;
    }

    private static List<string> Normalize(IEnumerable<string> values) => values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    private static bool Same(string? left, string? right) => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
    private static decimal? Min(IEnumerable<decimal?> values) { var a = values.Where(v => v.HasValue).Select(v => v!.Value).ToList(); return a.Count == 0 ? null : a.Min(); }
    private static decimal? Max(IEnumerable<decimal?> values) { var a = values.Where(v => v.HasValue).Select(v => v!.Value).ToList(); return a.Count == 0 ? null : a.Max(); }
    private static DateTime Max(DateTime left, DateTime right) => left > right ? left : right;
    private static DateTime Min(DateTime left, DateTime right) => left < right ? left : right;
    private static void EnsureSqlServer(ISqlSugarClient db, string source) { if (db.CurrentConnectionConfig.DbType != SqlSugar.DbType.SqlServer) throw new NotSupportedException($"批量货号销量仅支持 SQL Server {source} 数据源。"); }

    internal sealed class SqlAggregateRow { public DateTime Date { get; set; } public string BranchCode { get; set; } = string.Empty; public string ProductCode { get; set; } = string.Empty; public string SupplierCode { get; set; } = SalesStatisticsCodeRules.UnknownSupplierCode; public int DiscountKind { get; set; } public decimal Quantity { get; set; } public decimal SalesAmount { get; set; } public decimal ReturnQuantity { get; set; } public int UnknownRowCount { get; set; } public decimal? OriginalPriceMin { get; set; } public decimal? OriginalPriceMax { get; set; } public decimal? DiscountPriceMin { get; set; } public decimal? DiscountPriceMax { get; set; } }
    private sealed record BatchSqlParameter(string Name, object? Value, SqlDbType Type);
    private sealed class CatalogProductRow { public string? ProductCode { get; set; } public string? ItemNumber { get; set; } public string? Barcode { get; set; } }
    private class AliasCatalogRow { public string? Alias { get; set; } public string? ProductCode { get; set; } }
    private sealed class StoreAliasCatalogRow : AliasCatalogRow { public string? BranchCode { get; set; } }
}

/// <summary>已固定在日来源捕获中的 HBSales 缺码别名解析结果。</summary>
internal sealed record BatchProductSalesHBSalesAlias(string Alias, string? BranchCode, string ProductCode, string Scope);

/// <summary>一行对应一个折扣类别的 SQL 聚合；UnknownRowCount 使净未知量为零时仍保持 partial/unknown。</summary>
internal sealed class BatchProductSalesAggregateRow
{
    public DateTime Date { get; init; }
    public string BranchCode { get; init; } = string.Empty;
    public string ProductCode { get; init; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal RegularQuantity { get; set; }
    public decimal DiscountQuantity { get; set; }
    public decimal UnknownQuantity { get; set; }
    public decimal ReturnQuantity { get; set; }
    public decimal SalesAmount { get; set; }
    public int UnknownRowCount { get; set; }
    public decimal? OriginalPriceMin { get; set; }
    public decimal? OriginalPriceMax { get; set; }
    public decimal? DiscountPriceMin { get; set; }
    public decimal? DiscountPriceMax { get; set; }
    public BatchProductSalesMetricsDto Metrics => new()
    {
        Quantity = Quantity, RegularQuantity = RegularQuantity, DiscountQuantity = DiscountQuantity, UnknownQuantity = UnknownQuantity,
        ReturnQuantity = ReturnQuantity, SalesAmount = SalesAmount,
        DiscountStatus = UnknownRowCount > 0 ? (RegularQuantity != 0 || DiscountQuantity != 0 ? "partial" : "unknown") : "complete",
        OriginalPriceMin = OriginalPriceMin, OriginalPriceMax = OriginalPriceMax, DiscountPriceMin = DiscountPriceMin, DiscountPriceMax = DiscountPriceMax,
    };
}
