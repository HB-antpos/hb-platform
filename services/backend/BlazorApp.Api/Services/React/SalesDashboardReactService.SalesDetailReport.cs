using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using BlazorApp.Api.Services.LocalSupplierCategories;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.SqlClient;
using DbType = System.Data.DbType;

namespace BlazorApp.Api.Services.React;

public partial class SalesDashboardReactService
{
    // 分类选择会在服务端展开到子分类；这些上限同时保护 SQL Server 参数总数和分类接口查询次数。
    internal const int MaxSelectedSupplierCodes = 200;
    internal const int MaxRequestedCategoryGuids = 500;
    internal const int MaxExpandedCategoryGuids = 1500;
    internal const int MaxSqlParameterBudget = 2000;
    private sealed class SalesDetailReportSqlRow
    {
        public string Code { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string? ItemNumber { get; init; }
        public string? ProductImage { get; init; }
        public decimal Revenue { get; init; }
        public decimal CompareRevenue { get; init; }
        public int Quantity { get; init; }
        public int CompareQuantity { get; init; }
        public int OrderCount { get; init; }
        public int CompareOrderCount { get; init; }
        public decimal? GrossProfit { get; init; }
        public decimal? CompareGrossProfit { get; init; }
        public int StatisticRowCount { get; init; }
        public int CostedRowCount { get; init; }
        public int GrossProfitRowCount { get; init; }
        public int CompareStatisticRowCount { get; init; }
        public int CompareCostedRowCount { get; init; }
        public int CompareGrossProfitRowCount { get; init; }
        public int CurrentProductCount { get; init; }
        public int CompareProductCount { get; init; }
    }

    private sealed class SalesDetailReportStatusSqlRow
    {
        public string Type { get; init; } = string.Empty;
        public DateTime Date { get; init; }
        public string Status { get; init; } = string.Empty;
        public DateTime? LastAggregatedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        public string? SourceProductVersion { get; init; }
    }

    private sealed class SalesDetailReportRead
    {
        public List<SalesDetailReportStatusSqlRow> Status { get; } = new();
        public SalesDetailReportSqlRow? Summary { get; set; }
        public List<SalesDetailReportSqlRow> Suppliers { get; } = new();
        public List<SalesDetailReportSqlRow> Branches { get; } = new();
        public List<SalesDetailReportSqlRow> Products { get; } = new();
        public int ProductTotal { get; set; }
        public SalesDetailDenominator Denominator { get; set; } = new();
        /// <summary>关键词日投影路径因缺少或无法核验投影而跳过的日期，结果不含这些日期。</summary>
        public List<DateTime> SkippedDates { get; } = new();
    }

    private sealed class SalesDetailSkippedDateSqlRow
    {
        public DateTime Date { get; init; }
    }

    private sealed class SalesDetailProductCountSqlRow
    {
        public int Total { get; init; }
    }

    public Task<ProductReportResponseDto<SalesDetailReportDto>> GetSalesDetailReportAsync(
        DateRangeDto dateRange, SalesDetailKind kind, List<string>? branchCodes = null,
        string? selectedBranchCode = null, string? selectedSupplierCode = null, string? selectedProductCode = null,
        string? search = null, int pageIndex = 1, int pageSize = 20,
        IReadOnlyCollection<SalesDetailSection>? sections = null, CancellationToken cancellationToken = default)
        => GetSalesDetailReportFilteredAsync(dateRange, kind, branchCodes, selectedBranchCode, selectedSupplierCode,
            selectedProductCode, search, pageIndex, pageSize, sections, cancellationToken);

    public async Task<ProductReportResponseDto<SalesDetailReportDto>> GetSalesDetailReportFilteredAsync(
        DateRangeDto dateRange,
        SalesDetailKind kind,
        List<string>? branchCodes = null,
        string? selectedBranchCode = null,
        string? selectedSupplierCode = null,
        string? selectedProductCode = null,
        string? search = null,
        int pageIndex = 1,
        int pageSize = 20,
        IReadOnlyCollection<SalesDetailSection>? sections = null,
        CancellationToken cancellationToken = default,
        List<string>? selectedSupplierCodes = null,
        List<string>? supplierCategoryGuids = null,
        List<string>? warehouseCategoryGuids = null)
    {
        ValidateDateRange(dateRange);
        if (!Enum.IsDefined(kind)) throw new ArgumentException("kind 无效", nameof(kind));
        if (pageIndex < 1) throw new ArgumentException("pageIndex 必须大于 0", nameof(pageIndex));
        pageSize = Math.Clamp(pageSize, 1, 500);
        var wanted = sections is null || sections.Count == 0
            ? Enum.GetValues<SalesDetailSection>().ToHashSet()
            : sections.ToHashSet();
        if (wanted.Any(section => !Enum.IsDefined(section)))
            throw new ArgumentException("sections 包含无效栏位", nameof(sections));
        var branches = branchCodes?.Where(code => !string.IsNullOrWhiteSpace(code)).Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (branches is { Count: 0 }) return EmptySalesDetailReport("当前账号没有可访问的分店范围");
        var suppliers = selectedSupplierCodes?.Where(code => !string.IsNullOrWhiteSpace(code)).Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
        if (!string.IsNullOrWhiteSpace(selectedSupplierCode) && !suppliers.Contains(selectedSupplierCode.Trim(), StringComparer.OrdinalIgnoreCase))
            suppliers.Add(selectedSupplierCode.Trim());
        if (suppliers.Count > MaxSelectedSupplierCodes)
            throw new ArgumentException($"供应商筛选最多选择 {MaxSelectedSupplierCodes} 项，当前为 {suppliers.Count} 项", nameof(selectedSupplierCodes));
        var requestedCategoryGuids = (supplierCategoryGuids ?? Enumerable.Empty<string>())
            .Concat(warehouseCategoryGuids ?? Enumerable.Empty<string>())
            .Where(guid => !string.IsNullOrWhiteSpace(guid)).Select(guid => guid.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (requestedCategoryGuids.Count > MaxRequestedCategoryGuids)
            throw new ArgumentException($"分类筛选最多选择 {MaxRequestedCategoryGuids} 项，当前为 {requestedCategoryGuids.Count} 项", nameof(supplierCategoryGuids));
        var expandedCategories = requestedCategoryGuids.Count == 0
            ? (new List<string>(), new List<string>())
            : await LocalSupplierCategoryFilter.ExpandAsync(_context.Db, requestedCategoryGuids);
        var effectiveSupplierCategories = expandedCategories.Item1;
        var effectiveWarehouseCategories = expandedCategories.Item2;
        var expandedCategoryCount = effectiveSupplierCategories.Count + effectiveWarehouseCategories.Count;
        if (expandedCategoryCount > MaxExpandedCategoryGuids)
            throw new ArgumentException($"分类筛选展开后超过 {MaxExpandedCategoryGuids} 项，请缩小选择范围");
        var searchTokenCount = search?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0;
        var estimatedParameterCount = 20 + suppliers.Count + expandedCategoryCount
            + (branches?.Count ?? 0) * 2 + searchTokenCount;
        if (estimatedParameterCount > MaxSqlParameterBudget)
            throw new ArgumentException("筛选条件过多，请减少供应商、分类、分店或关键词后重试");
        // 当前 tab 传入了分类但全部无效/已删除时必须返回空结果，不能退化成无分类的全量查询。
        // 使用参数化哨兵值保持 SQL 结构不变，避免拼接用户输入。
        var requestedCategoriesForKind = kind == SalesDetailKind.China ? warehouseCategoryGuids : supplierCategoryGuids;
        var effectiveCategoriesForKind = kind == SalesDetailKind.China ? effectiveWarehouseCategories : effectiveSupplierCategories;
        if (requestedCategoriesForKind is { Count: > 0 } && effectiveCategoriesForKind.Count == 0
            && (kind == SalesDetailKind.China || effectiveWarehouseCategories.Count == 0))
            effectiveCategoriesForKind.Add("__sales_detail_no_matching_category__");
        cancellationToken.ThrowIfCancellationRequested();

        var read = await ReadSalesDetailReportSqlAsync(
            dateRange, kind, branches, selectedBranchCode, suppliers.FirstOrDefault(), suppliers,
            effectiveSupplierCategories, effectiveWarehouseCategories, selectedProductCode,
            search, pageIndex, pageSize, wanted, cancellationToken);
        // 四栏全部来自同一份商品日统计，不依赖供应商汇总的完成时间或另一次发布的归属映射。
        var status = BuildSalesDetailReportStatus(read.Status, dateRange, false, skipFailedDates: true, projectionSkippedDates: read.SkippedDates);
        var response = new ProductReportResponseDto<SalesDetailReportDto>
        {
            StatisticStatus = status.StatisticStatus, StatisticMessage = status.StatisticMessage,
            StatisticUpdatedAt = status.StatisticUpdatedAt, CacheVersion = status.CacheVersion,
            Data = new SalesDetailReportDto(),
        };
        if (!status.StatisticStatus.Equals(SalesStatisticRefreshStatus.Fresh, StringComparison.OrdinalIgnoreCase))
            return response;
        if (wanted.Contains(SalesDetailSection.Summary) && read.Summary != null)
        {
            response.Data!.Summary = ToSection(read.Summary, HasCompare(dateRange), "summary", "当前筛选汇总");
            if (string.IsNullOrWhiteSpace(selectedProductCode))
            {
                response.Data.Summary.Summary!.OrderCount = null;
                response.Data.Summary.Summary.CompareOrderCount = null;
                response.Data.Summary.Summary.AverageTransaction = null;
                response.Data.Summary.Summary.CompareAverageTransaction = null;
                response.Data.Summary.OrderCountNote = "跨商品/供应商范围未做收据去重，客单数返回 null。";
            }
        }
        if (wanted.Contains(SalesDetailSection.Suppliers))
        {
            var rows = read.Suppliers.Select(row => ToRow(row, SalesDetailSection.Suppliers, kind, read.Denominator, HasCompare(dateRange))).ToList();
            response.Data!.Suppliers = BuildRowsSection(rows, "当前筛选汇总");
        }
        if (wanted.Contains(SalesDetailSection.Branches))
        {
            var rows = read.Branches.Select(row => ToRow(row, SalesDetailSection.Branches, kind, null, HasCompare(dateRange))).ToList();
            var unscoped = suppliers.Count == 0 && string.IsNullOrWhiteSpace(selectedProductCode);
            response.Data!.Branches = BuildRowsSection(rows, "当前筛选汇总",
                unscoped ? "跨供应商商品订单未做收据去重，客单数返回 null。" : null);
            // 每家分店的单商品客单由 ToRow 判断；只对未缩小范围的跨分店汇总保留未知。
            if (unscoped && response.Data.Branches.Summary is { } branchSummary)
            {
                branchSummary.OrderCount = null; branchSummary.CompareOrderCount = null;
                branchSummary.AverageTransaction = null; branchSummary.CompareAverageTransaction = null;
            }
        }
        if (wanted.Contains(SalesDetailSection.Products))
        {
            var rows = read.Products.Select(row => ToRow(row, SalesDetailSection.Products, kind, null, HasCompare(dateRange))).ToList();
            response.Data!.Products = new SalesDetailSectionResultDto
            {
                Rows = rows, Total = read.ProductTotal, Summary = SumSalesDetailRows(rows, "page", "当前页商品")
            };
        }
        return response;
    }

    private async Task<SalesDetailReportRead> ReadSalesDetailReportSqlAsync(
        DateRangeDto range, SalesDetailKind kind, List<string>? branches, string? selectedBranchCode,
        string? selectedSupplierCode, IReadOnlyCollection<string> selectedSupplierCodes,
        IReadOnlyCollection<string> supplierCategoryGuids, IReadOnlyCollection<string> warehouseCategoryGuids,
        string? selectedProductCode, string? search,
        int pageIndex, int pageSize, IReadOnlySet<SalesDetailSection> wanted, CancellationToken cancellationToken)
    {
        var sqlServer = _context.Db.CurrentConnectionConfig.DbType == SqlSugar.DbType.SqlServer;
        // 分类筛选收窄全部栏位，走完整查询并先按商品索引预取分类商品的事实；其余筛选（分店范围、选中分店、多选供应商）都能走预聚合。
        var hasClassificationFilter = supplierCategoryGuids.Count > 0 || warehouseCategoryGuids.Count > 0;
        if (sqlServer && !hasClassificationFilter && _context.Db.Ado.Transaction == null && UsesMonthlyProjection(search, branches, selectedBranchCode, wanted)
            && !MonthlyProjectionRecentlyMissing() && TryGetSameServerPosmDatabase(out _))
        {
            try
            {
                return await ReadSalesDetailReportSqlCoreAsync(
                    range, kind, branches, selectedBranchCode, selectedSupplierCode, selectedSupplierCodes, supplierCategoryGuids, warehouseCategoryGuids, selectedProductCode, search,
                    pageIndex, pageSize, wanted, cancellationToken, useMonthly: true);
            }
            catch (SqlException ex) when (ex.Number == SalesDetailQueryMonthlyProjection.MissingSchemaErrorNumber)
            {
                // 月投影表尚未部署时回退原查询；5 分钟内不再尝试，避免每次请求多一次往返。
                RememberMonthlyProjectionMissing();
                _logger.LogInformation("销售明细按月投影尚未建立，改用原查询：{Reason}", ex.Number);
            }
        }
        if (sqlServer && !hasClassificationFilter && !string.IsNullOrWhiteSpace(search) && _context.Db.Ado.Transaction == null
            && (wanted.Contains(SalesDetailSection.Summary) || wanted.Contains(SalesDetailSection.Products))
            && TryGetSameServerPosmDatabase(out _))
        {
            try
            {
                return await ReadSalesDetailReportSqlCoreAsync(
                    range, kind, branches, selectedBranchCode, selectedSupplierCode, selectedSupplierCodes, supplierCategoryGuids, warehouseCategoryGuids, selectedProductCode, search,
                    pageIndex, pageSize, wanted, cancellationToken, useProjection: true);
            }
            catch (SqlException ex) when (ex.Number == 51012
                || (ex.Number == 208 && ex.Message.Contains("SalesDetailQuery", StringComparison.Ordinal)))
            {
                // 投影覆盖异常不能退回整段事实：失败日已由投影 SQL 跳过，其他覆盖异常应明确失败，避免再次全表扫描。
                _logger.LogWarning(ex, "销售明细查询投影不可用，拒绝回退完整事实快照：{Reason}", ex.Number);
                throw;
            }
        }
        return sqlServer
            ? await ReadSalesDetailReportSqlCoreAsync(
                range, kind, branches, selectedBranchCode, selectedSupplierCode, selectedSupplierCodes, supplierCategoryGuids, warehouseCategoryGuids, selectedProductCode, search,
                pageIndex, pageSize, wanted, cancellationToken)
            : await ReadReportSnapshotAsync(() => ReadSalesDetailReportSqlCoreAsync(
                range, kind, branches, selectedBranchCode, selectedSupplierCode, selectedSupplierCodes, supplierCategoryGuids, warehouseCategoryGuids, selectedProductCode, search,
                pageIndex, pageSize, wanted, cancellationToken));
    }

    private async Task<SalesDetailReportRead> ReadSalesDetailReportSqlCoreAsync(
        DateRangeDto range, SalesDetailKind kind, List<string>? branches, string? selectedBranchCode,
        string? selectedSupplierCode, IReadOnlyCollection<string> selectedSupplierCodes,
        IReadOnlyCollection<string> supplierCategoryGuids, IReadOnlyCollection<string> warehouseCategoryGuids,
        string? selectedProductCode, string? search,
        int pageIndex, int pageSize, IReadOnlySet<SalesDetailSection> wanted, CancellationToken cancellationToken,
        bool useProjection = false, bool useMonthly = false)
    {
        var sqlServer = _context.Db.CurrentConnectionConfig.DbType == SqlSugar.DbType.SqlServer;
        var direct = TryGetSameServerPosmDatabase(out var posmDatabase);
        // 不同服务器无法直接联查时，也只读取两期、授权分店内实际出现的旧 200 商品映射。
        var fallbackMap = direct ? null : await new SalesDetailLookupContext(this, range, branches).GetChinaSupplierProductMapAsync();
        var elapsed = Stopwatch.StartNew();
        var ownsTransaction = sqlServer && _context.Db.Ado.Transaction == null;
        // 自管快照只执行一个批次，使用独立的非 MARS 连接；已有外部事务仍使用其原连接。
        await using var dedicatedConnection = ownsTransaction
            ? new SqlConnection(new SqlConnectionStringBuilder(_context.Db.CurrentConnectionConfig.ConnectionString)
                { MultipleActiveResultSets = false, MinPoolSize = 1 }.ConnectionString)
            : null;
        var connection = (DbConnection?)dedicatedConnection ?? (DbConnection)_context.Db.Ado.Connection;
        var close = connection.State != ConnectionState.Open;
        if (close) await connection.OpenAsync(cancellationToken);
        var openedAt = elapsed.ElapsedMilliseconds;
        var failed = true;
        try
        {
            if (useProjection)
            {
                // 显式迁移前不让 SQL Server 编译不存在的派生表；实际覆盖仍在下方同一统计快照内核验。
                await using var capability = connection.CreateCommand();
                capability.CommandText = "SELECT CASE WHEN OBJECT_ID(N'dbo.SalesDetailQueryDaily',N'U') IS NOT NULL AND OBJECT_ID(N'dbo.SalesDetailQueryProductAlias',N'U') IS NOT NULL AND OBJECT_ID(N'dbo.SalesDetailQueryProjectionState',N'U') IS NOT NULL AND OBJECT_ID(N'dbo.SalesDetailQueryMappingUse',N'U') IS NOT NULL THEN 1 ELSE 0 END;";
                if (Convert.ToInt32(await capability.ExecuteScalarAsync(cancellationToken)) != 1)
                    throw new InvalidOperationException("销售明细关键词查询投影尚未建立，已拒绝整段事实回退。");
            }
            await using var command = connection.CreateCommand();
            command.CommandText = useMonthly
                ? BuildSalesDetailReportSqlMonthly(
                    posmDatabase, range, kind, selectedSupplierCode, selectedProductCode, pageIndex, pageSize, wanted,
                    selectedSupplierCodes, branches, selectedBranchCode)
                : useProjection
                ? BuildSalesDetailReportSqlServerCoreWithFilters(
                    posmDatabase, range, kind, branches, selectedBranchCode, selectedSupplierCode,
                    selectedProductCode, search, pageIndex, pageSize, wanted, fallbackMap, useProjection: true, compressOutput: true,
                    selectedSuppliers: selectedSupplierCodes)
                : BuildSalesDetailReportSqlWithFilters(
                    sqlServer, direct ? posmDatabase : null, range, kind, branches, selectedBranchCode,
                    selectedSupplierCode, selectedProductCode, search, pageIndex, pageSize, wanted, fallbackMap, selectedSupplierCodes, supplierCategoryGuids, warehouseCategoryGuids);
            if (ownsTransaction)
            {
                // 能力检查、事务和全部栏位共用一次往返；不修改数据库的快照配置。
                command.CommandText = """
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND snapshot_isolation_state = 1)
                        SET TRANSACTION ISOLATION LEVEL SNAPSHOT;
                    ELSE
                        THROW 51001, N'统计快照读取尚未启用，暂时无法读取完整报表。', 1;
                    BEGIN TRANSACTION;
                    BEGIN TRY
                    """ + "\n" + command.CommandText + "\n" + """
                    COMMIT TRANSACTION;
                    SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
                    END TRY
                    BEGIN CATCH
                        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                        SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
                        THROW;
                    END CATCH;
                    """;
            }
            command.CommandTimeout = Math.Max(1, _context.Db.Ado.CommandTimeOut);
            if (_context.Db.Ado.Transaction is DbTransaction tx) command.Transaction = tx;
            void Add(string name, object value, DbType type)
            {
                var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; parameter.DbType = type; command.Parameters.Add(parameter);
            }
            Add("@sdrCurrentStart", range.StartDate.Date, DbType.DateTime);
            Add("@sdrCurrentEnd", range.EndDate.Date.AddDays(1), DbType.DateTime);
            Add("@sdrHasCompare", HasCompare(range) ? 1 : 0, DbType.Int32);
            Add("@sdrCompareStart", (object?)range.CompareStartDate?.Date ?? DBNull.Value, DbType.DateTime);
            Add("@sdrCompareEnd", (object?)range.CompareEndDate?.Date.AddDays(1) ?? DBNull.Value, DbType.DateTime);
            Add("@sdrKind", kind == SalesDetailKind.China ? 1 : 0, DbType.Int32);
            if (!string.IsNullOrWhiteSpace(selectedBranchCode)) Add("@sdrSelectedBranch", selectedBranchCode.Trim(), DbType.String);
            if (!string.IsNullOrWhiteSpace(selectedSupplierCode)) Add("@sdrSelectedSupplier", selectedSupplierCode.Trim(), DbType.String);
            var normalizedSuppliers = selectedSupplierCodes.Where(code => !string.IsNullOrWhiteSpace(code)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!string.IsNullOrWhiteSpace(selectedSupplierCode) && !normalizedSuppliers.Contains(selectedSupplierCode.Trim(), StringComparer.OrdinalIgnoreCase)) normalizedSuppliers.Add(selectedSupplierCode.Trim());
            if (normalizedSuppliers.Count > 1)
                for (var i = 0; i < normalizedSuppliers.Count; i++) Add($"@sdrSelectedSupplier{i}", normalizedSuppliers[i], DbType.String);
            for (var i = 0; i < supplierCategoryGuids.Count; i++) Add($"@sdrCategorySupplier{i}", supplierCategoryGuids.ElementAt(i), DbType.String);
            for (var i = 0; i < warehouseCategoryGuids.Count; i++) Add($"@sdrCategoryWarehouse{i}", warehouseCategoryGuids.ElementAt(i), DbType.String);
            if (!string.IsNullOrWhiteSpace(selectedProductCode)) Add("@sdrSelectedProduct", selectedProductCode.Trim(), DbType.String);
            var tokens = search?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>();
            for (var i = 0; i < tokens.Length; i++) Add($"@sdrSearch{i}", $"%{tokens[i]}%", DbType.String);
            for (var i = 0; i < (branches?.Count ?? 0); i++) Add($"@sdrBranch{i}", branches![i], DbType.String);
            for (var i = 0; i < (branches?.Count ?? 0); i++) Add($"@sdrSelectedBranch{i}", branches![i], DbType.String);

            var read = new SalesDetailReportRead();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var firstResultAt = elapsed.ElapsedMilliseconds;
            if (useProjection)
            {
                // 状态与各栏按结果集压缩传输，避免大量小行跨服务器往返；空结果仍是明确的 []。
                read.Status.AddRange(await ReadCompressedRevenueRowsAsync<SalesDetailReportStatusSqlRow>(reader, cancellationToken));
                // SQL JSON 的 datetime2 不含时区，与原数据读取器一样明确按 UTC 解释。
                foreach (var state in read.Status)
                {
                    if (state.LastAggregatedAtUtc is DateTime aggregated)
                        state.LastAggregatedAtUtc = DateTime.SpecifyKind(aggregated, DateTimeKind.Utc);
                    if (state.CompletedAtUtc is DateTime completed)
                        state.CompletedAtUtc = DateTime.SpecifyKind(completed, DateTimeKind.Utc);
                }
                await NextResult();
                read.Summary = (await ReadCompressedRevenueRowsAsync<SalesDetailReportSqlRow>(reader, cancellationToken)).SingleOrDefault();
                await NextResult();
                read.Suppliers.AddRange(await ReadCompressedRevenueRowsAsync<SalesDetailReportSqlRow>(reader, cancellationToken));
                await NextResult();
                read.Branches.AddRange(await ReadCompressedRevenueRowsAsync<SalesDetailReportSqlRow>(reader, cancellationToken));
                await NextResult();
                read.Products.AddRange(await ReadCompressedRevenueRowsAsync<SalesDetailReportSqlRow>(reader, cancellationToken));
                await NextResult();
                read.ProductTotal = (await ReadCompressedRevenueRowsAsync<SalesDetailProductCountSqlRow>(reader, cancellationToken)).Single().Total;
                await NextResult();
                read.Denominator = (await ReadCompressedRevenueRowsAsync<SalesDetailDenominator>(reader, cancellationToken)).Single();
                await NextResult();
                read.SkippedDates.AddRange((await ReadCompressedRevenueRowsAsync<SalesDetailSkippedDateSqlRow>(reader, cancellationToken))
                    .Select(row => row.Date.Date));
            }
            else
            {
                while (await reader.ReadAsync(cancellationToken))
                    read.Status.Add(new SalesDetailReportStatusSqlRow { Type = S(reader, 0), Date = D(reader, 1), Status = S(reader, 2), LastAggregatedAtUtc = ND(reader, 3), CompletedAtUtc = ND(reader, 4), SourceProductVersion = NS(reader, 5) });
                await NextResult();
                if (await reader.ReadAsync(cancellationToken)) read.Summary = ReadSectionRow(reader);
                await NextResult();
                await ReadRowsAsync(reader, read.Suppliers, cancellationToken);
                await NextResult();
                await ReadRowsAsync(reader, read.Branches, cancellationToken);
                await NextResult();
                await ReadRowsAsync(reader, read.Products, cancellationToken);
                await NextResult();
                if (await reader.ReadAsync(cancellationToken)) read.ProductTotal = I(reader, 0);
                await NextResult();
                if (await reader.ReadAsync(cancellationToken))
                {
                    read.Denominator = new SalesDetailDenominator { AllRevenue = M(reader, 0), ChinaRevenue = M(reader, 1), CompareAllRevenue = M(reader, 2), CompareChinaRevenue = M(reader, 3) };
                }
            }
            // 必须消费命令尾部，确认 COMMIT 成功后才能向页面发布这一版统计。
            while (await reader.NextResultAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken)) { }
            failed = false;
            _logger.LogInformation(
                "销售明细统计批次读取完成：连接 {OpenMs}ms，首结果 {FirstResultMs}ms，读取结果 {ReadMs}ms，共 {TotalMs}ms，日投影 {Projection}，月投影 {Monthly}，跳过 {SkippedDays} 天",
                openedAt, firstResultAt - openedAt, elapsed.ElapsedMilliseconds - firstResultAt, elapsed.ElapsedMilliseconds, useProjection, useMonthly, read.SkippedDates.Count);
            return read;

            async Task NextResult()
            {
                if (!await reader.NextResultAsync(cancellationToken))
                    throw new InvalidOperationException("销售明细统计批次缺少结果集。");
            }
        }
        finally
        {
            // SQL 取消可能绕过 CATCH；归还连接前由驱动回滚尚未结束的自管事务。
            if (close || (failed && ownsTransaction)) await connection.CloseAsync();
        }
    }

