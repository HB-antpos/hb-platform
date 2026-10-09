using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Models;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class AdvertisementReactServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _sqliteConnection;
    private readonly SqlSugarClient _db;

    public AdvertisementReactServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _sqliteConnection = new SqliteConnection($"Data Source={_dbPath}");
        _sqliteConnection.Open();

        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _sqliteConnection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });

        _db.CodeFirst.InitTables(typeof(Store), typeof(Advertisement), typeof(AdvertisementStore));
        SeedStores();
    }

    [Fact]
    public async Task CreateAsync_StoresAdvertisementAndRelations()
    {
        var service = CreateService();

        var result = await service.CreateAsync(
            new CreateAdvertisementDto
            {
                Title = "EOFY Sale",
                Description = "Main foyer screen",
                MediaType = "Image",
                MediaUrl = "https://cdn.example.com/ads/a.jpg?signature=client-ignored",
                ThumbnailUrl = "https://cdn.example.com/ads/a-thumb.jpg",
                ObjectKey = "ads/2026/018f45ad00007000a000000000000001.jpg",
                OriginalFileName = "a.jpg",
                ContentType = "image/jpeg",
                FileSize = 1024,
                EffectiveStart = new DateTime(2026, 5, 1),
                EffectiveEnd = new DateTime(2026, 5, 31),
                IsEnabled = true,
                SortOrder = 2,
                Stores = new List<AdvertisementStoreItemDto>
                {
                    new() { StoreCode = "S01" },
                    new() { StoreCode = "S02", StoreName = "客户端名称不得写入" },
                },
            }
        );

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("EOFY Sale", result.Data!.Title);
        Assert.Equal(
            "https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/ads/2026/018f45ad00007000a000000000000001.jpg",
            result.Data.MediaUrl
        );
        Assert.Equal(2, result.Data.Stores.Count);
        Assert.Equal(new[] { "Store 1", "Store 2" }, result.Data.Stores.Select(GetStoreName).ToArray());

        var stores = await _db.Queryable<AdvertisementStore>().OrderBy(x => x.StoreCode).ToListAsync();
        Assert.Equal(new[] { "S01", "S02" }, stores.Select(item => item.StoreCode).ToArray());
    }

    [Fact]
    public async Task UpdateAsync_ReplacesStoreRelations()
    {
        await SeedAdvertisementAsync("ad-1", "Old", true, "Image", "S01", "S02");
        var service = CreateService();

        var result = await service.UpdateAsync(
            "ad-1",
            new UpdateAdvertisementDto
            {
                Title = "New",
                Description = "Updated",
                MediaType = "Video",
                MediaUrl = "https://cdn.example.com/ads/new.mp4?signature=client-ignored",
                ThumbnailUrl = "https://cdn.example.com/ads/new.jpg",
                ObjectKey = "ads/2026/018f45ad00007000a000000000000002.mp4",
                OriginalFileName = "new.mp4",
                ContentType = "video/mp4",
                FileSize = 4096,
                EffectiveStart = new DateTime(2026, 6, 1),
                EffectiveEnd = new DateTime(2026, 6, 30),
                IsEnabled = false,
                SortOrder = 5,
                Stores = new List<AdvertisementStoreItemDto>
                {
                    new() { StoreCode = "S03" },
                },
            }
        );

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("New", result.Data!.Title);
        Assert.Equal("Video", result.Data.MediaType);
        Assert.Equal(
            "https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/ads/2026/018f45ad00007000a000000000000002.mp4",
            result.Data.MediaUrl
        );
        Assert.Single(result.Data.Stores);
        Assert.Equal("S03", result.Data.Stores[0].StoreCode);
        Assert.Equal("Store 3", GetStoreName(result.Data.Stores[0]));

        var stores = await _db.Queryable<AdvertisementStore>().Where(x => x.AdvertisementId == "ad-1").ToListAsync();
        Assert.Single(stores);
        Assert.Equal("S03", stores[0].StoreCode);
    }

    [Fact]
    public async Task GetStoreOptionsAsync_ReturnsAllActiveStoresOnly()
    {
        var service = CreateService();

        var result = await service.GetStoreOptionsAsync();

        Assert.True(result.Success);
        // S04 未启用、S05 已删除，都不能出现在选项里；其余启用分店必须全部返回。
        Assert.Equal(new[] { "S01", "S02", "S03" }, result.Data!.Select(item => item.StoreCode).ToArray());
        Assert.Equal("Store 1", result.Data[0].StoreName);
    }

    [Fact]
    public async Task GetGridAsync_FiltersByStoreCodeAndSimpleFields()
    {
        await SeedAdvertisementAsync("ad-1", "Winter Image", true, "Image", "S01");
        await SeedAdvertisementAsync("ad-2", "Winter Video", false, "Video", "S02");
        var service = CreateService();

        var result = await service.GetGridAsync(
            new AdvertisementGridRequestDto
            {
                StartRow = 0,
                PageSize = 20,
                GlobalSearch = "Winter",
                StoreCode = "S01",
                MediaType = "Image",
                IsEnabled = true,
            }
        );

        Assert.True(result.Success);
        var item = Assert.Single(result.Items!);
        Assert.Equal("ad-1", item.Id);
        Assert.Single(item.Stores);
        Assert.Equal("S01", item.Stores[0].StoreCode);
        Assert.Equal("Store 1", GetStoreName(item.Stores[0]));
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNamesForInactiveStoresAndSafelyFallsBackForUnknownCodes()
    {
        await SeedAdvertisementAsync("ad-store-names", "Store names", true, "Image", "s04", "S05", "S99");
        var service = CreateService();

        var result = await service.GetByIdAsync("ad-store-names");

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var storesByCode = result.Data!.Stores.ToDictionary(
            store => store.StoreCode,
            StringComparer.OrdinalIgnoreCase
        );
        Assert.Equal("Store 4", GetStoreName(storesByCode["S04"]));
        Assert.Null(GetStoreName(storesByCode["S05"]));
        Assert.Null(GetStoreName(storesByCode["S99"]));
    }

    [Fact]
    public async Task GetGridAsync_FiltersByFilterModelDateRange()
    {
        await SeedAdvertisementAsync(
            "ad-1",
            "May Campaign",
            true,
            "Image",
            new[] { "S01" },
            new DateTime(2026, 5, 1),
            new DateTime(2026, 5, 31)
        );
        await SeedAdvertisementAsync(
            "ad-2",
            "June Campaign",
            true,
            "Image",
            new[] { "S01" },
            new DateTime(2026, 6, 1),
            new DateTime(2026, 6, 30)
        );

        var service = CreateService();
        var result = await service.GetGridAsync(
            new AdvertisementGridRequestDto
            {
                StartRow = 0,
                PageSize = 20,
                FilterModel = new Dictionary<string, FilterModelDto>
                {
                    ["effectiveStart"] = new()
                    {
                        FilterType = "date",
                        Type = "inRange",
                        Filter = "2026-05-01",
                        FilterTo = "2026-05-31",
                    },
                },
            }
        );

        Assert.True(result.Success);
        var item = Assert.Single(result.Items!);
        Assert.Equal("ad-1", item.Id);
    }

    [Fact]
    public async Task GetUploadSignatureAsync_UsesMainBucketAdsPrefixAndAllowedContentType()
    {
        var service = CreateService();

        var result = await service.GetUploadSignatureAsync(
            new AdvertisementUploadSignatureRequestDto
            {
                FileName = "promo-banner.jpg",
                ContentType = "image/jpeg",
                FileSize = 1234,
            }
        );

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.StartsWith($"ads/{DateTime.UtcNow:yyyy}/", result.Data!.ObjectKey);
        Assert.EndsWith(".jpg", result.Data.ObjectKey);
        Assert.Contains("hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com", result.Data.UploadUrl);
        Assert.Equal("image/jpeg", result.Data.Headers["Content-Type"]);
        Assert.Contains(result.Data.ObjectKey, result.Data.MediaUrl);
    }

    [Fact]
    public async Task GetUploadSignatureAsync_RejectsUnsupportedContentType()
    {
        var service = CreateService();

        var result = await service.GetUploadSignatureAsync(
            new AdvertisementUploadSignatureRequestDto
            {
                FileName = "promo.pdf",
                ContentType = "application/pdf",
                FileSize = 10,
            }
        );

        Assert.False(result.Success);
        Assert.Equal("INVALID_CONTENT_TYPE", result.ErrorCode);
    }

    [Theory]
    [InlineData("YW200/018f45ad00007000a000000000000003.jpg")]
    [InlineData("ads/banner.jpg")]
    [InlineData("ads/2026/banner.jpg")]
    [InlineData("ads/2026/018f45ad00007000a000000000000003.png")]
    public async Task CreateAsync_RejectsInvalidObjectKey(string objectKey)
    {
        var service = CreateService();

        var result = await service.CreateAsync(
            new CreateAdvertisementDto
            {
                Title = "Invalid key",
                Description = "Invalid",
                MediaType = "Image",
                MediaUrl = "https://evil.example.com/file.jpg?signature=bad",
                ThumbnailUrl = "https://evil.example.com/thumb.jpg",
                ObjectKey = objectKey,
                OriginalFileName = "file.jpg",
                ContentType = "image/jpeg",
                FileSize = 1024,
                EffectiveStart = new DateTime(2026, 5, 1),
                EffectiveEnd = new DateTime(2026, 5, 31),
                IsEnabled = true,
                SortOrder = 1,
                Stores = new List<AdvertisementStoreItemDto>
                {
                    new() { StoreCode = "S01" },
                },
            }
        );

        Assert.False(result.Success);
        Assert.Equal("INVALID_OBJECT_KEY", result.ErrorCode);
    }

    [Fact]
    public async Task CreateAsync_AllStoresScopeStoresNoRelations()
    {
        var service = CreateService();

        var result = await service.CreateAsync(
            new CreateAdvertisementDto
            {
                Title = "All stores",
                Description = "Global campaign",
                MediaType = "Image",
                MediaUrl = "https://cdn.example.com/file.jpg",
                ObjectKey = "ads/2026/018f45ad00007000a000000000000004.jpg",
                OriginalFileName = "file.jpg",
                ContentType = "image/jpeg",
                FileSize = 1024,
                EffectiveStart = new DateTime(2026, 5, 1),
                EffectiveEnd = new DateTime(2026, 5, 31),
                IsEnabled = true,
                SortOrder = 1,
                Stores = new List<AdvertisementStoreItemDto>(),
            }
        );

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data!.Stores);

        var stores = await _db.Queryable<AdvertisementStore>().ToListAsync();
        Assert.Empty(stores);
    }

    [Fact]
    public async Task CreateAsync_RejectsInactiveOrMissingStores()
    {
        var service = CreateService();

        var result = await service.CreateAsync(
            new CreateAdvertisementDto
            {
                Title = "Bad stores",
                Description = "Invalid",
                MediaType = "Image",
                MediaUrl = "https://cdn.example.com/file.jpg",
                ObjectKey = "ads/2026/018f45ad00007000a000000000000005.jpg",
                OriginalFileName = "file.jpg",
                ContentType = "image/jpeg",
                FileSize = 1024,
                EffectiveStart = new DateTime(2026, 5, 1),
                EffectiveEnd = new DateTime(2026, 5, 31),
                IsEnabled = true,
                SortOrder = 1,
                Stores = new List<AdvertisementStoreItemDto>
                {
                    new() { StoreCode = "S99" },
                    new() { StoreCode = "S04" },
                },
            }
        );

        Assert.False(result.Success);
        Assert.Equal("INVALID_STORE_SCOPE", result.ErrorCode);
    }

    [Fact]
    public async Task CreateAsync_NormalizesOrientationAndStoresMediaSize()
    {
        var service = CreateService();
        var dto = CreateValidDto("ads/2026/018f45ad00007000a000000000000011.jpg");
        dto.Orientation = " portrait ";
        dto.MediaWidth = 1080;
        dto.MediaHeight = 1920;

        var result = await service.CreateAsync(dto);

        Assert.True(result.Success, result.Message);
        Assert.Equal("Portrait", result.Data!.Orientation);
        Assert.Equal(1080, result.Data.MediaWidth);
        Assert.Equal(1920, result.Data.MediaHeight);
        var stored = await _db.Queryable<Advertisement>().SingleAsync(item => item.Id == result.Data.Id);
        Assert.Equal("Portrait", stored.Orientation);
        Assert.Equal(1080, stored.MediaWidth);
        Assert.Equal(1920, stored.MediaHeight);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateAsync_BlankOrientationStoresAnyWithoutMediaSize(string? orientation)
    {
        var service = CreateService();
        var dto = CreateValidDto("ads/2026/018f45ad00007000a000000000000012.jpg");
        dto.Orientation = orientation;

        var result = await service.CreateAsync(dto);

        Assert.True(result.Success, result.Message);
        Assert.Equal("Any", result.Data!.Orientation);
        Assert.Null(result.Data.MediaWidth);
        Assert.Null(result.Data.MediaHeight);
        var stored = await _db.Queryable<Advertisement>().SingleAsync(item => item.Id == result.Data.Id);
        Assert.Equal("Any", stored.Orientation);
    }

    [Theory]
    [InlineData("Square")]
    [InlineData("horizontal")]
    [InlineData("land scape")]
    public async Task CreateAsync_RejectsUnknownOrientation(string orientation)
    {
        var service = CreateService();
        var dto = CreateValidDto("ads/2026/018f45ad00007000a000000000000013.jpg");
        dto.Orientation = orientation;

        var result = await service.CreateAsync(dto);

        Assert.False(result.Success);
        Assert.Equal("INVALID_ORIENTATION", result.ErrorCode);
        Assert.Equal("广告版式无效", result.Message);
        Assert.Empty(await _db.Queryable<Advertisement>().ToListAsync());
    }

    [Theory]
    [InlineData(1920, null)]
    [InlineData(null, 1080)]
    [InlineData(0, 1080)]
    [InlineData(-1, 1080)]
    [InlineData(1920, 20001)]
    [InlineData(20001, 1)]
    public async Task CreateAsync_RejectsInvalidMediaSize(int? width, int? height)
    {
        var service = CreateService();
        var dto = CreateValidDto("ads/2026/018f45ad00007000a000000000000014.jpg");
        dto.MediaWidth = width;
        dto.MediaHeight = height;

        var result = await service.CreateAsync(dto);

        Assert.False(result.Success);
        Assert.Equal("INVALID_MEDIA_SIZE", result.ErrorCode);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(20000, 20000)]
    public async Task CreateAsync_AcceptsMediaSizeBoundaries(int width, int height)
    {
        var service = CreateService();
        var dto = CreateValidDto("ads/2026/018f45ad00007000a000000000000015.jpg");
        dto.Orientation = "LANDSCAPE";
        dto.MediaWidth = width;
        dto.MediaHeight = height;

        var result = await service.CreateAsync(dto);

        Assert.True(result.Success, result.Message);
        Assert.Equal("Landscape", result.Data!.Orientation);
        Assert.Equal(width, result.Data.MediaWidth);
        Assert.Equal(height, result.Data.MediaHeight);
    }

    [Fact]
    public async Task UpdateAsync_ChangesOrientationAndRejectsInvalidValueWithoutWriting()
    {
        await SeedAdvertisementAsync("ad-orientation", "Legacy", true, "Image", "S01");
        var service = CreateService();

        var legacy = await service.GetByIdAsync("ad-orientation");
        Assert.Equal("Any", legacy.Data!.Orientation);
        Assert.Null(legacy.Data.MediaWidth);

        var invalid = CreateValidUpdateDto("ads/2026/018f45ad00007000a000000000000016.jpg");
        invalid.Orientation = "Diagonal";
        var rejected = await service.UpdateAsync("ad-orientation", invalid);
        Assert.False(rejected.Success);
        Assert.Equal("INVALID_ORIENTATION", rejected.ErrorCode);
        Assert.Equal("Legacy", (await _db.Queryable<Advertisement>().SingleAsync(item => item.Id == "ad-orientation")).Title);

        var update = CreateValidUpdateDto("ads/2026/018f45ad00007000a000000000000016.jpg");
        update.Orientation = "landscape";
        update.MediaWidth = 1920;
        update.MediaHeight = 1080;
        var updated = await service.UpdateAsync("ad-orientation", update);

        Assert.True(updated.Success, updated.Message);
        Assert.Equal("Landscape", updated.Data!.Orientation);
        Assert.Equal(1920, updated.Data.MediaWidth);
        Assert.Equal(1080, updated.Data.MediaHeight);
        var stored = await _db.Queryable<Advertisement>().SingleAsync(item => item.Id == "ad-orientation");
        Assert.Equal("Landscape", stored.Orientation);
    }

    [Fact]
    public async Task GetGridAsync_FiltersByOrientationCaseInsensitiveExactMatch()
    {
        await SeedAdvertisementAsync("ad-any", "Any ad", true, "Image", Array.Empty<string>(),
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), "Any");
        await SeedAdvertisementAsync("ad-portrait", "Portrait ad", true, "Image", Array.Empty<string>(),
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), "Portrait");
        await SeedAdvertisementAsync("ad-landscape", "Landscape ad", true, "Image", Array.Empty<string>(),
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), "Landscape");
        var service = CreateService();

        var simple = await service.GetGridAsync(new AdvertisementGridRequestDto
        {
            StartRow = 0,
            PageSize = 20,
            Orientation = " PORTRAIT ",
        });
        Assert.Equal(new[] { "ad-portrait" }, simple.Items!.Select(item => item.Id).ToArray());
        Assert.Equal("Portrait", simple.Items![0].Orientation);

        var unfiltered = await service.GetGridAsync(new AdvertisementGridRequestDto
        {
            StartRow = 0,
            PageSize = 20,
            Orientation = "",
        });
        Assert.Equal(3, unfiltered.Items!.Count);

        // 无法识别的取值不放宽过滤：结果为空。
        var unknown = await service.GetGridAsync(new AdvertisementGridRequestDto
        {
            StartRow = 0,
            PageSize = 20,
            Orientation = "Square",
        });
        Assert.Empty(unknown.Items!);

        var filterModel = await service.GetGridAsync(new AdvertisementGridRequestDto
        {
            StartRow = 0,
            PageSize = 20,
            FilterModel = new Dictionary<string, FilterModelDto>
            {
                ["orientation"] = new()
                {
                    FilterType = "set",
                    Values = new List<string> { "landscape", "any" },
                },
            },
        });
        Assert.Equal(
            new[] { "ad-any", "ad-landscape" },
            filterModel.Items!.Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void AdvertisementDtos_SerializeOrientationAndMediaSizeAsCamelCase()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new AdvertisementDetailDto { Orientation = "Portrait", MediaWidth = 1080, MediaHeight = 1920 },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Contains("\"orientation\":\"Portrait\"", json);
        Assert.Contains("\"mediaWidth\":1080", json);
        Assert.Contains("\"mediaHeight\":1920", json);
    }

    public void Dispose()
    {
        _db.Dispose();
        _sqliteConnection.Dispose();

        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private AdvertisementReactService CreateService()
    {
        return new AdvertisementReactService(
            CreateSqlSugarContext(_db),
            new TencentCloudUploadService(
                Options.Create(
                    new TencentCloudSettings
                    {
                        SecretId = "secret-id",
                        SecretKey = "secret-key",
                        BucketName = "hb-sales-2019-1300114625",
                        Region = "ap-singapore",
                        ImageBucketName = "image-bucket",
                        ImageRegion = "ap-shanghai",
                    }
                ),
                NullLogger<TencentCloudUploadService>.Instance,
                new HttpClient()
            ),
            Options.Create(
                new TencentCloudSettings
                {
                    SecretId = "secret-id",
                    SecretKey = "secret-key",
                    BucketName = "hb-sales-2019-1300114625",
                    Region = "ap-singapore",
                    ImageBucketName = "image-bucket",
                    ImageRegion = "ap-shanghai",
                }
            )
        );
    }

    private void SeedStores()
    {
        _db.Insertable(
            new List<Store>
            {
                new()
                {
                    StoreGUID = "store-guid-1",
                    StoreCode = "S01",
                    StoreName = "Store 1",
                    IsActive = true,
                    IsDeleted = false,
                },
                new()
                {
                    StoreGUID = "store-guid-2",
                    StoreCode = "S02",
                    StoreName = "Store 2",
                    IsActive = true,
                    IsDeleted = false,
                },
                new()
                {
                    StoreGUID = "store-guid-3",
                    StoreCode = "S03",
                    StoreName = "Store 3",
                    IsActive = true,
                    IsDeleted = false,
                },
                new()
                {
                    StoreGUID = "store-guid-4",
                    StoreCode = "S04",
                    StoreName = "Store 4",
                    IsActive = false,
                    IsDeleted = false,
                },
                new()
                {
                    StoreGUID = "store-guid-5",
                    StoreCode = "S05",
                    StoreName = "Deleted store",
                    IsActive = true,
                    IsDeleted = true,
                },
            }
        ).ExecuteCommand();
    }

    private async Task SeedAdvertisementAsync(
        string id,
        string title,
        bool isEnabled,
        string mediaType,
        params string[] storeCodes
    )
    {
        await SeedAdvertisementAsync(
            id,
            title,
            isEnabled,
            mediaType,
            storeCodes,
            new DateTime(2026, 1, 1),
            new DateTime(2026, 12, 31)
        );
    }

    private async Task SeedAdvertisementAsync(
        string id,
        string title,
        bool isEnabled,
        string mediaType,
        string[] storeCodes,
        DateTime effectiveStart,
        DateTime effectiveEnd,
        string orientation = "Any"
    )
    {
        await _db.Insertable(
            new Advertisement
            {
                Orientation = orientation,
                Id = id,
                Title = title,
                Description = title,
                MediaType = mediaType,
                MediaUrl = $"https://cdn.example.com/{id}",
                ThumbnailUrl = $"https://cdn.example.com/{id}.jpg",
                ObjectKey = $"ads/2026/{id}.{(mediaType == "Video" ? "mp4" : "jpg")}",
                OriginalFileName = $"{id}.{(mediaType == "Video" ? "mp4" : "jpg")}",
                ContentType = mediaType == "Video" ? "video/mp4" : "image/jpeg",
                FileSize = 100,
                EffectiveStart = effectiveStart,
                EffectiveEnd = effectiveEnd,
                IsEnabled = isEnabled,
                SortOrder = 1,
                CreatedAt = new DateTime(2026, 1, 1),
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();

        if (storeCodes.Length == 0)
        {
            return;
        }

        await _db.Insertable(
            storeCodes.Select(storeCode => new AdvertisementStore
            {
                Id = $"{id}-{storeCode}",
                AdvertisementId = id,
                StoreCode = storeCode,
                CreatedAt = new DateTime(2026, 1, 1),
                IsDeleted = false,
            }).ToList()
        ).ExecuteCommandAsync();
    }

    private static CreateAdvertisementDto CreateValidDto(string objectKey)
    {
        return new CreateAdvertisementDto
        {
            Title = "Orientation",
            Description = "Orientation",
            MediaType = "Image",
            MediaUrl = "https://cdn.example.com/file.jpg",
            ObjectKey = objectKey,
            OriginalFileName = "file.jpg",
            ContentType = "image/jpeg",
            FileSize = 1024,
            EffectiveStart = new DateTime(2026, 5, 1),
            EffectiveEnd = new DateTime(2026, 5, 31),
            IsEnabled = true,
            SortOrder = 1,
            Stores = new List<AdvertisementStoreItemDto> { new() { StoreCode = "S01" } },
        };
    }

    private static UpdateAdvertisementDto CreateValidUpdateDto(string objectKey)
    {
        var source = CreateValidDto(objectKey);
        return new UpdateAdvertisementDto
        {
            Title = source.Title,
            Description = source.Description,
            MediaType = source.MediaType,
            MediaUrl = source.MediaUrl,
            ObjectKey = source.ObjectKey,
            OriginalFileName = source.OriginalFileName,
            ContentType = source.ContentType,
            FileSize = source.FileSize,
            EffectiveStart = source.EffectiveStart,
            EffectiveEnd = source.EffectiveEnd,
            IsEnabled = source.IsEnabled,
            SortOrder = source.SortOrder,
            Stores = source.Stores,
        };
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        var dbField = typeof(SqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic);
        dbField!.SetValue(context, db);
        return context;
    }

    private static string? GetStoreName(AdvertisementStoreItemDto store)
    {
        return store.StoreName;
    }
}
