using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.React.SupplierPreorders;

/// <summary>
/// 管理端「订货汇总」：矩阵（带去年对照、分店状态、总部改动标记）、总部直接改分店数量、商品去年进销与分店明细。
/// 总部改数量不改分店订单的提交状态，只在这一行上留标记（原值 / 谁 / 何时）并写操作记录；分店自己再改这一行时标记清除。
/// </summary>
public sealed partial class SupplierPreorderService
{
    private const string FilterAll = "all";
    private const string FilterOrdered = "ordered";
    private const string FilterMissed = "missed";
    private const string FilterAdjusted = "adjusted";

    /// <summary>全表合计（含每店合计）；只算开启分店、上架商品、Cartons &gt; 0 的明细。</summary>
    private static SupplierPreorderMatrixTotalsDto BuildMatrixTotals(
        IReadOnlyCollection<(SupplierPreorderStoreOrder Order, SupplierPreorderStoreOrderLine Line, SupplierPreorderFormItem Item)> counted)
    {
        var totals = new SupplierPreorderMatrixTotalsDto
        {
            OrderedItemCount = counted.Select(x => x.Item.ItemGuid).Distinct().Count(),
            TotalCartons = counted.Sum(x => x.Line.Cartons),
            TotalPieces = counted.Sum(x => x.Line.Cartons * x.Item.CartonQty),
            TotalAmount = counted.Sum(x => LineAmount(x.Line.Cartons, x.Item)),
            TotalCbm = Math.Round(counted.Sum(x => LineCbm(x.Line.Cartons, x.Item)), 3),
        };
        foreach (var group in counted.GroupBy(x => x.Order.StoreGuid, StringComparer.OrdinalIgnoreCase))
        {
            totals.StoreCartons[group.Key] = group.Sum(x => x.Line.Cartons);
            totals.StorePieces[group.Key] = group.Sum(x => x.Line.Cartons * x.Item.CartonQty);
            totals.StoreAmount[group.Key] = group.Sum(x => LineAmount(x.Line.Cartons, x.Item));
            totals.StoreCbm[group.Key] = Math.Round(group.Sum(x => LineCbm(x.Line.Cartons, x.Item)), 3);
        }

        return totals;
    }

    private static SupplierPreorderLineMarkDto? ToLineMark(SupplierPreorderStoreOrderLine? line) =>
        line is { HqOriginalCartons: not null, HqAdjustedAtUtc: not null }
            ? new SupplierPreorderLineMarkDto
            {
                OriginalCartons = line.HqOriginalCartons.Value,
                AdjustedBy = line.HqAdjustedBy,
                AdjustedAtUtc = AsUtc(line.HqAdjustedAtUtc)!.Value,
            }
            : null;

    /// <summary>有总部改动标记的明细（开启分店 + 上架商品；数量被调成 0 的行也算，它们要留住标记）。</summary>
    private static IEnumerable<(SupplierPreorderStoreOrder Order, SupplierPreorderStoreOrderLine Line, SupplierPreorderFormItem Item)> MarkedLines(FormSnapshot snapshot)
    {
        var enabled = snapshot.EnabledStoreGuids;
        var ordersByGuid = snapshot.Orders.ToDictionary(o => o.OrderGuid);
        foreach (var line in snapshot.Lines)
        {
            if (line.HqOriginalCartons is null
                || !ordersByGuid.TryGetValue(line.OrderGuid, out var order)
                || !enabled.Contains(order.StoreGuid)
                || !snapshot.ItemsByGuid.TryGetValue(line.ItemGuid, out var item)
                || !item.IsActive)
            {
                continue;
            }

            yield return (order, line, item);
        }
    }

    // ---------------- 矩阵 ----------------

