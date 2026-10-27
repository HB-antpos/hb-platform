using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.React.SupplierPreorders;

/// <summary>
/// 去年历史与参考商品（PR 2）：列表、展开行只读快照；参考商品由后台计算时系统推荐，采购可人工指定 / 清除 / 交回系统。
/// 权限：下单端只给本店数据；「各分店去年对比」只有能看其他分店的人（CanViewOtherStores 或 Manage）才返回。
/// </summary>
public sealed partial class SupplierPreorderService
{
    private const int RefSearchLimit = 30;
    private const int RefRecommendLimit = 3;
    private static readonly string AllStoresKey = SupplierPreorderHistorySnapshot.FormMetaKey;

    /// <summary>排队重算；快照表还没建（迁移没执行）时只记日志，不影响导入、发布这些主流程。</summary>
    private async Task TryRequestHistoryAsync(string formGuid)
    {
        try
        {
            await _history.RequestAsync(formGuid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "供应商预订单去年历史排队失败（迁移 20261023.002 是否已执行？）：{FormGuid}", formGuid);
        }
    }

    private sealed record HistoryState(
        SupplierPreorderHistoryStatusDto Status,
        (DateTime From, DateTime To)? SnapshotWindow,
        Dictionary<(string StoreCode, string ProductCode), SupplierPreorderHistoryPayload> Payloads);

