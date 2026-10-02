using System.Data;
using System.Data.Common;
using System.Diagnostics;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.SqlClient;
using DbType = System.Data.DbType;

namespace BlazorApp.Api.Services.React;

/// <summary>
/// 销售明细「澳洲供应商分类」页签：所选澳洲供应商按各自分类树汇总本期/同期，并可读取某个分类节点下的商品分页。
/// 口径与销售明细澳洲页签的分类筛选一致：供应商 200 合并原始码 200 与国内供应商码、使用商品的仓库分类；
/// 其他供应商要求「分类归属的供应商 = 商品当前供应商 = 事实原始供应商」，否则视为未归类。
/// </summary>
public partial class SalesDashboardReactService
{
    internal const int MaxCategoryReportSuppliers = 100;
    /// <summary>与 <see cref="ReadSectionRow"/> 第 5 列起的读取顺序一致。</summary>
    private static readonly string[] MetricColumns =
    {
        "Revenue", "CompareRevenue", "Quantity", "CompareQuantity", "OrderCount", "CompareOrderCount", "GrossProfit", "CompareGrossProfit",
        "StatisticRowCount", "CostedRowCount", "GrossProfitRowCount", "CompareStatisticRowCount", "CompareCostedRowCount",
        "CompareGrossProfitRowCount", "CurrentProductCount", "CompareProductCount",
    };
    private const string HotBargainSupplierCode = "200";

    private sealed class SalesDetailCategoryReportRead
    {
        public List<SalesDetailReportStatusSqlRow> Status { get; } = new();
        /// <summary>Code = 供应商，Name = 叶分类 GUID（大写；未归类为空串）。</summary>
        public List<SalesDetailReportSqlRow> Categories { get; } = new();
        public List<SalesDetailReportSqlRow> Products { get; } = new();
        public int ProductTotal { get; set; }
    }

    private sealed class SalesDetailCategoryTreeNode
    {
        public string Guid { get; init; } = string.Empty;
        public string? ParentGuid { get; init; }
        public string Name { get; init; } = string.Empty;
        public bool IsActive { get; init; }
        public int? SortOrder { get; init; }
        public SalesDetailCategoryTreeNode? Parent { get; set; }
        public List<SalesDetailCategoryTreeNode> Children { get; } = new();
    }

    private sealed class SalesDetailCategoryTree
    {
        public Dictionary<string, SalesDetailCategoryTreeNode> ByGuid { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<SalesDetailCategoryTreeNode> Roots { get; } = new();
    }

    /// <summary>未补全毛利前的累加值；毛利完整性由行数判断，因此行数与金额一起向上累加。</summary>
    private sealed class SalesDetailCategoryTotals
    {
        public decimal Revenue, CompareRevenue, GrossProfit, CompareGrossProfit;
        public int Quantity, CompareQuantity, ProductCount, CompareProductCount;
        public int StatisticRowCount, CostedRowCount, GrossProfitRowCount;
        public int CompareStatisticRowCount, CompareCostedRowCount, CompareGrossProfitRowCount;
        public bool HasRows => StatisticRowCount > 0 || CompareStatisticRowCount > 0;

        public void Add(SalesDetailReportSqlRow row)
        {
            Revenue += row.Revenue; CompareRevenue += row.CompareRevenue;
            GrossProfit += row.GrossProfit ?? 0m; CompareGrossProfit += row.CompareGrossProfit ?? 0m;
            Quantity += row.Quantity; CompareQuantity += row.CompareQuantity;
            ProductCount += row.CurrentProductCount; CompareProductCount += row.CompareProductCount;
            StatisticRowCount += row.StatisticRowCount; CostedRowCount += row.CostedRowCount; GrossProfitRowCount += row.GrossProfitRowCount;
            CompareStatisticRowCount += row.CompareStatisticRowCount; CompareCostedRowCount += row.CompareCostedRowCount;
            CompareGrossProfitRowCount += row.CompareGrossProfitRowCount;
        }

        public void Add(SalesDetailCategoryTotals other)
        {
            Revenue += other.Revenue; CompareRevenue += other.CompareRevenue;
            GrossProfit += other.GrossProfit; CompareGrossProfit += other.CompareGrossProfit;
            Quantity += other.Quantity; CompareQuantity += other.CompareQuantity;
            ProductCount += other.ProductCount; CompareProductCount += other.CompareProductCount;
            StatisticRowCount += other.StatisticRowCount; CostedRowCount += other.CostedRowCount; GrossProfitRowCount += other.GrossProfitRowCount;
            CompareStatisticRowCount += other.CompareStatisticRowCount; CompareCostedRowCount += other.CompareCostedRowCount;
            CompareGrossProfitRowCount += other.CompareGrossProfitRowCount;
        }

        public T Fill<T>(T dto, bool compare) where T : SalesDetailCategoryMetricsDto
        {
            var gross = CompleteGrossProfit(GrossProfit, StatisticRowCount, CostedRowCount, GrossProfitRowCount);
            var compareGross = compare ? CompleteGrossProfit(CompareGrossProfit, CompareStatisticRowCount, CompareCostedRowCount, CompareGrossProfitRowCount) : null;
            dto.Revenue = Revenue; dto.CompareRevenue = compare ? CompareRevenue : null;
            dto.Quantity = Quantity; dto.CompareQuantity = compare ? CompareQuantity : null;
            dto.GrossProfit = gross; dto.CompareGrossProfit = compareGross;
            dto.GrossMarginRate = CalculateGrossMarginRate(Revenue, gross);
            dto.CompareGrossMarginRate = compare ? CalculateGrossMarginRate(CompareRevenue, compareGross) : null;
            dto.ProductCount = ProductCount; dto.CompareProductCount = compare ? CompareProductCount : null;
            return dto;
        }
    }