    private static async Task ReadRowsAsync(DbDataReader reader, List<SalesDetailReportSqlRow> target, CancellationToken token)
    { while (await reader.ReadAsync(token)) target.Add(ReadSectionRow(reader)); }

    private static SalesDetailReportSqlRow ReadSectionRow(DbDataReader r) => new()
    {
        Code = S(r, 0), Name = S(r, 1), ItemNumber = NS(r, 2), ProductImage = NS(r, 3),
        Revenue = M(r, 4), CompareRevenue = M(r, 5), Quantity = I(r, 6), CompareQuantity = I(r, 7),
        OrderCount = I(r, 8), CompareOrderCount = I(r, 9), GrossProfit = NM(r, 10), CompareGrossProfit = NM(r, 11),
        StatisticRowCount = I(r, 12), CostedRowCount = I(r, 13), GrossProfitRowCount = I(r, 14),
        CompareStatisticRowCount = I(r, 15), CompareCostedRowCount = I(r, 16), CompareGrossProfitRowCount = I(r, 17),
        CurrentProductCount = I(r, 18), CompareProductCount = I(r, 19),
    };

    private static SalesDetailSectionResultDto ToSection(SalesDetailReportSqlRow row, bool compare, string code, string name)
    {
        var dto = ToRow(row, SalesDetailSection.Summary, SalesDetailKind.Australia, null, compare);
        dto.Code = code; dto.Name = name;
        return new SalesDetailSectionResultDto { Rows = new() { dto }, Total = 1, Summary = dto,
            OrderCountNote = dto.OrderCount == null ? "跨商品/供应商范围未做收据去重，客单数返回 null。" : null };
    }

    private static SalesDetailSectionResultDto BuildRowsSection(List<SalesDetailRowDto> rows, string summaryName, string? note = null)
    {
        var summary = SumSalesDetailRows(rows, "summary", summaryName);
        return new SalesDetailSectionResultDto { Rows = rows, Total = rows.Count, Summary = summary, OrderCountNote = note };
    }

    private static SalesDetailRowDto ToRow(SalesDetailReportSqlRow row, SalesDetailSection section, SalesDetailKind kind, SalesDetailDenominator? denominator, bool compare)
    {
        var gross = CompleteGrossProfit(row.GrossProfit ?? 0m, row.StatisticRowCount, row.CostedRowCount, row.GrossProfitRowCount);
        var compareGross = compare ? CompleteGrossProfit(row.CompareGrossProfit ?? 0m, row.CompareStatisticRowCount, row.CompareCostedRowCount, row.CompareGrossProfitRowCount) : null;
        var dto = new SalesDetailRowDto
        {
            Code = row.Code, Name = row.Name, ItemNumber = row.ItemNumber, ProductImage = row.ProductImage,
            Revenue = row.Revenue, CompareRevenue = compare ? row.CompareRevenue : null,
            Quantity = row.Quantity, CompareQuantity = compare ? row.CompareQuantity : null,
            OrderCount = section is SalesDetailSection.Suppliers or SalesDetailSection.Branches
                ? row.CurrentProductCount == 1 && row.OrderCount > 0 ? row.OrderCount : null
                : row.OrderCount > 0 ? row.OrderCount : null,
            CompareOrderCount = section is SalesDetailSection.Suppliers or SalesDetailSection.Branches
                ? compare && row.CompareProductCount == 1 && row.CompareOrderCount > 0 ? row.CompareOrderCount : null
                : compare && row.CompareOrderCount > 0 ? row.CompareOrderCount : null,
            AverageTransaction = section is SalesDetailSection.Suppliers or SalesDetailSection.Branches
                ? row.CurrentProductCount == 1 && row.OrderCount > 0 ? row.Revenue / row.OrderCount : null
                : row.OrderCount > 0 ? row.Revenue / row.OrderCount : null,
            CompareAverageTransaction = section is SalesDetailSection.Suppliers or SalesDetailSection.Branches
                ? compare && row.CompareProductCount == 1 && row.CompareOrderCount > 0 ? row.CompareRevenue / row.CompareOrderCount : null
                : compare && row.CompareOrderCount > 0 ? row.CompareRevenue / row.CompareOrderCount : null,
            AverageUnitPrice = row.Quantity > 0 ? row.Revenue / row.Quantity : null,
            CompareAverageUnitPrice = compare && row.CompareQuantity > 0 ? row.CompareRevenue / row.CompareQuantity : null,
            GrossProfit = gross, CompareGrossProfit = compareGross,
            GrossMarginRate = CalculateGrossMarginRate(row.Revenue, gross), CompareGrossMarginRate = compare ? CalculateGrossMarginRate(row.CompareRevenue, compareGross) : null,
        };
        if (denominator != null)
        {
            var primary = kind == SalesDetailKind.China ? denominator.ChinaRevenue : denominator.AllRevenue;
            var comparePrimary = kind == SalesDetailKind.China ? denominator.CompareChinaRevenue : denominator.CompareAllRevenue;
            dto.Share = primary > 0 ? dto.Revenue / primary : null;
            dto.CompareShare = compare && comparePrimary > 0 ? dto.CompareRevenue!.Value / comparePrimary : null;
            if (kind == SalesDetailKind.China)
            {
                dto.ChinaShare = denominator.AllRevenue > 0 ? dto.Revenue / denominator.AllRevenue : null;
                dto.CompareChinaShare = compare && denominator.CompareAllRevenue > 0 ? dto.CompareRevenue!.Value / denominator.CompareAllRevenue : null;
            }
        }
        return dto;
    }

