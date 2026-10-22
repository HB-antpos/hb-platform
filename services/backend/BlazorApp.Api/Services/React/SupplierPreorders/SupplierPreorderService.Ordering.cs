using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.React.SupplierPreorders;

/// <summary>
/// 下单端：能编辑某店 = 下单人员授权有（本人, 该店）∩ 订单表开启了该店 ∩ 未截止（Manage 不受截止限制，可改全部开启分店）。
/// 能看其他分店 = 本人「可查看其他分店」开着或持有 Manage，且只读。
/// </summary>
public sealed partial class SupplierPreorderService
{
    private const int MaxCartonsPerLine = 9999;

    /// <summary>当前用户在下单端的身份：是否管理员、可下单分店、能否看其他分店。</summary>
    private sealed record OrderingIdentity(bool IsManager, HashSet<string> OrdererStoreGuids, bool CanViewOtherStores, List<SupplierPreorderOrdererStore> OrdererStores);

    private async Task<OrderingIdentity> LoadOrderingIdentityAsync()
    {
        var isManager = await RequireOrderingEntryAsync();
        var userGuid = CurrentUserGuid;
        var orderer = string.IsNullOrEmpty(userGuid)
            ? null
            : await _db.Queryable<SupplierPreorderOrderer>().FirstAsync(o => o.UserGuid == userGuid);
        var stores = orderer is null
            ? new List<SupplierPreorderOrdererStore>()
            : await _db.Queryable<SupplierPreorderOrdererStore>().Where(s => s.UserGuid == userGuid).ToListAsync();
        return new OrderingIdentity(
            isManager,
            stores.Select(s => s.StoreGuid).ToHashSet(StringComparer.OrdinalIgnoreCase),
            isManager || orderer?.CanViewOtherStores == true,
            stores);
    }

    /// <summary>我在这张订单表里能看到的分店：管理员 = 全部开启分店 + 自己被授权的；普通下单人 = 自己被授权的（没开启的也列出，只读并说明原因）。</summary>
    private List<SupplierPreorderMyStoreDto> BuildMyStores(FormSnapshot snapshot, OrderingIdentity identity)
    {
        var enabled = snapshot.EnabledStoreGuids;
        var refs = new List<SupplierPreorderStoreRefDto>();
        if (identity.IsManager)
        {
            refs.AddRange(snapshot.EnabledStores.Select(s => ToStoreRef(s.StoreGuid, s.StoreCode, s.StoreName)));
        }

        foreach (var store in identity.OrdererStores)
        {
            if (refs.All(r => !string.Equals(r.StoreGuid, store.StoreGuid, StringComparison.OrdinalIgnoreCase)))
            {
                refs.Add(ToStoreRef(store.StoreGuid, store.StoreCode, store.StoreName));
            }
        }

        var counted = snapshot.CountedLines().ToList();
        var now = NowUtc;
        return refs
            .OrderByDescending(r => enabled.Contains(r.StoreGuid))
            .ThenBy(r => r.StoreCode)
            .Select(r =>
            {
                var decision = SupplierPreorderAccessRules.EvaluateStoreEdit(
                    snapshot.Form.Status,
                    AsUtc(snapshot.Form.DeadlineAtUtc),
                    identity.IsManager,
                    identity.OrdererStoreGuids.Contains(r.StoreGuid),
                    enabled.Contains(r.StoreGuid),
                    now);
                var order = snapshot.Orders.FirstOrDefault(o => string.Equals(o.StoreGuid, r.StoreGuid, StringComparison.OrdinalIgnoreCase));
                var mine = counted.Where(x => string.Equals(x.Order.StoreGuid, r.StoreGuid, StringComparison.OrdinalIgnoreCase)).ToList();
                return new SupplierPreorderMyStoreDto
                {
                    StoreGuid = r.StoreGuid,
                    StoreCode = r.StoreCode,
                    StoreName = r.StoreName,
                    CanEdit = decision.CanEdit,
                    ReadOnlyReason = decision.ReadOnlyReason,
                    OrderStatus = order?.Status ?? "None",
                    SubmittedAtUtc = AsUtc(order?.SubmittedAtUtc),
                    LastEditedAtUtc = AsUtc(order?.LastEditedAtUtc),
                    LastEditedBy = order?.LastEditedBy,
                    Cartons = mine.Sum(x => x.Line.Cartons),
                    Amount = mine.Sum(x => LineAmount(x.Line.Cartons, x.Item)),
                };
            })
            .ToList();
    }

