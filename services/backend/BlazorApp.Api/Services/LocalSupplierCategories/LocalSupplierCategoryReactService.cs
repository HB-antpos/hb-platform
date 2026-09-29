using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Models;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace BlazorApp.Api.Services.LocalSupplierCategories;

public sealed class LocalSupplierCategoryReactService : ILocalSupplierCategoryReactService
{
    private readonly ISqlSugarClient _db;
    private readonly IOptionsSnapshot<BrowserExtensionOptions> _options;
    private readonly TimeProvider _timeProvider;

    public LocalSupplierCategoryReactService(
        SqlSugarContext context,
        IOptionsSnapshot<BrowserExtensionOptions> options,
        TimeProvider? timeProvider = null
    )
    {
        _db = context.Db;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private sealed class CodeCountRow
    {
        public string? Code { get; set; }
        public int Count { get; set; }
        public int Extra { get; set; }
    }

    private sealed class CodeDateRow
    {
        public string? Code { get; set; }
        public DateTime? Value { get; set; }
    }

    private sealed class GuidCountRow
    {
        public string? Guid { get; set; }
        public int Count { get; set; }
    }

    public async Task<List<LocalSupplierCategorySupplierSummaryDto>> GetSummaryAsync(
        CancellationToken cancellationToken = default
    )
    {
        // 概览只列 200 与「有采集配置或已有分类数据」的供应商，避免把上百个无关供应商塞进管理弹窗。
        var profileCodes = BrowserExtensionProfileCatalog.BuildProfiles(_options.Value)
            .Profiles.Where(profile => profile.Category != null)
            .ToDictionary(profile => profile.SupplierCode, profile => profile.DisplayName, StringComparer.OrdinalIgnoreCase);

        var categoryStats = await _db.Queryable<LocalSupplierCategory>()
            .Where(category => category.IsDeleted == false)
            .GroupBy(category => category.LocalSupplierCode)
            .Select(category => new CodeCountRow
            {
                Code = category.LocalSupplierCode,
                Count = SqlFunc.AggregateCount(category.CategoryGUID),
                Extra = SqlFunc.AggregateSum(SqlFunc.IIF(category.IsPromotional == true, 1, 0)),
            })
            .ToListAsync();
        var snapshotStats = await _db.Queryable<LocalSupplierCategory>()
            .Where(category => category.IsDeleted == false)
            .GroupBy(category => category.LocalSupplierCode)
            .Select(category => new CodeDateRow
            {
                Code = category.LocalSupplierCode,
                Value = SqlFunc.AggregateMax(category.LastSeenAt),
            })
            .ToListAsync();
        var captureStats = await _db.Queryable<LocalSupplierCategoryCapture>()
            .GroupBy(capture => capture.LocalSupplierCode)
            .Select(capture => new CodeDateRow
            {
                Code = capture.LocalSupplierCode,
                Value = SqlFunc.AggregateMax(capture.LastSeenAt),
            })
            .ToListAsync();

        var codes = new HashSet<string>(profileCodes.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var row in categoryStats.Where(row => !string.IsNullOrWhiteSpace(row.Code)))
        {
            codes.Add(row.Code!.Trim());
        }
        codes.Remove(LocalSupplierCategoryConstants.HotBargainSupplierCode);
        var codeList = codes.ToList();

        var productStats = codeList.Count == 0
            ? new List<CodeCountRow>()
            : await _db.Queryable<Product>()
                .Where(product => product.IsDeleted == false && codeList.Contains(product.LocalSupplierCode!))
                .GroupBy(product => product.LocalSupplierCode)
                .Select(product => new CodeCountRow
                {
                    Code = product.LocalSupplierCode,
                    Count = SqlFunc.AggregateCount(product.UUID),
                })
                .ToListAsync();
        // 只统计仍属于该供应商的有效商品归属；HQ 改了供应商的陈旧归属不计入。
        var assignmentStats = codeList.Count == 0
            ? new List<CodeCountRow>()
            : await _db.Queryable<LocalSupplierCategoryProductAssignment>()
                .InnerJoin<Product>((assignment, product) =>
                    assignment.ProductCode == product.ProductCode
                    && assignment.LocalSupplierCode == product.LocalSupplierCode
                    && product.IsDeleted == false
                )
                .Where((assignment, product) => codeList.Contains(assignment.LocalSupplierCode))
                .GroupBy((assignment, product) => assignment.LocalSupplierCode)
                .Select((assignment, product) => new CodeCountRow
                {
                    Code = assignment.LocalSupplierCode,
                    Count = SqlFunc.AggregateCount(assignment.ProductCode),
                    Extra = SqlFunc.AggregateSum(SqlFunc.IIF(assignment.Source == LocalSupplierCategorySources.Manual, 1, 0)),
                })
                .ToListAsync();

        var names = await _db.Queryable<HBLocalSupplier>()
            .Where(supplier => supplier.IsDeleted == false)
            .Select(supplier => new { supplier.LocalSupplierCode, supplier.Name })
            .ToListAsync();
        var nameByCode = names
            .Where(row => !string.IsNullOrWhiteSpace(row.LocalSupplierCode))
            .GroupBy(row => row.LocalSupplierCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.OrdinalIgnoreCase);

        var result = new List<LocalSupplierCategorySupplierSummaryDto> { await BuildHotBargainSummaryAsync(nameByCode) };
        foreach (var code in codeList.OrderBy(code => code, StringComparer.OrdinalIgnoreCase))
        {
            var category = categoryStats.FirstOrDefault(row => Same(row.Code, code));
            var products = productStats.Where(row => Same(row.Code, code)).Sum(row => row.Count);
            var assignment = assignmentStats.Where(row => Same(row.Code, code)).ToList();
            var assigned = assignment.Sum(row => row.Count);
            result.Add(new LocalSupplierCategorySupplierSummaryDto
            {
                SupplierCode = code,
                SupplierName = nameByCode.TryGetValue(code, out var name) && !string.IsNullOrWhiteSpace(name)
                    ? name
                    : profileCodes.GetValueOrDefault(code) ?? code,
                SourceKind = "website",
                CategoryCount = category?.Count ?? 0,
                PromotionalCount = category?.Extra ?? 0,
                ProductCount = products,
                AssignedCount = assigned,
                ManualCount = assignment.Sum(row => row.Extra),
                UnassignedCount = Math.Max(0, products - assigned),
                LastCapturedAt = AsUtc(captureStats.FirstOrDefault(row => Same(row.Code, code))?.Value),
                LastSnapshotAt = AsUtc(snapshotStats.FirstOrDefault(row => Same(row.Code, code))?.Value),
            });
        }

        return result;
    }

    public async Task<List<LocalSupplierCategoryNodeDto>> GetTreeAsync(
        string supplierCode,
        CancellationToken cancellationToken = default
    )
    {
        var supplier = LocalSupplierCategoryAssignmentService.NormalizeSupplierCode(supplierCode);
        if (LocalSupplierCategoryConstants.IsHotBargain(supplier))
        {
            return await BuildWarehouseTreeAsync();
        }

        var categories = await _db.Queryable<LocalSupplierCategory>()
            .Where(category => category.LocalSupplierCode == supplier && category.IsDeleted == false)
            .ToListAsync();
        var counts = await _db.Queryable<LocalSupplierCategoryProductAssignment>()
            .InnerJoin<Product>((assignment, product) =>
                assignment.ProductCode == product.ProductCode
                && assignment.LocalSupplierCode == product.LocalSupplierCode
                && product.IsDeleted == false
            )
            .Where((assignment, product) => assignment.LocalSupplierCode == supplier)
            .GroupBy((assignment, product) => assignment.CategoryGUID)
            .Select((assignment, product) => new GuidCountRow
            {
                Guid = assignment.CategoryGUID,
                Count = SqlFunc.AggregateCount(assignment.ProductCode),
            })
            .ToListAsync();
        var countByGuid = counts
            .Where(row => row.Guid != null)
            .ToDictionary(row => row.Guid!, row => row.Count, StringComparer.Ordinal);

        var nodes = categories.Select(category => new LocalSupplierCategoryNodeDto
        {
            CategoryGuid = category.CategoryGUID,
            ParentGuid = category.ParentGUID,
            Name = category.CategoryName,
            ExternalKey = category.ExternalKey,
            FullPath = category.FullPath,
            Depth = category.Depth,
            IsPromotional = category.IsPromotional,
            PromotionalSource = category.PromotionalSource,
            IsActive = category.IsActive,
            SortOrder = category.SortOrder,
            SourceUrl = category.SourceUrl,
            ProductCount = countByGuid.GetValueOrDefault(category.CategoryGUID),
            LastSeenAt = AsUtc(category.LastSeenAt),
        }).ToList();
        return BuildTree(nodes);
    }

    public async Task<LocalSupplierCategoryPromotionalResultDto> SetPromotionalAsync(
        string categoryGuid,
        bool isPromotional,
        string? actor,
        CancellationToken cancellationToken = default
    )
    {
        var guid = categoryGuid?.Trim() ?? string.Empty;
        var result = new LocalSupplierCategoryPromotionalResultDto();
        var transaction = await _db.Ado.UseTranAsync(async () =>
        {
            var category = await _db.Queryable<LocalSupplierCategory>()
                .Where(item => item.CategoryGUID == guid && item.IsDeleted == false)
                .FirstAsync()
                ?? throw new KeyNotFoundException("供应商分类不存在或已删除。");

            // 人工切换后标记来源为 manual，之后按规则重算时不再覆盖。
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            await _db.Updateable<LocalSupplierCategory>()
                .SetColumns(item => new LocalSupplierCategory
                {
                    IsPromotional = isPromotional,
                    PromotionalSource = LocalSupplierCategoryPromotionalSources.Manual,
                    UpdatedAt = now,
                    UpdatedBy = actor,
                })
                .Where(item => item.CategoryGUID == guid)
                .ExecuteCommandAsync();

            var items = await _db.Queryable<LocalSupplierCategoryCapture>()
                .Where(capture => capture.LocalSupplierCode == category.LocalSupplierCode && capture.CategoryGUID == guid)
                .Select(capture => capture.ItemNumber)
                .ToListAsync();
            var assignments = new LocalSupplierCategoryAssignmentService(_db, _timeProvider);
            foreach (var chunk in items.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(500))
            {
                var reconcile = await assignments.ReconcileByItemNumbersAsync(category.LocalSupplierCode, chunk, actor);
                result.Reassigned += reconcile.Assigned + reconcile.Updated;
                result.Cleared += reconcile.Cleared;
            }
        });
        if (!transaction.IsSuccess)
        {
            throw transaction.ErrorException ?? new InvalidOperationException("更新促销标记失败。");
        }

        return result;
    }

    public async Task<LocalSupplierCategoryResolveResultDto> ResolveSupplierAsync(
        string supplierCode,
        string? actor,
        CancellationToken cancellationToken = default
    )
    {
        LocalSupplierCategoryResolveResultDto? result = null;
        var transaction = await _db.Ado.UseTranAsync(async () =>
        {
            result = await new LocalSupplierCategoryAssignmentService(_db, _timeProvider)
                .ResolveSupplierAsync(supplierCode, actor);
        });
        if (!transaction.IsSuccess)
        {
            throw transaction.ErrorException ?? new InvalidOperationException("重新解析失败。");
        }

        return result!;
    }

    private async Task<LocalSupplierCategorySupplierSummaryDto> BuildHotBargainSummaryAsync(
        IReadOnlyDictionary<string, string> nameByCode
    )
    {
        var code = LocalSupplierCategoryConstants.HotBargainSupplierCode;
        var categoryCount = await _db.Queryable<WarehouseCategory>()
            .Where(category => category.IsDeleted == false)
            .CountAsync();
        // 与商品写入口径一致：空供应商码视为 200。
        var productQuery = _db.Queryable<Product>()
            .Where(product =>
                product.IsDeleted == false
                && (product.LocalSupplierCode == code || product.LocalSupplierCode == null || product.LocalSupplierCode == "")
            );
        var productCount = await productQuery.Clone().CountAsync();
        var assignedCount = await productQuery.Clone()
            .Where(product => product.WarehouseCategoryGUID != null && product.WarehouseCategoryGUID != "")
            .CountAsync();
        return new LocalSupplierCategorySupplierSummaryDto
        {
            SupplierCode = code,
            SupplierName = nameByCode.TryGetValue(code, out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : "Hot Bargain",
            SourceKind = LocalSupplierCategorySources.Warehouse,
            CategoryCount = categoryCount,
            ProductCount = productCount,
            AssignedCount = assignedCount,
            UnassignedCount = Math.Max(0, productCount - assignedCount),
        };
    }

    private async Task<List<LocalSupplierCategoryNodeDto>> BuildWarehouseTreeAsync()
    {
        var code = LocalSupplierCategoryConstants.HotBargainSupplierCode;
        var categories = await _db.Queryable<WarehouseCategory>()
            .Where(category => category.IsDeleted == false)
            .Select(category => new
            {
                category.CategoryGUID,
                category.ParentGUID,
                category.CategoryName,
                category.IsActive,
                category.SortOrder,
            })
            .ToListAsync();
        var counts = await _db.Queryable<Product>()
            .Where(product =>
                product.IsDeleted == false
                && product.WarehouseCategoryGUID != null
                && (product.LocalSupplierCode == code || product.LocalSupplierCode == null || product.LocalSupplierCode == "")
            )
            .GroupBy(product => product.WarehouseCategoryGUID)
            .Select(product => new GuidCountRow
            {
                Guid = product.WarehouseCategoryGUID,
                Count = SqlFunc.AggregateCount(product.UUID),
            })
            .ToListAsync();
        var countByGuid = counts
            .Where(row => row.Guid != null)
            .GroupBy(row => row.Guid!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Count), StringComparer.OrdinalIgnoreCase);
        var nodes = categories.Select(category => new LocalSupplierCategoryNodeDto
        {
            CategoryGuid = category.CategoryGUID,
            ParentGuid = string.IsNullOrWhiteSpace(category.ParentGUID) ? null : category.ParentGUID,
            Name = category.CategoryName,
            IsActive = category.IsActive,
            SortOrder = category.SortOrder,
            ProductCount = countByGuid.GetValueOrDefault(category.CategoryGUID),
        }).ToList();
        var tree = BuildTree(nodes);
        FillPathAndDepth(tree, parentPath: null, depth: 0);
        return tree;
    }

    /// <summary>
    /// 扁平节点组装为嵌套树：找不到父节点的挂到根；同层按排序号、名称定序。
    /// </summary>
    internal static List<LocalSupplierCategoryNodeDto> BuildTree(List<LocalSupplierCategoryNodeDto> nodes)
    {
        var byGuid = nodes
            .GroupBy(node => node.CategoryGuid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var roots = new List<LocalSupplierCategoryNodeDto>();
        foreach (var node in byGuid.Values)
        {
            node.Children = new List<LocalSupplierCategoryNodeDto>();
        }
        foreach (var node in byGuid.Values)
        {
            if (
                node.ParentGuid != null
                && !string.Equals(node.ParentGuid, node.CategoryGuid, StringComparison.OrdinalIgnoreCase)
                && byGuid.TryGetValue(node.ParentGuid, out var parent)
            )
            {
                parent.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        SortRecursive(roots, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return roots;
    }

    private static void SortRecursive(List<LocalSupplierCategoryNodeDto> nodes, HashSet<string> visited)
    {
        nodes.Sort((left, right) =>
        {
            var order = (left.SortOrder ?? int.MaxValue).CompareTo(right.SortOrder ?? int.MaxValue);
            return order != 0 ? order : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });
        foreach (var node in nodes)
        {
            if (visited.Add(node.CategoryGuid))
            {
                SortRecursive(node.Children, visited);
            }
        }
    }

    private static void FillPathAndDepth(List<LocalSupplierCategoryNodeDto> nodes, string? parentPath, int depth)
    {
        foreach (var node in nodes)
        {
            node.Depth = depth;
            node.FullPath = parentPath == null
                ? node.Name
                : parentPath + LocalSupplierCategoryConstants.PathSeparator + node.Name;
            if (depth < 32)
            {
                FillPathAndDepth(node.Children, node.FullPath, depth + 1);
            }
        }
    }

    /// <summary>
    /// 采集时间以 UTC 写入，但数据库读回为未指定时区；显式标为 UTC，序列化带 Z，前端按本地时区换算。
    /// </summary>
    private static DateTime? AsUtc(DateTime? value) =>
        value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null;

    private static bool Same(string? left, string right) =>
        string.Equals(left?.Trim(), right, StringComparison.OrdinalIgnoreCase);
}