    private static string BuildSalesDetailReportSql(bool sqlServer, string? posmDatabase, DateRangeDto range, SalesDetailKind kind,
        IReadOnlyCollection<string>? branches, string? selectedBranch, string? selectedSupplier, string? selectedProduct,
        string? search, int pageIndex, int pageSize, IReadOnlySet<SalesDetailSection> wanted, IReadOnlyDictionary<string, string>? fallbackMap)
        => BuildSalesDetailReportSqlWithFilters(sqlServer, posmDatabase, range, kind, branches, selectedBranch, selectedSupplier,
            selectedProduct, search, pageIndex, pageSize, wanted, fallbackMap, null, null, null);

    private static string BuildSalesDetailReportSqlWithFilters(bool sqlServer, string? posmDatabase, DateRangeDto range, SalesDetailKind kind,
        IReadOnlyCollection<string>? branches, string? selectedBranch, string? selectedSupplier, string? selectedProduct,
        string? search, int pageIndex, int pageSize, IReadOnlySet<SalesDetailSection> wanted, IReadOnlyDictionary<string, string>? fallbackMap,
        IReadOnlyCollection<string>? selectedSuppliers = null, IReadOnlyCollection<string>? supplierCategoryGuids = null,
        IReadOnlyCollection<string>? warehouseCategoryGuids = null)
        => sqlServer
            ? BuildSalesDetailReportSqlServerWithFilters(
                posmDatabase, range, kind, branches, selectedBranch, selectedSupplier, selectedProduct,
                search, pageIndex, pageSize, wanted, fallbackMap, selectedSuppliers, supplierCategoryGuids, warehouseCategoryGuids)
            : BuildSalesDetailReportSqlLegacyWithFilters(
                false, posmDatabase, range, kind, branches, selectedBranch, selectedSupplier, selectedProduct,
                search, pageIndex, pageSize, wanted, fallbackMap, selectedSuppliers, supplierCategoryGuids, warehouseCategoryGuids);

    private static string BuildSalesDetailReportSqlServer(
        string? posmDatabase, DateRangeDto range, SalesDetailKind kind,
        IReadOnlyCollection<string>? branches, string? selectedBranch, string? selectedSupplier, string? selectedProduct,
        string? search, int pageIndex, int pageSize, IReadOnlySet<SalesDetailSection> wanted,
        IReadOnlyDictionary<string, string>? fallbackMap)
        => BuildSalesDetailReportSqlServerWithFilters(posmDatabase, range, kind, branches, selectedBranch, selectedSupplier,
            selectedProduct, search, pageIndex, pageSize, wanted, fallbackMap, null, null, null);

    private static string BuildSalesDetailReportSqlServerWithFilters(
        string? posmDatabase, DateRangeDto range, SalesDetailKind kind,
        IReadOnlyCollection<string>? branches, string? selectedBranch, string? selectedSupplier, string? selectedProduct,
        string? search, int pageIndex, int pageSize, IReadOnlySet<SalesDetailSection> wanted,
        IReadOnlyDictionary<string, string>? fallbackMap, IReadOnlyCollection<string>? selectedSuppliers,
        IReadOnlyCollection<string>? supplierCategoryGuids, IReadOnlyCollection<string>? warehouseCategoryGuids)
        => BuildSalesDetailReportSqlServerCoreWithFilters(posmDatabase, range, kind, branches, selectedBranch,
            selectedSupplier, selectedProduct, search, pageIndex, pageSize, wanted, fallbackMap, useProjection: false,
            selectedSuppliers: selectedSuppliers, supplierCategoryGuids: supplierCategoryGuids, warehouseCategoryGuids: warehouseCategoryGuids);

    private static string BuildSalesDetailReportSqlServerCore(
        string? posmDatabase, DateRangeDto range, SalesDetailKind kind,
        IReadOnlyCollection<string>? branches, string? selectedBranch, string? selectedSupplier, string? selectedProduct,
        string? search, int pageIndex, int pageSize, IReadOnlySet<SalesDetailSection> wanted,
        IReadOnlyDictionary<string, string>? fallbackMap, bool useProjection, bool compressOutput = false)
        => BuildSalesDetailReportSqlServerCoreWithFilters(posmDatabase, range, kind, branches, selectedBranch,
            selectedSupplier, selectedProduct, search, pageIndex, pageSize, wanted, fallbackMap, useProjection,
            compressOutput);

