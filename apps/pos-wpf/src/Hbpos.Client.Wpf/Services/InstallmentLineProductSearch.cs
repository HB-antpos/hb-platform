using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 分期单“修改商品”加商品用的本地商品检索。
/// <see cref="IsExactMatch"/> 区分“编码精确命中”和“名称模糊搜索”：只有精确命中才允许界面直接加入，模糊结果必须让收银员点选。
/// </summary>
public sealed record InstallmentLineProductSearchResult(IReadOnlyList<SellableItemDto> Items, bool IsExactMatch)
{
    public static InstallmentLineProductSearchResult Empty { get; } = new([], false);
}

public interface IInstallmentLineProductSearch
{
    Task<InstallmentLineProductSearchResult> SearchAsync(
        string storeCode,
        string query,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 基于本机 <see cref="LocalSellableItemIndex"/> 的检索：先按编码精确匹配，没有命中再做名称 / 编码模糊搜索（最多 8 条），
/// 与无小票退货的取商品流程一致。
/// </summary>
public sealed class LocalInstallmentLineProductSearch(LocalSellableItemIndex index) : IInstallmentLineProductSearch
{
    private const int MaxCandidates = 8;

    public async Task<InstallmentLineProductSearchResult> SearchAsync(
        string storeCode,
        string query,
        CancellationToken cancellationToken = default)
    {
        var normalized = query?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return InstallmentLineProductSearchResult.Empty;
        }

        // 索引扫描放到后台线程并可取消，避免在 UI 线程上遍历整份目录。
        var exact = await index.FindExactMatchesAsync(storeCode, normalized, cancellationToken);
        if (exact.Count > 0)
        {
            return new InstallmentLineProductSearchResult(exact.Take(MaxCandidates).ToList(), IsExactMatch: true);
        }

        var fuzzy = await index.SearchAsync(storeCode, normalized, cancellationToken, MaxCandidates);
        return new InstallmentLineProductSearchResult(fuzzy, IsExactMatch: false);
    }
}

/// <summary>未注入商品索引时的占位实现：永远查不到商品。</summary>
public sealed class NoopInstallmentLineProductSearch : IInstallmentLineProductSearch
{
    public static NoopInstallmentLineProductSearch Instance { get; } = new();

    private NoopInstallmentLineProductSearch()
    {
    }

    public Task<InstallmentLineProductSearchResult> SearchAsync(
        string storeCode,
        string query,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(InstallmentLineProductSearchResult.Empty);
}
