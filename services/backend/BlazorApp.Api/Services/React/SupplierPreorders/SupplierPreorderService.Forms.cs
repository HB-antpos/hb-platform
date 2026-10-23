using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.React.SupplierPreorders;

public sealed partial class SupplierPreorderService
{
    public async Task<PagedResult<SupplierPreorderFormSummaryDto>> GetFormsAsync(SupplierPreorderFormQueryDto query)
    {
        var keyword = query.Keyword?.Trim();
        var forms = await _db.Queryable<SupplierPreorderForm>()
            .Where(f => !f.IsDeleted)
            .WhereIF(!string.IsNullOrEmpty(query.Status), f => f.Status == query.Status)
            .WhereIF(!string.IsNullOrEmpty(keyword), f => f.Title.Contains(keyword!) || f.SupplierName.Contains(keyword!) || (f.QuoteNo != null && f.QuoteNo.Contains(keyword!)))
            .OrderByDescending(f => f.CreatedAtUtc)
            .ToListAsync();
        var paged = Page(forms, query.Page, query.PageSize);
        var summaries = await BuildSummariesAsync(paged.Items ?? new());
        return new PagedResult<SupplierPreorderFormSummaryDto>
        {
            Items = summaries,
            Total = paged.Total,
            Page = paged.Page,
            PageSize = paged.PageSize,
        };
    }