    private static string BuildSalesDetailReportSqlServerCoreWithFilters(
        string? posmDatabase, DateRangeDto range, SalesDetailKind kind,
        IReadOnlyCollection<string>? branches, string? selectedBranch, string? selectedSupplier, string? selectedProduct,
        string? search, int pageIndex, int pageSize, IReadOnlySet<SalesDetailSection> wanted,
        IReadOnlyDictionary<string, string>? fallbackMap, bool useProjection, bool compressOutput = false,
        IReadOnlyCollection<string>? selectedSuppliers = null, IReadOnlyCollection<string>? supplierCategoryGuids = null,
        IReadOnlyCollection<string>? warehouseCategoryGuids = null, bool prefetchCategorySource = true)
    {
        var hasCompare = HasCompare(range);
        var categorySourceFilter = BuildSalesDetailCategoryFilter(kind, supplierCategoryGuids, warehouseCategoryGuids, "s.[ProductCode]");
        var sourceBranch = branches is { Count: > 0 }
            ? $" AND s.[BranchCode] IN ({string.Join(",", branches.Select((_, i) => $"@sdrBranch{i}"))})"
            : string.Empty;
        // 统计失败日只在状态结果集中保留提示；事实读取必须跳过，避免筛选失败时退回整段原始数据。
        var failedDateFilter = " AND NOT EXISTS (SELECT 1 FROM [SalesStatisticRefreshState] failedState WHERE failedState.[StatisticType]='ProductStoreDaily' AND failedState.[Status]='Failed' AND failedState.[Date]=s.[Date])";
        // 分类筛选收窄全部栏位（含供应商、分店与分母），先按商品索引只读分类商品的事实，其余整套 SQL 不变地读这份小表。
        var categoryPrefetch = prefetchCategorySource && !useProjection && categorySourceFilter.Length > 0
            ? BuildSalesDetailCategoryPrefetchSql(kind, supplierCategoryGuids, warehouseCategoryGuids, sourceBranch, categorySourceFilter)
            : string.Empty;
        var factSource = useProjection ? "#SalesDetailCandidateSource"
            : categoryPrefetch.Length > 0 ? "#SalesDetailCategorySource" : "ProductStoreDailySalesStatistic";
        // 预取表已按分类精确判定过，主查询不再逐行回查商品与分类分配表。
        if (categoryPrefetch.Length > 0) categorySourceFilter = string.Empty;
        // 商品抽屉只请求分店栏，可在聚合前缩小事实范围；全量报表仍保留其他商品候选和供应商分母。
        var sourceProduct = wanted.Count == 1 && wanted.Contains(SalesDetailSection.Branches)
            && !string.IsNullOrWhiteSpace(selectedProduct)
            ? " AND LTRIM(RTRIM(s.[ProductCode])) = @sdrSelectedProduct"
            : string.Empty;
        var fallbackRows = fallbackMap is { Count: > 0 }
            ? string.Join(" UNION ALL ", fallbackMap.Select(item =>
                $"SELECT '{SqlLiteral(item.Key, true)}' [ProductCode], '{SqlLiteral(item.Value, true)}' [ChinaSupplierCode]"))
            : string.Empty;
        var mapping = posmDatabase == null && fallbackRows.Length == 0
            ? "CAST(NULL AS nvarchar(50))"
            : "m.[ChinaSupplierCode]";
        // 候选日事实的商品码为 nvarchar，映射为 varchar；哈希连接避免每条事实因类型转换重扫映射表。
        var mappingJoin = useProjection ? "LEFT HASH JOIN" : "LEFT JOIN";
        var joinMapping = posmDatabase != null
            ? $"{mappingJoin} {QuoteIdentifier(posmDatabase)}.[dbo].[posm_product_supplier_mapping] m ON m.[ProductCode] = LTRIM(RTRIM(s.[ProductCode])) AND m.[LocalSupplierCode] = '200' AND m.[IsDeleted] = 0"
            : fallbackRows.Length > 0
                ? $"LEFT JOIN ({fallbackRows}) m ON m.[ProductCode] = LTRIM(RTRIM(s.[ProductCode]))"
                : string.Empty;
        var supplierFilter = BuildSalesDetailSupplierFilter(selectedSupplier, selectedSuppliers);
        var productFilter = string.IsNullOrWhiteSpace(selectedProduct) ? string.Empty : " AND [ProductCode] = @sdrSelectedProduct";
        var selectedBranchFilter = string.IsNullOrWhiteSpace(selectedBranch)
            ? (branches is { Count: > 0 } ? $" AND [BranchCode] IN ({string.Join(",", branches.Select((_, i) => $"@sdrSelectedBranch{i}"))})" : string.Empty)
            : " AND [BranchCode] = @sdrSelectedBranch";
        var authorizedBranchFilter = branches is { Count: > 0 }
            ? $" AND [BranchCode] IN ({string.Join(",", branches.Select((_, i) => $"@sdrBranch{i}"))})"
            : string.Empty;
        var tokens = search?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>();
        // 多词候选的聚合工作量较大，将这些语句的并行度限制为 4；不修改服务器全局配置。
        var projectionQueryHint = useProjection && tokens.Length > 1 ? " OPTION(MAXDOP 4)" : string.Empty;
        // 先只按事实主键聚合数值和统计表商品名/条码；商品、门店、供应商的宽字段延后到各栏位查询。
        var wideFacts = $"""
WITH Periods AS
(
 SELECT 0 [Period], @sdrCurrentStart [StartDate], @sdrCurrentEnd [EndDate]
 UNION ALL
 SELECT 1 [Period], @sdrCompareStart [StartDate], @sdrCompareEnd [EndDate] WHERE @sdrHasCompare=1
), SourceRows AS
(
 SELECT periods.[Period],
        LTRIM(RTRIM(COALESCE(s.[SupplierCode],''))) [RawSupplierCode],
        CASE WHEN LTRIM(RTRIM(COALESCE(s.[SupplierCode],'')))='200' THEN NULLIF(LTRIM(RTRIM({mapping})), '')
             WHEN cs.[SupplierCode] IS NOT NULL THEN LTRIM(RTRIM(s.[SupplierCode])) END [ChinaSupplierCode],
        CASE WHEN LTRIM(RTRIM(COALESCE(s.[SupplierCode],'')))='200' OR cs.[SupplierCode] IS NOT NULL THEN '200'
             ELSE NULLIF(LTRIM(RTRIM(s.[SupplierCode])), '') END [AustralianSupplierCode],
        LTRIM(RTRIM(COALESCE(s.[BranchCode],''))) [BranchCode], LTRIM(RTRIM(COALESCE(s.[ProductCode],''))) [ProductCode],
        s.[ProductName] [StatisticProductName], s.[Barcode] [StatisticBarcode],
        s.[TotalQuantity], s.[TotalAmount], s.[OrderCount], s.[GrossProfit], s.[TotalCost]
 FROM [{factSource}] s
 CROSS JOIN Periods periods
 {joinMapping}
 LEFT JOIN (SELECT [SupplierCode] FROM [ChinaSupplier] WHERE [SupplierCode] IS NOT NULL AND [SupplierCode]<>'' GROUP BY [SupplierCode]) cs
   ON cs.[SupplierCode]=LTRIM(RTRIM(s.[SupplierCode]))
 WHERE s.[Date]>=periods.[StartDate] AND s.[Date]<periods.[EndDate]{failedDateFilter}{sourceBranch}{sourceProduct}{categorySourceFilter}
), NarrowFacts AS
(
 SELECT [Period], [RawSupplierCode], [ChinaSupplierCode], [AustralianSupplierCode], [BranchCode], [ProductCode],
        MAX([StatisticProductName]) [StatisticProductName], MAX([StatisticBarcode]) [StatisticBarcode],
        SUM([TotalAmount]) [Revenue], SUM([TotalQuantity]) [Quantity], SUM([OrderCount]) [OrderCount],
        SUM([GrossProfit]) [GrossProfit], COUNT([ProductCode]) [StatisticRowCount], COUNT([TotalCost]) [CostedRowCount],
        COUNT([GrossProfit]) [GrossProfitRowCount]
 FROM SourceRows
 GROUP BY [Period], [RawSupplierCode], [ChinaSupplierCode], [AustralianSupplierCode], [BranchCode], [ProductCode]
)
SELECT [Period], [RawSupplierCode], [ChinaSupplierCode], [AustralianSupplierCode], [BranchCode], [ProductCode],
       [StatisticProductName], [StatisticBarcode], [Revenue], [Quantity], [OrderCount], [GrossProfit],
       [StatisticRowCount], [CostedRowCount], [GrossProfitRowCount]
INTO #SalesDetailFacts
FROM NarrowFacts{projectionQueryHint};
""";
        // 首屏没有关键词时先压缩商品事实，再解析供应商归属；映射连接因此只面对聚合后的键。
        // 内层只按事实表原始列分组、不在分组键上套函数，扫描能走列存索引的批处理聚合；
        // 去空格与空值归一化放在外层，只对聚合后的几十万行做，结果与直接按去空格键分组完全一致。
        var rawMapping = joinMapping.Replace("s.[ProductCode]", "r.[ProductCode]", StringComparison.Ordinal);
        var rawFacts = $"""
WITH Periods AS
(
 SELECT 0 [Period], @sdrCurrentStart [StartDate], @sdrCurrentEnd [EndDate]
 UNION ALL
 SELECT 1 [Period], @sdrCompareStart [StartDate], @sdrCompareEnd [EndDate] WHERE @sdrHasCompare=1
), SourceFacts AS
(
 SELECT periods.[Period], s.[SupplierCode] [RawSupplierCode], s.[BranchCode], s.[ProductCode],
        SUM(s.[TotalAmount]) [Revenue], SUM(s.[TotalQuantity]) [Quantity], SUM(s.[OrderCount]) [OrderCount],
        SUM(s.[GrossProfit]) [GrossProfit], COUNT(*) [StatisticRowCount], COUNT(s.[TotalCost]) [CostedRowCount],
        COUNT(s.[GrossProfit]) [GrossProfitRowCount]
 FROM [{factSource}] s
 CROSS JOIN Periods periods
 WHERE s.[Date]>=periods.[StartDate] AND s.[Date]<periods.[EndDate]{failedDateFilter}{sourceBranch}{sourceProduct}{categorySourceFilter}
 GROUP BY periods.[Period], s.[SupplierCode], s.[BranchCode], s.[ProductCode]
), RawFacts AS
(
 SELECT [Period],
        LTRIM(RTRIM(COALESCE([RawSupplierCode],''))) [RawSupplierCode],
        LTRIM(RTRIM(COALESCE([BranchCode],''))) [BranchCode],
        LTRIM(RTRIM(COALESCE([ProductCode],''))) [ProductCode],
        SUM([Revenue]) [Revenue], SUM([Quantity]) [Quantity], SUM([OrderCount]) [OrderCount],
        SUM([GrossProfit]) [GrossProfit], SUM([StatisticRowCount]) [StatisticRowCount], SUM([CostedRowCount]) [CostedRowCount],
        SUM([GrossProfitRowCount]) [GrossProfitRowCount]
 FROM SourceFacts
 GROUP BY [Period], LTRIM(RTRIM(COALESCE([RawSupplierCode],''))),
          LTRIM(RTRIM(COALESCE([BranchCode],''))), LTRIM(RTRIM(COALESCE([ProductCode],'')))
), ResolvedFacts AS
(
 SELECT r.[Period], r.[RawSupplierCode],
        CASE WHEN r.[RawSupplierCode]='200' THEN NULLIF(LTRIM(RTRIM({mapping})), '')
             WHEN cs.[SupplierCode] IS NOT NULL THEN r.[RawSupplierCode] END [ChinaSupplierCode],
        CASE WHEN r.[RawSupplierCode]='200' OR cs.[SupplierCode] IS NOT NULL THEN '200'
             ELSE NULLIF(r.[RawSupplierCode], '') END [AustralianSupplierCode],
        r.[BranchCode], r.[ProductCode],
        CAST(NULL AS nvarchar(255)) [StatisticProductName], CAST(NULL AS nvarchar(100)) [StatisticBarcode],
        r.[Revenue], r.[Quantity], r.[OrderCount], r.[GrossProfit], r.[StatisticRowCount],
        r.[CostedRowCount], r.[GrossProfitRowCount]
 FROM RawFacts r
 {rawMapping}
 LEFT JOIN (SELECT [SupplierCode] FROM [ChinaSupplier] WHERE [SupplierCode] IS NOT NULL AND [SupplierCode]<>'' GROUP BY [SupplierCode]) cs
   ON cs.[SupplierCode]=r.[RawSupplierCode]
)
SELECT [Period], [RawSupplierCode], [ChinaSupplierCode], [AustralianSupplierCode], [BranchCode], [ProductCode],
       [StatisticProductName], [StatisticBarcode], [Revenue], [Quantity], [OrderCount], [GrossProfit],
       [StatisticRowCount], [CostedRowCount], [GrossProfitRowCount]
INTO #SalesDetailFacts
FROM ResolvedFacts;
""";
        var facts = tokens.Length == 0 ? rawFacts : wideFacts;

        string buildWideFactRows(string source, bool includeBounds = false) => $"""
SELECT {(includeBounds ? "f.[MinProductCode], f.[MaxProductCode]," : string.Empty)} f.[Period], f.[RawSupplierCode], f.[ChinaSupplierCode], f.[AustralianSupplierCode], f.[BranchCode], f.[ProductCode],
       f.[StatisticProductName], f.[StatisticBarcode], f.[Revenue], f.[Quantity], f.[OrderCount], f.[GrossProfit],
       f.[StatisticRowCount], f.[CostedRowCount], f.[GrossProfitRowCount],
       CASE WHEN @sdrKind=1 THEN
                CASE WHEN f.[RawSupplierCode]='200' OR f.[ChinaSupplierCode] IS NOT NULL
                     THEN COALESCE(NULLIF(LTRIM(RTRIM(china.[SupplierName])), ''),'200')
                     ELSE COALESCE(NULLIF(LTRIM(RTRIM(local.[Name])), ''),f.[RawSupplierCode]) END
            ELSE COALESCE(NULLIF(LTRIM(RTRIM(local.[Name])), ''),CASE WHEN f.[AustralianSupplierCode]='200' THEN 'hotbargain' ELSE f.[AustralianSupplierCode] END) END [SupplierName],
       COALESCE(store.[StoreName],f.[BranchCode]) [BranchName],
       f.[StatisticProductName] [ProductName],
       CAST(NULL AS nvarchar(200)) [EnglishName], CAST(NULL AS nvarchar(50)) [ItemNumber],
       CAST(NULL AS nvarchar(200)) [ProductImage], CAST(NULL AS nvarchar(50)) [ProductBarcode],
       CASE WHEN @sdrKind=1 THEN f.[ChinaSupplierCode] ELSE f.[AustralianSupplierCode] END [SupplierCode]
FROM [{source}] f
LEFT JOIN (SELECT [SupplierCode], MAX([SupplierName]) [SupplierName]
           FROM [ChinaSupplier] WHERE [SupplierCode] IS NOT NULL AND [SupplierCode]<>'' GROUP BY [SupplierCode]) china
  ON china.[SupplierCode]=f.[ChinaSupplierCode]
LEFT JOIN [LocalSupplier] local ON local.[LocalSupplierCode]=CASE WHEN @sdrKind=1 THEN f.[RawSupplierCode] ELSE f.[AustralianSupplierCode] END AND local.[IsDeleted]=0
LEFT JOIN [Store] store ON store.[StoreCode]=f.[BranchCode]
""";
        var wideFactRows = buildWideFactRows("#SalesDetailFacts");
        var projectedFactRows = buildWideFactRows("#SalesDetailProjectionTotals", includeBounds: true);
        // 无关键词时，目录名称只从窄事实的 distinct 供应商/分店键解析一次；搜索路径保留完整名称连接，避免改变搜索口径。
        var narrowFactRows = """
SELECT f.[Period], f.[RawSupplierCode], f.[ChinaSupplierCode], f.[AustralianSupplierCode], f.[BranchCode], f.[ProductCode],
       f.[StatisticProductName], f.[StatisticBarcode], f.[Revenue], f.[Quantity], f.[OrderCount], f.[GrossProfit],
       f.[StatisticRowCount], f.[CostedRowCount], f.[GrossProfitRowCount],
       CAST(NULL AS nvarchar(200)) [SupplierName], CAST(NULL AS nvarchar(200)) [BranchName],
       f.[StatisticProductName] [ProductName],
       CAST(NULL AS nvarchar(200)) [EnglishName], CAST(NULL AS nvarchar(50)) [ItemNumber],
       CAST(NULL AS nvarchar(200)) [ProductImage], CAST(NULL AS nvarchar(50)) [ProductBarcode],
       CASE WHEN @sdrKind=1 THEN f.[ChinaSupplierCode] ELSE f.[AustralianSupplierCode] END [SupplierCode]
FROM [#SalesDetailFacts] f
""";
        var factRows = tokens.Length == 0 ? narrowFactRows : wideFactRows;
        // 商品资料的模糊匹配每个关键词只做一次，避免每条分店事实及每个结果集反复扫描 Product。
        // 保留按关键词独立的命中集合，让多个关键词仍可分别命中统计名称、供应商和商品资料。
        var needsProductSearch = tokens.Length > 0
            && (wanted.Contains(SalesDetailSection.Summary) || wanted.Contains(SalesDetailSection.Products));
        var candidateToken = tokens.Length > 0
            ? Enumerable.Range(0, tokens.Length).OrderByDescending(i => tokens[i].Length).First() : 0;
        var initialProductTokens = Enumerable.Range(0, tokens.Length)
            .Where(i => !useProjection || i == candidateToken);
        string productTokenMatch(string table, int i) =>
            $"SELECT pSearch.[ProductCode], {i} [TokenIndex] FROM [{table}] pSearch WHERE pSearch.[ProductCode] IS NOT NULL AND (pSearch.[Barcode] LIKE @sdrSearch{i} OR pSearch.[ProductName] LIKE @sdrSearch{i} OR pSearch.[EnglishName] LIKE @sdrSearch{i} OR pSearch.[ItemNumber] LIKE @sdrSearch{i} OR pSearch.[LocalSupplierCode] LIKE @sdrSearch{i})";
        var productSearchMatches = needsProductSearch
            ? "SELECT DISTINCT [ProductCode], [TokenIndex] INTO #SalesDetailProductSearchMatches FROM ("
                + string.Join(" UNION ALL ", initialProductTokens.Select(i => productTokenMatch("Product", i)))
                + ") matches" + projectionQueryHint + ";"
            : string.Empty;
        var remainingTokenIndexes = Enumerable.Range(0, tokens.Length).Where(i => i != candidateToken).ToArray();
        // 候选键在外侧，逐键读取同码的全部当前资料；命中集合按词去重，重复资料不会放大销售数值。
        string readRemainingProductMatches(string source, string metadataTable) => $"""
SELECT pSearch.[ProductCode],pSearch.[Barcode],pSearch.[ProductName],pSearch.[EnglishName],pSearch.[ItemNumber],pSearch.[LocalSupplierCode]
INTO {metadataTable}
FROM (SELECT DISTINCT LTRIM(RTRIM([ProductCode])) [ProductCode] FROM {source}) known
INNER LOOP JOIN dbo.Product pSearch ON pSearch.[ProductCode]=known.[ProductCode];
INSERT INTO #SalesDetailProductSearchMatches
SELECT DISTINCT [ProductCode],[TokenIndex] FROM (
""" + string.Join(" UNION ALL ", remainingTokenIndexes.Select(i => productTokenMatch(metadataTable, i)))
                + $") matches;DROP TABLE {metadataTable};";
        // 没有供应商整段事实待读时，资料匹配可提前到候选商品；否则维持事实读取后的完整匹配。
        var candidateRefinement = useProjection && tokens.Length > 1
            ? "IF NOT EXISTS (SELECT 1 FROM #SalesDetailCandidateSuppliers) BEGIN\n"
                + readRemainingProductMatches("#SalesDetailCandidateProducts", "#SalesDetailEarlyProductMetadata")
                + BuildSalesDetailProjectionRefinementSql(remainingTokenIndexes, selectedProduct) + "\nEND;"
            : string.Empty;
        var remainingProductMatches = useProjection && tokens.Length > 1
            ? "IF EXISTS (SELECT 1 FROM #SalesDetailCandidateSuppliers) BEGIN\n"
                + readRemainingProductMatches("#SalesDetailCandidateSource", "#SalesDetailLateProductMetadata") + "\nEND;"
            : string.Empty;
        var supplierSearchMatches = needsProductSearch
            ? "SELECT DISTINCT [SupplierCode], [TokenIndex] INTO #SalesDetailSupplierSearchMatches FROM ("
                + string.Join(" UNION ALL ", tokens.Select((_, i) =>
                    $"SELECT cSearch.[SupplierCode], {i} [TokenIndex] FROM [ChinaSupplier] cSearch WHERE cSearch.[SupplierCode] IS NOT NULL AND (cSearch.[SupplierCode] LIKE @sdrSearch{i} OR cSearch.[SupplierName] LIKE @sdrSearch{i})"))
                + ") matches;"
            : string.Empty;
        // 唯一命中键用连接复用，避免 OR 内的相关 EXISTS 被逐条执行，也避免重复资料放大销售数值。
        var searchJoins = string.Join("\n", tokens.Select((_, i) =>
            $"LEFT JOIN #SalesDetailProductSearchMatches pMatch{i} ON pMatch{i}.[ProductCode]=f.[ProductCode] AND pMatch{i}.[TokenIndex]={i}\nLEFT JOIN #SalesDetailSupplierSearchMatches cMatch{i} ON cMatch{i}.[SupplierCode]=f.[ChinaSupplierCode] AND cMatch{i}.[TokenIndex]={i}"));
        var searchFilter = string.Join(" AND ", tokens.Select((_, i) =>
            $"(f.[ProductCode] LIKE @sdrSearch{i} OR f.[StatisticBarcode] LIKE @sdrSearch{i} OR f.[ProductName] LIKE @sdrSearch{i} OR f.[RawSupplierCode] LIKE @sdrSearch{i} OR f.[SupplierCode] LIKE @sdrSearch{i} OR f.[SupplierName] LIKE @sdrSearch{i} OR pMatch{i}.[ProductCode] IS NOT NULL OR cMatch{i}.[SupplierCode] IS NOT NULL)"));
        // 汇总、商品分页和商品总数共用同一份筛选事实，搜索与目录连接只执行一次。
        var searchFacts = needsProductSearch
            ? $"SELECT f.* INTO #SalesDetailSearchFacts FROM ({factRows}) f {searchJoins} WHERE {searchFilter}{projectionQueryHint};"
            : string.Empty;
        var searchedFactRows = tokens.Length == 0 ? factRows : "SELECT * FROM #SalesDetailSearchFacts";
        var baseSupplier = $"WHERE [SupplierCode] IS NOT NULL{selectedBranchFilter}{productFilter}";
        var baseBranch = $"WHERE [SupplierCode] IS NOT NULL{authorizedBranchFilter}{supplierFilter}{productFilter}";
        var baseProduct = $"WHERE [SupplierCode] IS NOT NULL{selectedBranchFilter}{supplierFilter}";
        var baseSummary = $"WHERE [SupplierCode] IS NOT NULL{selectedBranchFilter}{supplierFilter}{productFilter}";
        string productMultiplicity(string period, bool projected = false)
        {
            var min = projected ? "MinProductCode" : "ProductCode";
            var max = projected ? "MaxProductCode" : "ProductCode";
            return $"CASE WHEN MIN(CASE WHEN [Period]={period} THEN [{min}] END) IS NULL THEN 0 WHEN MIN(CASE WHEN [Period]={period} THEN [{min}] END) = MAX(CASE WHEN [Period]={period} THEN [{max}] END) THEN 1 ELSE 2 END";
        }
        var supplierName = tokens.Length == 0
            ? $"CASE WHEN @sdrKind=1 THEN COALESCE(NULLIF(LTRIM(RTRIM((SELECT MAX(cName.[SupplierName]) FROM [ChinaSupplier] cName WHERE cName.[SupplierCode]=f.[SupplierCode]))), ''), f.[SupplierCode]) ELSE COALESCE(NULLIF(LTRIM(RTRIM((SELECT MAX(lName.[Name]) FROM [LocalSupplier] lName WHERE lName.[LocalSupplierCode]=f.[SupplierCode] AND lName.[IsDeleted]=0))), ''), CASE WHEN f.[SupplierCode]='{CHINA_LOCAL_SUPPLIER_CODE}' THEN '{CHINA_LOCAL_SUPPLIER_FALLBACK_NAME}' ELSE f.[SupplierCode] END) END"
            : "MAX([SupplierName])";
        var branchName = tokens.Length == 0
            ? "COALESCE(NULLIF(LTRIM(RTRIM((SELECT MAX(sName.[StoreName]) FROM [Store] sName WHERE sName.[StoreCode]=f.[BranchCode]))), ''), f.[BranchCode])"
            : "MAX([BranchName])";
        // 空事实仍返回一行汇总，计数必须为 0 才符合压缩 JSON 的非空整数契约；利润继续保留 NULL。
        string rowSelect(string source, string whereClause, string group, string code, string name, string order, string page = "", bool projected = false) => $"""
SELECT {code} [Code], {name} [Name], MAX([ItemNumber]) [ItemNumber], MAX([ProductImage]) [ProductImage],
 COALESCE(SUM(CASE WHEN [Period]=0 THEN [Revenue] ELSE 0 END),0) [Revenue], {(hasCompare ? "COALESCE(SUM(CASE WHEN [Period]=1 THEN [Revenue] ELSE 0 END),0)" : "0")} [CompareRevenue],
 COALESCE(SUM(CASE WHEN [Period]=0 THEN [Quantity] ELSE 0 END),0) [Quantity], {(hasCompare ? "COALESCE(SUM(CASE WHEN [Period]=1 THEN [Quantity] ELSE 0 END),0)" : "0")} [CompareQuantity],
 COALESCE(SUM(CASE WHEN [Period]=0 THEN [OrderCount] ELSE 0 END),0) [OrderCount], {(hasCompare ? "COALESCE(SUM(CASE WHEN [Period]=1 THEN [OrderCount] ELSE 0 END),0)" : "0")} [CompareOrderCount],
 SUM(CASE WHEN [Period]=0 THEN [GrossProfit] END) [GrossProfit], {(hasCompare ? "SUM(CASE WHEN [Period]=1 THEN [GrossProfit] END)" : "CAST(NULL AS decimal(18,2))")} [CompareGrossProfit],
 COALESCE(SUM(CASE WHEN [Period]=0 THEN [StatisticRowCount] ELSE 0 END),0) [StatisticRowCount], COALESCE(SUM(CASE WHEN [Period]=0 THEN [CostedRowCount] ELSE 0 END),0) [CostedRowCount], COALESCE(SUM(CASE WHEN [Period]=0 THEN [GrossProfitRowCount] ELSE 0 END),0) [GrossProfitRowCount],
 {(hasCompare ? "COALESCE(SUM(CASE WHEN [Period]=1 THEN [StatisticRowCount] ELSE 0 END),0)" : "0")} [CompareStatisticRowCount], {(hasCompare ? "COALESCE(SUM(CASE WHEN [Period]=1 THEN [CostedRowCount] ELSE 0 END),0)" : "0")} [CompareCostedRowCount], {(hasCompare ? "COALESCE(SUM(CASE WHEN [Period]=1 THEN [GrossProfitRowCount] ELSE 0 END),0)" : "0")} [CompareGrossProfitRowCount],
 {productMultiplicity("0", projected)} [CurrentProductCount], {(hasCompare ? productMultiplicity("1", projected) : "0")} [CompareProductCount]
FROM ({source}) f {whereClause}
{(string.IsNullOrWhiteSpace(group) ? "" : $"GROUP BY {group}")} {order} {page};
""";
        var summary = rowSelect(searchedFactRows, baseSummary, "", "'summary'", "'当前筛选汇总'", "");
        // 关键词不改变供应商/分店的统计范围；未选商品时直接合并小的日投影。
        var useProjectedColumns = useProjection && string.IsNullOrWhiteSpace(selectedProduct);
        var columnFacts = useProjectedColumns ? projectedFactRows : factRows;
        var suppliers = rowSelect(columnFacts, baseSupplier, "[SupplierCode]", "[SupplierCode]", supplierName, "ORDER BY [Revenue] DESC, [CompareRevenue] DESC, [Code] ASC", projected: useProjectedColumns);
        var branchesSql = rowSelect(columnFacts, baseBranch, "[BranchCode]", "[BranchCode]", branchName, "ORDER BY [Revenue] DESC, [CompareRevenue] DESC, [Code] ASC", projected: useProjectedColumns);
        var offset = ((long)pageIndex - 1L) * pageSize;
        var productAggregate = $"""
SELECT [ProductCode], MAX([StatisticProductName]) [StatisticProductName],
       SUM(CASE WHEN [Period]=0 THEN [Revenue] ELSE 0 END) [Revenue], {(hasCompare ? "SUM(CASE WHEN [Period]=1 THEN [Revenue] ELSE 0 END)" : "0")} [CompareRevenue],
       SUM(CASE WHEN [Period]=0 THEN [Quantity] ELSE 0 END) [Quantity], {(hasCompare ? "SUM(CASE WHEN [Period]=1 THEN [Quantity] ELSE 0 END)" : "0")} [CompareQuantity],
       SUM(CASE WHEN [Period]=0 THEN [OrderCount] ELSE 0 END) [OrderCount], {(hasCompare ? "SUM(CASE WHEN [Period]=1 THEN [OrderCount] ELSE 0 END)" : "0")} [CompareOrderCount],
       SUM(CASE WHEN [Period]=0 THEN [GrossProfit] END) [GrossProfit], {(hasCompare ? "SUM(CASE WHEN [Period]=1 THEN [GrossProfit] END)" : "CAST(NULL AS decimal(18,2))")} [CompareGrossProfit],
       SUM(CASE WHEN [Period]=0 THEN [StatisticRowCount] ELSE 0 END) [StatisticRowCount], SUM(CASE WHEN [Period]=0 THEN [CostedRowCount] ELSE 0 END) [CostedRowCount], SUM(CASE WHEN [Period]=0 THEN [GrossProfitRowCount] ELSE 0 END) [GrossProfitRowCount],
       {(hasCompare ? "SUM(CASE WHEN [Period]=1 THEN [StatisticRowCount] ELSE 0 END)" : "0")} [CompareStatisticRowCount], {(hasCompare ? "SUM(CASE WHEN [Period]=1 THEN [CostedRowCount] ELSE 0 END)" : "0")} [CompareCostedRowCount], {(hasCompare ? "SUM(CASE WHEN [Period]=1 THEN [GrossProfitRowCount] ELSE 0 END)" : "0")} [CompareGrossProfitRowCount],
       {productMultiplicity("0")} [CurrentProductCount], {(hasCompare ? productMultiplicity("1") : "0")} [CompareProductCount]
FROM ({searchedFactRows}) f {baseProduct}
GROUP BY [ProductCode]
""";
        var statBranchFilter = branches is { Count: > 0 }
            ? $" AND s0.[BranchCode] IN ({string.Join(",", branches.Select((_, i) => $"@sdrBranch{i}"))})"
            : string.Empty;
        statBranchFilter += string.IsNullOrWhiteSpace(selectedBranch)
            ? (branches is { Count: > 0 } ? $" AND s0.[BranchCode] IN ({string.Join(",", branches.Select((_, i) => $"@sdrSelectedBranch{i}"))})" : string.Empty)
            : " AND s0.[BranchCode] = @sdrSelectedBranch";
        var statPeriodFilter = $" AND ((s0.[Date]>=@sdrCurrentStart AND s0.[Date]<@sdrCurrentEnd) OR (@sdrHasCompare=1 AND s0.[Date]>=@sdrCompareStart AND s0.[Date]<@sdrCompareEnd)){statBranchFilter}";
        var productMetadata = $"""
OUTER APPLY (SELECT TOP (1) p0.[ProductName], p0.[EnglishName], p0.[ItemNumber], p0.[ProductImage], p0.[Barcode]
             FROM [Product] p0 WHERE p0.[ProductCode]=a.[ProductCode] ORDER BY p0.[UUID]) p
OUTER APPLY (SELECT TOP (1) s0.[ProductName] [StatisticProductName]
             FROM [ProductStoreDailySalesStatistic] s0
             WHERE NULLIF(LTRIM(RTRIM(p.[ProductName])), '') IS NULL AND NULLIF(LTRIM(RTRIM(a.[StatisticProductName])), '') IS NULL
               AND s0.[ProductCode]=a.[ProductCode]
               {statPeriodFilter}
             ORDER BY s0.[Date] DESC) stat
""";
        var products = $"""
SELECT a.[ProductCode] [Code], COALESCE(NULLIF(LTRIM(RTRIM(p.[ProductName])), ''),NULLIF(LTRIM(RTRIM(a.[StatisticProductName])), ''),NULLIF(LTRIM(RTRIM(stat.[StatisticProductName])), ''),a.[ProductCode]) [Name], p.[ItemNumber], p.[ProductImage],
       a.[Revenue], a.[CompareRevenue], a.[Quantity], a.[CompareQuantity], a.[OrderCount], a.[CompareOrderCount],
       a.[GrossProfit], a.[CompareGrossProfit], a.[StatisticRowCount], a.[CostedRowCount], a.[GrossProfitRowCount],
       a.[CompareStatisticRowCount], a.[CompareCostedRowCount], a.[CompareGrossProfitRowCount], a.[CurrentProductCount], a.[CompareProductCount]
FROM ({productAggregate} ORDER BY [Quantity] DESC, [CompareQuantity] DESC, [ProductCode] ASC OFFSET {offset} ROWS FETCH NEXT {pageSize} ROWS ONLY) a
{productMetadata}
ORDER BY a.[Quantity] DESC, a.[CompareQuantity] DESC, a.[ProductCode] ASC;
""";
        var useGroupingSets = tokens.Length == 0
            && string.IsNullOrWhiteSpace(selectedBranch)
            && string.IsNullOrWhiteSpace(selectedSupplier)
            && string.IsNullOrWhiteSpace(selectedProduct);
        var aggregateAuthorizedBranchFilter = branches is { Count: > 0 }
            ? $" AND f.[BranchCode] IN ({string.Join(",", branches.Select((_, i) => $"@sdrBranch{i}"))})"
            : string.Empty;
        var groupingFacts = $"""
WITH FactSource AS
(
    SELECT f.[Period], f.[BranchCode], f.[ProductCode],
           CASE WHEN @sdrKind=1 THEN f.[ChinaSupplierCode] ELSE f.[AustralianSupplierCode] END [SupplierCode],
           f.[Revenue], f.[Quantity], f.[OrderCount], f.[GrossProfit],
           f.[StatisticRowCount], f.[CostedRowCount], f.[GrossProfitRowCount]
    FROM [#SalesDetailFacts] f
    WHERE CASE WHEN @sdrKind=1 THEN f.[ChinaSupplierCode] ELSE f.[AustralianSupplierCode] END IS NOT NULL
           {aggregateAuthorizedBranchFilter}
), Grouped AS
(
    SELECT [Period], [SupplierCode], [BranchCode], [ProductCode],
           SUM([Revenue]) [Revenue], SUM([Quantity]) [Quantity], SUM([OrderCount]) [OrderCount], SUM([GrossProfit]) [GrossProfit],
           SUM([StatisticRowCount]) [StatisticRowCount], SUM([CostedRowCount]) [CostedRowCount], SUM([GrossProfitRowCount]) [GrossProfitRowCount],
           MIN(CASE WHEN [Period]=0 THEN [ProductCode] END) [CurrentProductMinCode], MAX(CASE WHEN [Period]=0 THEN [ProductCode] END) [CurrentProductMaxCode],
           MIN(CASE WHEN [Period]=1 THEN [ProductCode] END) [CompareProductMinCode], MAX(CASE WHEN [Period]=1 THEN [ProductCode] END) [CompareProductMaxCode],
           CASE WHEN GROUPING([SupplierCode])=1 AND GROUPING([BranchCode])=1 AND GROUPING([ProductCode])=1 THEN 0
                WHEN GROUPING([SupplierCode])=0 AND GROUPING([BranchCode])=1 AND GROUPING([ProductCode])=1 THEN 1
                WHEN GROUPING([SupplierCode])=1 AND GROUPING([BranchCode])=0 AND GROUPING([ProductCode])=1 THEN 2
                ELSE 3 END [GroupType]
    FROM FactSource
    GROUP BY GROUPING SETS (([Period],[SupplierCode]), ([Period],[BranchCode]), ([Period],[ProductCode]), ([Period]))
)
-- 供应商与分店两组只有几百个键，优化器却会为它们排序几十万行事实再流式聚合；生产实测 8 个月双期要 8 秒。
-- 强制哈希聚合后四组各扫一遍窄事实、不再排序。
SELECT * INTO #SalesDetailAggregates FROM Grouped OPTION (HASH GROUP);
""";
        string aggregateRowSelect(int groupType, string code, string name, string groupBy, string order) => $"""
SELECT {code} [Code], {name} [Name], CAST(NULL AS nvarchar(50)) [ItemNumber], CAST(NULL AS nvarchar(200)) [ProductImage],
 COALESCE(SUM(CASE WHEN [Period]=0 THEN [Revenue] ELSE 0 END),0) [Revenue], {(hasCompare ? "COALESCE(SUM(CASE WHEN [Period]=1 THEN [Revenue] ELSE 0 END),0)" : "0")} [CompareRevenue],
 COALESCE(SUM(CASE WHEN [Period]=0 THEN [Quantity] ELSE 0 END),0) [Quantity], {(hasCompare ? "COALESCE(SUM(CASE WHEN [Period]=1 THEN [Quantity] ELSE 0 END),0)" : "0")} [CompareQuantity],
 COALESCE(SUM(CASE WHEN [Period]=0 THEN [OrderCount] ELSE 0 END),0) [OrderCount], {(hasCompare ? "COALESCE(SUM(CASE WHEN [Period]=1 THEN [OrderCount] ELSE 0 END),0)" : "0")} [CompareOrderCount],
 SUM(CASE WHEN [Period]=0 THEN [GrossProfit] END) [GrossProfit], {(hasCompare ? "SUM(CASE WHEN [Period]=1 THEN [GrossProfit] END)" : "CAST(NULL AS decimal(18,2))")} [CompareGrossProfit],
 COALESCE(SUM(CASE WHEN [Period]=0 THEN [StatisticRowCount] ELSE 0 END),0) [StatisticRowCount], COALESCE(SUM(CASE WHEN [Period]=0 THEN [CostedRowCount] ELSE 0 END),0) [CostedRowCount], COALESCE(SUM(CASE WHEN [Period]=0 THEN [GrossProfitRowCount] ELSE 0 END),0) [GrossProfitRowCount],
 {(hasCompare ? "COALESCE(SUM(CASE WHEN [Period]=1 THEN [StatisticRowCount] ELSE 0 END),0)" : "0")} [CompareStatisticRowCount], {(hasCompare ? "COALESCE(SUM(CASE WHEN [Period]=1 THEN [CostedRowCount] ELSE 0 END),0)" : "0")} [CompareCostedRowCount], {(hasCompare ? "COALESCE(SUM(CASE WHEN [Period]=1 THEN [GrossProfitRowCount] ELSE 0 END),0)" : "0")} [CompareGrossProfitRowCount],
 CASE WHEN MIN([CurrentProductMinCode]) IS NULL THEN 0 WHEN MIN([CurrentProductMinCode])=MAX([CurrentProductMaxCode]) THEN 1 ELSE 2 END [CurrentProductCount],
 CASE WHEN MIN([CompareProductMinCode]) IS NULL THEN 0 WHEN MIN([CompareProductMinCode])=MAX([CompareProductMaxCode]) THEN 1 ELSE 2 END [CompareProductCount]
FROM #SalesDetailAggregates f
WHERE [GroupType]={groupType}
{(string.IsNullOrWhiteSpace(groupBy) ? string.Empty : $"GROUP BY {groupBy}")} {order};
""";
        var groupingSummary = aggregateRowSelect(0, "'summary'", "'当前筛选汇总'", "", string.Empty);
        var groupingSuppliers = aggregateRowSelect(1, "f.[SupplierCode]", supplierName, "f.[SupplierCode]", "ORDER BY [Revenue] DESC, [CompareRevenue] DESC, [Code] ASC");
        var groupingBranches = aggregateRowSelect(2, "f.[BranchCode]", branchName, "f.[BranchCode]", "ORDER BY [Revenue] DESC, [CompareRevenue] DESC, [Code] ASC");
        var groupingProductAggregate = $"""
SELECT [ProductCode], CAST(NULL AS nvarchar(255)) [StatisticProductName],
       SUM(CASE WHEN [Period]=0 THEN [Revenue] ELSE 0 END) [Revenue], {(hasCompare ? "SUM(CASE WHEN [Period]=1 THEN [Revenue] ELSE 0 END)" : "0")} [CompareRevenue],
       SUM(CASE WHEN [Period]=0 THEN [Quantity] ELSE 0 END) [Quantity], {(hasCompare ? "SUM(CASE WHEN [Period]=1 THEN [Quantity] ELSE 0 END)" : "0")} [CompareQuantity],
       SUM(CASE WHEN [Period]=0 THEN [OrderCount] ELSE 0 END) [OrderCount], {(hasCompare ? "SUM(CASE WHEN [Period]=1 THEN [OrderCount] ELSE 0 END)" : "0")} [CompareOrderCount],
       SUM(CASE WHEN [Period]=0 THEN [GrossProfit] END) [GrossProfit], {(hasCompare ? "SUM(CASE WHEN [Period]=1 THEN [GrossProfit] END)" : "CAST(NULL AS decimal(18,2))")} [CompareGrossProfit],
       SUM(CASE WHEN [Period]=0 THEN [StatisticRowCount] ELSE 0 END) [StatisticRowCount], SUM(CASE WHEN [Period]=0 THEN [CostedRowCount] ELSE 0 END) [CostedRowCount], SUM(CASE WHEN [Period]=0 THEN [GrossProfitRowCount] ELSE 0 END) [GrossProfitRowCount],
       {(hasCompare ? "SUM(CASE WHEN [Period]=1 THEN [StatisticRowCount] ELSE 0 END)" : "0")} [CompareStatisticRowCount], {(hasCompare ? "SUM(CASE WHEN [Period]=1 THEN [CostedRowCount] ELSE 0 END)" : "0")} [CompareCostedRowCount], {(hasCompare ? "SUM(CASE WHEN [Period]=1 THEN [GrossProfitRowCount] ELSE 0 END)" : "0")} [CompareGrossProfitRowCount],
       CASE WHEN MAX(CASE WHEN [Period]=0 THEN 1 ELSE 0 END)=1 THEN 1 ELSE 0 END [CurrentProductCount],
       CASE WHEN MAX(CASE WHEN [Period]=1 THEN 1 ELSE 0 END)=1 THEN 1 ELSE 0 END [CompareProductCount]
FROM #SalesDetailAggregates WHERE [GroupType]=3
GROUP BY [ProductCode]
""";
        var groupingProducts = $"""
SELECT a.[ProductCode] [Code], COALESCE(NULLIF(LTRIM(RTRIM(p.[ProductName])), ''),NULLIF(LTRIM(RTRIM(stat.[StatisticProductName])), ''),a.[ProductCode]) [Name], p.[ItemNumber], p.[ProductImage],
       a.[Revenue], a.[CompareRevenue], a.[Quantity], a.[CompareQuantity], a.[OrderCount], a.[CompareOrderCount],
       a.[GrossProfit], a.[CompareGrossProfit], a.[StatisticRowCount], a.[CostedRowCount], a.[GrossProfitRowCount],
       a.[CompareStatisticRowCount], a.[CompareCostedRowCount], a.[CompareGrossProfitRowCount], a.[CurrentProductCount], a.[CompareProductCount]
FROM ({groupingProductAggregate} ORDER BY [Quantity] DESC, [CompareQuantity] DESC, [ProductCode] ASC OFFSET {offset} ROWS FETCH NEXT {pageSize} ROWS ONLY) a
{productMetadata}
ORDER BY a.[Quantity] DESC, a.[CompareQuantity] DESC, a.[ProductCode] ASC;
""";
        // 每个商品在本期/同期各有一行；total 必须按商品代码去重，避免跨期把同一商品算两次。
        var groupingProductCount = "SELECT COUNT(DISTINCT [ProductCode]) FROM #SalesDetailAggregates WHERE [GroupType]=3;";
        var emptyRows = "SELECT TOP 0 CAST(NULL AS nvarchar(50)) [Code], CAST(NULL AS nvarchar(200)) [Name], CAST(NULL AS nvarchar(50)) [ItemNumber], CAST(NULL AS nvarchar(200)) [ProductImage], CAST(0 AS decimal(18,2)) [Revenue], CAST(0 AS decimal(18,2)) [CompareRevenue], CAST(0 AS int) [Quantity], CAST(0 AS int) [CompareQuantity], CAST(0 AS int) [OrderCount], CAST(0 AS int) [CompareOrderCount], CAST(NULL AS decimal(18,2)) [GrossProfit], CAST(NULL AS decimal(18,2)) [CompareGrossProfit], CAST(0 AS int) [StatisticRowCount], CAST(0 AS int) [CostedRowCount], CAST(0 AS int) [GrossProfitRowCount], CAST(0 AS int) [CompareStatisticRowCount], CAST(0 AS int) [CompareCostedRowCount], CAST(0 AS int) [CompareGrossProfitRowCount], CAST(0 AS int) [CurrentProductCount], CAST(0 AS int) [CompareProductCount];";
        var emptyCount = "SELECT 0;";
        var productCount = $"SELECT COUNT(*) FROM ({productAggregate}) x;";
        var denominatorFacts = useProjection ? projectedFactRows : factRows;
        var denominator = $"SELECT COALESCE(SUM(CASE WHEN [Period]=0 AND [AustralianSupplierCode] IS NOT NULL THEN [Revenue] ELSE 0 END),0), COALESCE(SUM(CASE WHEN [Period]=0 AND [ChinaSupplierCode] IS NOT NULL THEN [Revenue] ELSE 0 END),0), COALESCE(SUM(CASE WHEN [Period]=1 AND [AustralianSupplierCode] IS NOT NULL THEN [Revenue] ELSE 0 END),0), COALESCE(SUM(CASE WHEN [Period]=1 AND [ChinaSupplierCode] IS NOT NULL THEN [Revenue] ELSE 0 END),0) FROM ({denominatorFacts}) f WHERE [AustralianSupplierCode] IS NOT NULL{selectedBranchFilter};";
        if (!wanted.Contains(SalesDetailSection.Summary)) summary = emptyRows;
        if (!wanted.Contains(SalesDetailSection.Suppliers)) suppliers = emptyRows;
        if (!wanted.Contains(SalesDetailSection.Branches)) branchesSql = emptyRows;
        if (!wanted.Contains(SalesDetailSection.Products)) { products = emptyRows; productCount = emptyCount; }
        if (!wanted.Contains(SalesDetailSection.Suppliers)) denominator = "SELECT 0,0,0,0;";
        // 本报表只使用 ProductStoreDaily 的发布状态和版本，其他统计类型会被状态解析器忽略。
        var status = "SELECT [StatisticType],[Date],[Status],[LastAggregatedAtUtc],[CompletedAtUtc],[SourceProductVersion] FROM [SalesStatisticRefreshState] WHERE [StatisticType]='ProductStoreDaily' AND (([Date]>=@sdrCurrentStart AND [Date]<@sdrCurrentEnd) OR (@sdrHasCompare=1 AND [Date]>=@sdrCompareStart AND [Date]<@sdrCompareEnd)) ORDER BY [Date],[StatisticType];";
        if (useGroupingSets)
        {
            // GROUPING SETS 只服务默认全量路径；仍按请求 sections 跳过未请求栏位，保持旧接口契约。
            summary = wanted.Contains(SalesDetailSection.Summary) ? groupingSummary : emptyRows;
            suppliers = wanted.Contains(SalesDetailSection.Suppliers) ? groupingSuppliers : emptyRows;
            branchesSql = wanted.Contains(SalesDetailSection.Branches) ? groupingBranches : emptyRows;
            products = wanted.Contains(SalesDetailSection.Products) ? groupingProducts : emptyRows;
            productCount = wanted.Contains(SalesDetailSection.Products) ? groupingProductCount : emptyCount;
            denominator = wanted.Contains(SalesDetailSection.Suppliers)
                ? $"SELECT COALESCE(SUM(CASE WHEN [Period]=0 AND [AustralianSupplierCode] IS NOT NULL THEN [Revenue] ELSE 0 END),0), COALESCE(SUM(CASE WHEN [Period]=0 AND [ChinaSupplierCode] IS NOT NULL THEN [Revenue] ELSE 0 END),0), COALESCE(SUM(CASE WHEN [Period]=1 AND [AustralianSupplierCode] IS NOT NULL THEN [Revenue] ELSE 0 END),0), COALESCE(SUM(CASE WHEN [Period]=1 AND [ChinaSupplierCode] IS NOT NULL THEN [Revenue] ELSE 0 END),0) FROM [#SalesDetailFacts] f WHERE [AustralianSupplierCode] IS NOT NULL{aggregateAuthorizedBranchFilter};"
                : "SELECT 0,0,0,0;";
            return categoryPrefetch + facts + groupingFacts + status + summary + suppliers + branchesSql + products + productCount + denominator + "DROP TABLE #SalesDetailAggregates;DROP TABLE #SalesDetailFacts;"
                + (categoryPrefetch.Length > 0 ? "DROP TABLE #SalesDetailCategorySource;" : string.Empty);
        }
        var projectionGuard = useProjection ? BuildSalesDetailProjectionGuardSql(posmDatabase!, sourceBranch) : string.Empty;
        var projectionSource = useProjection
            ? BuildSalesDetailProjectionSourceSql(posmDatabase!, sourceBranch, candidateToken, selectedProduct, projectionQueryHint, candidateRefinement)
            : string.Empty;
        if (compressOutput)
        {
            status = CompressSalesDetailResultSql(status.Replace("SELECT [StatisticType],", "SELECT [StatisticType] [Type],", StringComparison.Ordinal));
            summary = CompressSalesDetailResultSql(summary);
            suppliers = CompressSalesDetailResultSql(suppliers);
            branchesSql = CompressSalesDetailResultSql(branchesSql);
            products = CompressSalesDetailResultSql(products);
            productCount = CompressSalesDetailResultSql(productCount.Replace("SELECT COUNT(*) FROM", "SELECT COUNT(*) [Total] FROM", StringComparison.Ordinal)
                .Replace("SELECT 0;", "SELECT 0 [Total];", StringComparison.Ordinal));
            // 分母的列名是内部传输约定，外部 DTO 和原有数值口径保持一致。
            var denominatorSelect = wanted.Contains(SalesDetailSection.Suppliers)
                ? $"SELECT COALESCE(SUM(CASE WHEN [Period]=0 AND [AustralianSupplierCode] IS NOT NULL THEN [Revenue] ELSE 0 END),0) [AllRevenue], COALESCE(SUM(CASE WHEN [Period]=0 AND [ChinaSupplierCode] IS NOT NULL THEN [Revenue] ELSE 0 END),0) [ChinaRevenue], COALESCE(SUM(CASE WHEN [Period]=1 AND [AustralianSupplierCode] IS NOT NULL THEN [Revenue] ELSE 0 END),0) [CompareAllRevenue], COALESCE(SUM(CASE WHEN [Period]=1 AND [ChinaSupplierCode] IS NOT NULL THEN [Revenue] ELSE 0 END),0) [CompareChinaRevenue] FROM ({denominatorFacts}) f WHERE [AustralianSupplierCode] IS NOT NULL{selectedBranchFilter};"
                : "SELECT 0 [AllRevenue],0 [ChinaRevenue],0 [CompareAllRevenue],0 [CompareChinaRevenue];";
            denominator = CompressSalesDetailResultSql(denominatorSelect);
        }
        // 日投影路径多返回一个结果集：守卫跳过的日期，接口据此在提示里列出未计入的日期。
        var skippedDates = useProjection
            ? "SELECT DISTINCT CAST([Date] AS datetime2(0)) [Date] FROM #SalesDetailSkippedDates ORDER BY [Date];"
            : string.Empty;
        if (useProjection && compressOutput) skippedDates = CompressSalesDetailResultSql(skippedDates);
        return categoryPrefetch + productSearchMatches + supplierSearchMatches + projectionGuard + projectionSource + remainingProductMatches + facts + searchFacts + status + summary + suppliers + branchesSql + products + productCount + denominator + skippedDates
            + "DROP TABLE #SalesDetailFacts;" + (categoryPrefetch.Length > 0 ? "DROP TABLE #SalesDetailCategorySource;" : string.Empty) + (needsProductSearch ? "DROP TABLE #SalesDetailSearchFacts;DROP TABLE #SalesDetailProductSearchMatches;DROP TABLE #SalesDetailSupplierSearchMatches;" : string.Empty)
            + (useProjection ? "DROP TABLE #SalesDetailRequiredDates;DROP TABLE #SalesDetailSkippedDates;DROP TABLE #SalesDetailProjectionTotals;DROP TABLE #SalesDetailCandidateProducts;DROP TABLE #SalesDetailCandidateSuppliers;DROP TABLE #SalesDetailCandidateSource;" : string.Empty);
    }