    private SupplierPreorderOrderingFormDto ToOrderingForm(FormSnapshot snapshot, List<SupplierPreorderMyStoreDto> myStores)
    {
        var form = snapshot.Form;
        return new SupplierPreorderOrderingFormDto
        {
            FormGuid = form.FormGuid,
            Title = form.Title,
            SupplierName = form.SupplierName,
            HolidayType = form.HolidayType,
            SeasonYear = form.SeasonYear,
            Status = form.Status,
            QuoteNo = form.QuoteNo,
            OfferDate = form.OfferDate,
            DeadlineAtUtc = AsUtc(form.DeadlineAtUtc),
            ContainerType = form.ContainerType,
            ContainerCapacityCbm = form.ContainerCapacityCbm,
            PriceTerms = form.PriceTerms,
            ItemCount = form.ItemCount,
            IsLocked = SupplierPreorderAccessRules.IsLockedForOrderers(form.Status, AsUtc(form.DeadlineAtUtc), NowUtc),
            MyStores = myStores,
        };
    }

    public async Task<List<SupplierPreorderOrderingFormDto>> GetOrderingFormsAsync()
    {
        var identity = await LoadOrderingIdentityAsync();
        var forms = await _db.Queryable<SupplierPreorderForm>()
            .Where(f => !f.IsDeleted && f.Status != SupplierPreorderFormStatuses.Draft)
            .OrderByDescending(f => f.CreatedAtUtc)
            .ToListAsync();
        var result = new List<SupplierPreorderOrderingFormDto>();
        foreach (var form in forms)
        {
            var snapshot = await LoadSnapshotAsync(form);
            var myStores = BuildMyStores(snapshot, identity);
            // 普通下单人只看到开启了自己分店的订单表
            if (!identity.IsManager && !myStores.Any(s => snapshot.EnabledStoreGuids.Contains(s.StoreGuid)))
            {
                continue;
            }

            result.Add(ToOrderingForm(snapshot, myStores));
        }

        return result;
    }

    private async Task<(FormSnapshot Snapshot, OrderingIdentity Identity, List<SupplierPreorderMyStoreDto> MyStores)> LoadOrderingAsync(string formGuid)
    {
        var identity = await LoadOrderingIdentityAsync();
        var form = await RequireFormAsync(formGuid);
        if (form.Status == SupplierPreorderFormStatuses.Draft)
        {
            throw new SupplierPreorderException("订单表尚未发布", "FORM_NOT_PUBLISHED", 404);
        }

        var snapshot = await LoadSnapshotAsync(form);
        var myStores = BuildMyStores(snapshot, identity);
        // 普通下单人：这张订单表至少要开启了他被授权的一家分店，否则和列表接口一样视为看不到（防止直接拿 formGuid 访问）
        if (!identity.IsManager && !myStores.Any(s => snapshot.EnabledStoreGuids.Contains(s.StoreGuid)))
        {
            throw new SupplierPreorderException("这张订单表没有开启你被授权的分店", "NO_STORE_ACCESS", 403);
        }

        if (myStores.Count == 0)
        {
            throw new SupplierPreorderException("你没有被授权替任何分店下单，请联系采购在「下单人员授权」里开通", "NO_STORE_ACCESS", 403);
        }

        return (snapshot, identity, myStores);
    }

    private static SupplierPreorderMyStoreDto RequireMyStore(List<SupplierPreorderMyStoreDto> myStores, string storeGuid) =>
        myStores.FirstOrDefault(s => string.Equals(s.StoreGuid, storeGuid, StringComparison.OrdinalIgnoreCase))
        ?? throw new SupplierPreorderException("你没有被授权查看这家分店", "STORE_FORBIDDEN", 403);

    public async Task<SupplierPreorderOrderingContextDto> GetOrderingContextAsync(string formGuid)
    {
        var (snapshot, identity, myStores) = await LoadOrderingAsync(formGuid);
        return new SupplierPreorderOrderingContextDto
        {
            Form = ToOrderingForm(snapshot, myStores),
            IsManager = identity.IsManager,
            CanViewOtherStores = identity.CanViewOtherStores,
            Categories = snapshot.Items.Where(i => i.IsActive).Select(i => i.Category).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().OrderBy(c => c).Cast<string>().ToList(),
            ServerNowUtc = NowUtc,
        };
    }