    public async Task<ProductReportResponseDto<SalesDetailCategoryReportDto>> GetSalesDetailCategoryReportAsync(
        DateRangeDto dateRange,
        IReadOnlyCollection<string> supplierCodes,
        List<string>? branchCodes = null,
        string? selectedBranchCode = null,
        string? nodeSupplierCode = null,
        string? nodeCategoryGuid = null,
        string? search = null,
        int pageIndex = 1,
        int pageSize = 20,
        bool includeTree = true,
        CancellationToken cancellationToken = default)
    {
        ValidateDateRange(dateRange);
        if (pageIndex < 1) throw new ArgumentException("pageIndex 必须大于 0", nameof(pageIndex));
        pageSize = Math.Clamp(pageSize, 1, 500);
        var suppliers = (supplierCodes ?? Array.Empty<string>()).Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (suppliers.Count == 0) throw new ArgumentException("请至少选择一个供应商", nameof(supplierCodes));
        if (suppliers.Count > MaxCategoryReportSuppliers)
            throw new ArgumentException($"供应商最多选择 {MaxCategoryReportSuppliers} 项，当前为 {suppliers.Count} 项", nameof(supplierCodes));
        var branches = branchCodes?.Where(code => !string.IsNullOrWhiteSpace(code)).Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (branches is { Count: 0 })
            return new() { StatisticStatus = SalesStatisticRefreshStatus.Fresh, StatisticMessage = "当前账号没有可访问的分店范围", CacheVersion = "no-access", Data = new() };

        var nodeSupplier = string.IsNullOrWhiteSpace(nodeSupplierCode) ? null
            : suppliers.FirstOrDefault(code => code.Equals(nodeSupplierCode.Trim(), StringComparison.OrdinalIgnoreCase))
              ?? throw new ArgumentException("所选节点的供应商不在当前供应商范围内", nameof(nodeSupplierCode));
        var nodeGuid = string.IsNullOrWhiteSpace(nodeCategoryGuid) ? null : nodeCategoryGuid.Trim();
        if (nodeGuid != null && nodeSupplier == null)
            throw new ArgumentException("指定分类时必须同时指定供应商", nameof(nodeSupplierCode));

        var trees = await LoadSalesDetailCategoryTreesAsync(suppliers, cancellationToken);
        // 节点筛选在 C# 里展开成叶分类集合：普通节点取自身与全部子孙；未归类取「不在该供应商分类树里」。
        var nodeGuids = new List<string>();
        var unassignedNode = false;
        if (nodeSupplier != null && nodeGuid != null)
        {
            var tree = trees[nodeSupplier];
            if (nodeGuid.Equals(SalesDetailCategorySources.UnassignedKey, StringComparison.Ordinal))
            {
                unassignedNode = true;
                nodeGuids.AddRange(tree.ByGuid.Keys);
            }
            else if (tree.ByGuid.TryGetValue(nodeGuid, out var node))
                nodeGuids.AddRange(EnumerateSubtree(node).Select(item => item.Guid));
            else
                throw new ArgumentException("所选分类不存在或已删除", nameof(nodeCategoryGuid));
        }
        var tokens = search?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>();
        if (12 + suppliers.Count + (branches?.Count ?? 0) + nodeGuids.Count + tokens.Length > MaxSqlParameterBudget)
            throw new ArgumentException("筛选条件过多，请减少供应商、分店或关键词后重试");
        cancellationToken.ThrowIfCancellationRequested();

        var sqlServer = _context.Db.CurrentConnectionConfig.DbType == SqlSugar.DbType.SqlServer;
        var sql = BuildSalesDetailCategoryReportSql(sqlServer, dateRange, suppliers, branches, selectedBranchCode,
            includeTree, nodeSupplier, nodeGuid == null ? null : nodeGuids, unassignedNode, tokens, pageIndex, pageSize);
        var read = await ReadSalesDetailCategoryReportAsync(sql, dateRange, suppliers, branches, selectedBranchCode,
            nodeSupplier, nodeGuids, tokens, includeTree, nodeSupplier != null, cancellationToken);

        var status = BuildSalesDetailReportStatus(read.Status, dateRange, false, skipFailedDates: true);
        var response = new ProductReportResponseDto<SalesDetailCategoryReportDto>
        {
            StatisticStatus = status.StatisticStatus, StatisticMessage = status.StatisticMessage,
            StatisticUpdatedAt = status.StatisticUpdatedAt, CacheVersion = status.CacheVersion,
            Data = new SalesDetailCategoryReportDto(),
        };
        if (!status.StatisticStatus.Equals(SalesStatisticRefreshStatus.Fresh, StringComparison.OrdinalIgnoreCase))
            return response;
        var compare = HasCompare(dateRange);
        if (includeTree)
        {
            var names = await LoadLocalSupplierNamesAsync(suppliers, cancellationToken);
            BuildSalesDetailCategoryTree(response.Data!, read.Categories, suppliers, trees, names, compare);
        }
        if (nodeSupplier != null)
        {
            var rows = read.Products.Select(row => ToRow(row, SalesDetailSection.Products, SalesDetailKind.Australia, null, compare)).ToList();
            response.Data!.Products = new SalesDetailSectionResultDto
            {
                Rows = rows, Total = read.ProductTotal, Summary = SumSalesDetailRows(rows, "page", "当前页商品"),
            };
        }
        return response;
    }