    private static string BuildSalesDetailReportSqlLegacy(bool sqlServer, string? posmDatabase, DateRangeDto range, SalesDetailKind kind,
        IReadOnlyCollection<string>? branches, string? selectedBranch, string? selectedSupplier, string? selectedProduct,
        string? search, int pageIndex, int pageSize, IReadOnlySet<SalesDetailSection> wanted, IReadOnlyDictionary<string, string>? fallbackMap)
        => BuildSalesDetailReportSqlLegacyWithFilters(sqlServer, posmDatabase, range, kind, branches, selectedBranch, selectedSupplier,
            selectedProduct, search, pageIndex, pageSize, wanted, fallbackMap, null, null, null);

    private static string BuildSalesDetailReportSqlLegacyWithFilters(bool sqlServer, string? posmDatabase, DateRangeDto range, SalesDetailKind kind,
        IReadOnlyCollection<string>? branches, string? selectedBranch, string? selectedSupplier, string? selectedProduct,
        string? search, int pageIndex, int pageSize, IReadOnlySet<SalesDetailSection> wanted, IReadOnlyDictionary<string, string>? fallbackMap,
        IReadOnlyCollection<string>? selectedSuppliers, IReadOnlyCollection<string>? supplierCategoryGuids,
        IReadOnlyCollection<string>? warehouseCategoryGuids)
    {
        var hasCompare = HasCompare(range);
        var categorySourceFilter = BuildSalesDetailCategoryFilter(kind, supplierCategoryGuids, warehouseCategoryGuids, "s.[ProductCode]");
        var sourceBranch = branches is { Count: > 0 } ? $" AND s.[BranchCode] IN ({string.Join(",", branches.Select((_, i) => $"@sdrBranch{i}"))})" : "";
        var failedDateFilter = " AND NOT EXISTS (SELECT 1 FROM [SalesStatisticRefreshState] failedState WHERE failedState.[StatisticType]='ProductStoreDaily' AND failedState.[Status]='Failed' AND failedState.[Date]=s.[Date])";
        // SQLite 不支持 SQL Server 的 VALUES 派生表列定义语法；用 UNION ALL 的 SELECT
        // 同时覆盖 SQLite fallback 与异服务器 SQL Server fallback，避免把 POSM 映射搬回全量事实。
        var fallbackRows = fallbackMap is { Count: > 0 }
            ? string.Join(" UNION ALL ", fallbackMap.Select(item =>
                $"SELECT '{SqlLiteral(item.Key, sqlServer)}' [ProductCode], '{SqlLiteral(item.Value, sqlServer)}' [ChinaSupplierCode]"))
            : string.Empty;
        var mapping = posmDatabase == null && fallbackRows.Length == 0 ? "CAST(NULL AS nvarchar(50))" : "m.[ChinaSupplierCode]";
        var joinMapping = posmDatabase != null
            ? $"LEFT JOIN {QuoteIdentifier(posmDatabase)}.[dbo].[posm_product_supplier_mapping] m ON m.[ProductCode] = LTRIM(RTRIM(s.[ProductCode])) AND m.[LocalSupplierCode] = '200' AND m.[IsDeleted] = 0"
            : fallbackRows.Length > 0
                ? $"LEFT JOIN ({fallbackRows}) m ON m.[ProductCode] = LTRIM(RTRIM(s.[ProductCode]))"
                : "";
        var supplierFilter = BuildSalesDetailSupplierFilter(selectedSupplier, selectedSuppliers);
        var productFilter = string.IsNullOrWhiteSpace(selectedProduct) ? "" : " AND [ProductCode] = @sdrSelectedProduct";
        var selectedBranchFilter = string.IsNullOrWhiteSpace(selectedBranch)
            ? (branches is { Count: > 0 } ? $" AND [BranchCode] IN ({string.Join(",", branches.Select((_, i) => $"@sdrSelectedBranch{i}"))})" : "")
            : " AND [BranchCode] = @sdrSelectedBranch";
        var authorizedBranchFilter = branches is { Count: > 0 } ? $" AND [BranchCode] IN ({string.Join(",", branches.Select((_, i) => $"@sdrBranch{i}"))})" : "";
        var tokens = search?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>();
        var searchFilter = string.Join(" AND ", tokens.Select((_, i) => $"([ProductCode] LIKE @sdrSearch{i} OR [Barcode] LIKE @sdrSearch{i} OR [ProductName] LIKE @sdrSearch{i} OR [EnglishName] LIKE @sdrSearch{i} OR [RawSupplierCode] LIKE @sdrSearch{i} OR [SupplierCode] LIKE @sdrSearch{i} OR [SupplierName] LIKE @sdrSearch{i} OR [ItemNumber] LIKE @sdrSearch{i} OR EXISTS (SELECT 1 FROM [Product] pSearch WHERE pSearch.[ProductCode]=f.[ProductCode] AND pSearch.[LocalSupplierCode] LIKE @sdrSearch{i}) OR EXISTS (SELECT 1 FROM [ChinaSupplier] cSearch WHERE cSearch.[SupplierCode]=f.[ChinaSupplierCode] AND (cSearch.[SupplierCode] LIKE @sdrSearch{i} OR cSearch.[SupplierName] LIKE @sdrSearch{i})))"));
        if (searchFilter.Length > 0) searchFilter = " AND " + searchFilter;
        var table = sqlServer ? "[#SalesDetailFacts]" : "[SalesDetailFacts]";
        // SQL Server 支持 CTE 后接 SELECT INTO；SQLite 要求 CREATE TEMP TABLE AS 放在 WITH 之前。
        var materializePrefix = sqlServer ? string.Empty : "CREATE TEMP TABLE [SalesDetailFacts] AS ";
        var materialize = "SELECT";
        var into = sqlServer ? "INTO #SalesDetailFacts" : "";
        var cte = $"""
WITH Periods AS
(
 SELECT 0 [Period], @sdrCurrentStart [StartDate], @sdrCurrentEnd [EndDate]
 UNION ALL
 SELECT 1 [Period], @sdrCompareStart [StartDate], @sdrCompareEnd [EndDate] WHERE @sdrHasCompare=1
), SourceRows AS
(
 SELECT periods.[Period],
        LTRIM(RTRIM(COALESCE(s.[SupplierCode],''))) [RawSupplierCode],
        CASE WHEN LTRIM(RTRIM(COALESCE(s.[SupplierCode],'')))='200' THEN NULLIF(LTRIM(RTRIM({mapping})), '')
             WHEN cs.[SupplierCode] IS NOT NULL THEN LTRIM(RTRIM(s.[SupplierCode])) END [ChinaSupplierCode],
        CASE WHEN LTRIM(RTRIM(COALESCE(s.[SupplierCode],'')))='200' OR cs.[SupplierCode] IS NOT NULL THEN '200'
             ELSE NULLIF(LTRIM(RTRIM(s.[SupplierCode])), '') END [AustralianSupplierCode],
        LTRIM(RTRIM(COALESCE(s.[BranchCode],''))) [BranchCode], LTRIM(RTRIM(COALESCE(s.[ProductCode],''))) [ProductCode],
        s.[ProductName] [StatisticProductName], s.[Barcode], s.[TotalQuantity], s.[TotalAmount], s.[OrderCount], s.[GrossProfit], s.[TotalCost],
        p.[ProductName], p.[EnglishName], p.[ItemNumber], p.[ProductImage],
        CASE WHEN @sdrKind=1 THEN
                 CASE WHEN LTRIM(RTRIM(COALESCE(s.[SupplierCode],'')))='200' OR cs.[SupplierCode] IS NOT NULL THEN COALESCE(china.[SupplierName],'200')
                      ELSE COALESCE(local.[Name],LTRIM(RTRIM(COALESCE(s.[SupplierCode],'')))) END
             ELSE COALESCE(local.[Name],CASE WHEN LTRIM(RTRIM(COALESCE(s.[SupplierCode],'')))='200' OR cs.[SupplierCode] IS NOT NULL THEN '200' ELSE LTRIM(RTRIM(COALESCE(s.[SupplierCode],''))) END) END [SupplierName],
        COALESCE(store.[StoreName],LTRIM(RTRIM(COALESCE(s.[BranchCode],'')))) [BranchName]
 FROM [ProductStoreDailySalesStatistic] s
 CROSS JOIN Periods periods
 {joinMapping}
 LEFT JOIN (SELECT [SupplierCode], MAX([SupplierName]) [SupplierName] FROM [ChinaSupplier] WHERE [SupplierCode] IS NOT NULL AND [SupplierCode]<>'' GROUP BY [SupplierCode]) cs ON cs.[SupplierCode]=LTRIM(RTRIM(s.[SupplierCode]))
 LEFT JOIN (SELECT [SupplierCode], MAX([SupplierName]) [SupplierName] FROM [ChinaSupplier] WHERE [SupplierCode] IS NOT NULL AND [SupplierCode]<>'' GROUP BY [SupplierCode]) china ON china.[SupplierCode]=CASE WHEN LTRIM(RTRIM(COALESCE(s.[SupplierCode],'')))='200' THEN NULLIF(LTRIM(RTRIM({mapping})), '') ELSE LTRIM(RTRIM(s.[SupplierCode])) END
 LEFT JOIN [LocalSupplier] local ON local.[LocalSupplierCode]=CASE WHEN @sdrKind=0 AND (LTRIM(RTRIM(COALESCE(s.[SupplierCode],'')))='200' OR cs.[SupplierCode] IS NOT NULL) THEN '200' ELSE LTRIM(RTRIM(s.[SupplierCode])) END AND local.[IsDeleted]=0
 LEFT JOIN [Store] store ON store.[StoreCode]=LTRIM(RTRIM(s.[BranchCode]))
 LEFT JOIN [Product] p ON p.[ProductCode]=LTRIM(RTRIM(s.[ProductCode]))
 WHERE s.[Date]>=periods.[StartDate] AND s.[Date]<periods.[EndDate]{failedDateFilter}{sourceBranch}{categorySourceFilter}
), ResolvedRows AS
(
 SELECT *, COALESCE([ProductName],[StatisticProductName]) [ResolvedProductName]
 FROM SourceRows
)
{materialize} [Period], [RawSupplierCode], [ChinaSupplierCode], [AustralianSupplierCode], [BranchCode], [BranchName], [ProductCode], [Barcode], [ResolvedProductName] [ProductName], [EnglishName], [ItemNumber], [ProductImage], [SupplierName],
 SUM([TotalAmount]) [Revenue], SUM([TotalQuantity]) [Quantity], SUM([OrderCount]) [OrderCount], SUM([GrossProfit]) [GrossProfit], COUNT([ProductCode]) [StatisticRowCount], COUNT([TotalCost]) [CostedRowCount], COUNT([GrossProfit]) [GrossProfitRowCount]
 {into}
 FROM ResolvedRows
 GROUP BY [Period],[RawSupplierCode],[ChinaSupplierCode],[AustralianSupplierCode],[BranchCode],[BranchName],[ProductCode],[Barcode],[ResolvedProductName],[EnglishName],[ItemNumber],[ProductImage],[SupplierName];
""";
        var factProjection = "[Period], CASE WHEN @sdrKind=1 THEN [ChinaSupplierCode] ELSE [AustralianSupplierCode] END [SupplierCode], [RawSupplierCode], [ChinaSupplierCode], [AustralianSupplierCode], [BranchCode], [BranchName], [ProductCode], [Barcode], [ProductName], [EnglishName], [ItemNumber], [ProductImage], [SupplierName], [Revenue], [Quantity], [OrderCount], [GrossProfit], [StatisticRowCount], [CostedRowCount], [GrossProfitRowCount]";
        var baseSupplier = $"WHERE [SupplierCode] IS NOT NULL{selectedBranchFilter}{productFilter}";
        var baseBranch = $"WHERE [SupplierCode] IS NOT NULL{authorizedBranchFilter}{supplierFilter}{productFilter}";
        var baseProduct = $"WHERE [SupplierCode] IS NOT NULL{selectedBranchFilter}{supplierFilter}{searchFilter}";
        var baseSummary = $"WHERE [SupplierCode] IS NOT NULL{selectedBranchFilter}{supplierFilter}{productFilter}{searchFilter}";
        string rowSelect(string whereClause, string group, string code, string name, string order, string page = "") => $"""
SELECT {code} [Code], {name} [Name], MAX([ItemNumber]) [ItemNumber], MAX([ProductImage]) [ProductImage],
 COALESCE(SUM(CASE WHEN [Period]=0 THEN [Revenue] ELSE 0 END),0) [Revenue], {(hasCompare ? "COALESCE(SUM(CASE WHEN [Period]=1 THEN [Revenue] ELSE 0 END),0)" : "0")} [CompareRevenue],
 COALESCE(SUM(CASE WHEN [Period]=0 THEN [Quantity] ELSE 0 END),0) [Quantity], {(hasCompare ? "COALESCE(SUM(CASE WHEN [Period]=1 THEN [Quantity] ELSE 0 END),0)" : "0")} [CompareQuantity],
 COALESCE(SUM(CASE WHEN [Period]=0 THEN [OrderCount] ELSE 0 END),0) [OrderCount], {(hasCompare ? "COALESCE(SUM(CASE WHEN [Period]=1 THEN [OrderCount] ELSE 0 END),0)" : "0")} [CompareOrderCount],
 SUM(CASE WHEN [Period]=0 THEN [GrossProfit] END) [GrossProfit], {(hasCompare ? "SUM(CASE WHEN [Period]=1 THEN [GrossProfit] END)" : "CAST(NULL AS decimal(18,2))")} [CompareGrossProfit],
 SUM(CASE WHEN [Period]=0 THEN [StatisticRowCount] ELSE 0 END) [StatisticRowCount], SUM(CASE WHEN [Period]=0 THEN [CostedRowCount] ELSE 0 END) [CostedRowCount], SUM(CASE WHEN [Period]=0 THEN [GrossProfitRowCount] ELSE 0 END) [GrossProfitRowCount],
 {(hasCompare ? "SUM(CASE WHEN [Period]=1 THEN [StatisticRowCount] ELSE 0 END)" : "0")} [CompareStatisticRowCount], {(hasCompare ? "SUM(CASE WHEN [Period]=1 THEN [CostedRowCount] ELSE 0 END)" : "0")} [CompareCostedRowCount], {(hasCompare ? "SUM(CASE WHEN [Period]=1 THEN [GrossProfitRowCount] ELSE 0 END)" : "0")} [CompareGrossProfitRowCount]
 ,COUNT(DISTINCT CASE WHEN [Period]=0 THEN [ProductCode] END) [CurrentProductCount], {(hasCompare ? "COUNT(DISTINCT CASE WHEN [Period]=1 THEN [ProductCode] END)" : "0")} [CompareProductCount]
FROM (SELECT {factProjection} FROM {table}) f {whereClause}
{(string.IsNullOrWhiteSpace(group) ? "" : $"GROUP BY {group}")} {order} {page};
""";
        var summary = rowSelect(baseSummary, "", "'summary'", "'当前筛选汇总'", "");
        var suppliers = rowSelect(baseSupplier, "[SupplierCode]", "[SupplierCode]", "MAX([SupplierName])", "ORDER BY [Revenue] DESC, [CompareRevenue] DESC, [Code] ASC");
        var branchesSql = rowSelect(baseBranch, "[BranchCode]", "[BranchCode]", "MAX([BranchName])", "ORDER BY [Revenue] DESC, [CompareRevenue] DESC, [Code] ASC");
        var offset = ((long)pageIndex - 1L) * pageSize;
        var paging = sqlServer ? $"OFFSET {offset} ROWS FETCH NEXT {pageSize} ROWS ONLY" : $"LIMIT {pageSize} OFFSET {offset}";
        var products = rowSelect(baseProduct, "[ProductCode]", "[ProductCode]", "MAX([ProductName])", $"ORDER BY [Quantity] DESC, [CompareQuantity] DESC, [Code] ASC", paging);
        var emptyRows = $"SELECT [ProductCode] [Code], [ProductName] [Name], [ItemNumber], [ProductImage], [Revenue], [Revenue] [CompareRevenue], [Quantity], [Quantity] [CompareQuantity], [OrderCount], [OrderCount] [CompareOrderCount], [GrossProfit], [GrossProfit] [CompareGrossProfit], [StatisticRowCount], [CostedRowCount], [GrossProfitRowCount], [StatisticRowCount] [CompareStatisticRowCount], [CostedRowCount] [CompareCostedRowCount], [GrossProfitRowCount] [CompareGrossProfitRowCount], 0 [CurrentProductCount], 0 [CompareProductCount] FROM {table} WHERE 1=0;";
        var emptyCount = "SELECT 0;";
        var emptyDenominator = "SELECT 0,0,0,0;";
        var productCount = $"SELECT COUNT(*) FROM (SELECT [ProductCode] FROM (SELECT {factProjection} FROM {table}) f {baseProduct} GROUP BY [ProductCode]) x;";
        var denominator = $"SELECT COALESCE(SUM(CASE WHEN [Period]=0 AND [AustralianSupplierCode] IS NOT NULL THEN [Revenue] ELSE 0 END),0), COALESCE(SUM(CASE WHEN [Period]=0 AND [ChinaSupplierCode] IS NOT NULL THEN [Revenue] ELSE 0 END),0), COALESCE(SUM(CASE WHEN [Period]=1 AND [AustralianSupplierCode] IS NOT NULL THEN [Revenue] ELSE 0 END),0), COALESCE(SUM(CASE WHEN [Period]=1 AND [ChinaSupplierCode] IS NOT NULL THEN [Revenue] ELSE 0 END),0) FROM {table} WHERE [AustralianSupplierCode] IS NOT NULL{selectedBranchFilter};";
        if (!wanted.Contains(SalesDetailSection.Summary)) summary = emptyRows;
        if (!wanted.Contains(SalesDetailSection.Suppliers)) suppliers = emptyRows;
        if (!wanted.Contains(SalesDetailSection.Branches)) branchesSql = emptyRows;
        if (!wanted.Contains(SalesDetailSection.Products)) { products = emptyRows; productCount = emptyCount; }
        if (!wanted.Contains(SalesDetailSection.Suppliers)) denominator = emptyDenominator;
        var status = "SELECT [StatisticType],[Date],[Status],[LastAggregatedAtUtc],[CompletedAtUtc],[SourceProductVersion] FROM [SalesStatisticRefreshState] WHERE ([Date]>=@sdrCurrentStart AND [Date]<@sdrCurrentEnd) OR (@sdrHasCompare=1 AND [Date]>=@sdrCompareStart AND [Date]<@sdrCompareEnd) ORDER BY [Date],[StatisticType];";
        return materializePrefix + cte + status + summary + suppliers + branchesSql + products + productCount + denominator + (sqlServer ? "DROP TABLE #SalesDetailFacts;" : "DROP TABLE [SalesDetailFacts];");
    }