    public async Task<SupplierPreorderMatrixDto> GetMatrixAsync(string formGuid, SupplierPreorderMatrixQueryDto query)
    {
        var form = await RequireFormAsync(formGuid);
        var snapshot = await LoadSnapshotAsync(form);
        var counted = snapshot.CountedLines().ToList();
        var byItem = counted.GroupBy(x => x.Item.ItemGuid).ToDictionary(g => g.Key, g => g.ToList());
        var marksByItem = MarkedLines(snapshot).GroupBy(x => x.Item.ItemGuid).ToDictionary(g => g.Key, g => g.ToList());
        var storeRefs = snapshot.EnabledStores.Select(s => ToStoreRef(s.StoreGuid, s.StoreCode, s.StoreName)).ToList();

        // 去年历史：全部分店合计 + 每个开启分店（只读快照，快照还没算出来时全部降级为「没有记录」）
        var storeCodes = snapshot.EnabledStores.Select(s => s.StoreCode.Trim()).Append(AllStoresKey).ToList();
        var history = await LoadHistoryStateAsync(form, storeCodes);
        var activeItems = snapshot.Items.Where(i => i.IsActive).ToList();
        var sources = activeItems.ToDictionary(i => i.ItemGuid, i => ResolveItemSource(i, history));
        decimal AllStoresSales(SupplierPreorderFormItem item) =>
            sources[item.ItemGuid] is { } source ? AllStores(history, source.ProductCode)?.SalesQty ?? 0m : 0m;

        // 筛选芯片的计数不受当前筛选与分页影响
        var counts = new SupplierPreorderMatrixCountsDto
        {
            Total = activeItems.Count,
            Ordered = byItem.Count,
            Missed = activeItems.Count(i => !byItem.ContainsKey(i.ItemGuid) && AllStoresSales(i) > 0),
            Adjusted = marksByItem.Count,
        };

        var filter = string.IsNullOrWhiteSpace(query.Filter)
            ? (query.OnlyOrdered ? FilterOrdered : FilterAll)
            : query.Filter.Trim().ToLowerInvariant();
        IEnumerable<SupplierPreorderFormItem> items = activeItems;
        items = filter switch
        {
            FilterOrdered => items.Where(i => byItem.ContainsKey(i.ItemGuid)),
            FilterMissed => items.Where(i => !byItem.ContainsKey(i.ItemGuid) && AllStoresSales(i) > 0),
            FilterAdjusted => items.Where(i => marksByItem.ContainsKey(i.ItemGuid)),
            _ => items,
        };
        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            items = items.Where(i => string.Equals(i.Category, query.Category, StringComparison.OrdinalIgnoreCase));
        }

        items = FilterByKeyword(items, query.Keyword);

        // 排序：没有数据的商品一律排最后，同值按原表序号（SortOrder）
        var sortStoreGuid = query.SortStoreGuid?.Trim();
        var sortKey = (query.Sort ?? string.Empty).Trim();
        Dictionary<string, int>? sortStoreCartons = null;
        if (sortKey is "storeCartonsDesc" or "storeCartonsAsc")
        {
            if (string.IsNullOrEmpty(sortStoreGuid))
            {
                throw new SupplierPreorderException("按分店排序需要选择一家分店", "SORT_STORE_INVALID");
            }

            // 前端会把「按某店排序」记在本机；那家店后来被关掉（或订单表换了）时回退成默认序号，不让整页加载失败
            if (!snapshot.EnabledStoreGuids.Contains(sortStoreGuid))
            {
                sortKey = "seq";
            }
        }