    /// <summary>
    /// 历史状态 + 所需快照。窗口优先取「快照实际算时」的窗口（日销下标以它为起点），还没算过时显示订单表配置的窗口。
    /// 快照表不存在等异常时降级为「没有数据」，不让下单页报错。
    /// </summary>
    private async Task<HistoryState> LoadHistoryStateAsync(SupplierPreorderForm form, IReadOnlyCollection<string>? storeCodes)
    {
        SupplierPreorderHistoryService.HistoryMeta? meta = null;
        var payloads = new Dictionary<(string StoreCode, string ProductCode), SupplierPreorderHistoryPayload>(new SupplierPreorderHistoryRules.StoreProductComparer());
        try
        {
            meta = await _history.ReadMetaAsync(form.FormGuid);
            if (storeCodes is { Count: > 0 } && meta?.CompletedAtUtc is not null)
            {
                payloads = await _history.LoadPayloadsAsync(form.FormGuid, storeCodes);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取供应商预订单去年历史失败：{FormGuid}", form.FormGuid);
        }

        var configured = SupplierPreorderHistoryRules.ResolveWindow(form.HistoryFrom, form.HistoryTo, form.HolidayType, form.SeasonYear);
        (DateTime From, DateTime To)? snapshotWindow = meta is { CompletedAtUtc: not null, WindowFrom: not null, WindowTo: not null }
            ? (meta.WindowFrom.Value, meta.WindowTo.Value)
            : null;
        var shown = snapshotWindow ?? configured;
        var status = new SupplierPreorderHistoryStatusDto
        {
            Status = meta?.Status ?? (configured is null ? "NoWindow" : "None"),
            ComputedAtUtc = AsUtc(meta?.CompletedAtUtc),
            RequestedAtUtc = AsUtc(meta?.RequestedAtUtc),
            Error = meta?.Error,
            WindowFrom = shown?.From,
            WindowTo = shown?.To,
            KeyDates = shown is null
                ? new()
                : SupplierPreorderKeyDates.InRange(shown.Value.From, shown.Value.To)
                    .Select(k => new SupplierPreorderKeyDateDto { Date = k.Date, Key = k.Key })
                    .ToList(),
        };
        return new HistoryState(status, snapshotWindow, payloads);
    }

    private static SupplierPreorderHistoryPayload? AllStores(HistoryState state, string code) =>
        state.Payloads.GetValueOrDefault((AllStoresKey, code.Trim()));

    private static (string Source, string ProductCode)? ResolveItemSource(SupplierPreorderFormItem item, HistoryState state) =>
        SupplierPreorderRefDecisions.ResolveSource(item.MatchedProductCode, item.RefProductCode, code => AllStores(state, code));

    private static string? HttpImage(string? image) =>
        !string.IsNullOrWhiteSpace(image)
        && (image.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || image.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            ? image.Trim()
            : null;

    private static SupplierPreorderItemRefDto? ToRefDto(SupplierPreorderFormItem item, IReadOnlyDictionary<string, SupplierPreorderRefCandidate> products, HistoryState? state)
    {
        if (item.RefSource is null && item.RefProductCode is null)
        {
            return null;
        }

        var product = item.RefProductCode is null ? null : products.GetValueOrDefault(item.RefProductCode);
        return new SupplierPreorderItemRefDto
        {
            Source = item.RefSource ?? SupplierPreorderRefSources.Auto,
            ProductCode = item.RefProductCode,
            ItemNumber = product?.ItemNumber,
            Barcode = product?.Barcode,
            Name = product?.Name,
            PurchasePrice = product?.PurchasePrice,
            ImageUrl = HttpImage(product?.ProductImage),
            Score = item.RefScore,
            SetBy = item.RefSetBy,
            SetAtUtc = AsUtc(item.RefSetAtUtc),
            SalesQty = item.RefProductCode is null || state is null ? null : AllStores(state, item.RefProductCode)?.SalesQty ?? 0m,
        };
    }

    private static List<SupplierPreorderDateRangeDto> ToRanges(IEnumerable<SupplierPreorderDateRange> ranges) =>
        ranges.Select(r => new SupplierPreorderDateRangeDto { From = r.From, To = r.To }).ToList();

    /// <summary>列表里一行的去年本店数据；本品与参考都没有记录时为 null。</summary>
    private static SupplierPreorderItemHistoryDto? BuildItemHistory(
        SupplierPreorderFormItem item,
        string storeCode,
        HistoryState state,
        IReadOnlyDictionary<string, SupplierPreorderRefCandidate>? refProducts)
    {
        var source = ResolveItemSource(item, state);
        if (source is null)
        {
            return null;
        }

        var payload = state.Payloads.GetValueOrDefault((storeCode.Trim(), source.Value.ProductCode.Trim()));
        var dto = new SupplierPreorderItemHistoryDto
        {
            Source = source.Value.Source,
            ProductCode = source.Value.ProductCode,
            Ref = source.Value.Source == "Ref" && refProducts is not null ? ToRefDto(item, refProducts, state) : null,
            HasStoreData = payload?.HasAny == true,
        };
        if (payload is null)
        {
            return dto;
        }

        dto.PurchaseFirst = payload.PurchaseFirst;
        dto.PurchaseLast = payload.PurchaseLast;
        dto.PurchaseBatches = payload.PurchaseBatches;
        dto.PurchaseQty = payload.PurchaseQty;
        dto.SalesFirst = payload.SalesFirst;
        dto.SalesLast = payload.SalesLast;
        dto.SalesQty = payload.SalesQty;
        dto.SellThrough = SupplierPreorderHistoryRules.SellThrough(payload.SalesQty, payload.PurchaseQty);
        dto.DiscountStatus = payload.DiscountStatus;
        dto.HasDiscount = payload.DiscountQty > 0;
        dto.DiscountShare = SupplierPreorderHistoryRules.DiscountShare(payload.DiscountQty, payload.RegularQty);
        dto.DiscountRanges = ToRanges(payload.DiscountRanges);
        dto.Daily = payload.Daily;
        return dto;
    }

    private static SupplierPreorderStoreHistoryDto BuildStoreHistory(string storeCode, string storeName, SupplierPreorderHistoryPayload? payload, bool full)
    {
        var dto = new SupplierPreorderStoreHistoryDto { StoreCode = storeCode, StoreName = storeName, HasData = payload?.HasAny == true };
        if (payload is null)
        {
            return dto;
        }

        dto.PurchaseFirst = payload.PurchaseFirst;
        dto.PurchaseLast = payload.PurchaseLast;
        dto.PurchaseBatches = payload.PurchaseBatches;
        dto.PurchaseQty = payload.PurchaseQty;
        dto.WarehouseQty = payload.WarehouseQty;
        dto.SupplierQty = payload.SupplierQty;
        dto.SalesFirst = payload.SalesFirst;
        dto.SalesLast = payload.SalesLast;
        dto.SalesQty = payload.SalesQty;
        dto.SellThrough = SupplierPreorderHistoryRules.SellThrough(payload.SalesQty, payload.PurchaseQty);
        dto.DiscountStatus = payload.DiscountStatus;
        dto.HasDiscount = payload.DiscountQty > 0;
        dto.DiscountShare = SupplierPreorderHistoryRules.DiscountShare(payload.DiscountQty, payload.RegularQty);
        dto.RetailPrice = payload.RetailPrice;
        dto.DiscountPriceMin = payload.DiscountPriceMin;
        if (full)
        {
            dto.DiscountRanges = ToRanges(payload.DiscountRanges);
            dto.Daily = payload.Daily;
            dto.Batches = payload.Batches
                .Select(b => new SupplierPreorderHistoryBatchDto { Date = b.Date, Source = b.Source, Quantity = b.Quantity, DocumentNo = b.DocumentNo })
                .ToList();
        }

        return dto;
    }

    // ---------------- 下单端：展开行 ----------------

    public async Task<SupplierPreorderItemHistoryDetailDto> GetOrderingItemHistoryAsync(string formGuid, string itemGuid, string storeGuid)
    {
        var (snapshot, identity, myStores) = await LoadOrderingAsync(formGuid);
        var store = RequireMyStore(myStores, storeGuid);
        if (!snapshot.ItemsByGuid.TryGetValue(itemGuid, out var item) || !item.IsActive)
        {
            throw new SupplierPreorderException("商品不存在或已下架", "ITEM_NOT_FOUND", 404);
        }

        // 不能看其他分店的人只读本店 + 全部分店合计（合计只用来判定本品有没有历史，不返回给前端）
        var allStoreRows = identity.CanViewOtherStores ? await LoadStoreNamesAsync() : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var storeCodes = new List<string> { store.StoreCode.Trim(), AllStoresKey };
        if (identity.CanViewOtherStores)
        {
            storeCodes.AddRange(allStoreRows.Keys);
        }

        var state = await LoadHistoryStateAsync(snapshot.Form, storeCodes);
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

        var mine = state.Payloads.GetValueOrDefault((store.StoreCode.Trim(), productCode));
        detail.Store = BuildStoreHistory(store.StoreCode, store.StoreName, mine, full: true);
        detail.Store.IsCurrent = true;
        detail.SuggestedCartons = mine is null ? null : SupplierPreorderHistoryRules.SuggestedCartons(mine.SalesQty, item.CartonQty);

        if (identity.CanViewOtherStores)
        {
            // 今年各店已订（只算开启分店）；对比表列出：去年有数据的店 + 本订单表开启的店
            var thisYear = snapshot.CountedLines()
                .Where(x => x.Item.ItemGuid == item.ItemGuid)
                .GroupBy(x => x.Order.StoreCode.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Line.Cartons), StringComparer.OrdinalIgnoreCase);
            var enabledCodes = snapshot.EnabledStores.Select(s => s.StoreCode.Trim());
            var withData = state.Payloads.Keys
                .Where(k => k.StoreCode != AllStoresKey && string.Equals(k.ProductCode, productCode, StringComparison.OrdinalIgnoreCase))
                .Select(k => k.StoreCode);
            detail.Stores = withData.Concat(enabledCodes).Append(store.StoreCode.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .Select(code =>
                {
                    var name = allStoreRows.GetValueOrDefault(code)
                               ?? snapshot.EnabledStores.FirstOrDefault(s => string.Equals(s.StoreCode.Trim(), code, StringComparison.OrdinalIgnoreCase))?.StoreName
                               ?? code;
                    var dto = BuildStoreHistory(code, name, state.Payloads.GetValueOrDefault((code, productCode)), full: false);
                    dto.IsCurrent = string.Equals(code, store.StoreCode.Trim(), StringComparison.OrdinalIgnoreCase);
                    dto.ThisYearCartons = thisYear.TryGetValue(code, out var cartons) ? cartons : null;
                    return dto;
                })
                .ToList();
        }

        return detail;
    }

    private async Task<Dictionary<string, string>> LoadStoreNamesAsync()
    {
        var stores = await _db.Queryable<Store>()
            .Where(s => !s.IsDeleted && s.StoreCode != null && s.StoreCode != "")
            .Select(s => new { s.StoreCode, s.StoreName })
            .ToListAsync();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in stores)
        {
            var code = s.StoreCode!.Trim();
            if (code.Length > 0 && !result.ContainsKey(code))
            {
                result[code] = string.IsNullOrWhiteSpace(s.StoreName) ? code : s.StoreName.Trim();
            }
        }

        return result;
    }

    // ---------------- 管理端 ----------------

    /// <summary>订单表详情里的历史状态与参考商品计数。</summary>
    private async Task FillHistorySummaryAsync(SupplierPreorderForm form, SupplierPreorderFormDetailDto detail)
    {
        var state = await LoadHistoryStateAsync(form, new[] { AllStoresKey });
        detail.History = state.Status;
        var items = await _db.Queryable<SupplierPreorderFormItem>()
            .Where(i => i.FormGuid == form.FormGuid && i.IsActive)
            .Select(i => new SupplierPreorderFormItem { ItemGuid = i.ItemGuid, MatchedProductCode = i.MatchedProductCode, RefProductCode = i.RefProductCode, RefSource = i.RefSource })
            .ToListAsync();
        foreach (var item in items)
        {
            var ownSales = item.MatchedProductCode is not null && (AllStores(state, item.MatchedProductCode)?.SalesQty ?? 0m) > 0;
            if (ownSales)
            {
                detail.OwnHistoryCount++;
            }
            else if (item.RefProductCode is null)
            {
                detail.NeedRefCount++;
            }

            if (item.RefProductCode is not null && item.RefSource == SupplierPreorderRefSources.Auto)
            {
                detail.RefAutoCount++;
            }
            else if (item.RefProductCode is not null && item.RefSource == SupplierPreorderRefSources.Manual)
            {
                detail.RefManualCount++;
            }
        }
    }

    public async Task<SupplierPreorderFormDetailDto> RecomputeHistoryAsync(string formGuid)
    {
        var form = await RequireFormAsync(formGuid);
        try
        {
            await _history.RequestAsync(form.FormGuid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "供应商预订单去年历史排队失败：{FormGuid}", formGuid);
            throw new SupplierPreorderException("排队失败：历史快照表可能还没建好（需执行迁移 20261023.002）", "HISTORY_UNAVAILABLE", 503);
        }

        return await GetFormAsync(formGuid);
    }

    private static SupplierPreorderRefCandidateDto ToCandidateDto(SupplierPreorderRefCandidate c, SupplierPreorderRefScore score) => new()
    {
        ProductCode = c.ProductCode,
        ItemNumber = c.ItemNumber,
        Barcode = c.Barcode,
        Name = c.Name,
        PurchasePrice = c.PurchasePrice,
        ImageUrl = HttpImage(c.ProductImage),
        LocalSupplierCode = c.LocalSupplierCode,
        SalesQty = c.SalesQty,
        PurchaseQty = c.PurchaseQty,
        Score = score.Score,
        NameScore = score.NameScore,
        PriceScore = score.PriceScore,
        Eligible = score.Eligible,
        HasHistory = c.SalesQty > 0 || c.PurchaseQty > 0,
    };

    /// <summary>候选：无关键词 → 同供应商、窗口内有记录、过门槛的前 3；有关键词 → 按货号 / 条码 / 名称搜索（不限供应商）。</summary>
    private async Task<SupplierPreorderRefCandidatesDto> FindRefCandidatesAsync(
        string localSupplierCode,
        (DateTime From, DateTime To)? window,
        string itemName,
        decimal itemPrice,
        string? keyword,
        ISet<string> exclude)
    {
        var result = new SupplierPreorderRefCandidatesDto { WindowFrom = window?.From, WindowTo = window?.To };
        var k = keyword?.Trim();
        if (!string.IsNullOrEmpty(k))
        {
            var found = await _history.SearchProductsAsync(k, window?.From, window?.To, RefSearchLimit);
            result.SearchResults = found
                .Where(c => !exclude.Contains(c.ProductCode))
                .Select(c => ToCandidateDto(c, SupplierPreorderRefMatcher.ScoreCandidate(itemName, itemPrice, c)))
                .OrderByDescending(c => c.Eligible)
                .ThenByDescending(c => c.Score)
                .ThenByDescending(c => c.SalesQty)
                .ToList();
            return result;
        }

        if (window is null || string.IsNullOrWhiteSpace(localSupplierCode))
        {
            return result;
        }

        var candidates = await _history.GetSupplierCandidatesCachedAsync(localSupplierCode, window.Value.From, window.Value.To);
        var index = new SupplierPreorderRefMatcher.CandidateIndex(candidates);
        result.Recommended = index.Top(itemName, itemPrice, RefRecommendLimit, exclude)
            .Select(p => ToCandidateDto(p.Candidate, p.Score))
            .ToList();
        return result;
    }

    public async Task<SupplierPreorderRefCandidatesDto> GetRefCandidatesAsync(string formGuid, string itemGuid, string? keyword)
    {
        var form = await RequireFormAsync(formGuid);
        var item = await RequireItemAsync(formGuid, itemGuid);
        var exclude = await LoadMatchedCodesAsync(formGuid);
        var window = SupplierPreorderHistoryRules.ResolveWindow(form.HistoryFrom, form.HistoryTo, form.HolidayType, form.SeasonYear);
        return await FindRefCandidatesAsync(form.LocalSupplierCode, window, item.Name, item.Price, keyword, exclude);
    }

    public async Task<SupplierPreorderRefCandidatesDto> GetImportRefCandidatesAsync(SupplierPreorderImportRefCandidatesRequestDto request)
    {
        var window = SupplierPreorderHistoryRules.ResolveWindow(request.HistoryFrom, request.HistoryTo, request.HolidayType, request.SeasonYear);
        var exclude = (request.ExcludeProductCodes ?? new()).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return await FindRefCandidatesAsync(request.LocalSupplierCode?.Trim() ?? string.Empty, window, request.Name ?? string.Empty, request.Price, request.Keyword, exclude);
    }

    private async Task<SupplierPreorderFormItem> RequireItemAsync(string formGuid, string itemGuid)
    {
        var item = string.IsNullOrWhiteSpace(itemGuid)
            ? null
            : await _db.Queryable<SupplierPreorderFormItem>().FirstAsync(i => i.FormGuid == formGuid && i.ItemGuid == itemGuid && i.IsActive);
        return item ?? throw new SupplierPreorderException("商品不存在或已下架", "ITEM_NOT_FOUND", 404);
    }

    private async Task<HashSet<string>> LoadMatchedCodesAsync(string formGuid)
    {
        var codes = await _db.Queryable<SupplierPreorderFormItem>()
            .Where(i => i.FormGuid == formGuid && i.IsActive && i.MatchedProductCode != null)
            .Select(i => i.MatchedProductCode)
            .ToListAsync();
        return codes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<SupplierPreorderFormItemDto> SetItemRefAsync(string formGuid, string itemGuid, SupplierPreorderSetRefDto request)
    {
        var form = await RequireFormAsync(formGuid);
        var item = await RequireItemAsync(formGuid, itemGuid);
        var now = NowUtc;
        switch (request.Mode)
        {
            case "Manual":
            {
                var code = request.ProductCode?.Trim();
                if (string.IsNullOrEmpty(code))
                {
                    throw new SupplierPreorderException("请选择参考商品", "REF_REQUIRED");
                }

                if (string.Equals(code, item.MatchedProductCode, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SupplierPreorderException("参考商品不能是这款商品自己", "REF_IS_SELF");
                }

                var product = (await _history.LoadProductsAsync(new[] { code })).GetValueOrDefault(code)
                              ?? throw new SupplierPreorderException("商品表里没有这个商品", "REF_NOT_FOUND", 404);
                item.RefProductCode = product.ProductCode;
                item.RefSource = SupplierPreorderRefSources.Manual;
                item.RefScore = SupplierPreorderRefMatcher.ScoreCandidate(item.Name, item.Price, product).Score;
                break;
            }
            case "Clear":
                item.RefProductCode = null;
                item.RefSource = SupplierPreorderRefSources.Cleared;
                item.RefScore = null;
                break;
            case "Auto":
                SupplierPreorderRefDecisions.ClearAuto(item);
                break;
            default:
                throw new SupplierPreorderException("不支持的操作", "REF_MODE_INVALID");
        }

        item.RefSetBy = request.Mode == "Auto" ? null : CurrentUserName;
        item.RefSetAtUtc = request.Mode == "Auto" ? null : now;
        await _db.Updateable(item)
            .UpdateColumns(i => new { i.RefProductCode, i.RefSource, i.RefScore, i.RefSetBy, i.RefSetAtUtc })
            .ExecuteCommandAsync();
        _logger.LogInformation("供应商预订单参考商品：{FormGuid} {ItemCode} {Mode} {RefCode}，操作人 {User}",
            formGuid, item.SupplierItemCode, request.Mode, item.RefProductCode, CurrentUserName);

        if (request.Mode == "Auto")
        {
            // 交回系统：整表重算时重新挑（推荐要看全供应商候选，放后台做）
            await TryRequestHistoryAsync(formGuid);
        }
        else if (item.RefProductCode is not null)
        {
            // 新指定的参考商品立刻补算快照，列表马上能看到；还没算过整表的话等后台整表计算
            try
            {
                var meta = await _history.ReadMetaAsync(formGuid);
                if (meta is { CompletedAtUtc: not null, WindowFrom: not null, WindowTo: not null })
                {
                    await _history.RefreshProductAsync(formGuid, meta.WindowFrom.Value, meta.WindowTo.Value, item.RefProductCode);
                }
                else
                {
                    await TryRequestHistoryAsync(formGuid);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "参考商品快照补算失败，改为排队整表重算：{FormGuid}", formGuid);
                await TryRequestHistoryAsync(formGuid);
            }
        }

        var snapshot = await LoadSnapshotAsync(form);
        var state = await LoadHistoryStateAsync(form, new[] { AllStoresKey });
        return (await BuildFormItemDtosAsync(snapshot, new List<SupplierPreorderFormItem> { snapshot.ItemsByGuid[item.ItemGuid] }, state))[0];
    }

    /// <summary>管理端商品行（含去年销量与参考商品）。</summary>
    private async Task<List<SupplierPreorderFormItemDto>> BuildFormItemDtosAsync(FormSnapshot snapshot, List<SupplierPreorderFormItem> pageItems, HistoryState state)
    {
        var totals = snapshot.CountedLines()
            .GroupBy(x => x.Item.ItemGuid)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Line.Cartons));
        var images = await LoadProductImagesAsync(pageItems);
        var refProducts = await _history.LoadProductsAsync(pageItems.Select(i => i.RefProductCode).Where(c => c is not null).Cast<string>());
        var computed = state.Status.ComputedAtUtc is not null;
        return pageItems.Select(i => new SupplierPreorderFormItemDto
        {
            ItemGuid = i.ItemGuid,
            Seq = i.Seq,
            SupplierItemCode = i.SupplierItemCode,
            Barcode = i.Barcode,
            Name = i.Name,
            Unit = i.Unit,
            CartonQty = i.CartonQty,
            CartonCbm = i.CartonCbm,
            Price = i.Price,
            Category = i.Category,
            ImageUrl = ResolveImageUrl(i, images),
            ImageSourceUrl = i.ImageSourceUrl,
            MatchMethod = i.MatchMethod,
            MatchedProductCode = i.MatchedProductCode,
            MatchNote = i.MatchNote,
            TotalCartons = totals.GetValueOrDefault(i.ItemGuid),
            OwnSalesQty = !computed ? null : i.MatchedProductCode is null ? 0m : AllStores(state, i.MatchedProductCode)?.SalesQty ?? 0m,
            Ref = ToRefDto(i, refProducts, state),
            IsActive = i.IsActive,
            RevisionMark = ToRevisionMark(i),
        }).ToList();
    }
}