    private static string BuildSalesDetailSupplierFilter(string? legacySupplier, IReadOnlyCollection<string>? selectedSuppliers)
    {
        var codes = (selectedSuppliers ?? Array.Empty<string>()).Where(code => !string.IsNullOrWhiteSpace(code)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!string.IsNullOrWhiteSpace(legacySupplier) && !codes.Contains(legacySupplier.Trim(), StringComparer.OrdinalIgnoreCase)) codes.Add(legacySupplier.Trim());
        return codes.Count switch
        {
            0 => string.Empty,
            1 => " AND [SupplierCode] = @sdrSelectedSupplier",
            _ => $" AND [SupplierCode] IN ({string.Join(",", codes.Select((_, index) => $"@sdrSelectedSupplier{index}"))})",
        };
    }

    /// <summary>
    /// 分类商品事实预取：候选商品码按与 <see cref="BuildSalesDetailCategoryFilter"/> 相同的分类规则取超集
    /// （国内标签只看仓库分类，澳洲标签是供应商分类并上仓库分类），再按 (ProductCode, Date) 索引读事实。
    /// 精确的分类判定（含原始供应商条件）只取决于 (原始供应商, 商品)：在不同的键上各判一次，删掉不符合的行，
    /// 预取表即与"扫描事实表 + 逐行判定"得到的事实完全相同。
    /// </summary>
    private static string BuildSalesDetailCategoryPrefetchSql(SalesDetailKind kind,
        IReadOnlyCollection<string>? supplierCategoryGuids, IReadOnlyCollection<string>? warehouseCategoryGuids, string sourceBranch,
        string categorySourceFilter)
    {
        var candidates = new List<string>();
        if (kind != SalesDetailKind.China && supplierCategoryGuids is { Count: > 0 })
            candidates.Add($"SELECT categoryProduct.[ProductCode] FROM [Product] categoryProduct INNER JOIN [LocalSupplierCategoryProductAssignment] categoryAssignment ON categoryAssignment.[ProductCode]=categoryProduct.[ProductCode] AND categoryAssignment.[LocalSupplierCode]=categoryProduct.[LocalSupplierCode] WHERE categoryProduct.[IsDeleted]=0 AND categoryAssignment.[CategoryGUID] IN ({string.Join(",", supplierCategoryGuids.Select((_, index) => $"@sdrCategorySupplier{index}"))})");
        if (warehouseCategoryGuids is { Count: > 0 })
            candidates.Add($"SELECT categoryProduct.[ProductCode] FROM [Product] categoryProduct WHERE categoryProduct.[IsDeleted]=0 AND (categoryProduct.[LocalSupplierCode]='200' OR categoryProduct.[LocalSupplierCode] IS NULL OR categoryProduct.[LocalSupplierCode]='') AND categoryProduct.[WarehouseCategoryGUID] IN ({string.Join(",", warehouseCategoryGuids.Select((_, index) => $"@sdrCategoryWarehouse{index}"))})");
        var candidateSql = candidates.Count == 0 ? "SELECT CAST(NULL AS nvarchar(50)) [ProductCode] WHERE 1=0" : string.Join(" UNION ", candidates);
        const string columns = "s.[Date],s.[SupplierCode],s.[BranchCode],s.[ProductCode],s.[ProductName],s.[Barcode],s.[TotalQuantity],s.[TotalAmount],s.[OrderCount],s.[GrossProfit],s.[TotalCost]";
        const string dates = "((s.[Date]>=@sdrCurrentStart AND s.[Date]<@sdrCurrentEnd) OR (@sdrHasCompare=1 AND s.[Date]>=@sdrCompareStart AND s.[Date]<@sdrCompareEnd))";
        return $"""
SELECT DISTINCT c.[ProductCode] INTO #SalesDetailCategoryProducts FROM ({candidateSql}) c WHERE c.[ProductCode] IS NOT NULL;
SELECT TOP (0) {columns} INTO #SalesDetailCategorySource FROM [ProductStoreDailySalesStatistic] s;
-- 常见分类几千个商品：逐商品按 (ProductCode, Date) 索引查找（生产 2391 个商品约 0.7 秒）；极大的分类交给优化器选择扫描方式。
IF (SELECT COUNT_BIG(*) FROM #SalesDetailCategoryProducts)<=20000
 INSERT INTO #SalesDetailCategorySource
 SELECT {columns} FROM #SalesDetailCategoryProducts p
 INNER LOOP JOIN [ProductStoreDailySalesStatistic] s ON s.[ProductCode]=p.[ProductCode]
 WHERE {dates}{sourceBranch};
ELSE
 INSERT INTO #SalesDetailCategorySource
 SELECT {columns} FROM [ProductStoreDailySalesStatistic] s
 JOIN #SalesDetailCategoryProducts p ON p.[ProductCode]=s.[ProductCode]
 WHERE {dates}{sourceBranch};
DROP TABLE #SalesDetailCategoryProducts;
SELECT s.[SupplierCode], s.[ProductCode] INTO #SalesDetailCategoryKeys
FROM (SELECT DISTINCT [SupplierCode], [ProductCode] FROM #SalesDetailCategorySource) s
WHERE 1=1{categorySourceFilter};
DELETE s FROM #SalesDetailCategorySource s
WHERE NOT EXISTS (SELECT 1 FROM #SalesDetailCategoryKeys k WHERE k.[SupplierCode]=s.[SupplierCode] AND k.[ProductCode]=s.[ProductCode]);
DROP TABLE #SalesDetailCategoryKeys;
""";
    }