    /// <summary>列表统计：开启分店数、已提交/草稿分店数、合计箱数/金额/体积（只算开启分店）。只取需要的列，避免整表加载。</summary>
    private async Task<List<SupplierPreorderFormSummaryDto>> BuildSummariesAsync(List<SupplierPreorderForm> forms)
    {
        if (forms.Count == 0)
        {
            return new();
        }

        var formGuids = forms.Select(f => f.FormGuid).ToList();
        var stores = await _db.Queryable<SupplierPreorderFormStore>()
            .Where(s => formGuids.Contains(s.FormGuid))
            .Select(s => new { s.FormGuid, s.StoreGuid })
            .ToListAsync();
        var orders = await _db.Queryable<SupplierPreorderStoreOrder>()
            .Where(o => formGuids.Contains(o.FormGuid))
            .Select(o => new { o.OrderGuid, o.FormGuid, o.StoreGuid, o.Status })
            .ToListAsync();
        var lines = await _db.Queryable<SupplierPreorderStoreOrderLine>()
            .Where(l => formGuids.Contains(l.FormGuid) && l.Cartons > 0)
            .Select(l => new { l.FormGuid, l.OrderGuid, l.ItemGuid, l.Cartons })
            .ToListAsync();
        var itemGuids = lines.Select(l => l.ItemGuid).Distinct().ToList();
        var items = new Dictionary<string, SupplierPreorderFormItem>();
        foreach (var chunk in itemGuids.Chunk(500))
        {
            var list = chunk.ToList();
            var rows = await _db.Queryable<SupplierPreorderFormItem>()
                .Where(i => list.Contains(i.ItemGuid))
                .Select(i => new SupplierPreorderFormItem { ItemGuid = i.ItemGuid, CartonQty = i.CartonQty, Price = i.Price, CartonCbm = i.CartonCbm, IsActive = i.IsActive })
                .ToListAsync();
            foreach (var row in rows)
            {
                items[row.ItemGuid] = row;
            }
        }

        var enabled = stores.GroupBy(s => s.FormGuid).ToDictionary(g => g.Key, g => g.Select(s => s.StoreGuid).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var orderByGuid = orders.ToDictionary(o => o.OrderGuid);
        var formsWithFiles = forms.Where(f => !string.IsNullOrEmpty(f.SourceFileGuid)).Select(f => f.FormGuid).ToHashSet();

        return forms.Select(form =>
        {
            var enabledStores = enabled.GetValueOrDefault(form.FormGuid) ?? new HashSet<string>();
            var counted = lines
                .Where(l => l.FormGuid == form.FormGuid
                            && orderByGuid.TryGetValue(l.OrderGuid, out var o)
                            && enabledStores.Contains(o.StoreGuid)
                            && items.TryGetValue(l.ItemGuid, out var it) && it.IsActive)
                .ToList();
            var storesWithLines = counted.Select(l => orderByGuid[l.OrderGuid].StoreGuid).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var formOrders = orders.Where(o => o.FormGuid == form.FormGuid && enabledStores.Contains(o.StoreGuid)).ToList();
            return new SupplierPreorderFormSummaryDto
            {
                FormGuid = form.FormGuid,
                LocalSupplierCode = form.LocalSupplierCode,
                SupplierName = form.SupplierName,
                HolidayType = form.HolidayType,
                SeasonYear = form.SeasonYear,
                Title = form.Title,
                QuoteNo = form.QuoteNo,
                Status = form.Status,
                DeadlineAtUtc = AsUtc(form.DeadlineAtUtc),
                ItemCount = form.ItemCount,
                EnabledStoreCount = enabledStores.Count,
                SubmittedStoreCount = formOrders.Count(o => o.Status == SupplierPreorderStoreOrderStatuses.Submitted),
                DraftStoreCount = formOrders.Count(o => o.Status == SupplierPreorderStoreOrderStatuses.Draft && storesWithLines.Contains(o.StoreGuid)),
                TotalCartons = counted.Sum(l => l.Cartons),
                TotalAmount = counted.Sum(l => LineAmount(l.Cartons, items[l.ItemGuid])),
                TotalCbm = Math.Round(counted.Sum(l => LineCbm(l.Cartons, items[l.ItemGuid])), 3),
                HasSourceFile = formsWithFiles.Contains(form.FormGuid),
                ExportQtyUnit = form.ExportQtyUnit,
                CreatedAtUtc = AsUtc(form.CreatedAtUtc)!.Value,
                CreatedBy = form.CreatedBy,
            };
        }).ToList();
    }

    /// <summary>库里读出的 datetime2 不带 Kind，统一标成 UTC，前端按 ISO（带 Z）解析。</summary>
    private static DateTime? AsUtc(DateTime? value) =>
        value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null;

    public async Task<SupplierPreorderFormDetailDto> GetFormAsync(string formGuid)
    {
        var form = await RequireFormAsync(formGuid);
        var summary = (await BuildSummariesAsync(new List<SupplierPreorderForm> { form }))[0];
        var items = await _db.Queryable<SupplierPreorderFormItem>()
            .Where(i => i.FormGuid == formGuid && i.IsActive)
            .Select(i => new { i.MatchMethod, i.MatchNote, i.Category })
            .ToListAsync();
        var detail = new SupplierPreorderFormDetailDto
        {
            Summary = summary,
            OfferDate = form.OfferDate,
            ContainerType = form.ContainerType,
            ContainerCapacityCbm = form.ContainerCapacityCbm,
            PriceTerms = form.PriceTerms,
            HistoryFrom = form.HistoryFrom,
            HistoryTo = form.HistoryTo,
            Notes = form.Notes,
            MatchedCount = items.Count(i => i.MatchMethod != SupplierPreorderMatchMethods.None && i.MatchNote == null),
            WarningCount = items.Count(i => i.MatchMethod != SupplierPreorderMatchMethods.None && i.MatchNote != null),
            NewCount = items.Count(i => i.MatchMethod == SupplierPreorderMatchMethods.None),
            Categories = items.Select(i => i.Category).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().OrderBy(c => c).Cast<string>().ToList(),
        };
        await FillHistorySummaryAsync(form, detail);
        return detail;
    }

    public async Task<SupplierPreorderFormDetailDto> UpdateFormAsync(string formGuid, SupplierPreorderUpdateFormDto request)
    {
        var form = await RequireFormAsync(formGuid);
        var header = ValidateHeader(request.Title, request.HolidayType, request.SeasonYear, request.ContainerCapacityCbm, request.ExportQtyUnit);
        // 历史窗口（或决定默认窗口的节日 / 年份）变了：排队重算去年历史
        var windowBefore = SupplierPreorderHistoryRules.ResolveWindow(form.HistoryFrom, form.HistoryTo, form.HolidayType, form.SeasonYear);
        var windowAfter = SupplierPreorderHistoryRules.ResolveWindow(request.HistoryFrom?.Date, request.HistoryTo?.Date, header.HolidayType, request.SeasonYear);
        form.Title = header.Title;
        form.HolidayType = header.HolidayType;
        form.SeasonYear = request.SeasonYear;
        form.QuoteNo = Trim(request.QuoteNo, 100);
        form.OfferDate = request.OfferDate?.Date;
        form.DeadlineAtUtc = request.DeadlineAtUtc is DateTime d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;
        form.ContainerType = Trim(request.ContainerType, 50);
        form.ContainerCapacityCbm = request.ContainerCapacityCbm;
        form.PriceTerms = Trim(request.PriceTerms, 200);
        form.HistoryFrom = request.HistoryFrom?.Date;
        form.HistoryTo = request.HistoryTo?.Date;
        form.ExportQtyUnit = header.ExportQtyUnit;
        form.Notes = Trim(request.Notes, 1000);
        form.UpdatedAtUtc = NowUtc;
        form.UpdatedBy = CurrentUserName;
        await _db.Updateable(form).ExecuteCommandAsync();
        if (windowBefore != windowAfter)
        {
            await TryRequestHistoryAsync(formGuid);
        }

        return await GetFormAsync(formGuid);
    }

    public async Task<SupplierPreorderFormDetailDto> SetStatusAsync(string formGuid, SupplierPreorderSetStatusDto request)
    {
        var form = await RequireFormAsync(formGuid);
        var hasLines = await _db.Queryable<SupplierPreorderStoreOrderLine>().AnyAsync(l => l.FormGuid == formGuid && l.Cartons > 0);
        var error = SupplierPreorderAccessRules.ValidateStatusTransition(form.Status, request.Status, form.ItemCount, hasLines);
        if (error is not null)
        {
            throw new SupplierPreorderException(error, "STATUS_TRANSITION_INVALID", 409);
        }

        var from = form.Status;
        form.Status = request.Status;
        form.UpdatedAtUtc = NowUtc;
        form.UpdatedBy = CurrentUserName;
        await _db.Updateable(form).UpdateColumns(f => new { f.Status, f.UpdatedAtUtc, f.UpdatedBy }).ExecuteCommandAsync();
        await WriteAuthLogAsync("FormStore", "StatusChanged", $"{from} → {request.Status}", formGuid);
        if (from == SupplierPreorderFormStatuses.Draft && request.Status == SupplierPreorderFormStatuses.Open)
        {
            // 发布后排后台任务重算去年历史（导入时已算过一次草稿版，这里按最新的匹配与参考再算一遍）
            await TryRequestHistoryAsync(formGuid);
        }
        return await GetFormAsync(formGuid);
    }

    public async Task<bool> DeleteFormAsync(string formGuid)
    {
        var form = await RequireFormAsync(formGuid);
        if (await _db.Queryable<SupplierPreorderStoreOrderLine>().AnyAsync(l => l.FormGuid == formGuid && l.Cartons > 0))
        {
            throw new SupplierPreorderException("已有分店下单，不能删除（可以截止锁定）", "FORM_HAS_ORDERS", 409);
        }

        form.IsDeleted = true;
        form.UpdatedAtUtc = NowUtc;
        form.UpdatedBy = CurrentUserName;
        await _db.Updateable(form).UpdateColumns(f => new { f.IsDeleted, f.UpdatedAtUtc, f.UpdatedBy }).ExecuteCommandAsync();
        await WriteAuthLogAsync("FormStore", "FormDeleted", form.Title, formGuid);
        return true;
    }

    public async Task<PagedResult<SupplierPreorderFormItemDto>> GetFormItemsAsync(string formGuid, SupplierPreorderFormItemQueryDto query)
    {
        var form = await RequireFormAsync(formGuid);
        var snapshot = await LoadSnapshotAsync(form);
        var state = await LoadHistoryStateAsync(form, new[] { AllStoresKey });
        bool OwnHasSales(SupplierPreorderFormItem i) =>
            i.MatchedProductCode is not null && (AllStores(state, i.MatchedProductCode)?.SalesQty ?? 0m) > 0;
        IEnumerable<SupplierPreorderFormItem> filtered = snapshot.Items.Where(i => i.IsActive);
        filtered = (query.Match ?? "all") switch
        {
            "matched" => filtered.Where(i => i.MatchMethod != SupplierPreorderMatchMethods.None && i.MatchNote == null),
            "new" => filtered.Where(i => i.MatchMethod == SupplierPreorderMatchMethods.None),
            "warning" => filtered.Where(i => i.MatchNote != null),
            // 需要参考：本品去年没有销量，也没有挂参考商品（快照没算出来前无法判断，返回空）
            "needRef" => state.Status.ComputedAtUtc is null ? Enumerable.Empty<SupplierPreorderFormItem>() : filtered.Where(i => !OwnHasSales(i) && i.RefProductCode is null),
            "ref" => filtered.Where(i => i.RefProductCode is not null),
            _ => filtered,
        };
        filtered = FilterByKeyword(filtered, query.Keyword);
        var paged = Page(filtered.ToList(), query.Page, query.PageSize);
        return new PagedResult<SupplierPreorderFormItemDto>
        {
            Items = await BuildFormItemDtosAsync(snapshot, paged.Items!, state),
            Total = paged.Total,
            Page = paged.Page,
            PageSize = paged.PageSize,
        };
    }

    private static IEnumerable<SupplierPreorderFormItem> FilterByKeyword(IEnumerable<SupplierPreorderFormItem> items, string? keyword)
    {
        var k = keyword?.Trim();
        if (string.IsNullOrEmpty(k))
        {
            return items;
        }

        return items.Where(i =>
            i.SupplierItemCode.Contains(k, StringComparison.OrdinalIgnoreCase)
            || i.Name.Contains(k, StringComparison.OrdinalIgnoreCase)
            || (i.Barcode != null && i.Barcode.Contains(k, StringComparison.OrdinalIgnoreCase)));
    }

    // ---------------- 分店开启 ----------------

    public async Task<List<SupplierPreorderFormStoreStatusDto>> GetFormStoresAsync(string formGuid)
    {
        var form = await RequireFormAsync(formGuid);
        return await BuildFormStoreStatusesAsync(form);
    }

    private async Task<List<SupplierPreorderFormStoreStatusDto>> BuildFormStoreStatusesAsync(SupplierPreorderForm form)
    {
        var snapshot = await LoadSnapshotAsync(form);
        var activeStores = await LoadActiveStoresAsync();
        var enabled = snapshot.EnabledStoreGuids;
        var ordererStores = await _db.Queryable<SupplierPreorderOrdererStore>()
            .Select(s => new { s.UserGuid, s.StoreGuid })
            .ToListAsync();
        var ordererGuids = ordererStores.Select(s => s.UserGuid).Distinct().ToList();
        var userNames = await LoadUserDisplayNamesAsync(ordererGuids);
        var orderersByStore = ordererStores
            .GroupBy(s => s.StoreGuid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(s => userNames.GetValueOrDefault(s.UserGuid)).Where(n => n is not null).Cast<string>().OrderBy(n => n).ToList(),
                StringComparer.OrdinalIgnoreCase);
        var counted = snapshot.CountedLines().ToList();
        var ordersByStore = snapshot.Orders.ToDictionary(o => o.StoreGuid, StringComparer.OrdinalIgnoreCase);

        // 启用的分店全部列出；已开启但分店后来被停用的也保留
        var storeRefs = activeStores.Select(s => ToStoreRef(s.StoreGUID, s.StoreCode, s.StoreName)).ToList();
        foreach (var formStore in snapshot.EnabledStores.Where(fs => storeRefs.All(r => !string.Equals(r.StoreGuid, fs.StoreGuid, StringComparison.OrdinalIgnoreCase))))
        {
            storeRefs.Add(ToStoreRef(formStore.StoreGuid, formStore.StoreCode, formStore.StoreName));
        }

        return storeRefs.Select(store =>
        {
            var isEnabled = enabled.Contains(store.StoreGuid);
            ordersByStore.TryGetValue(store.StoreGuid, out var order);
            var storeLines = counted.Where(x => string.Equals(x.Order.StoreGuid, store.StoreGuid, StringComparison.OrdinalIgnoreCase)).ToList();
            return new SupplierPreorderFormStoreStatusDto
            {
                StoreGuid = store.StoreGuid,
                StoreCode = store.StoreCode,
                StoreName = store.StoreName,
                Enabled = isEnabled,
                OrderStatus = order is null ? "None" : order.Status,
                SubmittedAtUtc = AsUtc(order?.SubmittedAtUtc),
                SubmittedBy = order?.SubmittedBy,
                LastEditedAtUtc = AsUtc(order?.LastEditedAtUtc),
                LastEditedBy = order?.LastEditedBy,
                ItemCount = storeLines.Count,
                Cartons = storeLines.Sum(x => x.Line.Cartons),
                Pieces = storeLines.Sum(x => x.Line.Cartons * x.Item.CartonQty),
                Amount = storeLines.Sum(x => LineAmount(x.Line.Cartons, x.Item)),
                Cbm = Math.Round(storeLines.Sum(x => LineCbm(x.Line.Cartons, x.Item)), 3),
                Orderers = orderersByStore.GetValueOrDefault(store.StoreGuid) ?? new List<string>(),
            };
        })
        .OrderByDescending(s => s.Enabled)
        .ThenBy(s => s.StoreCode)
        .ToList();
    }

    public async Task<List<SupplierPreorderFormStoreStatusDto>> UpdateFormStoresAsync(string formGuid, SupplierPreorderUpdateFormStoresDto request)
    {
        var form = await RequireFormAsync(formGuid);
        await ApplyFormStoresAsync(form, request.StoreGuids ?? new(), replace: true);
        return await BuildFormStoreStatusesAsync(form);
    }

    /// <summary>replace=true 整体覆盖；false 只增加（复制去年授权时用，不会关掉已开启的分店）。</summary>
    private async Task<(List<string> Added, List<string> Removed)> ApplyFormStoresAsync(SupplierPreorderForm form, IEnumerable<string> storeGuids, bool replace)
    {
        var activeStores = (await LoadActiveStoresAsync()).ToDictionary(s => s.StoreGUID, StringComparer.OrdinalIgnoreCase);
        var target = storeGuids.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var existing = await _db.Queryable<SupplierPreorderFormStore>().Where(s => s.FormGuid == form.FormGuid).ToListAsync();
        var existingGuids = existing.Select(s => s.StoreGuid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // 已开启、后来分店被停用的允许原样保留（前端整体覆盖时会带回来）；新开启的必须是启用分店
        var unknown = target.Where(g => !activeStores.ContainsKey(g) && !existingGuids.Contains(g)).ToList();
        if (unknown.Count > 0)
        {
            throw new SupplierPreorderException("包含不存在或已停用的分店", "STORE_INVALID");
        }
        var toAdd = target.Where(g => !existingGuids.Contains(g)).ToList();
        var toRemove = replace ? existing.Where(s => !target.Contains(s.StoreGuid, StringComparer.OrdinalIgnoreCase)).ToList() : new List<SupplierPreorderFormStore>();
        if (toAdd.Count == 0 && toRemove.Count == 0)
        {
            return (new(), new());
        }

        var now = NowUtc;
        await RunInTransactionAsync(async () =>
        {
            if (toAdd.Count > 0)
            {
                await _db.Insertable(toAdd.Select(g => new SupplierPreorderFormStore
                {
                    FormGuid = form.FormGuid,
                    StoreGuid = activeStores[g].StoreGUID,
                    StoreCode = activeStores[g].StoreCode,
                    StoreName = activeStores[g].StoreName,
                    CreatedAtUtc = now,
                    CreatedBy = CurrentUserName,
                }).ToList()).ExecuteCommandAsync();
            }

            if (toRemove.Count > 0)
            {
                var removeGuids = toRemove.Select(s => s.FormStoreGuid).ToList();
                await _db.Deleteable<SupplierPreorderFormStore>().Where(s => removeGuids.Contains(s.FormStoreGuid)).ExecuteCommandAsync();
            }
        });

        var added = toAdd.Select(g => activeStores[g].StoreName).ToList();
        var removed = toRemove.Select(s => s.StoreName).ToList();
        var detail = string.Join("；", new[]
        {
            added.Count > 0 ? "开启：" + string.Join("、", added) : null,
            removed.Count > 0 ? "关闭：" + string.Join("、", removed) : null,
        }.Where(x => x is not null));
        await WriteAuthLogAsync("FormStore", replace ? "StoresUpdated" : "StoresCopied", detail, form.FormGuid);
        return (added, removed);
    }

    public async Task<SupplierPreorderCopyStoresResultDto> CopyStoresFromPreviousAsync(string formGuid)
    {
        var form = await RequireFormAsync(formGuid);
        // 上一张：同供应商、同节日、年份更早（同年份则创建更早）、且开启过分店
        var candidates = await _db.Queryable<SupplierPreorderForm>()
            .Where(f => !f.IsDeleted && f.FormGuid != formGuid && f.LocalSupplierCode == form.LocalSupplierCode && f.HolidayType == form.HolidayType)
            .ToListAsync();
        var previous = candidates
            .Where(f => f.SeasonYear < form.SeasonYear || (f.SeasonYear == form.SeasonYear && f.CreatedAtUtc < form.CreatedAtUtc))
            .OrderByDescending(f => f.SeasonYear)
            .ThenByDescending(f => f.CreatedAtUtc)
            .ToList();
        foreach (var source in previous)
        {
            var storeGuids = await _db.Queryable<SupplierPreorderFormStore>()
                .Where(s => s.FormGuid == source.FormGuid)
                .Select(s => s.StoreGuid)
                .ToListAsync();
            if (storeGuids.Count == 0)
            {
                continue;
            }

            var activeGuids = (await LoadActiveStoresAsync()).Select(s => s.StoreGUID).ToHashSet(StringComparer.OrdinalIgnoreCase);
            await ApplyFormStoresAsync(form, storeGuids.Where(activeGuids.Contains), replace: false);
            return new SupplierPreorderCopyStoresResultDto
            {
                SourceFormGuid = source.FormGuid,
                SourceTitle = source.Title,
                Stores = await BuildFormStoreStatusesAsync(form),
            };
        }

        return new SupplierPreorderCopyStoresResultDto { Stores = await BuildFormStoreStatusesAsync(form) };
    }

    // ---------------- 矩阵 ----------------

    public async Task<SupplierPreorderMatrixDto> GetMatrixAsync(string formGuid, SupplierPreorderMatrixQueryDto query)
    {
        var form = await RequireFormAsync(formGuid);
        var snapshot = await LoadSnapshotAsync(form);
        var counted = snapshot.CountedLines().ToList();
        var byItem = counted.GroupBy(x => x.Item.ItemGuid).ToDictionary(g => g.Key, g => g.ToList());
        var storesWithOrders = snapshot.EnabledStores.Select(s => ToStoreRef(s.StoreGuid, s.StoreCode, s.StoreName)).ToList();

        IEnumerable<SupplierPreorderFormItem> items = snapshot.Items.Where(i => i.IsActive);
        if (query.OnlyOrdered)
        {
            items = items.Where(i => byItem.ContainsKey(i.ItemGuid));
        }

        items = FilterByKeyword(items, query.Keyword);
        var paged = Page(items.ToList(), query.Page, query.PageSize);
        var images = await LoadProductImagesAsync(paged.Items!);

        var totals = new SupplierPreorderMatrixTotalsDto
        {
            OrderedItemCount = byItem.Count,
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

        return new SupplierPreorderMatrixDto
        {
            Stores = storesWithOrders,
            Totals = totals,
            Rows = new PagedResult<SupplierPreorderMatrixRowDto>
            {
                Total = paged.Total,
                Page = paged.Page,
                PageSize = paged.PageSize,
                Items = paged.Items!.Select(item =>
                {
                    var rows = byItem.GetValueOrDefault(item.ItemGuid) ?? new();
                    var cartons = rows.Sum(x => x.Line.Cartons);
                    return new SupplierPreorderMatrixRowDto
                    {
                        ItemGuid = item.ItemGuid,
                        Seq = item.Seq,
                        SupplierItemCode = item.SupplierItemCode,
                        Name = item.Name,
                        ImageUrl = ResolveImageUrl(item, images),
                        Price = item.Price,
                        CartonQty = item.CartonQty,
                        CartonCbm = item.CartonCbm,
                        StoreCartons = rows.GroupBy(x => x.Order.StoreGuid).ToDictionary(g => g.Key, g => g.Sum(x => x.Line.Cartons)),
                        TotalCartons = cartons,
                        TotalPieces = cartons * item.CartonQty,
                        TotalAmount = LineAmount(cartons, item),
                        TotalCbm = Math.Round(LineCbm(cartons, item), 3),
                    };
                }).ToList(),
            },
        };
    }

    private async Task<Dictionary<string, string>> LoadUserDisplayNamesAsync(IReadOnlyCollection<string> userGuids)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in userGuids.Chunk(500))
        {
            var list = chunk.ToList();
            var users = await _db.Queryable<User>()
                .Where(u => list.Contains(u.UserGUID))
                .Select(u => new { u.UserGUID, u.Username, u.FullName })
                .ToListAsync();
            foreach (var user in users)
            {
                result[user.UserGUID] = string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName!;
            }
        }

        return result;
    }
}