        if (sortKey is "storeCartonsDesc" or "storeCartonsAsc")
        {
            sortStoreCartons = counted
                .Where(x => string.Equals(x.Order.StoreGuid, sortStoreGuid, StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => x.Item.ItemGuid)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Line.Cartons));
        }

        decimal? LastYearSalesOrNull(SupplierPreorderFormItem item) =>
            sources[item.ItemGuid] is { } source && AllStores(history, source.ProductCode) is { HasAny: true } all ? all.SalesQty : null;
        decimal? SellThroughOrNull(SupplierPreorderFormItem item) =>
            sources[item.ItemGuid] is { } source && AllStores(history, source.ProductCode) is { HasAny: true } all
                ? SupplierPreorderHistoryRules.SellThrough(all.SalesQty, all.PurchaseQty)
                : null;
        // 今年订得有多少相对去年卖的：订得越少越靠前，用来找漏订；去年没卖过的没有比值
        decimal? RatioOrNull(SupplierPreorderFormItem item)
        {
            var sales = LastYearSalesOrNull(item);
            return sales is > 0 ? (byItem.TryGetValue(item.ItemGuid, out var rows) ? rows.Sum(x => x.Line.Cartons) : 0) * item.CartonQty / sales : null;
        }

        static IEnumerable<SupplierPreorderFormItem> OrderNullsLast(IEnumerable<SupplierPreorderFormItem> source, Func<SupplierPreorderFormItem, decimal?> key, bool descending) =>
            (descending
                ? source.OrderBy(i => key(i) is null ? 1 : 0).ThenByDescending(i => key(i) ?? 0m)
                : source.OrderBy(i => key(i) is null ? 1 : 0).ThenBy(i => key(i) ?? 0m))
            .ThenBy(i => i.SortOrder);

        items = sortKey switch
        {
            "totalCartonsDesc" => OrderNullsLast(items, i => byItem.TryGetValue(i.ItemGuid, out var r) ? r.Sum(x => x.Line.Cartons) : 0, true),
            "lastYearSalesDesc" => OrderNullsLast(items, LastYearSalesOrNull, true),
            "sellThroughDesc" => OrderNullsLast(items, SellThroughOrNull, true),
            "ratioAsc" => OrderNullsLast(items, RatioOrNull, false),
            "priceAsc" => OrderNullsLast(items, i => i.Price, false),
            "priceDesc" => OrderNullsLast(items, i => i.Price, true),
            "codeAsc" => items.OrderBy(i => i.SupplierItemCode, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.SortOrder),
            "storeCartonsDesc" => OrderNullsLast(items, i => sortStoreCartons!.GetValueOrDefault(i.ItemGuid), true),
            "storeCartonsAsc" => OrderNullsLast(items, i => sortStoreCartons!.GetValueOrDefault(i.ItemGuid), false),
            _ => items,
        };

        var paged = Page(items.ToList(), query.Page, query.PageSize);
        var images = await LoadProductImagesAsync(paged.Items!);
        // 借用参考商品的行，展示参考商品的货号
        var refProducts = await _history.LoadProductsAsync(paged.Items!
            .Where(i => sources[i.ItemGuid] is { Source: "Ref" })
            .Select(i => sources[i.ItemGuid]!.Value.ProductCode));

        var statuses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var store in snapshot.EnabledStores)
        {
            statuses[store.StoreGuid] = snapshot.Orders.FirstOrDefault(o => string.Equals(o.StoreGuid, store.StoreGuid, StringComparison.OrdinalIgnoreCase))?.Status
                                        ?? "None";
        }

        return new SupplierPreorderMatrixDto
        {
            Stores = storeRefs,
            Totals = BuildMatrixTotals(counted),
            StoreStatuses = statuses,
            Counts = counts,
            Categories = activeItems.Select(i => i.Category).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().OrderBy(c => c).Cast<string>().ToList(),
            Editable = form.Status != SupplierPreorderFormStatuses.Draft,
            History = history.Status,
            Rows = new PagedResult<SupplierPreorderMatrixRowDto>
            {
                Total = paged.Total,
                Page = paged.Page,
                PageSize = paged.PageSize,
                Items = paged.Items!.Select(item =>
                {
                    var rows = byItem.GetValueOrDefault(item.ItemGuid) ?? new();
                    var cartons = rows.Sum(x => x.Line.Cartons);
                    var row = new SupplierPreorderMatrixRowDto
                    {
                        ItemGuid = item.ItemGuid,
                        Seq = item.Seq,
                        SupplierItemCode = item.SupplierItemCode,
                        Barcode = item.Barcode,
                        Name = item.Name,
                        Category = item.Category,
                        ImageUrl = ResolveImageUrl(item, images),
                        Price = item.Price,
                        PrevPrice = item.PrevPrice,
                        IsNew = item.MatchMethod == SupplierPreorderMatchMethods.None,
                        CartonQty = item.CartonQty,
                        CartonCbm = item.CartonCbm,
                        StoreCartons = rows.GroupBy(x => x.Order.StoreGuid).ToDictionary(g => g.Key, g => g.Sum(x => x.Line.Cartons)),
                        TotalCartons = cartons,
                        TotalPieces = cartons * item.CartonQty,
                        TotalAmount = LineAmount(cartons, item),
                        TotalCbm = Math.Round(LineCbm(cartons, item), 3),
                    };
                    if (marksByItem.TryGetValue(item.ItemGuid, out var marked))
                    {
                        foreach (var m in marked)
                        {
                            if (ToLineMark(m.Line) is { } mark)
                            {
                                row.StoreMarks[m.Order.StoreGuid] = mark;
                            }
                        }
                    }

                    if (sources[item.ItemGuid] is { } source)
                    {
                        var all = AllStores(history, source.ProductCode);
                        if (all is not null && all.HasAny)
                        {
                            var refItemNumber = source.Source == "Ref" ? refProducts.GetValueOrDefault(source.ProductCode)?.ItemNumber : null;
                            row.LastYear = new SupplierPreorderMatrixLastYearDto
                            {
                                Source = source.Source,
                                ProductCode = source.ProductCode,
                                RefDisplayCode = source.Source == "Ref" ? (string.IsNullOrWhiteSpace(refItemNumber) ? source.ProductCode : refItemNumber) : null,
                                PurchaseQty = all.PurchaseQty,
                                SalesQty = all.SalesQty,
                                SellThrough = SupplierPreorderHistoryRules.SellThrough(all.SalesQty, all.PurchaseQty),
                                Daily = all.Daily,
                            };
                        }

                        foreach (var store in snapshot.EnabledStores)
                        {
                            var payload = history.Payloads.GetValueOrDefault((store.StoreCode.Trim(), source.ProductCode.Trim()));
                            if (payload?.HasAny == true)
                            {
                                row.StoreLastYearSales[store.StoreGuid] = payload.SalesQty;
                            }
                        }
                    }

                    return row;
                }).ToList(),
            },
        };
    }

    // ---------------- 添加分店：候选与单店开启 ----------------

    /// <summary>
    /// 「添加分店」下拉里的未开启分店：附上去年这批商品（含参考商品）在该店的销量与授权下单人，帮助判断值不值得开启。
    /// 没有历史快照时销量为 0，不影响开启。
    /// </summary>
    public async Task<List<SupplierPreorderStoreCandidateDto>> GetStoreCandidatesAsync(string formGuid)
    {
        var form = await RequireFormAsync(formGuid);
        var candidates = (await BuildFormStoreStatusesAsync(form)).Where(s => !s.Enabled).ToList();
        if (candidates.Count == 0)
        {
            return new();
        }

        var snapshot = await LoadSnapshotAsync(form);
        var history = await LoadHistoryStateAsync(form, candidates.Select(c => c.StoreCode.Trim()).Append(AllStoresKey).ToList());
        var sources = snapshot.Items.Where(i => i.IsActive).Select(i => ResolveItemSource(i, history)).Where(x => x is not null).Select(x => x!.Value.ProductCode.Trim()).ToList();
        return candidates.Select(c =>
        {
            var payloads = sources
                .Select(code => history.Payloads.GetValueOrDefault((c.StoreCode.Trim(), code)))
                .Where(p => p?.HasAny == true)
                .ToList();
            return new SupplierPreorderStoreCandidateDto
            {
                StoreGuid = c.StoreGuid,
                StoreCode = c.StoreCode,
                StoreName = c.StoreName,
                Orderers = c.Orderers,
                LastYearSalesQty = payloads.Sum(p => p!.SalesQty),
                LastYearItemCount = payloads.Count,
            };
        })
        .OrderByDescending(c => c.LastYearSalesQty)
        .ThenBy(c => c.StoreCode)
        .ToList();
    }

    /// <summary>只开启一家分店（不影响其他分店）：已开启则原样返回；记录动作 StoreEnabled。</summary>
    public async Task<SupplierPreorderFormStoreStatusDto> EnableFormStoreAsync(string formGuid, string storeGuid)
    {
        var form = await RequireFormAsync(formGuid);
        if (string.IsNullOrWhiteSpace(storeGuid))
        {
            throw new SupplierPreorderException("包含不存在或已停用的分店", "STORE_INVALID");
        }

        await ApplyFormStoresAsync(form, new[] { storeGuid.Trim() }, replace: false, logAction: "StoreEnabled");
        return (await BuildFormStoreStatusesAsync(form)).FirstOrDefault(s => string.Equals(s.StoreGuid, storeGuid.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? throw new SupplierPreorderException("包含不存在或已停用的分店", "STORE_INVALID");
    }

    // ---------------- 总部直接改分店数量 ----------------

    /// <summary>总部改某店某商品的箱数：不改订单提交状态；与分店原填值不同就留标记，改回原值则清除标记。</summary>
    public async Task<SupplierPreorderAdminSaveLineResultDto> AdminSaveLineAsync(string formGuid, string storeGuid, string itemGuid, SupplierPreorderSaveLineDto request)
    {
        if (request.Cartons is < 0 or > MaxCartonsPerLine)
        {
            throw new SupplierPreorderException($"箱数必须是 0–{MaxCartonsPerLine} 的整数", "CARTONS_INVALID");
        }

        var form = await RequireFormAsync(formGuid);
        if (form.Status == SupplierPreorderFormStatuses.Draft)
        {
            throw new SupplierPreorderException("订单表尚未发布，不能修改分店数量", "FORM_NOT_PUBLISHED", 409);
        }

        var snapshot = await LoadSnapshotAsync(form);
        var storeRow = snapshot.EnabledStores.FirstOrDefault(s => string.Equals(s.StoreGuid, storeGuid, StringComparison.OrdinalIgnoreCase))
                       ?? throw new SupplierPreorderException("这张订单表没有开启该分店", "STORE_NOT_ENABLED", 409);
        if (!snapshot.ItemsByGuid.TryGetValue(itemGuid, out var item) || !item.IsActive)
        {
            throw new SupplierPreorderException("商品不存在或已下架", "ITEM_NOT_FOUND", 404);
        }

        var now = NowUtc;
        var storeDto = new SupplierPreorderMyStoreDto { StoreGuid = storeRow.StoreGuid, StoreCode = storeRow.StoreCode, StoreName = storeRow.StoreName };
        var order = await EnsureStoreOrderAsync(form.FormGuid, storeDto, now);
        await RetryOnceAsync(() => RunInTransactionAsync(async () =>
        {
            // 重试时重新读行；保存的是绝对箱数，重试不会重复累加
            var line = await _db.Queryable<SupplierPreorderStoreOrderLine>().FirstAsync(l => l.OrderGuid == order.OrderGuid && l.ItemGuid == itemGuid);
            var before = line?.Cartons ?? 0;
            if (before == request.Cartons)
            {
                return;
            }

            // 分店自己填的原值：连续多次调整始终保留最初那个
            var original = line?.HqOriginalCartons ?? before;
            await WriteAuthLogAsync(
                SupplierPreorderLogScopes.Order,
                SupplierPreorderLogActions.LineAdjusted,
                BuildLineAdjustDetail(storeRow.StoreName, item, before, request.Cartons, original),
                form.FormGuid);

            if (request.Cartons == original)
            {
                // 改回分店原值：不再算「总部改过」，标记清除
                if (request.Cartons == 0)
                {
                    await _db.Deleteable<SupplierPreorderStoreOrderLine>().Where(l => l.LineGuid == line!.LineGuid).ExecuteCommandAsync();
                }
                else
                {
                    line!.Cartons = request.Cartons;
                    line.UpdatedAtUtc = now;
                    line.UpdatedBy = CurrentUserName;
                    line.HqOriginalCartons = null;
                    line.HqAdjustedAtUtc = null;
                    line.HqAdjustedBy = null;
                    await _db.Updateable(line).UpdateColumns(l => new { l.Cartons, l.UpdatedAtUtc, l.UpdatedBy, l.HqOriginalCartons, l.HqAdjustedAtUtc, l.HqAdjustedBy }).ExecuteCommandAsync();
                }

                return;
            }

            // 数量调成 0 时这一行也保留（Cartons = 0），只为留住标记；所有汇总都只算 Cartons > 0
            if (line is null)
            {
                await _db.Insertable(new SupplierPreorderStoreOrderLine
                {
                    OrderGuid = order.OrderGuid,
                    FormGuid = form.FormGuid,
                    ItemGuid = itemGuid,
                    Cartons = request.Cartons,
                    UpdatedAtUtc = now,
                    UpdatedBy = CurrentUserName,
                    HqOriginalCartons = original,
                    HqAdjustedAtUtc = now,
                    HqAdjustedBy = CurrentUserName,
                }).ExecuteCommandAsync();
            }
            else
            {
                line.Cartons = request.Cartons;
                line.UpdatedAtUtc = now;
                line.UpdatedBy = CurrentUserName;
                line.HqOriginalCartons = original;
                line.HqAdjustedAtUtc = now;
                line.HqAdjustedBy = CurrentUserName;
                await _db.Updateable(line).UpdateColumns(l => new { l.Cartons, l.UpdatedAtUtc, l.UpdatedBy, l.HqOriginalCartons, l.HqAdjustedAtUtc, l.HqAdjustedBy }).ExecuteCommandAsync();
            }
        }));

        var fresh = await LoadSnapshotAsync(form);
        var counted = fresh.CountedLines().ToList();
        var rowCartons = counted.Where(x => x.Item.ItemGuid == itemGuid).Sum(x => x.Line.Cartons);
        var freshLine = fresh.Lines.FirstOrDefault(l => l.OrderGuid == order.OrderGuid && l.ItemGuid == itemGuid);
        return new SupplierPreorderAdminSaveLineResultDto
        {
            ItemGuid = itemGuid,
            StoreGuid = storeRow.StoreGuid,
            Cartons = freshLine?.Cartons ?? 0,
            Mark = ToLineMark(freshLine),
            RowTotalCartons = rowCartons,
            RowTotalPieces = rowCartons * item.CartonQty,
            RowTotalAmount = LineAmount(rowCartons, item),
            RowTotalCbm = Math.Round(LineCbm(rowCartons, item), 3),
            Totals = BuildMatrixTotals(counted),
            AdjustedCount = MarkedLines(fresh).Select(x => x.Item.ItemGuid).Distinct().Count(),
        };
    }

    /// <summary>总部调整的操作记录：在普通变更记录后注明分店原填的箱数，一眼看出改了多少。</summary>
    internal static string BuildLineAdjustDetail(string storeName, SupplierPreorderFormItem item, int beforeCartons, int afterCartons, int originalCartons)
    {
        var text = BuildLineChangeDetail(storeName, item, beforeCartons, afterCartons, false);
        return originalCartons == beforeCartons ? text + " · 总部调整" : text + $" · 总部调整（分店原填 {originalCartons} 箱）";
    }

    // ---------------- 商品历史进销（管理端抽屉） ----------------

    /// <summary>
    /// 管理端抽屉：全部分店合计的大图 + 每家分店（开启的 + 去年有记录的）去年进销明细与今年已订。
    /// 与下单端展开行相比：不绑定「当前分店」，每家分店都带完整的日销与进货批次，前端可切换看单店。
    /// </summary>
    public async Task<SupplierPreorderItemHistoryDetailDto> GetAdminItemHistoryAsync(string formGuid, string itemGuid)
    {
        var form = await RequireFormAsync(formGuid);
        var snapshot = await LoadSnapshotAsync(form);
        if (!snapshot.ItemsByGuid.TryGetValue(itemGuid, out var item) || !item.IsActive)
        {
            throw new SupplierPreorderException("商品不存在或已下架", "ITEM_NOT_FOUND", 404);
        }

        var storeNames = await LoadStoreNamesAsync();
        var storeCodes = storeNames.Keys.Append(AllStoresKey).ToList();
        var state = await LoadHistoryStateAsync(form, storeCodes);
        var detail = new SupplierPreorderItemHistoryDetailDto
        {
            ItemGuid = item.ItemGuid,
            WindowFrom = state.Status.WindowFrom,
            WindowTo = state.Status.WindowTo,
            KeyDates = state.Status.KeyDates,
        };
        var source = ResolveItemSource(item, state);
        if (source is null)
        {
            return detail;
        }

        var (sourceKind, productCode) = source.Value;
        detail.Source = sourceKind;
        detail.ProductCode = productCode;
        if (sourceKind == "Ref")
        {
            var products = await _history.LoadProductsAsync(new[] { productCode });
            detail.Ref = ToRefDto(item, products, state);
        }

        var all = AllStores(state, productCode);
        detail.Store = BuildStoreHistory("ALL", "全部分店", all, full: true);
        detail.SuggestedCartons = all is null ? null : SupplierPreorderHistoryRules.SuggestedCartons(all.SalesQty, item.CartonQty);

        var thisYear = snapshot.CountedLines()
            .Where(x => x.Item.ItemGuid == item.ItemGuid)
            .GroupBy(x => x.Order.StoreGuid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Line.Cartons), StringComparer.OrdinalIgnoreCase);
        var enabledByCode = snapshot.EnabledStores
            .GroupBy(s => s.StoreCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var withData = state.Payloads.Keys
            .Where(k => k.StoreCode != AllStoresKey && string.Equals(k.ProductCode, productCode, StringComparison.OrdinalIgnoreCase))
            .Select(k => k.StoreCode);
        detail.Stores = withData.Concat(enabledByCode.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .Select(code =>
            {
                enabledByCode.TryGetValue(code, out var enabledStore);
                var name = enabledStore?.StoreName ?? storeNames.GetValueOrDefault(code) ?? code;
                var dto = BuildStoreHistory(code, name, state.Payloads.GetValueOrDefault((code, productCode)), full: true);
                dto.StoreGuid = enabledStore?.StoreGuid;
                // 开启的分店没订也给 0（抽屉里要显示可编辑的输入框）；没开启的分店不参与订货
                dto.ThisYearCartons = enabledStore is null ? null : thisYear.GetValueOrDefault(enabledStore.StoreGuid);
                return dto;
            })
            .ToList();
        return detail;
    }
}