    private static string BuildSalesDetailCategoryFilter(
        SalesDetailKind kind,
        IReadOnlyCollection<string>? supplierCategoryGuids,
        IReadOnlyCollection<string>? warehouseCategoryGuids,
        string productExpression)
    {
        var supplierParameters = supplierCategoryGuids is { Count: > 0 }
            ? string.Join(",", supplierCategoryGuids.Select((_, index) => $"@sdrCategorySupplier{index}")) : string.Empty;
        var warehouseParameters = warehouseCategoryGuids is { Count: > 0 }
            ? string.Join(",", warehouseCategoryGuids.Select((_, index) => $"@sdrCategoryWarehouse{index}")) : string.Empty;
        if (kind == SalesDetailKind.China)
            return warehouseParameters.Length == 0 ? string.Empty
                : $" AND EXISTS (SELECT 1 FROM [Product] categoryProduct WHERE categoryProduct.[ProductCode]=LTRIM(RTRIM({productExpression})) AND categoryProduct.[IsDeleted]=0 AND (categoryProduct.[LocalSupplierCode]='200' OR categoryProduct.[LocalSupplierCode] IS NULL OR categoryProduct.[LocalSupplierCode]='') AND categoryProduct.[WarehouseCategoryGUID] IN ({warehouseParameters}))";

        // 澳洲标签中的 200 使用仓库分类树；其他供应商按商品当前归属匹配各自的网站分类。
        var filters = new List<string>();
        if (supplierParameters.Length > 0)
            filters.Add($"EXISTS (SELECT 1 FROM [Product] categoryProduct INNER JOIN [LocalSupplierCategoryProductAssignment] categoryAssignment ON categoryAssignment.[ProductCode]=categoryProduct.[ProductCode] AND categoryAssignment.[LocalSupplierCode]=categoryProduct.[LocalSupplierCode] WHERE categoryProduct.[ProductCode]=LTRIM(RTRIM({productExpression})) AND categoryProduct.[IsDeleted]=0 AND categoryAssignment.[LocalSupplierCode]=LTRIM(RTRIM(s.[SupplierCode])) AND categoryAssignment.[CategoryGUID] IN ({supplierParameters}))");
        if (warehouseParameters.Length > 0)
            filters.Add($"((LTRIM(RTRIM(s.[SupplierCode]))='200' OR EXISTS (SELECT 1 FROM [ChinaSupplier] categoryChina WHERE categoryChina.[SupplierCode]=LTRIM(RTRIM(s.[SupplierCode])))) AND EXISTS (SELECT 1 FROM [Product] categoryProduct WHERE categoryProduct.[ProductCode]=LTRIM(RTRIM({productExpression})) AND categoryProduct.[IsDeleted]=0 AND (categoryProduct.[LocalSupplierCode]='200' OR categoryProduct.[LocalSupplierCode] IS NULL OR categoryProduct.[LocalSupplierCode]='') AND categoryProduct.[WarehouseCategoryGUID] IN ({warehouseParameters})))");
        return filters.Count == 0 ? string.Empty : " AND (" + string.Join(" OR ", filters) + ")";
    }

