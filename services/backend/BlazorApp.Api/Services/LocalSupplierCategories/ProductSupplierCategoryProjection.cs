using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.LocalSupplierCategories;

/// <summary>
/// 商品列表/详情的供应商分类回填：只针对当前页商品做小查询，不参与分页前的计数与排序，
/// 保持商品列表快路径（单表计数、排序、取页）不变。
/// </summary>
public static class ProductSupplierCategoryProjection
{
    private const int InListChunkSize = 500;
    private const int MaxWarehouseDepth = 16;

    private sealed class WarehouseNodeRow
    {
        public string CategoryGUID { get; set; } = string.Empty;
        public string? ParentGUID { get; set; }
        public string CategoryName { get; set; } = string.Empty;
    }

    public static async Task FillAsync(ISqlSugarClient db, IReadOnlyList<ProductDto> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var hotBargainItems = items
            .Where(item => LocalSupplierCategoryConstants.IsHotBargain(item.LocalSupplierCode))
            .ToList();
        var websiteItems = items
            .Where(item => !LocalSupplierCategoryConstants.IsHotBargain(item.LocalSupplierCode))
            .ToList();

        await FillHotBargainAsync(db, hotBargainItems);
        await FillWebsiteAsync(db, websiteItems);
    }

    /// <summary>
    /// 200 的供应商分类即仓库分类：GUID 直接取仓库分类，名称与路径逐级回溯祖先拼出。
    /// </summary>
    private static async Task FillHotBargainAsync(ISqlSugarClient db, List<ProductDto> items)
    {
        var guids = items
            .Select(item => item.WarehouseCategoryGUID?.Trim())
            .Where(guid => !string.IsNullOrEmpty(guid))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var nodes = await LoadWarehouseAncestorsAsync(db, guids);
        foreach (var item in items)
        {
            var guid = item.WarehouseCategoryGUID?.Trim();
            if (string.IsNullOrEmpty(guid))
            {
                continue;
            }

            item.SupplierCategoryGUID = guid;
            item.SupplierCategorySource = LocalSupplierCategorySources.Warehouse;
            if (nodes.TryGetValue(guid, out var node))
            {
                item.SupplierCategoryName = node.CategoryName;
                item.SupplierCategoryPath = BuildWarehousePath(nodes, node);
            }
        }
    }

    private static async Task FillWebsiteAsync(ISqlSugarClient db, List<ProductDto> items)
    {
        var codes = items
            .Select(item => item.ProductCode?.Trim())
            .Where(code => !string.IsNullOrEmpty(code))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (codes.Count == 0)
        {
            return;
        }

        var assignments = new List<LocalSupplierCategoryProductAssignment>();
        foreach (var chunk in codes.Chunk(InListChunkSize))
        {
            var chunkList = chunk.ToList();
            assignments.AddRange(
                await db.Queryable<LocalSupplierCategoryProductAssignment>()
                    .Where(assignment => chunkList.Contains(assignment.ProductCode))
                    .ToListAsync()
            );
        }

        var categoryGuids = assignments.Select(assignment => assignment.CategoryGUID).Distinct().ToList();
        var categories = new Dictionary<string, LocalSupplierCategory>(StringComparer.Ordinal);
        foreach (var chunk in categoryGuids.Chunk(InListChunkSize))
        {
            var chunkList = chunk.ToList();
            var rows = await db.Queryable<LocalSupplierCategory>()
                .Where(category => chunkList.Contains(category.CategoryGUID) && category.IsDeleted == false)
                .ToListAsync();
            foreach (var row in rows)
            {
                categories.TryAdd(row.CategoryGUID, row);
            }
        }

        var assignmentByCode = new Dictionary<string, LocalSupplierCategoryProductAssignment>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in assignments)
        {
            assignmentByCode.TryAdd(assignment.ProductCode, assignment);
        }

