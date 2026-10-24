using System.Security.Claims;
using BlazorApp.Api.Features.ProductDescriptions;
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
/// 新品图片转存 COS（SQLite 真实落库，下载与上传用替身）：有我方商品图的跳过、同一原图地址只下载一次、
/// 失败最多 3 次且间隔 1 小时、管理端「重试失败」清零、列表优先显示 COS 地址。
/// </summary>
public sealed class SupplierPreorderImageCopyTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"supplier-preorder-image-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;
    private readonly ManualClock _clock = new(new DateTime(2026, 2, 20, 0, 0, 0, DateTimeKind.Utc));
    private readonly FakeFetcher _fetcher = new();
    private readonly FakeStore _store = new();

    public SupplierPreorderImageCopyTests()
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
        _db.Insertable(new HBLocalSupplier { Guid = "sup-257", LocalSupplierCode = "257", Name = "Uncle Bills (KFC)", Status = 1 }).ExecuteCommand();
        _db.Insertable(new Product
        {
            ProductCode = "P-A", ItemNumber = "XD0001815", Barcode = "9333527865090", ProductName = "Bauble", LocalSupplierCode = "257",
            PurchasePrice = 1.02m, IsActive = true, ProductImage = "https://hotbargain-yw.cos.ap-shanghai.myqcloud.com/YW200/XD0001815.jpg",
        }).ExecuteCommand();
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

    private sealed class FakeFetcher : IProductImageFetcher
    {
        public List<string> Calls { get; } = new();

        public Task<ProductImagePayload> FetchAsync(string imageUrl, CancellationToken cancellationToken)
        {
            Calls.Add(imageUrl);
            if (imageUrl.Contains("broken", StringComparison.Ordinal))
            {
                throw new ProductImageFetchException("图片下载失败（HTTP 404）");
            }

            return Task.FromResult(new ProductImagePayload(new byte[] { 1, 2, 3 }, "image/jpeg"));
        }
    }

    private sealed class FakeStore : ISupplierPreorderImageStore
    {
        public List<string> Keys { get; } = new();
        public bool IsConfigured => true;

        public Task<(string? Url, string? Error)> UploadAsync(string objectKey, byte[] content, string contentType, CancellationToken cancellationToken)
        {
            Keys.Add(objectKey);
            return Task.FromResult<(string?, string?)>(($"https://img.cos.ap-shanghai.myqcloud.com/{objectKey}", null));
        }
    }

    private SupplierPreorderImageCopyService Copier() => new(_db, _fetcher, _store, NullLogger.Instance, _clock);

    private SupplierPreorderService Admin()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("userId", "admin-1") }, "test"));
        var http = new Mock<IHttpContextAccessor>();
        http.SetupGet(x => x.HttpContext).Returns(new DefaultHttpContext { User = principal });
        var auth = new Mock<IAuthorizationService>();
        auth.Setup(x => x.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<string>()))
            .ReturnsAsync(AuthorizationResult.Success());
        var user = new Mock<ICurrentUserService>();
        user.Setup(x => x.GetCurrentUserGuid()).Returns("admin-1");
        user.Setup(x => x.GetCurrentUsername()).Returns("admin");
        return new SupplierPreorderService(_db, user.Object, auth.Object, http.Object, new Mock<IRoleService>().Object,
            NullLogger<SupplierPreorderService>.Instance, _clock, _store);
    }

    [Fact]
    public void 对象名按原图地址哈希_同一地址同一对象_供应商代码只留字母数字()
    {
        var key = SupplierPreorderImageCopyService.BuildObjectKey("257", "https://unclebills.hosted-by-files.com/Christmas/2026/XD0001817.jpg");
        Assert.Matches("^supplier-preorder/257/[0-9a-f]{24}\\.jpg$", key);
        Assert.Equal(key, SupplierPreorderImageCopyService.BuildObjectKey(" 257 ", " https://unclebills.hosted-by-files.com/Christmas/2026/XD0001817.jpg "));
        Assert.StartsWith("supplier-preorder/unknown/", SupplierPreorderImageCopyService.BuildObjectKey("../", "x"));
    }

    [Fact]
    public async Task 转存_跳过有我方图的_同地址只下载一次_失败三次后停止_重试失败清零_列表优先COS()
    {
        var admin = Admin();
        var detail = await admin.CreateFormAsync(new SupplierPreorderCreateFormDto
        {
            LocalSupplierCode = "257",
            HolidayType = "Christmas",
            SeasonYear = 2026,
            Title = "KFC Christmas 2026",
            HeaderRowNo = 1,
            Mapping = new SupplierPreorderColumnMappingDto { ItemCode = 0, Name = 1, CartonQty = 2, Price = 3, Barcode = 4, Image = 5 },
            Rows = new List<SupplierPreorderParsedRowDto>
            {
                new() { RowNo = 2, Cells = new List<string?> { "XD0001815", "Bauble", "24", "1.10", "9333527865090", "https://unclebills.example/XD0001815.jpg" } },
                new() { RowNo = 3, Cells = new List<string?> { "XD0001817", "Lantern", "24", "1.53", null, "https://unclebills.example/XD0001817.jpg" } },
                new() { RowNo = 4, Cells = new List<string?> { "XD0001818", "Lantern Dup", "24", "1.53", null, "https://unclebills.example/XD0001817.jpg" } },
                new() { RowNo = 5, Cells = new List<string?> { "XD0001819", "Broken", "24", "1.53", null, "https://unclebills.example/broken.jpg" } },
                new() { RowNo = 6, Cells = new List<string?> { "XD0001820", "No image", "24", "1.53", null, null } },
            },
        });
        var formGuid = detail.Summary.FormGuid;
        Assert.Equal((4, 0, 0, 4), (detail.ImageCopy.Total, detail.ImageCopy.Done, detail.ImageCopy.Skipped, detail.ImageCopy.Pending));

        Assert.Equal(4, await Copier().RunBatchAsync(CancellationToken.None));
        var items = (await _db.Queryable<SupplierPreorderFormItem>().Where(i => i.FormGuid == formGuid).ToListAsync()).ToDictionary(i => i.SupplierItemCode);
        Assert.Equal(SupplierPreorderImageCopyStatuses.Skipped, items["XD0001815"].ImageCopyStatus);
        Assert.Equal(SupplierPreorderImageCopyStatuses.Done, items["XD0001817"].ImageCopyStatus);
        Assert.Equal(items["XD0001817"].ImageCosUrl, items["XD0001818"].ImageCosUrl);
        Assert.Equal(1, _fetcher.Calls.Count(c => c.EndsWith("XD0001817.jpg", StringComparison.Ordinal))); // 同一地址只下载一次
        Assert.Equal((SupplierPreorderImageCopyStatuses.Failed, 1, "图片下载失败（HTTP 404）"),
            (items["XD0001819"].ImageCopyStatus, items["XD0001819"].ImageCopyAttempts, items["XD0001819"].ImageCopyError));
        Assert.Null(items["XD0001820"].ImageCopyStatus);

        // 1 小时内不重试；过 1 小时再试，直到满 3 次
        Assert.Equal(0, await Copier().RunBatchAsync(CancellationToken.None));
        for (var attempt = 2; attempt <= 3; attempt++)
        {
            _clock.UtcNow = _clock.UtcNow.AddHours(2);
            Assert.Equal(1, await Copier().RunBatchAsync(CancellationToken.None));
        }

        _clock.UtcNow = _clock.UtcNow.AddHours(2);
        Assert.Equal(0, await Copier().RunBatchAsync(CancellationToken.None));
        var form = await admin.GetFormAsync(formGuid);
        Assert.Equal((2, 1, 1, 0), (form.ImageCopy.Done, form.ImageCopy.Skipped, form.ImageCopy.Failed, form.ImageCopy.Pending));

        // 重试失败：次数清零，后台再下载
        var retried = await admin.RetryFailedImagesAsync(formGuid);
        Assert.Equal((0, 1), (retried.ImageCopy.Failed, retried.ImageCopy.Pending));
        Assert.Equal(1, await Copier().RunBatchAsync(CancellationToken.None));

        // 列表：新品显示 COS 地址，有我方图的显示我方图，原表地址仍保留给「查看原图」
        var page = await admin.GetFormItemsAsync(formGuid, new SupplierPreorderFormItemQueryDto());
        var lantern = page.Items!.Single(i => i.SupplierItemCode == "XD0001817");
        Assert.StartsWith("https://img.cos.ap-shanghai.myqcloud.com/supplier-preorder/257/", lantern.ImageUrl);
        Assert.Equal("https://unclebills.example/XD0001817.jpg", lantern.ImageSourceUrl);
        Assert.Contains("YW200/XD0001815.jpg", page.Items!.Single(i => i.SupplierItemCode == "XD0001815").ImageUrl);
    }
}
