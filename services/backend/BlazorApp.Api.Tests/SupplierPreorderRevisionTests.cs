using System.Security.Claims;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React.SupplierPreorders;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 供应商节日预订单 PR 3：修订版按货号对齐（已下箱数保留、删掉的下架不计入、改价改箱规记旧值高亮、重新上架）与
/// 提醒未提交分店（按人合并、截止前 72 / 24 小时档位、幂等防重复、只限进行中）。
/// </summary>
public sealed class SupplierPreorderRevisionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"supplier-preorder-revision-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;
    private readonly ManualClock _clock = new(new DateTime(2026, 2, 20, 0, 0, 0, DateTimeKind.Utc));
    private readonly FakePublisher _publisher = new();

    public SupplierPreorderRevisionTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_path}",
            DbType = DbType.Sqlite,
            InitKeyType = InitKeyType.Attribute,
            IsAutoCloseConnection = false,
        });
        _db.CodeFirst.InitTables(
            typeof(Store), typeof(User), typeof(UserStore), typeof(Product), typeof(HBLocalSupplier),
            typeof(SupplierPreorderForm), typeof(SupplierPreorderFormItem), typeof(SupplierPreorderFormStore),
            typeof(SupplierPreorderOrderer), typeof(SupplierPreorderOrdererStore), typeof(SupplierPreorderAuthLog),
            typeof(SupplierPreorderStoreOrder), typeof(SupplierPreorderStoreOrderLine),
            typeof(SupplierPreorderImportProfile), typeof(SupplierPreorderSourceFile));
        _db.Insertable(new[]
        {
            new Store { StoreGUID = "S1", StoreCode = "1001", StoreName = "Orion", IsActive = true },
            new Store { StoreGUID = "S2", StoreCode = "1002", StoreName = "Kawana", IsActive = true },
            new Store { StoreGUID = "S3", StoreCode = "1003", StoreName = "Peninsula", IsActive = true },
        }).ExecuteCommand();
        _db.Insertable(new[]
        {
            new User { UserGUID = "user-1", Username = "orion", Email = "u1@example.test", PasswordHash = "x", FullName = "王店长", IsActive = true },
            new User { UserGUID = "user-2", Username = "kawana", Email = "u2@example.test", PasswordHash = "x", FullName = "李店长", IsActive = true },
        }).ExecuteCommand();
        _db.Insertable(new HBLocalSupplier { Guid = "sup-257", LocalSupplierCode = "257", Name = "Uncle Bills (KFC)", Status = 1 }).ExecuteCommand();
        _db.Insertable(new Product { ProductCode = "P-NEW", ItemNumber = "XD0002000", Barcode = "9333527800099", ProductName = "Star Topper", LocalSupplierCode = "257", PurchasePrice = 3m, IsActive = true }).ExecuteCommand();
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_path);
    }

    private sealed class ManualClock : TimeProvider
    {
        public ManualClock(DateTime utcNow) => UtcNow = utcNow;
        public DateTime UtcNow { get; set; }
        public override DateTimeOffset GetUtcNow() => new(UtcNow);
    }

    /// <summary>记录发布请求；同一幂等键第二次返回 Deduplicated（与通知平台一致）。</summary>
    private sealed class FakePublisher : INotificationPublisher
    {
        private readonly HashSet<string> _keys = new();
        public List<NotificationPublishRequest> Sent { get; } = new();

        public Task<NotificationPublishResult> PublishAsync(NotificationPublishRequest request, CancellationToken cancellationToken = default)
        {
            if (request.DedupeKey is not null && !_keys.Add(request.DedupeKey))
            {
                return Task.FromResult(new NotificationPublishResult(null, 0, true));
            }

            Sent.Add(request);
            return Task.FromResult(new NotificationPublishResult(Guid.NewGuid().ToString("N"), request.Audience.UserGuids.Count, false));
        }

        public Task<bool> RevokeAsync(string dedupeKey, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private SupplierPreorderService CreateService(string userGuid, params string[] policies)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("userId", userGuid) }, "test"));
        var http = new Mock<IHttpContextAccessor>();
        http.SetupGet(x => x.HttpContext).Returns(new DefaultHttpContext { User = principal });
        var auth = new Mock<IAuthorizationService>();
        auth.Setup(x => x.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<string>()))
            .ReturnsAsync((ClaimsPrincipal _, object? _, string policy) =>
                policies.Contains(policy) ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var user = new Mock<ICurrentUserService>();
        user.Setup(x => x.GetCurrentUserGuid()).Returns(userGuid);
        user.Setup(x => x.GetCurrentUsername()).Returns(userGuid + "-name");
        return new SupplierPreorderService(_db, user.Object, auth.Object, http.Object, new Mock<IRoleService>().Object,
            NullLogger<SupplierPreorderService>.Instance, _clock, null, _publisher);
    }

    private static readonly SupplierPreorderColumnMappingDto Mapping = new() { ItemCode = 0, Name = 1, CartonQty = 2, Price = 3, Barcode = 4, Image = 5 };

    private static SupplierPreorderParsedRowDto Row(int rowNo, string code, string name, string ctn, string price, string? barcode = null, string? image = null) =>
        new() { RowNo = rowNo, Cells = new List<string?> { code, name, ctn, price, barcode, image } };

    private async Task<(string FormGuid, Dictionary<string, string> Items)> CreateOpenFormAsync(SupplierPreorderService admin)
    {
        var detail = await admin.CreateFormAsync(new SupplierPreorderCreateFormDto
        {
            LocalSupplierCode = "257",
            HolidayType = "Christmas",
            SeasonYear = 2026,
            Title = "KFC Christmas 2026",
            DeadlineAtUtc = new DateTime(2026, 3, 6, 12, 59, 0, DateTimeKind.Utc),
            HeaderRowNo = 1,
            Mapping = Mapping,
            Rows = new List<SupplierPreorderParsedRowDto>
            {
                Row(2, "XD1", "Bauble", "24", "1.10", image: "https://img.example/1.jpg"),
                Row(3, "XD2", "Lantern", "24", "1.53"),
                Row(4, "XD3", "Sleigh", "2", "43.07"),
            },
        });
        var formGuid = detail.Summary.FormGuid;
        await admin.UpdateFormStoresAsync(formGuid, new SupplierPreorderUpdateFormStoresDto { StoreGuids = new() { "S1", "S2" } });
        await admin.SetStatusAsync(formGuid, new SupplierPreorderSetStatusDto { Status = "Open" });
        var items = (await _db.Queryable<SupplierPreorderFormItem>().Where(i => i.FormGuid == formGuid).ToListAsync())
            .ToDictionary(i => i.SupplierItemCode, i => i.ItemGuid);
        return (formGuid, items);
    }

    [Fact]
    public void 对比_按货号对齐不分大小写_重复货号按顺序_下架与重新上架_只比影响下单展示的字段()
    {
        SupplierPreorderFormItem Item(string guid, string code, decimal price, int ctn, bool active = true, int sort = 1) =>
            new() { ItemGuid = guid, SupplierItemCode = code, Name = "N " + code, Price = price, CartonQty = ctn, IsActive = active, SortOrder = sort };
        SupplierPreorderNormalizedRow NRow(int rowNo, string code, decimal price, int ctn, string? name = null) =>
            new() { RowNo = rowNo, SupplierItemCode = code, Name = name ?? "N " + code.ToUpperInvariant(), Price = price, CartonQty = ctn };

        var existing = new List<SupplierPreorderFormItem>
        {
            Item("a", "XD1", 1.10m, 24, sort: 1),
            Item("b", "XD2", 1.53m, 24, sort: 2),
            Item("c", "XD9", 5m, 6, active: false, sort: 3),   // 上一版已下架
            Item("d", "DUP", 1m, 1, sort: 4),
            Item("e", "DUP", 2m, 1, sort: 5),
            Item("f", "XD4", 3m, 12, sort: 6),
        };
        var rows = new List<SupplierPreorderNormalizedRow>
        {
            NRow(10, " xd1 ", 1.20m, 12, "N XD1"),            // 改价 + 改箱规（货号大小写空格不同仍对上）
            NRow(11, "XD2", 1.53m, 24),               // 不变
            NRow(12, "XD9", 5m, 6),                   // 重新上架
            NRow(13, "DUP", 1m, 1),
            NRow(14, "DUP", 2m, 1),
            NRow(15, "XD5", 9m, 1),                   // 新增
        };
        var diffs = SupplierPreorderRevisionRules.Diff(existing, rows);
        Assert.Equal(
            new[] { "Changed", "Unchanged", "Restored", "Unchanged", "Unchanged", "Added", "Removed" },
            diffs.Select(d => d.Kind));
        Assert.Equal(new[] { "Price", "Carton" }, diffs[0].Changes);
        Assert.Equal(new[] { "d", "e" }, diffs.Skip(3).Take(2).Select(d => d.Existing!.ItemGuid));
        Assert.Equal("f", diffs[^1].Existing!.ItemGuid);

        // 修订版里 XD4 这一行不完整（不能导入）：老商品保持原样，不算下架
        var kept = SupplierPreorderRevisionRules.Diff(existing, rows, new[] { " xd4" });
        Assert.DoesNotContain(kept, d => d.Kind == "Removed");

        // 写回：记旧值、高亮；重新上架加 Restored；没变的清掉以前的高亮
        var a = existing[0];
        SupplierPreorderRevisionRules.ApplyRow(a, rows[0], diffs[0], revision: 2, sortOrder: 1, keepSourceRowNo: true);
        Assert.Equal((1.20m, 12, 1.10m, 24, "Price,Carton", 2, 10), (a.Price, a.CartonQty, a.PrevPrice!.Value, a.PrevCartonQty!.Value, a.RevisionChange, a.RevisionNo, a.SourceRowNo));
        var c = existing[2];
        SupplierPreorderRevisionRules.ApplyRow(c, rows[2], diffs[2], 2, 3, keepSourceRowNo: false);
        Assert.True(c.IsActive);
        Assert.Equal(("Restored", 0), (c.RevisionChange, c.SourceRowNo));
        var b = existing[1];
        b.RevisionChange = "Price";
        b.PrevPrice = 1.40m;
        SupplierPreorderRevisionRules.ApplyRow(b, rows[1], diffs[1], 2, 2, true);
        Assert.Null(b.RevisionChange);
        Assert.Null(b.PrevPrice);

        // 换图：之前转存的 COS 图作废
        var withImage = new SupplierPreorderFormItem { ItemGuid = "g", SupplierItemCode = "IMG", Name = "Img", Price = 1, CartonQty = 1, ImageSourceUrl = "https://a/1.jpg", ImageCosUrl = "https://cos/1.jpg", ImageCopyStatus = "Done", ImageCopyAttempts = 1 };
        var imageRow = new SupplierPreorderNormalizedRow { RowNo = 2, SupplierItemCode = "IMG", Name = "Img", Price = 1, CartonQty = 1, ImageSourceUrl = "https://a/2.jpg" };
        var imageDiff = SupplierPreorderRevisionRules.Diff(new[] { withImage }, new[] { imageRow }).Single();
        Assert.Equal(new[] { "Image" }, imageDiff.Changes);
        SupplierPreorderRevisionRules.ApplyRow(withImage, imageRow, imageDiff, 1, 1, true);
        Assert.Equal((null, null, 0), (withImage.ImageCosUrl, withImage.ImageCopyStatus, withImage.ImageCopyAttempts));
    }

    [Fact]
    public async Task 修订版_预览不写库_应用后箱数保留_下架不计入且提醒本店_改价高亮_新增匹配_再改一版清掉旧高亮()
    {
        var admin = CreateService("admin-1", Permissions.SupplierPreorder.Manage, Permissions.SupplierPreorder.Order);
        var (formGuid, items) = await CreateOpenFormAsync(admin);
        await admin.SaveOrdererAsync("user-1", new SupplierPreorderSaveOrdererDto { StoreGuids = new() { "S1" } });
        var orderer = CreateService("user-1", Permissions.SupplierPreorder.Order);
        await orderer.SaveLineAsync(formGuid, "S1", items["XD1"], new SupplierPreorderSaveLineDto { Cartons = 4 });
        await orderer.SaveLineAsync(formGuid, "S1", items["XD3"], new SupplierPreorderSaveLineDto { Cartons = 2 });

        var revision = new SupplierPreorderRevisionRequestDto
        {
            HeaderRowNo = 1,
            Mapping = Mapping,
            Rows = new List<SupplierPreorderParsedRowDto>
            {
                Row(2, "XD1", "Bauble", "12", "1.20", image: "https://img.example/1.jpg"), // 改价改箱规
                Row(3, "XD2", "Lantern", "24", "1.53"),                                     // 不变
                Row(4, "XD2000", "Star Topper", "6", "3.10", barcode: "9333527800099"),     // 新增（条码对上我方商品）
                // XD3 被删掉
            },
        };

        var preview = await admin.PreviewRevisionAsync(formGuid, revision);
        Assert.Equal((1, 1, 1, 1, 1, 1), (preview.Counts.Added, preview.Counts.Removed, preview.Counts.Changed, preview.Counts.PriceChanged, preview.Counts.CartonChanged, preview.Counts.RemovedWithOrders));
        var removedEntry = preview.Entries.Single(e => e.Kind == "Removed");
        Assert.Equal(("XD3", 2, "Orion"), (removedEntry.SupplierItemCode, removedEntry.OrderedCartons, removedEntry.OrderedStores.Single()));
        Assert.Contains(preview.Warnings, w => w.Contains("已有分店下单", StringComparison.Ordinal));
        Assert.Equal(0, (await admin.GetFormAsync(formGuid)).Revision);   // 预览不写库

        var detail = await admin.ApplyRevisionAsync(formGuid, revision);
        Assert.Equal((1, 3, 1), (detail.Revision, detail.Summary.ItemCount, detail.RemovedCount));

        // 已下的箱数保留在下架商品上，但不计入汇总；本店汇总里提醒
        var summary = await orderer.GetOrderingSummaryAsync(formGuid, "S1");
        Assert.Equal(4, summary.Cartons);
        Assert.Equal(48, summary.Pieces);                                  // 新箱规 12 × 4
        Assert.Equal(("XD3", 2), (summary.RemovedLines.Single().SupplierItemCode, summary.RemovedLines.Single().Cartons));
        Assert.True(await _db.Queryable<SupplierPreorderStoreOrderLine>().AnyAsync(l => l.ItemGuid == items["XD3"] && l.Cartons == 2));

        var page = await orderer.GetOrderingItemsAsync(formGuid, new SupplierPreorderOrderingItemQueryDto { StoreGuid = "S1", Filter = "revised", Sort = "seq" });
        Assert.Equal(new[] { "XD1", "XD2000" }, page.Items!.Select(i => i.SupplierItemCode));
        var bauble = page.Items![0].RevisionMark!;
        Assert.Equal((1, 1.10m, 24), (bauble.Revision, bauble.PrevPrice!.Value, bauble.PrevCartonQty!.Value));
        Assert.Equal(new[] { "Price", "Carton" }, bauble.Changes);
        Assert.Equal(new[] { "Added" }, page.Items[1].RevisionMark!.Changes);

        var added = await _db.Queryable<SupplierPreorderFormItem>().FirstAsync(i => i.FormGuid == formGuid && i.SupplierItemCode == "XD2000");
        Assert.Equal("P-NEW", added.MatchedProductCode);
        var removedPage = await admin.GetFormItemsAsync(formGuid, new SupplierPreorderFormItemQueryDto { Match = "removed" });
        Assert.Equal(("XD3", false), (removedPage.Items!.Single().SupplierItemCode, removedPage.Items![0].IsActive));
        Assert.Contains((await admin.GetAuthLogsAsync(new SupplierPreorderAuthLogQueryDto { FormGuid = formGuid })).Items!, l => l.Action == "FormRevised");

        // 第 2 版：XD3 回来（重新上架，箱数照旧计入），XD1 不再变 → 旧高亮清掉
        revision.Rows.Add(Row(5, "XD3", "Sleigh", "2", "43.07"));
        revision.Rows[0] = Row(2, "XD1", "Bauble", "12", "1.20", image: "https://img.example/1.jpg");
        var second = await admin.ApplyRevisionAsync(formGuid, revision);
        Assert.Equal((2, 0), (second.Revision, second.RemovedCount));
        var after = (await _db.Queryable<SupplierPreorderFormItem>().Where(i => i.FormGuid == formGuid).ToListAsync()).ToDictionary(i => i.SupplierItemCode);
        Assert.Null(after["XD1"].RevisionChange);
        Assert.Equal("Restored", after["XD3"].RevisionChange);
        Assert.Equal(6, (await orderer.GetOrderingSummaryAsync(formGuid, "S1")).Cartons);
    }

    [Fact]
    public void 提醒规则_档位_按人合并_英文正文含截止悉尼时间()
    {
        var deadline = new DateTime(2026, 3, 6, 12, 59, 0, DateTimeKind.Utc);   // 悉尼 3/6 23:59
        Assert.Null(SupplierPreorderReminderService.DueTier(deadline, deadline.AddHours(-80)));
        Assert.Equal(TimeSpan.FromHours(72), SupplierPreorderReminderService.DueTier(deadline, deadline.AddHours(-50)));
        Assert.Equal(TimeSpan.FromHours(24), SupplierPreorderReminderService.DueTier(deadline, deadline.AddHours(-3)));
        Assert.Null(SupplierPreorderReminderService.DueTier(deadline, deadline.AddMinutes(1)));

        var groups = SupplierPreorderReminderService.GroupRecipients(
            new[] { ("S2", "1002", "Kawana"), ("S1", "1001", "Orion") },
            new[] { ("u1", "S2"), ("u1", "S1"), ("u2", "S2"), ("u3", "S3") });
        Assert.Equal(new[] { "Orion", "Kawana" }, groups["u1"]);
        Assert.Equal(new[] { "Kawana" }, groups["u2"]);
        Assert.False(groups.ContainsKey("u3"));

        var (title, body) = SupplierPreorderReminderService.BuildMessage("KFC Christmas 2026", new[] { "Orion", "Kawana" }, deadline);
        Assert.Equal("Preorder not submitted: KFC Christmas 2026", title);
        Assert.Contains("Orion, Kawana have not submitted", body);
        Assert.Contains("Fri 6 Mar 23:59 (Sydney)", body);
        var (_, many) = SupplierPreorderReminderService.BuildMessage("X", new[] { "A", "B", "C", "D", "E" }, null);
        Assert.StartsWith("A, B, C and 2 more have not submitted", many);
    }

    [Fact]
    public async Task 提醒_只发给能替未提交分店下单的人_已提交的不提醒_手动按分钟去重_自动按档位只发一次()
    {
        var admin = CreateService("admin-1", Permissions.SupplierPreorder.Manage, Permissions.SupplierPreorder.Order);
        var (formGuid, items) = await CreateOpenFormAsync(admin);
        await admin.SaveOrdererAsync("user-1", new SupplierPreorderSaveOrdererDto { StoreGuids = new() { "S1", "S2" } });
        await admin.SaveOrdererAsync("user-2", new SupplierPreorderSaveOrdererDto { StoreGuids = new() { "S3" } }); // S3 没开启
        // Orion 已提交
        var orderer = CreateService("user-1", Permissions.SupplierPreorder.Order);
        await orderer.SaveLineAsync(formGuid, "S1", items["XD1"], new SupplierPreorderSaveLineDto { Cartons = 1 });
        await orderer.SubmitStoreOrderAsync(formGuid, "S1");

        var result = await admin.RemindPendingStoresAsync(formGuid);
        Assert.Equal((1, 1), (result.PendingStoreCount, result.NotifiedUserCount));
        Assert.Empty(result.StoresWithoutOrderers);
        var sent = _publisher.Sent.Single();
        Assert.Equal(AppNotificationTypes.SupplierPreorderReminder, sent.Type);
        Assert.Equal(new[] { "user-1" }, sent.Audience.UserGuids);
        Assert.Contains("Kawana has not submitted", sent.Body);
        Assert.Equal(formGuid, sent.Data!["formGuid"]);

        // 同一分钟内重复点：不重复发
        Assert.Equal(0, (await admin.RemindPendingStoresAsync(formGuid)).NotifiedUserCount);

        // 没人能替它下单的分店会单独列出
        await admin.SaveOrdererAsync("user-1", new SupplierPreorderSaveOrdererDto { StoreGuids = new() { "S1" } });
        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);
        var uncovered = await admin.RemindPendingStoresAsync(formGuid);
        Assert.Equal(0, uncovered.NotifiedUserCount);
        Assert.Equal(new[] { "Kawana" }, uncovered.StoresWithoutOrderers);

        // 自动：离截止 > 72 小时不发；进入 24 小时档发一次，再跑不重复
        await admin.SaveOrdererAsync("user-1", new SupplierPreorderSaveOrdererDto { StoreGuids = new() { "S1", "S2" } });
        _publisher.Sent.Clear();
        var reminder = new SupplierPreorderReminderService(_db, _publisher, NullLogger.Instance, _clock);
        Assert.Equal(0, await reminder.RunAutoAsync(CancellationToken.None));
        _clock.UtcNow = new DateTime(2026, 3, 6, 2, 0, 0, DateTimeKind.Utc);
        Assert.Equal(1, await reminder.RunAutoAsync(CancellationToken.None));
        Assert.Equal(0, await reminder.RunAutoAsync(CancellationToken.None));
        Assert.Single(_publisher.Sent);

        // 截止锁定后不能手动提醒
        await admin.SetStatusAsync(formGuid, new SupplierPreorderSetStatusDto { Status = "Closed" });
        var closed = await Assert.ThrowsAsync<SupplierPreorderException>(() => admin.RemindPendingStoresAsync(formGuid));
        Assert.Equal("FORM_NOT_OPEN", closed.ErrorCode);
    }
}
