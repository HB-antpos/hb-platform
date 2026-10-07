using System.Text.Json;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.StoreCash;

/// <summary>某分店某设备某营业日最终纳入现金池的日结存档。</summary>
public sealed record ResolvedCloseSelection(
    string Mode,
    IReadOnlyList<CashCloseArchive> Included,
    bool Stale,
    bool OverlapWarning,
    string? Reason,
    string? SelectedByName,
    DateTime? SelectedAtUtc
)
{
    public decimal IncludedCash => Included.Sum(archive => archive.CountedCash);
}

/// <summary>
/// 同一设备同一营业日有多份日结存档时，决定哪几份计入现金池：
/// 默认取保存时间最新的一份；手选时取选定的几份求和。手选后又出现更新的存档不会自动切换，
/// 只标记 Stale 交给人确认——自动切换会让已经对过账的余额悄悄变动。
/// </summary>
public static class CashCloseSelectionResolver
{
    public static ResolvedCloseSelection Resolve(
        IReadOnlyList<CashCloseArchive> archives,
        StoreCashCloseSelection? current
    )
    {
        if (archives.Count == 0)
        {
            return new ResolvedCloseSelection(
                StoreCashConstants.SelectionMode.Default,
                Array.Empty<CashCloseArchive>(),
                false,
                false,
                null,
                null,
                null
            );
        }

        var latest = archives
            .OrderByDescending(archive => archive.SavedAtUtc)
            .ThenByDescending(archive => archive.CloseId, StringComparer.Ordinal)
            .First();

        if (current is null || current.Mode != StoreCashConstants.SelectionMode.Manual)
        {
            return new ResolvedCloseSelection(
                StoreCashConstants.SelectionMode.Default,
                new[] { latest },
                false,
                false,
                null,
                current?.SelectedByName,
                current?.SelectedAtUtc
            );
        }

        var selectedIds = ParseCloseIds(current.CloseIdsJson);
        var included = archives
            .Where(archive => selectedIds.Contains(archive.CloseId, StringComparer.Ordinal))
            .OrderBy(archive => archive.SavedAtUtc)
            .ToList();
        var stale = current.LatestSavedAtUtcAtSelection is { } selectedLatest
            && latest.SavedAtUtc > selectedLatest;
        if (included.Count == 0)
        {
            // 手选的存档已不在数据源里（被撤销或重建）：先按默认规则计入，并强制标记需要人确认。
            return new ResolvedCloseSelection(
                StoreCashConstants.SelectionMode.Manual,
                new[] { latest },
                true,
                false,
                current.Reason,
                current.SelectedByName,
                current.SelectedAtUtc
            );
        }

        return new ResolvedCloseSelection(
            StoreCashConstants.SelectionMode.Manual,
            included,
            stale,
            current.OverlapWarning,
            current.Reason,
            current.SelectedByName,
            current.SelectedAtUtc
        );
    }

    /// <summary>统计区间两两相交（半开区间）就可能把同一段时间的现金重复计入。</summary>
    public static bool HasOverlap(IReadOnlyList<CashCloseArchive> archives)
    {
        for (var i = 0; i < archives.Count; i++)
        {
            for (var j = i + 1; j < archives.Count; j++)
            {
                if (archives[i].PeriodFromUtc < archives[j].PeriodToUtc
                    && archives[j].PeriodFromUtc < archives[i].PeriodToUtc)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static string SerializeCloseIds(IEnumerable<string> closeIds) =>
        JsonSerializer.Serialize(closeIds.ToArray());

    public static IReadOnlyList<string> ParseCloseIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}
