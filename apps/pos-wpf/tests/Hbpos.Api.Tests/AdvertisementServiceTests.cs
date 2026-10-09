using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Shared.Models.HBweb;
using Hbpos.Api.Data;
using Hbpos.Api.Services;
using SqlSugar;

namespace Hbpos.Api.Tests;

public sealed class AdvertisementServiceTests
{
    [Fact]
    public async Task GetActiveAsync_FiltersSortsAndMapsAdvertisements()
    {
        await using var fixture = await AdvertisementSqliteFixture.CreateAsync();
        var now = DateTime.UtcNow;

        await fixture.SeedAdvertisementAsync(
            CreateAdvertisement("AD-001", "Image Promo", " IMAGE ", 2, now.AddMinutes(-10), now.AddHours(-1), now.AddHours(2)),
            storeCode: "S01");
        await fixture.SeedAdvertisementAsync(
            CreateAdvertisement("AD-002", "Video Promo", " video ", 1, now.AddMinutes(-5), now.AddHours(-1), now.AddHours(2)),
            storeCode: "S01");
        await fixture.SeedAdvertisementAsync(
            CreateAdvertisement("AD-003", "Older Video Promo", "video", 1, now.AddMinutes(-20), now.AddHours(-1), now.AddHours(2)),
            storeCode: "S01");
        await fixture.SeedAdvertisementAsync(
            CreateAdvertisement("AD-009", "All Stores Promo", "image", 1, now.AddMinutes(-1), now.AddHours(-1), now.AddHours(2)),
            storeCode: null);
        await fixture.SeedAdvertisementAsync(
            CreateAdvertisement("AD-004", "Other Store Promo", "image", 1, now.AddMinutes(-1), now.AddHours(-1), now.AddHours(2)),
            storeCode: "S02");
        await fixture.SeedAdvertisementAsync(
            CreateAdvertisement("AD-005", "Expired Promo", "image", 1, now.AddMinutes(-2), now.AddHours(-3), now.AddHours(-1)),
            storeCode: "S01");
        await fixture.SeedAdvertisementAsync(
            CreateAdvertisement("AD-006", "Disabled Promo", "image", 1, now.AddMinutes(-3), now.AddHours(-1), now.AddHours(2), isEnabled: false),
            storeCode: "S01");
        await fixture.SeedAdvertisementAsync(
            CreateAdvertisement("AD-007", "Deleted Promo", "image", 1, now.AddMinutes(-4), now.AddHours(-1), now.AddHours(2), isDeleted: true),
            storeCode: "S01");
        await fixture.SeedAdvertisementAsync(
            CreateAdvertisement("AD-008", "Deleted Store Scope Promo", "image", 1, now.AddMinutes(-6), now.AddHours(-1), now.AddHours(2)),
            storeCode: "S01",
            storeIsDeleted: true);

        var service = new AdvertisementPlaybackService(fixture.DbContext);

        var response = await service.GetActiveAsync(" S01 ", 20, CancellationToken.None);

        Assert.Equal("S01", response.StoreCode);
        Assert.Equal(["AD-009", "AD-002", "AD-003", "AD-001"], response.Items.Select(item => item.Id).ToArray());
        Assert.Equal("video", response.Items[1].MediaType);
        Assert.Equal("image", response.Items[0].MediaType);
        Assert.Equal("image", response.Items[3].MediaType);
        Assert.All(response.Items, item =>
        {
            Assert.Equal(TimeSpan.Zero, item.EffectiveStart.Offset);
            Assert.Equal(TimeSpan.Zero, item.EffectiveEnd.Offset);
        });
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-3, 20)]
    [InlineData(100, 50)]
    [InlineData(7, 7)]
    public async Task GetActiveAsync_NormalizesTake(int requestedTake, int expectedCount)
    {
        await using var fixture = await AdvertisementSqliteFixture.CreateAsync();
        var now = DateTime.UtcNow;

        for (var index = 1; index <= 60; index++)
        {
            await fixture.SeedAdvertisementAsync(
                CreateAdvertisement(
                    $"AD-{index:D3}",
                    $"Promo {index:D3}",
                    "image",
                    sortOrder: index,
                    createdAt: now.AddMinutes(-index),
                    effectiveStart: now.AddHours(-1),
                    effectiveEnd: now.AddHours(1)),
                storeCode: "S01");
        }

        var service = new AdvertisementPlaybackService(fixture.DbContext);

        var response = await service.GetActiveAsync("S01", requestedTake, CancellationToken.None);

        Assert.Equal(expectedCount, response.Items.Count);
    }

    [Fact]
    public async Task GetActiveAsync_MapsOrientationToLowercaseAndMediaSize()
    {
        await using var fixture = await AdvertisementSqliteFixture.CreateAsync();
        var now = DateTime.UtcNow;

        await fixture.SeedAdvertisementAsync(
            CreateAdvertisement("AD-L", "Landscape", "image", 1, now.AddMinutes(-1), now.AddHours(-1), now.AddHours(1),
                orientation: "Landscape", mediaWidth: 1920, mediaHeight: 1080),
            storeCode: "S01");
        await fixture.SeedAdvertisementAsync(
            CreateAdvertisement("AD-P", "Portrait", "image", 2, now.AddMinutes(-1), now.AddHours(-1), now.AddHours(1),
                orientation: "portrait", mediaWidth: 1080, mediaHeight: 1920),
            storeCode: "S01");
        await fixture.SeedAdvertisementAsync(
            CreateAdvertisement("AD-A", "Any", "image", 3, now.AddMinutes(-1), now.AddHours(-1), now.AddHours(1),
                orientation: "Any"),
            storeCode: "S01");
        await fixture.SeedAdvertisementAsync(
            CreateAdvertisement("AD-U", "Unknown", "image", 4, now.AddMinutes(-1), now.AddHours(-1), now.AddHours(1),
                orientation: "Square"),
            storeCode: "S01");
        await fixture.SeedAdvertisementAsync(
            CreateAdvertisement("AD-E", "Empty", "image", 5, now.AddMinutes(-1), now.AddHours(-1), now.AddHours(1),
                orientation: ""),
            storeCode: "S01");

        var service = new AdvertisementPlaybackService(fixture.DbContext);

        var response = await service.GetActiveAsync("S01", 20, CancellationToken.None);

        // 库里大小写不规范的值也按版式识别；空值与未知值一律回落为 any。
        Assert.Equal(["AD-L", "AD-P", "AD-A", "AD-U", "AD-E"], response.Items.Select(item => item.Id).ToArray());
        Assert.Equal(
            ["landscape", "portrait", "any", "any", "any"],
            response.Items.Select(item => item.Orientation).ToArray());
        Assert.Equal(1920, response.Items[0].MediaWidth);
        Assert.Equal(1080, response.Items[0].MediaHeight);
        Assert.Equal(1080, response.Items[1].MediaWidth);
        Assert.Equal(1920, response.Items[1].MediaHeight);
        Assert.Null(response.Items[2].MediaWidth);
        Assert.Null(response.Items[2].MediaHeight);
    }

    [Fact]
    public void AdvertisementPlaybackItemDto_OldPositionalCallersDefaultToAny()
    {
        // 末尾追加的可选参数不影响旧的 13 参数构造方式，默认 any 且无宽高。
        var item = new Hbpos.Contracts.Advertisements.AdvertisementPlaybackItemDto(
            "AD-OLD", "Old", null, "image", "https://cdn.example.com/a.jpg", null, "ads/a.jpg", "a.jpg",
            "image/jpeg", 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1);

        Assert.Equal("any", item.Orientation);
        Assert.Null(item.MediaWidth);
        Assert.Null(item.MediaHeight);

        var json = System.Text.Json.JsonSerializer.Serialize(
            item,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"orientation\":\"any\"", json);
        Assert.Contains("\"mediaWidth\":null", json);
    }

    private static Advertisement CreateAdvertisement(
        string id,
        string title,
        string mediaType,
        int sortOrder,
        DateTime createdAt,
        DateTime effectiveStart,
        DateTime effectiveEnd,
        bool isEnabled = true,
        bool isDeleted = false,
        string orientation = "Any",
        int? mediaWidth = null,
        int? mediaHeight = null)
    {
        return new Advertisement
        {
            Orientation = orientation,
            MediaWidth = mediaWidth,
            MediaHeight = mediaHeight,
            Id = id,
            Title = title,
            Description = $"{title} description",
            MediaType = mediaType,
            MediaUrl = $"https://cdn.example.com/{id}.jpg",
            ThumbnailUrl = $"https://cdn.example.com/{id}-thumb.jpg",
            ObjectKey = $"advertisements/{id}.jpg",
            OriginalFileName = $"{id}.jpg",
            ContentType = "image/jpeg",
            FileSize = 1024,
            EffectiveStart = effectiveStart,
            EffectiveEnd = effectiveEnd,
            IsEnabled = isEnabled,
            SortOrder = sortOrder,
            CreatedAt = createdAt,
            IsDeleted = isDeleted
        };
    }

    private sealed class AdvertisementSqliteFixture : IAsyncDisposable
    {
        private readonly string databasePath = Path.Combine(
            Path.GetTempPath(),
            $"hbpos-advertisement-tests-{Guid.NewGuid():N}.db");
        private readonly SqlSugarClient client;

        private AdvertisementSqliteFixture()
        {
            client = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = $"Data Source={databasePath}",
                DbType = DbType.Sqlite,
                InitKeyType = InitKeyType.Attribute,
                IsAutoCloseConnection = true
            });

            client.CodeFirst.InitTables<Advertisement, AdvertisementStore>();
            DbContext = CreateDbContext(client);
        }

        public HbposSqlSugarContext DbContext { get; }

        public static Task<AdvertisementSqliteFixture> CreateAsync()
        {
            return Task.FromResult(new AdvertisementSqliteFixture());
        }

        public async Task SeedAdvertisementAsync(
            Advertisement advertisement,
            string? storeCode,
            bool storeIsDeleted = false)
        {
            // 直接构造 MainDb 数据，验证 SqlSugar 查询条件和排序逻辑。
            await client.Insertable(advertisement).ExecuteCommandAsync();
            if (string.IsNullOrWhiteSpace(storeCode))
            {
                return;
            }

            await client.Insertable(new AdvertisementStore
            {
                Id = $"{advertisement.Id}-STORE-{storeCode}",
                AdvertisementId = advertisement.Id,
                StoreCode = storeCode,
                CreatedAt = advertisement.CreatedAt,
                IsDeleted = storeIsDeleted
            }).ExecuteCommandAsync();
        }

        public ValueTask DisposeAsync()
        {
            client.Dispose();
            if (File.Exists(databasePath))
            {
                try
                {
                    File.Delete(databasePath);
                }
                catch (IOException)
                {
                    // SQLite 句柄可能短暂占用测试库文件，不影响断言结果。
                }
            }

            return ValueTask.CompletedTask;
        }

        private static HbposSqlSugarContext CreateDbContext(ISqlSugarClient mainDb)
        {
            var context = (HbposSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HbposSqlSugarContext));
            SetAutoProperty(context, nameof(HbposSqlSugarContext.MainDb), mainDb);
            SetAutoProperty(context, nameof(HbposSqlSugarContext.PosmDb), mainDb);
            return context;
        }

        private static void SetAutoProperty(HbposSqlSugarContext context, string propertyName, ISqlSugarClient value)
        {
            var backingField = typeof(HbposSqlSugarContext).GetField(
                $"<{propertyName}>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(backingField);
            backingField!.SetValue(context, value);
        }
    }
}