    public async Task<SupplierPreorderOrderingSummaryDto> GetOrderingSummaryAsync(string formGuid, string storeGuid)
    {
        var (snapshot, identity, myStores) = await LoadOrderingAsync(formGuid);
        RequireMyStore(myStores, storeGuid);
        return BuildOrderingSummary(snapshot, identity, storeGuid);
    }

    private static SupplierPreorderOrderingSummaryDto BuildOrderingSummary(FormSnapshot snapshot, OrderingIdentity identity, string storeGuid)
    {
        // 关掉的分店不计入合计，但本店（即使没开启）仍按自己的数量显示，方便看清保留了什么
        var ordersByGuid = snapshot.Orders.ToDictionary(o => o.OrderGuid);
        var order = snapshot.Orders.FirstOrDefault(o => string.Equals(o.StoreGuid, storeGuid, StringComparison.OrdinalIgnoreCase));
        var mine = order is null
            ? new List<(SupplierPreorderStoreOrderLine Line, SupplierPreorderFormItem Item)>()
            : snapshot.Lines
                .Where(l => l.OrderGuid == order.OrderGuid && l.Cartons > 0 && snapshot.ItemsByGuid.TryGetValue(l.ItemGuid, out var i) && i.IsActive)
                .Select(l => (Line: l, Item: snapshot.ItemsByGuid[l.ItemGuid]))
                .ToList();
        var counted = snapshot.CountedLines().ToList();
        return new SupplierPreorderOrderingSummaryDto
        {
            StoreGuid = storeGuid,
            OrderStatus = order?.Status ?? "None",
            ItemCount = mine.Count,
            Cartons = mine.Sum(x => x.Line.Cartons),
            Pieces = mine.Sum(x => x.Line.Cartons * x.Item.CartonQty),
            Amount = mine.Sum(x => LineAmount(x.Line.Cartons, x.Item)),
            Cbm = Math.Round(mine.Sum(x => LineCbm(x.Line.Cartons, x.Item)), 3),
            LastEditedAtUtc = AsUtc(order?.LastEditedAtUtc),
            LastEditedBy = order?.LastEditedBy,
            SubmittedAtUtc = AsUtc(order?.SubmittedAtUtc),
            AllStoresCbm = Math.Round(counted.Sum(x => LineCbm(x.Line.Cartons, x.Item)), 3),
            StoreCbms = identity.CanViewOtherStores
                ? counted
                    .GroupBy(x => x.Order.StoreName)
                    .Select(g => new SupplierPreorderStoreCbmDto { StoreName = g.Key, Cbm = Math.Round(g.Sum(x => LineCbm(x.Line.Cartons, x.Item)), 3) })
                    .OrderByDescending(x => x.Cbm)
                    .ToList()
                : null,
        };
    }

