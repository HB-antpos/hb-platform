using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.Models;

/// <summary>
/// 一次「一个条码对应多个商品」的扫码，等待收银员回来选定具体商品。
/// 每次扫码一个条目：同一条码连续扫两次也是两个条目（同一条码下的两件实物可能是不同商品，不能合并）。
/// </summary>
/// <param name="Id">队列内递增编号，选择 / 放弃都按它定位，防止界面上的旧点击落到别的条目。</param>
/// <param name="Barcode">扫到的原始条码。</param>
/// <param name="Source">扫码来源（raw / keyboard-fallback），仅用于日志。</param>
/// <param name="TraceId">原扫码的 traceId，选定入车时带进购物车日志 / 审计，把「扫码」与「选定」串起来。</param>
/// <param name="StoreCode">扫码时的门店，候选商品属于它。</param>
/// <param name="CreatedAt">入队（扫码）时间，界面显示「几点几分扫的」帮助收银员对上手里的实物。</param>
/// <param name="Candidates">候选商品，至少 1 个（仅元数据命中时可能只有 1 个）。</param>
/// <param name="SameBarcodeAsPrevious">与队列里上一个条目是同一个条码：提示收银员这可能是重复扫码，可一键放弃。</param>
public sealed record PendingScanChoice(
    long Id,
    string Barcode,
    string Source,
    string? TraceId,
    string StoreCode,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SellableItemDto> Candidates,
    bool SameBarcodeAsPrevious)
{
    /// <summary>条码末 4 位，界面标题用它提示「是哪一次扫码」，不展示完整条码。</summary>
    public string BarcodeTail => Barcode.Length <= 4 ? Barcode : Barcode[^4..];
}