    private bool TryGetSameServerPosmDatabase(out string database)
    {
        database = string.Empty;
        if (_context.Db.CurrentConnectionConfig.DbType != SqlSugar.DbType.SqlServer || _posmContext.Db.CurrentConnectionConfig.DbType != SqlSugar.DbType.SqlServer) return false;
        try
        {
            var local = new SqlConnectionStringBuilder(_context.Db.CurrentConnectionConfig.ConnectionString);
            var posm = new SqlConnectionStringBuilder(_posmContext.Db.CurrentConnectionConfig.ConnectionString);
            if (!string.Equals(local.DataSource, posm.DataSource, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(posm.InitialCatalog)) return false;
            database = posm.InitialCatalog; return true;
        }
        catch { return false; }
    }

    private static string QuoteIdentifier(string value) => "[" + value.Replace("]", "]]", StringComparison.Ordinal) + "]";
    private static string SqlLiteral(string value, bool sqlServer) => value.Replace("'", "''", StringComparison.Ordinal);

    private static ProductReportStatisticStatusDto BuildSalesDetailReportStatus(IEnumerable<SalesDetailReportStatusSqlRow> rows, DateRangeDto range, bool useSupplierRollups,
        bool skipFailedDates = false, IReadOnlyCollection<DateTime>? projectionSkippedDates = null)
    {
        var dates = EnumerateSalesDetailDates(range).ToList();
        var required = useSupplierRollups ? new[] { SalesStatisticType.ProductStoreDaily, SalesStatisticType.AustralianSupplierStoreSales, SalesStatisticType.ChinaSupplierStoreSales } : new[] { SalesStatisticType.ProductStoreDaily };
        var states = rows.Where(row => dates.Contains(row.Date.Date) && required.Contains(row.Type, StringComparer.OrdinalIgnoreCase)).ToList();
        var source = string.Join("|", states.OrderBy(row => row.Date).ThenBy(row => row.Type).Select(row => $"{row.Type}:{row.Date:yyyyMMdd}:{row.Status}:{row.LastAggregatedAtUtc?.Ticks}:{row.CompletedAtUtc?.Ticks}:{row.SourceProductVersion}"));
        var result = new ProductReportStatisticStatusDto { CacheVersion = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))), StatisticUpdatedAt = useSupplierRollups ? states.Select(row => row.CompletedAtUtc ?? row.LastAggregatedAtUtc).DefaultIfEmpty().Min() : states.Select(row => row.CompletedAtUtc ?? row.LastAggregatedAtUtc).DefaultIfEmpty().Max(), StatisticStatus = SalesStatisticRefreshStatus.Pending, StatisticMessage = "商品统计尚未准备完成。" };
        if (states.Count == 0) return result;
        // 其他调用方保留原有已聚合失败日口径；销售明细通过 skipFailedDates 单独排除失败日。
        bool IsFailedButAggregated(SalesDetailReportStatusSqlRow row) =>
            row.Status.Equals(SalesStatisticRefreshStatus.Failed, StringComparison.OrdinalIgnoreCase)
            && row.LastAggregatedAtUtc.HasValue
            && row.Type.Equals(SalesStatisticType.ProductStoreDaily, StringComparison.OrdinalIgnoreCase);
        var skippedFailedDates = new List<DateTime>();
        if (!skipFailedDates && states.Any(row => row.Status.Equals(SalesStatisticRefreshStatus.Failed, StringComparison.OrdinalIgnoreCase) && !IsFailedButAggregated(row))) { result.StatisticStatus = SalesStatisticRefreshStatus.Failed; result.StatisticMessage = "统计更新失败，等待后台恢复。"; return result; }
        if (states.Any(row => row.Status.Equals(SalesStatisticRefreshStatus.Stale, StringComparison.OrdinalIgnoreCase))) { result.StatisticStatus = SalesStatisticRefreshStatus.Stale; result.StatisticMessage = "商品统计等待更新。"; return result; }
        var reconciliationFailedDates = new List<DateTime>();
        // 生产上 2025-05-04～05-31 从未生成商品日统计（事实表无行、无状态行），早于最新状态的这类历史缺口
        // 只提示、不阻断；排在最新状态之后又没有状态的日期（尚未排队的今天）仍按未发布处理。
        var latestTrackedDate = states
            .Where(row => row.Type.Equals(SalesStatisticType.ProductStoreDaily, StringComparison.OrdinalIgnoreCase))
            .Select(row => (DateTime?)row.Date.Date).DefaultIfEmpty().Max();
        var missingHistoryDates = new List<DateTime>();
        foreach (var date in dates)
        {
            var product = states.SingleOrDefault(row => row.Date.Date == date && row.Type.Equals(SalesStatisticType.ProductStoreDaily, StringComparison.OrdinalIgnoreCase));
            if (skipFailedDates && product?.Status.Equals(SalesStatisticRefreshStatus.Failed, StringComparison.OrdinalIgnoreCase) == true)
            {
                skippedFailedDates.Add(date);
                continue;
            }
            if (product == null && !useSupplierRollups && latestTrackedDate.HasValue && date < latestTrackedDate.Value)
            {
                missingHistoryDates.Add(date);
                continue;
            }
            if (product == null || !product.LastAggregatedAtUtc.HasValue) return result;
            if (!useSupplierRollups)
            {
                if (IsFailedButAggregated(product)) { reconciliationFailedDates.Add(date); continue; }
                // 日统计在一个事务内整体替换；刷新排队或运行期间仍可读到上一版已发布快照。
                if (product.Status is not (SalesStatisticRefreshStatus.Fresh or SalesStatisticRefreshStatus.Queued or SalesStatisticRefreshStatus.Running)) return result;
                // 业务校验失败也会留下 LastAggregatedAtUtc；只有成功发布的版本号才能证明旧事实可用。
                if (product.Status != SalesStatisticRefreshStatus.Fresh && string.IsNullOrWhiteSpace(product.SourceProductVersion)) return result;
                continue;
            }
            if (IsFailedButAggregated(product)) { result.StatisticStatus = SalesStatisticRefreshStatus.Failed; result.StatisticMessage = "统计更新失败，等待后台恢复。"; return result; }
            if (product.Status is not (SalesStatisticRefreshStatus.Fresh or SalesStatisticRefreshStatus.Queued or SalesStatisticRefreshStatus.Running) || string.IsNullOrWhiteSpace(product.SourceProductVersion)) return result;
            foreach (var type in new[] { SalesStatisticType.AustralianSupplierStoreSales, SalesStatisticType.ChinaSupplierStoreSales })
            {
                var supplier = states.SingleOrDefault(row => row.Date.Date == date && row.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
                if (supplier == null || !supplier.Status.Equals(SalesStatisticRefreshStatus.Fresh, StringComparison.OrdinalIgnoreCase) || !supplier.CompletedAtUtc.HasValue || !supplier.LastAggregatedAtUtc.HasValue || supplier.SourceProductVersion != product.SourceProductVersion) return result;
            }
        }
        result.StatisticStatus = SalesStatisticRefreshStatus.Fresh;
        // 关键词查询因投影缺失跳过的日期不计入结果（失败日已由 skipFailedDates 单独提示），不再重复提示其对账或缺统计情况。
        var skipped = (projectionSkippedDates ?? Array.Empty<DateTime>()).Select(date => date.Date).ToHashSet();
        reconciliationFailedDates.RemoveAll(date => skipped.Contains(date.Date));
        missingHistoryDates.RemoveAll(date => skipped.Contains(date.Date));
        result.StatisticMessage = string.Join(" ", new[] { DescribeSkippedFailedDates(skippedFailedDates), DescribeProjectionSkippedDates(skipped), DescribeReconciliationFailedDates(reconciliationFailedDates), DescribeMissingHistoryDates(missingHistoryDates) }
            .Where(message => !string.IsNullOrWhiteSpace(message))) is { Length: > 0 } message ? message : null;
        return result;
    }

    /// <summary>销售明细跳过统计失败日，并明确提示该日金额未计入结果。</summary>
    internal static string? DescribeSkippedFailedDates(IReadOnlyCollection<DateTime> dates)
    {
        if (dates.Count == 0) return null;
        var ordered = dates.Select(date => date.Date).Distinct().OrderBy(date => date).ToList();
        var shown = string.Join("、", ordered.Take(5).Select(date => date.ToString("yyyy-MM-dd")));
        var suffix = ordered.Count > 5 ? $" 等 {ordered.Count} 天" : string.Empty;
        return $"{shown}{suffix}的商品统计失败，已跳过，该日金额未计入当前结果。";
    }

    /// <summary>从未生成商品日统计的历史日期：按连续区间合并后列出，提醒该段金额缺失、需另行回填统计。</summary>
    internal static string? DescribeMissingHistoryDates(IReadOnlyCollection<DateTime> dates)
        => dates.Count == 0 ? null : $"{DescribeDateRanges(dates)}没有商品日统计，该段金额未计入，需另行回填统计。";

    /// <summary>关键词查询缺少可用日投影而跳过的日期：按连续区间列出，说明结果未包含这些日期。</summary>
    internal static string? DescribeProjectionSkippedDates(IReadOnlyCollection<DateTime> dates)
        => dates.Count == 0 ? null : $"{DescribeDateRanges(dates)}缺少可用的关键词查询投影，本次搜索已跳过，结果未包含这些日期的销售。";

    /// <summary>日期去重后合并为连续区间，最多列出前三段，形如"2026-05-04～2026-05-06、2026-05-10共 4 天"。</summary>
    private static string DescribeDateRanges(IReadOnlyCollection<DateTime> dates)
    {
        var ordered = dates.Select(date => date.Date).Distinct().OrderBy(date => date).ToList();
        var ranges = new List<string>();
        var start = ordered[0]; var end = ordered[0];
        foreach (var date in ordered.Skip(1))
        {
            if (date == end.AddDays(1)) { end = date; continue; }
            ranges.Add(start == end ? start.ToString("yyyy-MM-dd") : $"{start:yyyy-MM-dd}～{end:yyyy-MM-dd}");
            start = end = date;
        }
        ranges.Add(start == end ? start.ToString("yyyy-MM-dd") : $"{start:yyyy-MM-dd}～{end:yyyy-MM-dd}");
        var shown = string.Join("、", ranges.Take(3));
        var suffix = ranges.Count > 3 ? $" 等 {ranges.Count} 段" : string.Empty;
        return $"{shown}{suffix}共 {ordered.Count} 天";
    }

    /// <summary>已聚合但对账未通过的日期只提示、不阻断；日期去重后按时间列出，过多时只列前几天。</summary>
    internal static string? DescribeReconciliationFailedDates(IReadOnlyCollection<DateTime> dates)
    {
        if (dates.Count == 0) return null;
        var ordered = dates.Select(date => date.Date).Distinct().OrderBy(date => date).ToList();
        var shown = string.Join("、", ordered.Take(5).Select(date => date.ToString("yyyy-MM-dd")));
        var suffix = ordered.Count > 5 ? $" 等 {ordered.Count} 天" : string.Empty;
        return $"{shown}{suffix}的商品统计与分店营业额对账未通过，该日金额可能与收银记录有细微出入。";
    }

    private static IEnumerable<DateTime> EnumerateSalesDetailDates(DateRangeDto range)
    {
        for (var date = range.StartDate.Date; date <= range.EndDate.Date; date = date.AddDays(1)) yield return date;
        if (HasCompare(range)) for (var date = range.CompareStartDate!.Value.Date; date <= range.CompareEndDate!.Value.Date; date = date.AddDays(1)) yield return date;
    }

    private static ProductReportResponseDto<SalesDetailReportDto> EmptySalesDetailReport(string message) => new() { StatisticStatus = SalesStatisticRefreshStatus.Fresh, StatisticMessage = message, CacheVersion = "no-access", Data = new SalesDetailReportDto() };
    private static string S(DbDataReader r, int i) => r.IsDBNull(i) ? string.Empty : Convert.ToString(r.GetValue(i)) ?? string.Empty;
    private static string? NS(DbDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i));
    private static DateTime D(DbDataReader r, int i) => Convert.ToDateTime(r.GetValue(i));
    private static DateTime? ND(DbDataReader r, int i) => r.IsDBNull(i)
        ? null
        : DateTime.SpecifyKind(Convert.ToDateTime(r.GetValue(i)), DateTimeKind.Utc);
    private static decimal M(DbDataReader r, int i) => r.IsDBNull(i) ? 0m : Convert.ToDecimal(r.GetValue(i));
    private static decimal? NM(DbDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToDecimal(r.GetValue(i));
    private static int I(DbDataReader r, int i) => r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i));
}