    public async Task<SalesDetailCategoryOptionsDto> GetSalesDetailCategoryOptionsAsync(List<string>? branchCodes, CancellationToken cancellationToken = default)
    {
        // 分店选项只给账号可见范围内的启用分店；null 表示不限分店。
        var scope = branchCodes?.Where(code => !string.IsNullOrWhiteSpace(code)).Select(code => code.Trim()).ToList();
        var stores = scope is { Count: 0 } ? new List<SalesDetailCategoryStoreOptionDto>() : (await _context.Db.Queryable<Store>()
                .Where(store => store.IsDeleted == false && store.IsActive)
                .WhereIF(scope != null, store => scope!.Contains(store.StoreCode))
                .Select(store => new { store.StoreCode, store.StoreName })
                .ToListAsync())
            .Select(store => new SalesDetailCategoryStoreOptionDto { StoreCode = store.StoreCode.Trim(), StoreName = string.IsNullOrWhiteSpace(store.StoreName) ? store.StoreCode.Trim() : store.StoreName.Trim() })
            .OrderBy(store => store.StoreName, StringComparer.OrdinalIgnoreCase).ToList();
        return new SalesDetailCategoryOptionsDto { Suppliers = await GetSalesDetailCategorySuppliersAsync(cancellationToken), Stores = stores };
    }