    public async Task<PagedResult<SupplierPreorderOrderingItemDto>> GetOrderingItemsAsync(string formGuid, SupplierPreorderOrderingItemQueryDto query)
    {
        var (snapshot, identity, myStores) = await LoadOrderingAsync(formGuid);
        var store = RequireMyStore(myStores, query.StoreGuid);
        var myOrder = snapshot.Orders.FirstOrDefault(o => string.Equals(o.StoreGuid, store.StoreGuid, StringComparison.OrdinalIgnoreCase));
        var myCartons = myOrder is null
            ? new Dictionary<string, int>()
            : snapshot.Lines.Where(l => l.OrderGuid == myOrder.OrderGuid && l.Cartons > 0).ToDictionary(l => l.ItemGuid, l => l.Cartons);
        // 其他分店：只算开启的分店，且不含本店
        var others = identity.CanViewOtherStores
            ? snapshot.CountedLines()
                .Where(x => !string.Equals(x.Order.StoreGuid, store.StoreGuid, StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => x.Item.ItemGuid)
                .ToDictionary(g => g.Key, g => g.GroupBy(x => x.Order.StoreName).Select(s => new SupplierPreorderOtherStoreCartonsDto { StoreName = s.Key, Cartons = s.Sum(x => x.Line.Cartons) }).OrderByDescending(s => s.Cartons).ToList())
            : new Dictionary<string, List<SupplierPreorderOtherStoreCartonsDto>>();

        IEnumerable<SupplierPreorderFormItem> items = snapshot.Items.Where(i => i.IsActive);
        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            items = items.Where(i => string.Equals(i.Category, query.Category, StringComparison.OrdinalIgnoreCase));
        }

        items = (query.Filter ?? "all") switch
        {
            "mine" => items.Where(i => myCartons.ContainsKey(i.ItemGuid)),
            "others" when identity.CanViewOtherStores => items.Where(i => others.ContainsKey(i.ItemGuid)),
            "new" => items.Where(i => i.MatchMethod == SupplierPreorderMatchMethods.None),
            "matched" => items.Where(i => i.MatchMethod != SupplierPreorderMatchMethods.None),
            _ => items,
        };
        items = FilterByKeyword(items, query.Keyword);
        items = (query.Sort ?? "seq") switch
        {
            "priceAsc" => items.OrderBy(i => i.Price).ThenBy(i => i.SortOrder),
            "priceDesc" => items.OrderByDescending(i => i.Price).ThenBy(i => i.SortOrder),
            "othersDesc" when identity.CanViewOtherStores => items.OrderByDescending(i => others.TryGetValue(i.ItemGuid, out var o) ? o.Sum(x => x.Cartons) : 0).ThenBy(i => i.SortOrder),
            _ => items.OrderBy(i => i.SortOrder),
        };

        var paged = Page(items.ToList(), query.Page, query.PageSize);
        var images = await LoadProductImagesAsync(paged.Items!);
        return new PagedResult<SupplierPreorderOrderingItemDto>
        {
            Total = paged.Total,
            Page = paged.Page,
            PageSize = paged.PageSize,
            Items = paged.Items!.Select(i => new SupplierPreorderOrderingItemDto
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
                IsNew = i.MatchMethod == SupplierPreorderMatchMethods.None,
                MyCartons = myCartons.GetValueOrDefault(i.ItemGuid),
                OtherStores = identity.CanViewOtherStores ? others.GetValueOrDefault(i.ItemGuid) ?? new List<SupplierPreorderOtherStoreCartonsDto>() : null,
            }).ToList(),
        };
    }

    public async Task<SupplierPreorderSaveLineResultDto> SaveLineAsync(string formGuid, string storeGuid, string itemGuid, SupplierPreorderSaveLineDto request)
    {
        if (request.Cartons is < 0 or > MaxCartonsPerLine)
        {
            throw new SupplierPreorderException($"箱数必须是 0–{MaxCartonsPerLine} 的整数", "CARTONS_INVALID");
        }

        var (snapshot, identity, myStores) = await LoadOrderingAsync(formGuid);
        var store = RequireMyStore(myStores, storeGuid);
        if (!store.CanEdit)
        {
            throw new SupplierPreorderException(store.ReadOnlyReason ?? "不能修改本店订单", "STORE_READ_ONLY", 409);
        }

        if (!snapshot.ItemsByGuid.TryGetValue(itemGuid, out var item) || !item.IsActive)
        {
            throw new SupplierPreorderException("商品不存在或已下架", "ITEM_NOT_FOUND", 404);
        }

        var now = NowUtc;
        // 订单先在事务外建好（并发首笔时撞唯一索引会重读已建的那条）；行保存失败（同一行两处同时首次写入）重试一次，
        // 保存的是绝对箱数，重试不会重复累加
        var order = await EnsureStoreOrderAsync(snapshot.Form.FormGuid, store, now);
        await RetryOnceAsync(() => RunInTransactionAsync(async () =>
        {
            var line = await _db.Queryable<SupplierPreorderStoreOrderLine>().FirstAsync(l => l.OrderGuid == order.OrderGuid && l.ItemGuid == itemGuid);
            if (request.Cartons == 0)
            {
                if (line is not null)
                {
                    await _db.Deleteable<SupplierPreorderStoreOrderLine>().Where(l => l.LineGuid == line.LineGuid).ExecuteCommandAsync();
                }
            }
            else if (line is null)
            {
                await _db.Insertable(new SupplierPreorderStoreOrderLine
                {
                    OrderGuid = order.OrderGuid,
                    FormGuid = snapshot.Form.FormGuid,
                    ItemGuid = itemGuid,
                    Cartons = request.Cartons,
                    UpdatedAtUtc = now,
                    UpdatedBy = CurrentUserName,
                }).ExecuteCommandAsync();
            }
            else
            {
                line.Cartons = request.Cartons;
                line.UpdatedAtUtc = now;
                line.UpdatedBy = CurrentUserName;
                await _db.Updateable(line).UpdateColumns(l => new { l.Cartons, l.UpdatedAtUtc, l.UpdatedBy }).ExecuteCommandAsync();
            }

            // 提交后再改：回到草稿，需要重新提交（保留上次提交时间供参考）
            order.Status = SupplierPreorderStoreOrderStatuses.Draft;
            order.LastEditedAtUtc = now;
            order.LastEditedBy = CurrentUserName;
            await _db.Updateable(order).UpdateColumns(o => new { o.Status, o.LastEditedAtUtc, o.LastEditedBy }).ExecuteCommandAsync();
        }));

        var fresh = await LoadSnapshotAsync(snapshot.Form);
        return new SupplierPreorderSaveLineResultDto
        {
            ItemGuid = itemGuid,
            Cartons = request.Cartons,
            Summary = BuildOrderingSummary(fresh, identity, store.StoreGuid),
        };
    }

    private static async Task RetryOnceAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not SupplierPreorderException)
        {
            await action();
        }
    }

    /// <summary>
    /// 取（没有就建）该店订单。同一店并发下第一笔（连点两行、两个标签页）时，后插入的会撞唯一索引，
    /// 这时重读已建好的那一条，不让用户看到保存失败。
    /// </summary>
    private async Task<SupplierPreorderStoreOrder> EnsureStoreOrderAsync(string formGuid, SupplierPreorderMyStoreDto store, DateTime now)
    {
        var order = await _db.Queryable<SupplierPreorderStoreOrder>().FirstAsync(o => o.FormGuid == formGuid && o.StoreGuid == store.StoreGuid);
        if (order is not null)
        {
            return order;
        }

        try
        {
            return await InsertStoreOrderAsync(formGuid, store, now);
        }
        catch (Exception)
        {
            var existing = await _db.Queryable<SupplierPreorderStoreOrder>().FirstAsync(o => o.FormGuid == formGuid && o.StoreGuid == store.StoreGuid);
            if (existing is not null)
            {
                return existing;
            }

            throw;
        }
    }

    private async Task<SupplierPreorderStoreOrder> InsertStoreOrderAsync(string formGuid, SupplierPreorderMyStoreDto store, DateTime now)
    {
        var order = new SupplierPreorderStoreOrder
        {
            FormGuid = formGuid,
            StoreGuid = store.StoreGuid,
            StoreCode = store.StoreCode,
            StoreName = store.StoreName,
            Status = SupplierPreorderStoreOrderStatuses.Draft,
            CreatedAtUtc = now,
        };
        await _db.Insertable(order).ExecuteCommandAsync();
        return order;
    }

    public async Task<SupplierPreorderOrderingSummaryDto> SubmitStoreOrderAsync(string formGuid, string storeGuid)
    {
        var (snapshot, identity, myStores) = await LoadOrderingAsync(formGuid);
        var store = RequireMyStore(myStores, storeGuid);
        if (!store.CanEdit)
        {
            throw new SupplierPreorderException(store.ReadOnlyReason ?? "不能提交本店订单", "STORE_READ_ONLY", 409);
        }

        var now = NowUtc;
        var order = await EnsureStoreOrderAsync(snapshot.Form.FormGuid, store, now);
        await RunInTransactionAsync(async () =>
        {
            order.Status = SupplierPreorderStoreOrderStatuses.Submitted;
            order.SubmittedAtUtc = now;
            order.SubmittedBy = CurrentUserName;
            await _db.Updateable(order).UpdateColumns(o => new { o.Status, o.SubmittedAtUtc, o.SubmittedBy }).ExecuteCommandAsync();
        });

        var fresh = await LoadSnapshotAsync(snapshot.Form);
        return BuildOrderingSummary(fresh, identity, store.StoreGuid);
    }
}