        foreach (var item in items)
        {
            if (
                string.IsNullOrWhiteSpace(item.ProductCode)
                || !assignmentByCode.TryGetValue(item.ProductCode.Trim(), out var assignment)
                // 归属登记的供应商与商品当前供应商不一致（HQ 改了供应商）时视为未归类。
                || !string.Equals(assignment.LocalSupplierCode, item.LocalSupplierCode?.Trim(), StringComparison.OrdinalIgnoreCase)
                || !categories.TryGetValue(assignment.CategoryGUID, out var category)
            )
            {
                continue;
            }

            item.SupplierCategoryGUID = category.CategoryGUID;
            item.SupplierCategoryName = category.CategoryName;
            item.SupplierCategoryPath = category.FullPath;
            item.SupplierCategorySource = assignment.Source;
        }
    }

    private static async Task<Dictionary<string, WarehouseNodeRow>> LoadWarehouseAncestorsAsync(
        ISqlSugarClient db,
        IReadOnlyCollection<string> guids
    )
    {
        var loaded = new Dictionary<string, WarehouseNodeRow>(StringComparer.OrdinalIgnoreCase);
        var pending = guids.ToList();
        for (var level = 0; level < MaxWarehouseDepth && pending.Count > 0; level++)
        {
            var next = new List<string>();
            foreach (var chunk in pending.Chunk(InListChunkSize))
            {
                var chunkList = chunk.ToList();
                var rows = await db.Queryable<WarehouseCategory>()
                    .Where(category => chunkList.Contains(category.CategoryGUID))
                    .Select(category => new WarehouseNodeRow
                    {
                        CategoryGUID = category.CategoryGUID,
                        ParentGUID = category.ParentGUID,
                        CategoryName = category.CategoryName,
                    })
                    .ToListAsync();
                foreach (var row in rows)
                {
                    if (!loaded.TryAdd(row.CategoryGUID, row))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(row.ParentGUID) && !loaded.ContainsKey(row.ParentGUID))
                    {
                        next.Add(row.ParentGUID);
                    }
                }
            }

            pending = next.Distinct(StringComparer.OrdinalIgnoreCase).Where(guid => !loaded.ContainsKey(guid)).ToList();
        }

        return loaded;
    }

    private static string BuildWarehousePath(Dictionary<string, WarehouseNodeRow> nodes, WarehouseNodeRow leaf)
    {
        var names = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var current = leaf; current != null && visited.Add(current.CategoryGUID);)
        {
            names.Add(current.CategoryName);
            current = !string.IsNullOrWhiteSpace(current.ParentGUID) && nodes.TryGetValue(current.ParentGUID, out var parent)
                ? parent
                : null;
        }

        names.Reverse();
        return string.Join(LocalSupplierCategoryConstants.PathSeparator, names);
    }
}

/// <summary>
/// 商品列表的供应商分类筛选：把所选 GUID 连同子分类展开，并按所属树分流为供应商分类与仓库分类两组。
/// </summary>
public static class LocalSupplierCategoryFilter
{
    private sealed class TreeEdge
    {
        public string Guid { get; set; } = string.Empty;
        public string? ParentGuid { get; set; }
    }

    public static async Task<(List<string> SupplierCategoryGuids, List<string> WarehouseCategoryGuids)> ExpandAsync(
        ISqlSugarClient db,
        IReadOnlyCollection<string> requestedGuids
    )
    {
        var requested = requestedGuids
            .Where(guid => !string.IsNullOrWhiteSpace(guid))
            .Select(guid => guid.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (requested.Count == 0)
        {
            return (new List<string>(), new List<string>());
        }

        // 先在供应商分类中命中，命中的供应商的整棵树一次载入后在内存展开子树。
        var supplierHits = await db.Queryable<LocalSupplierCategory>()
            .Where(category => requested.Contains(category.CategoryGUID) && category.IsDeleted == false)
            .Select(category => new { category.CategoryGUID, category.LocalSupplierCode })
            .ToListAsync();
        var supplierGuids = new List<string>();
        foreach (var supplierGroup in supplierHits.GroupBy(hit => hit.LocalSupplierCode, StringComparer.OrdinalIgnoreCase))
        {
            var supplier = supplierGroup.Key;
            var edges = await db.Queryable<LocalSupplierCategory>()
                .Where(category => category.LocalSupplierCode == supplier && category.IsDeleted == false)
                .Select(category => new TreeEdge { Guid = category.CategoryGUID, ParentGuid = category.ParentGUID })
                .ToListAsync();
            supplierGuids.AddRange(ExpandSubtree(edges, supplierGroup.Select(hit => hit.CategoryGUID)));
        }

        var remaining = requested
            .Where(guid => !supplierHits.Any(hit => string.Equals(hit.CategoryGUID, guid, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var warehouseGuids = new List<string>();
        if (remaining.Count > 0)
        {
            var warehouseEdges = await db.Queryable<WarehouseCategory>()
                .Where(category => category.IsDeleted == false)
                .Select(category => new TreeEdge { Guid = category.CategoryGUID, ParentGuid = category.ParentGUID })
                .ToListAsync();
            var known = warehouseEdges.Select(edge => edge.Guid).ToHashSet(StringComparer.OrdinalIgnoreCase);
            warehouseGuids.AddRange(ExpandSubtree(warehouseEdges, remaining.Where(known.Contains)));
        }

        return (
            supplierGuids.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            warehouseGuids.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        );
    }

    private static IEnumerable<string> ExpandSubtree(IReadOnlyList<TreeEdge> edges, IEnumerable<string> roots)
    {
        var childrenByParent = edges
            .Where(edge => !string.IsNullOrWhiteSpace(edge.ParentGuid))
            .GroupBy(edge => edge.ParentGuid!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.Guid).ToList(), StringComparer.OrdinalIgnoreCase);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>(roots);
        while (stack.Count > 0)
        {
            var guid = stack.Pop();
            if (!result.Add(guid))
            {
                continue;
            }

            if (childrenByParent.TryGetValue(guid, out var children))
            {
                foreach (var child in children)
                {
                    stack.Push(child);
                }
            }
        }

        return result;
    }
}