    private async Task<List<SalesDetailCategorySupplierOptionDto>> GetSalesDetailCategorySuppliersAsync(CancellationToken cancellationToken)
    {
        var suppliers = await _context.Db.Queryable<HBLocalSupplier>()
            .Where(supplier => supplier.IsDeleted == false)
            .Select(supplier => new { supplier.LocalSupplierCode, supplier.Name })
            .ToListAsync();
        var categoryCounts = await _context.Db.Queryable<LocalSupplierCategory>()
            .Where(category => category.IsDeleted == false && category.IsActive)
            .GroupBy(category => category.LocalSupplierCode)
            .Select(category => new { Code = category.LocalSupplierCode, Count = SqlSugar.SqlFunc.AggregateCount(category.CategoryGUID) })
            .ToListAsync();
        var assignedCounts = await _context.Db.Queryable<LocalSupplierCategoryProductAssignment>()
            .InnerJoin<Product>((assignment, product) => assignment.ProductCode == product.ProductCode
                && assignment.LocalSupplierCode == product.LocalSupplierCode && product.IsDeleted == false)
            // 归到已删除分类的商品在报表里算未归类，这里同样不计。
            .InnerJoin<LocalSupplierCategory>((assignment, product, category) => category.CategoryGUID == assignment.CategoryGUID
                && category.LocalSupplierCode == assignment.LocalSupplierCode && category.IsDeleted == false)
            .GroupBy((assignment, product, category) => assignment.LocalSupplierCode)
            .Select((assignment, product, category) => new { Code = assignment.LocalSupplierCode, Count = SqlSugar.SqlFunc.AggregateCount(assignment.ProductCode) })
            .ToListAsync();
        var warehouseCategoryCount = await _context.Db.Queryable<WarehouseCategory>()
            .Where(category => category.IsDeleted == false && category.IsActive).CountAsync();
        var warehouseAssigned = await _context.Db.Queryable<Product>()
            .Where(product => product.IsDeleted == false && product.WarehouseCategoryGUID != null && product.WarehouseCategoryGUID != ""
                && (product.LocalSupplierCode == HotBargainSupplierCode || product.LocalSupplierCode == null || product.LocalSupplierCode == ""))
            .CountAsync();
        cancellationToken.ThrowIfCancellationRequested();
        var categoriesByCode = categoryCounts.GroupBy(row => row.Code.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Count), StringComparer.OrdinalIgnoreCase);
        var assignedByCode = assignedCounts.GroupBy(row => row.Code.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Count), StringComparer.OrdinalIgnoreCase);
        return suppliers
            .Where(supplier => !string.IsNullOrWhiteSpace(supplier.LocalSupplierCode))
            .GroupBy(supplier => supplier.LocalSupplierCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var code = group.Key;
                var hotBargain = code == HotBargainSupplierCode;
                return new SalesDetailCategorySupplierOptionDto
                {
                    SupplierCode = code,
                    SupplierName = group.Select(item => item.Name?.Trim()).FirstOrDefault(name => !string.IsNullOrEmpty(name)) ?? code,
                    CategorySource = hotBargain ? SalesDetailCategorySources.Warehouse : SalesDetailCategorySources.Supplier,
                    CategoryCount = hotBargain ? warehouseCategoryCount : categoriesByCode.GetValueOrDefault(code),
                    AssignedProductCount = hotBargain ? warehouseAssigned : assignedByCode.GetValueOrDefault(code),
                };
            })
            // 有分类的排前面（按已归类商品数），其余按名称。
            .OrderByDescending(item => item.CategoryCount > 0).ThenByDescending(item => item.AssignedProductCount)
            .ThenBy(item => item.SupplierName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<SalesDetailCategoryTreeNode> EnumerateSubtree(SalesDetailCategoryTreeNode root)
    {
        // 数据异常形成环时也只访问每个节点一次。
        var visited = new HashSet<SalesDetailCategoryTreeNode>();
        var stack = new Stack<SalesDetailCategoryTreeNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!visited.Add(node)) continue;
            yield return node;
            foreach (var child in node.Children) stack.Push(child);
        }
    }

    /// <summary>
    /// 每个所选供应商一棵分类树：200 用仓库分类，其他供应商用各自网站分类；只读未删除的分类（含已停用）。
    /// </summary>
    private async Task<Dictionary<string, SalesDetailCategoryTree>> LoadSalesDetailCategoryTreesAsync(
        IReadOnlyCollection<string> suppliers, CancellationToken cancellationToken)
    {
        var result = suppliers.ToDictionary(code => code, _ => new SalesDetailCategoryTree(), StringComparer.OrdinalIgnoreCase);
        var localCodes = suppliers.Where(code => code != HotBargainSupplierCode).ToList();
        var flat = new List<(string Supplier, SalesDetailCategoryTreeNode Node)>();
        if (localCodes.Count > 0)
        {
            var categories = await _context.Db.Queryable<LocalSupplierCategory>()
                .Where(category => localCodes.Contains(category.LocalSupplierCode) && category.IsDeleted == false)
                .Select(category => new { category.LocalSupplierCode, category.CategoryGUID, category.ParentGUID, category.CategoryName, category.IsActive, category.SortOrder })
                .ToListAsync();
            flat.AddRange(categories.Select(category => (category.LocalSupplierCode, new SalesDetailCategoryTreeNode
            {
                Guid = category.CategoryGUID, ParentGuid = category.ParentGUID, Name = category.CategoryName,
                IsActive = category.IsActive, SortOrder = category.SortOrder,
            })));
        }
        if (suppliers.Contains(HotBargainSupplierCode))
        {
            var categories = await _context.Db.Queryable<WarehouseCategory>()
                .Where(category => category.IsDeleted == false)
                .Select(category => new { category.CategoryGUID, category.ParentGUID, category.CategoryName, category.IsActive, category.SortOrder })
                .ToListAsync();
            flat.AddRange(categories.Select(category => (HotBargainSupplierCode, new SalesDetailCategoryTreeNode
            {
                Guid = category.CategoryGUID, ParentGuid = category.ParentGUID, Name = category.CategoryName,
                IsActive = category.IsActive, SortOrder = category.SortOrder,
            })));
        }
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var (supplier, node) in flat)
        {
            if (!result.TryGetValue(supplier.Trim(), out var tree) || string.IsNullOrWhiteSpace(node.Guid)) continue;
            tree.ByGuid.TryAdd(node.Guid, node);
        }
        foreach (var tree in result.Values)
        {
            foreach (var node in tree.ByGuid.Values)
            {
                // 父节点缺失或指向自己时挂到根，与供应商分类管理页的组树规则一致。
                if (!string.IsNullOrWhiteSpace(node.ParentGuid) && tree.ByGuid.TryGetValue(node.ParentGuid, out var parent) && parent != node)
                {
                    node.Parent = parent;
                    parent.Children.Add(node);
                }
                else tree.Roots.Add(node);
            }
        }
        return result;
    }

    private async Task<Dictionary<string, string>> LoadLocalSupplierNamesAsync(IReadOnlyCollection<string> suppliers, CancellationToken cancellationToken)
    {
        var codes = suppliers.ToList();
        var rows = await _context.Db.Queryable<HBLocalSupplier>()
            .Where(supplier => codes.Contains(supplier.LocalSupplierCode) && supplier.IsDeleted == false)
            .Select(supplier => new { supplier.LocalSupplierCode, supplier.Name })
            .ToListAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return rows.Where(row => !string.IsNullOrWhiteSpace(row.Name))
            .GroupBy(row => row.LocalSupplierCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Name.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 叶分类汇总沿父链累加到各级节点。每个商品在同一供应商下只属于一个分类，所以商品数也可以直接相加。
    /// 分类 GUID 为空或不在该供应商分类树里（分类已删除）的商品计入未归类。
    /// </summary>
    private static void BuildSalesDetailCategoryTree(SalesDetailCategoryReportDto target, IEnumerable<SalesDetailReportSqlRow> rows,
        IReadOnlyList<string> suppliers, IReadOnlyDictionary<string, SalesDetailCategoryTree> trees,
        IReadOnlyDictionary<string, string> names, bool compare)
    {
        var supplierTotals = suppliers.ToDictionary(code => code, _ => new SalesDetailCategoryTotals(), StringComparer.OrdinalIgnoreCase);
        var unassignedTotals = suppliers.ToDictionary(code => code, _ => new SalesDetailCategoryTotals(), StringComparer.OrdinalIgnoreCase);
        var nodeTotals = new Dictionary<SalesDetailCategoryTreeNode, SalesDetailCategoryTotals>();
        foreach (var row in rows)
        {
            if (!supplierTotals.TryGetValue(row.Code, out var supplierTotal)) continue;
            supplierTotal.Add(row);
            if (string.IsNullOrEmpty(row.Name) || !trees[row.Code].ByGuid.TryGetValue(row.Name, out var node))
            {
                unassignedTotals[row.Code].Add(row);
                continue;
            }
            var visited = new HashSet<SalesDetailCategoryTreeNode>();
            for (var current = node; current != null && visited.Add(current); current = current.Parent)
            {
                if (!nodeTotals.TryGetValue(current, out var total)) nodeTotals[current] = total = new SalesDetailCategoryTotals();
                total.Add(row);
            }
        }

        List<SalesDetailCategoryNodeDto> buildNodes(IEnumerable<SalesDetailCategoryTreeNode> nodes, int depth, HashSet<SalesDetailCategoryTreeNode> path) => nodes
            .Where(node => nodeTotals.TryGetValue(node, out var total) && total.HasRows && !path.Contains(node))
            .Select(node =>
            {
                var dto = nodeTotals[node].Fill(new SalesDetailCategoryNodeDto
                {
                    CategoryGuid = node.Guid, Name = node.Name, Depth = depth, IsActive = node.IsActive,
                }, compare);
                path.Add(node);
                dto.Children = buildNodes(node.Children, depth + 1, path);
                path.Remove(node);
                return (dto, node.SortOrder);
            })
            .OrderByDescending(item => item.dto.Revenue).ThenByDescending(item => item.dto.CompareRevenue ?? 0m)
            .ThenBy(item => item.SortOrder ?? int.MaxValue).ThenBy(item => item.dto.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.dto).ToList();

        var summary = new SalesDetailCategoryTotals();
        var unassigned = new SalesDetailCategoryTotals();
        foreach (var code in suppliers)
        {
            var total = supplierTotals[code];
            summary.Add(total);
            unassigned.Add(unassignedTotals[code]);
            var supplier = total.Fill(new SalesDetailCategorySupplierDto
            {
                SupplierCode = code,
                SupplierName = names.TryGetValue(code, out var name) ? name : code == HotBargainSupplierCode ? "Hot Bargain" : code,
                CategorySource = code == HotBargainSupplierCode ? SalesDetailCategorySources.Warehouse : SalesDetailCategorySources.Supplier,
                Categories = buildNodes(trees[code].Roots, 0, new HashSet<SalesDetailCategoryTreeNode>()),
            }, compare);
            if (unassignedTotals[code].HasRows)
                supplier.Unassigned = unassignedTotals[code].Fill(new SalesDetailCategoryNodeDto
                {
                    CategoryGuid = SalesDetailCategorySources.UnassignedKey, Name = "未归类", Depth = 0,
                }, compare);
            target.Suppliers.Add(supplier);
        }
        target.Suppliers = target.Suppliers.OrderByDescending(item => item.Revenue)
            .ThenByDescending(item => item.CompareRevenue ?? 0m).ThenBy(item => item.SupplierCode, StringComparer.OrdinalIgnoreCase).ToList();
        target.Summary = summary.Fill(new SalesDetailCategoryMetricsDto(), compare);
        target.Unassigned = unassigned.Fill(new SalesDetailCategoryMetricsDto(), compare);
    }

    /// <summary>
    /// 一个批次：状态 →（可选）按供应商 × 叶分类汇总 →（可选）节点商品分页与总数。
    /// 事实先按 (期间, 原始供应商, 商品) 聚合再归并供应商，商品元数据与分类只按 distinct 商品各解析一次。
    /// </summary>
    private static string BuildSalesDetailCategoryReportSql(bool sqlServer, DateRangeDto range, IReadOnlyList<string> suppliers,
        IReadOnlyList<string>? branches, string? selectedBranch, bool includeTree, string? nodeSupplier,
        IReadOnlyList<string>? nodeGuids, bool unassignedNode, IReadOnlyList<string> tokens, int pageIndex, int pageSize)
    {
        var hasCompare = HasCompare(range);
        string table(string name) => sqlServer ? $"[#{name}]" : $"[{name}]";
        string create(string name, string body) => sqlServer
            ? $"SELECT * INTO {table(name)} FROM ({body}) materialized;\n"
            : $"CREATE TEMP TABLE {table(name)} AS {body};\n";
        const string periods = "(SELECT 0 [Period], @sdcCurrentStart [StartDate], @sdcCurrentEnd [EndDate] UNION ALL SELECT 1 [Period], @sdcCompareStart [StartDate], @sdcCompareEnd [EndDate] WHERE @sdcHasCompare=1)";

        // 原始供应商码 → 澳洲供应商：200 合并原始码 200 与全部国内供应商码；其他所选码若本身是国内供应商码，在销售明细里也归 200，这里同样不单独计入。
        var supplierRows = new List<string>();
        for (var i = 0; i < suppliers.Count; i++)
        {
            if (suppliers[i] == HotBargainSupplierCode) continue;
            supplierRows.Add($"SELECT @sdcSupplier{i} [RawSupplierCode], @sdcSupplier{i} [SupplierCode] WHERE NOT EXISTS (SELECT 1 FROM [ChinaSupplier] china WHERE LTRIM(RTRIM(china.[SupplierCode]))=@sdcSupplier{i})");
        }
        if (suppliers.Contains(HotBargainSupplierCode))
        {
            supplierRows.Add("SELECT @sdcHotBargain [RawSupplierCode], @sdcHotBargain [SupplierCode]");
            supplierRows.Add("SELECT DISTINCT LTRIM(RTRIM(china.[SupplierCode])) [RawSupplierCode], @sdcHotBargain [SupplierCode] FROM [ChinaSupplier] china WHERE china.[SupplierCode] IS NOT NULL AND LTRIM(RTRIM(china.[SupplierCode]))<>'' AND LTRIM(RTRIM(china.[SupplierCode]))<>@sdcHotBargain");
        }

        var branchFilter = !string.IsNullOrWhiteSpace(selectedBranch)
            ? " AND s.[BranchCode]=@sdcSelectedBranch"
            : branches is { Count: > 0 } ? $" AND s.[BranchCode] IN ({string.Join(",", branches.Select((_, i) => $"@sdcBranch{i}"))})" : string.Empty;
        // 与销售明细一致：统计失败日不读事实，状态里另行提示。
        const string failedDateFilter = " AND NOT EXISTS (SELECT 1 FROM [SalesStatisticRefreshState] failedState WHERE failedState.[StatisticType]='ProductStoreDaily' AND failedState.[Status]='Failed' AND failedState.[Date]=s.[Date])";
        // SQL Server 的等值比较忽略尾随空格（事实码没有前导空格），未去空格的原始列可让列存扫描直接按供应商集合过滤；
        // SQLite 按字面比较，因此去空格后再比。内层按原始列聚合，外层再去空格合并，结果与直接按去空格键分组相同。
        var supplierPredicate = sqlServer
            ? $"s.[SupplierCode] IN (SELECT [RawSupplierCode] FROM {table("SdcSuppliers")})"
            : $"LTRIM(RTRIM(s.[SupplierCode])) IN (SELECT [RawSupplierCode] FROM {table("SdcSuppliers")})";
        var facts = $"""
SELECT r.[Period], sup.[SupplierCode], LTRIM(RTRIM(r.[ProductCode])) [ProductCode],
       SUM(r.[Revenue]) [Revenue], SUM(r.[Quantity]) [Quantity], SUM(r.[OrderCount]) [OrderCount], SUM(r.[GrossProfit]) [GrossProfit],
       SUM(r.[StatisticRowCount]) [StatisticRowCount], SUM(r.[CostedRowCount]) [CostedRowCount], SUM(r.[GrossProfitRowCount]) [GrossProfitRowCount]
FROM (
 SELECT periods.[Period], s.[SupplierCode], s.[ProductCode],
        SUM(s.[TotalAmount]) [Revenue], SUM(s.[TotalQuantity]) [Quantity], SUM(s.[OrderCount]) [OrderCount], SUM(s.[GrossProfit]) [GrossProfit],
        COUNT(*) [StatisticRowCount], COUNT(s.[TotalCost]) [CostedRowCount], COUNT(s.[GrossProfit]) [GrossProfitRowCount]
 FROM [ProductStoreDailySalesStatistic] s
 CROSS JOIN {periods} periods
 WHERE s.[Date]>=periods.[StartDate] AND s.[Date]<periods.[EndDate] AND {supplierPredicate}{failedDateFilter}{branchFilter}
 GROUP BY periods.[Period], s.[SupplierCode], s.[ProductCode]
) r
INNER JOIN {table("SdcSuppliers")} sup ON sup.[RawSupplierCode]=LTRIM(RTRIM(r.[SupplierCode]))
GROUP BY r.[Period], sup.[SupplierCode], LTRIM(RTRIM(r.[ProductCode]))
""";
        // 商品当前供应商与仓库分类每个商品只解析一次；软删除条件放在 EXISTS 里，让商品资料走窄索引而不是整表宽行。
        // 按键分组：商品资料即使出现重复编码也只保留一行，不会放大后面的销售事实。
        var productMeta = $"""
SELECT k.[SupplierCode], k.[ProductCode], MAX(p.[LocalSupplierCode]) [ProductSupplierCode], MAX(p.[WarehouseCategoryGUID]) [WarehouseCategoryGUID]
FROM (SELECT DISTINCT [SupplierCode], [ProductCode] FROM {table("SdcFacts")}) k
LEFT JOIN [Product] p ON p.[ProductCode]=k.[ProductCode] AND EXISTS (SELECT 1 FROM [Product] active WHERE active.[ProductCode]=p.[ProductCode] AND active.[IsDeleted]=0)
GROUP BY k.[SupplierCode], k.[ProductCode]
""";
        var productCategories = $"""
SELECT m.[SupplierCode], m.[ProductCode],
       UPPER(CASE WHEN m.[SupplierCode]=@sdcHotBargain
                  THEN CASE WHEN m.[ProductSupplierCode]=@sdcHotBargain OR m.[ProductSupplierCode] IS NULL OR m.[ProductSupplierCode]='' THEN m.[WarehouseCategoryGUID] END
                  ELSE assignment.[CategoryGUID] END) [CategoryGuid]
FROM {table("SdcProductMeta")} m
LEFT JOIN [LocalSupplierCategoryProductAssignment] assignment
  ON assignment.[ProductCode]=m.[ProductCode] AND assignment.[LocalSupplierCode]=m.[ProductSupplierCode] AND m.[ProductSupplierCode]=m.[SupplierCode]
""";
        string metrics(string alias) => $"""
 COALESCE(SUM(CASE WHEN {alias}.[Period]=0 THEN {alias}.[Revenue] ELSE 0 END),0) [Revenue], {(hasCompare ? $"COALESCE(SUM(CASE WHEN {alias}.[Period]=1 THEN {alias}.[Revenue] ELSE 0 END),0)" : "0")} [CompareRevenue],
 COALESCE(SUM(CASE WHEN {alias}.[Period]=0 THEN {alias}.[Quantity] ELSE 0 END),0) [Quantity], {(hasCompare ? $"COALESCE(SUM(CASE WHEN {alias}.[Period]=1 THEN {alias}.[Quantity] ELSE 0 END),0)" : "0")} [CompareQuantity],
 COALESCE(SUM(CASE WHEN {alias}.[Period]=0 THEN {alias}.[OrderCount] ELSE 0 END),0) [OrderCount], {(hasCompare ? $"COALESCE(SUM(CASE WHEN {alias}.[Period]=1 THEN {alias}.[OrderCount] ELSE 0 END),0)" : "0")} [CompareOrderCount],
 SUM(CASE WHEN {alias}.[Period]=0 THEN {alias}.[GrossProfit] END) [GrossProfit], {(hasCompare ? $"SUM(CASE WHEN {alias}.[Period]=1 THEN {alias}.[GrossProfit] END)" : "NULL")} [CompareGrossProfit],
 COALESCE(SUM(CASE WHEN {alias}.[Period]=0 THEN {alias}.[StatisticRowCount] ELSE 0 END),0) [StatisticRowCount], COALESCE(SUM(CASE WHEN {alias}.[Period]=0 THEN {alias}.[CostedRowCount] ELSE 0 END),0) [CostedRowCount], COALESCE(SUM(CASE WHEN {alias}.[Period]=0 THEN {alias}.[GrossProfitRowCount] ELSE 0 END),0) [GrossProfitRowCount],
 COALESCE(SUM(CASE WHEN {alias}.[Period]=1 THEN {alias}.[StatisticRowCount] ELSE 0 END),0) [CompareStatisticRowCount], COALESCE(SUM(CASE WHEN {alias}.[Period]=1 THEN {alias}.[CostedRowCount] ELSE 0 END),0) [CompareCostedRowCount], COALESCE(SUM(CASE WHEN {alias}.[Period]=1 THEN {alias}.[GrossProfitRowCount] ELSE 0 END),0) [CompareGrossProfitRowCount],
 COUNT(DISTINCT CASE WHEN {alias}.[Period]=0 THEN {alias}.[ProductCode] END) [CurrentProductCount], COUNT(DISTINCT CASE WHEN {alias}.[Period]=1 THEN {alias}.[ProductCode] END) [CompareProductCount]
""";
        var join = $"FROM {table("SdcFacts")} f INNER JOIN {table("SdcProducts")} k ON k.[SupplierCode]=f.[SupplierCode] AND k.[ProductCode]=f.[ProductCode]";

        var sql = new System.Text.StringBuilder();
        if (sqlServer) sql.Append("SET NOCOUNT ON;\n");
        sql.Append("SELECT [StatisticType],[Date],[Status],[LastAggregatedAtUtc],[CompletedAtUtc],[SourceProductVersion] FROM [SalesStatisticRefreshState] WHERE [StatisticType]='ProductStoreDaily' AND (([Date]>=@sdcCurrentStart AND [Date]<@sdcCurrentEnd) OR (@sdcHasCompare=1 AND [Date]>=@sdcCompareStart AND [Date]<@sdcCompareEnd)) ORDER BY [Date],[StatisticType];\n");
        sql.Append(create("SdcSuppliers", string.Join(" UNION ALL ", supplierRows)));
        sql.Append(create("SdcFacts", facts));
        sql.Append(create("SdcProductMeta", productMeta));
        sql.Append(create("SdcProducts", productCategories));
        if (includeTree)
            sql.Append($"SELECT f.[SupplierCode] [Code], COALESCE(k.[CategoryGuid],'') [Name], NULL [ItemNumber], NULL [ProductImage],{metrics("f")} {join} GROUP BY f.[SupplierCode], k.[CategoryGuid];\n");
        if (nodeSupplier != null)
        {
            var nodeFilter = nodeGuids == null ? string.Empty
                : unassignedNode
                    ? nodeGuids.Count == 0 ? string.Empty : $" AND (k.[CategoryGuid] IS NULL OR k.[CategoryGuid] NOT IN ({string.Join(",", nodeGuids.Select((_, i) => $"@sdcNode{i}"))}))"
                    : $" AND k.[CategoryGuid] IN ({string.Join(",", nodeGuids.Select((_, i) => $"@sdcNode{i}"))})";
            var searchFilter = string.Concat(tokens.Select((_, i) => $" AND (f.[ProductCode] LIKE @sdcSearch{i} OR EXISTS (SELECT 1 FROM [Product] productSearch WHERE productSearch.[ProductCode]=f.[ProductCode] AND (productSearch.[ProductName] LIKE @sdcSearch{i} OR productSearch.[EnglishName] LIKE @sdcSearch{i} OR productSearch.[ItemNumber] LIKE @sdcSearch{i} OR productSearch.[Barcode] LIKE @sdcSearch{i})))"));
            var where = $"WHERE f.[SupplierCode]=@sdcNodeSupplier{nodeFilter}{searchFilter}";
            var offset = ((long)pageIndex - 1L) * pageSize;
            var paging = sqlServer ? $"OFFSET {offset} ROWS FETCH NEXT {pageSize} ROWS ONLY" : $"LIMIT {pageSize} OFFSET {offset}";
            // 先分页再取商品资料：资料只读当前页的几十个编码，重复资料也不会参与求和。
            string product(string column) => $"(SELECT MAX(p.[{column}]) FROM [Product] p WHERE p.[ProductCode]=page.[Code])";
            sql.Append($"""
SELECT page.[Code], COALESCE({product("ProductName")}, page.[Code]) [Name], {product("ItemNumber")} [ItemNumber], {product("ProductImage")} [ProductImage],
       {string.Join(",", MetricColumns.Select(column => $"page.[{column}]"))}
FROM (
 SELECT f.[ProductCode] [Code],{metrics("f")}
 {join}
 {where}
 GROUP BY f.[ProductCode]
 ORDER BY [Revenue] DESC, [CompareRevenue] DESC, [Code] ASC {paging}
) page
ORDER BY page.[Revenue] DESC, page.[CompareRevenue] DESC, page.[Code] ASC;
SELECT COUNT(*) FROM (SELECT f.[ProductCode] {join} {where} GROUP BY f.[ProductCode]) productKeys;

""");
        }
        foreach (var name in new[] { "SdcProducts", "SdcProductMeta", "SdcFacts", "SdcSuppliers" })
            sql.Append($"DROP TABLE {table(name)};\n");
        return sql.ToString();
    }

    private async Task<SalesDetailCategoryReportRead> ReadSalesDetailCategoryReportAsync(string sql, DateRangeDto range,
        IReadOnlyList<string> suppliers, IReadOnlyList<string>? branches, string? selectedBranch, string? nodeSupplier,
        IReadOnlyList<string> nodeGuids, IReadOnlyList<string> tokens, bool includeTree, bool includeProducts,
        CancellationToken cancellationToken)
    {
        var sqlServer = _context.Db.CurrentConnectionConfig.DbType == SqlSugar.DbType.SqlServer;
        var elapsed = Stopwatch.StartNew();
        var ownsTransaction = sqlServer && _context.Db.Ado.Transaction == null;
        // 与销售明细相同：自管快照只执行一个批次，使用独立的非 MARS 连接；已有外部事务时沿用其连接。
        await using var dedicatedConnection = ownsTransaction
            ? new SqlConnection(new SqlConnectionStringBuilder(_context.Db.CurrentConnectionConfig.ConnectionString)
                { MultipleActiveResultSets = false, MinPoolSize = 1 }.ConnectionString)
            : null;
        var connection = (DbConnection?)dedicatedConnection ?? (DbConnection)_context.Db.Ado.Connection;
        var close = connection.State != ConnectionState.Open;
        if (close) await connection.OpenAsync(cancellationToken);
        var failed = true;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = !ownsTransaction ? sql : """
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND snapshot_isolation_state = 1)
                    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;
                ELSE
                    THROW 51001, N'统计快照读取尚未启用，暂时无法读取完整报表。', 1;
                BEGIN TRANSACTION;
                BEGIN TRY
                """ + "\n" + sql + "\n" + """
                COMMIT TRANSACTION;
                SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
                END TRY
                BEGIN CATCH
                    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                    SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
                    THROW;
                END CATCH;
                """;
            command.CommandTimeout = Math.Max(1, _context.Db.Ado.CommandTimeOut);
            if (_context.Db.Ado.Transaction is DbTransaction tx) command.Transaction = tx;
            void Add(string name, object value, DbType type)
            {
                var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; parameter.DbType = type; command.Parameters.Add(parameter);
            }
            Add("@sdcCurrentStart", range.StartDate.Date, DbType.DateTime);
            Add("@sdcCurrentEnd", range.EndDate.Date.AddDays(1), DbType.DateTime);
            Add("@sdcHasCompare", HasCompare(range) ? 1 : 0, DbType.Int32);
            Add("@sdcCompareStart", (object?)range.CompareStartDate?.Date ?? DBNull.Value, DbType.DateTime);
            Add("@sdcCompareEnd", (object?)range.CompareEndDate?.Date.AddDays(1) ?? DBNull.Value, DbType.DateTime);
            Add("@sdcHotBargain", HotBargainSupplierCode, DbType.String);
            for (var i = 0; i < suppliers.Count; i++) Add($"@sdcSupplier{i}", suppliers[i], DbType.String);
            if (!string.IsNullOrWhiteSpace(selectedBranch)) Add("@sdcSelectedBranch", selectedBranch.Trim(), DbType.String);
            else for (var i = 0; i < (branches?.Count ?? 0); i++) Add($"@sdcBranch{i}", branches![i], DbType.String);
            if (nodeSupplier != null)
            {
                Add("@sdcNodeSupplier", nodeSupplier, DbType.String);
                for (var i = 0; i < nodeGuids.Count; i++) Add($"@sdcNode{i}", nodeGuids[i].ToUpperInvariant(), DbType.String);
                for (var i = 0; i < tokens.Count; i++) Add($"@sdcSearch{i}", $"%{tokens[i]}%", DbType.String);
            }

            var read = new SalesDetailCategoryReportRead();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var firstResultAt = elapsed.ElapsedMilliseconds;
            while (await reader.ReadAsync(cancellationToken))
                read.Status.Add(new SalesDetailReportStatusSqlRow { Type = S(reader, 0), Date = D(reader, 1), Status = S(reader, 2), LastAggregatedAtUtc = ND(reader, 3), CompletedAtUtc = ND(reader, 4), SourceProductVersion = NS(reader, 5) });
            if (includeTree)
            {
                await NextResult();
                await ReadRowsAsync(reader, read.Categories, cancellationToken);
            }
            if (includeProducts)
            {
                await NextResult();
                await ReadRowsAsync(reader, read.Products, cancellationToken);
                await NextResult();
                if (await reader.ReadAsync(cancellationToken)) read.ProductTotal = I(reader, 0);
            }
            // 消费到批次末尾，确认 COMMIT 成功后才发布结果。
            while (await reader.NextResultAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken)) { }
            failed = false;
            _logger.LogInformation(
                "销售明细分类汇总读取完成：首结果 {FirstResultMs}ms，共 {TotalMs}ms，供应商 {Suppliers} 个，分类行 {CategoryRows}，商品页 {ProductRows}",
                firstResultAt, elapsed.ElapsedMilliseconds, suppliers.Count, read.Categories.Count, read.Products.Count);
            return read;

            async Task NextResult()
            {
                if (!await reader.NextResultAsync(cancellationToken))
                    throw new InvalidOperationException("销售明细分类汇总批次缺少结果集。");
            }
        }
        finally
        {
            if (close || (failed && ownsTransaction)) await connection.CloseAsync();
        }
    }
}
