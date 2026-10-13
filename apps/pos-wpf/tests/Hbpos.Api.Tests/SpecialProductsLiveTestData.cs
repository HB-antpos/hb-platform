using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Shared.Models;
using Hbpos.Api.Data;
using Hbpos.Api.Services;
using SqlSugar;

namespace Hbpos.Api.Tests;

/// <summary>
/// 特殊商品实时查询测试共用的造数工具：SQLite 替身与真实 SQL Server 两套测试写入同一份数据，
/// 保证“实时查询 vs 目录工件”的对照在两种数据库上用的是同一个场景。
/// </summary>
internal static class SpecialProductsLiveTestData
{
    private static readonly DateTime BaseTime = new(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>以固定基准时间 + 分钟偏移造更新时间，决胜用的时间互不相同，避免平局造成不确定。</summary>
    public static DateTime At(int minutes) => BaseTime.AddMinutes(minutes);

    public static Task AddStoreAsync(ISqlSugarClient db, string storeCode, bool isActive = true, bool isDeleted = false)
    {
        return db.Insertable(new Store
        {
            StoreGUID = $"STORE-{storeCode}",
            StoreCode = storeCode,
            StoreName = $"Store {storeCode}",
            IsActive = isActive,
            IsDeleted = isDeleted,
            CreatedAt = At(0),
            UpdatedAt = At(0)
        }).ExecuteCommandAsync();
    }

    public static Task AddProductAsync(
        ISqlSugarClient db,
        string productCode,
        string? barcode,
        string? itemNumber,
        decimal? retailPrice,
        int updatedMinutes,
        bool isActive = true,
        bool isDeleted = false,
        bool productFlagSpecial = false,
        string? image = null)
    {
        return db.Insertable(new Product
        {
            UUID = $"PU-{productCode}",
            ProductCode = productCode,
            ProductName = $"Name {productCode}",
            Barcode = barcode,
            ItemNumber = itemNumber,
            RetailPrice = retailPrice,
            ProductImage = image,
            IsActive = isActive,
            IsDeleted = isDeleted,
            IsSpecialProduct = productFlagSpecial,
            CreatedAt = At(0),
            UpdatedAt = At(updatedMinutes)
        }).ExecuteCommandAsync();
    }

    public static Task AddStorePriceAsync(
        ISqlSugarClient db,
        string storeCode,
        string productCode,
        decimal? price,
        int updatedMinutes,
        bool special,
        bool isActive = true,
        bool isDeleted = false,
        decimal? discountRate = null,
        string? uuid = null)
    {
        return db.Insertable(new StoreRetailPrice
        {
            UUID = uuid ?? $"SRP-{storeCode}-{productCode}-{updatedMinutes}-{(special ? "S" : "N")}",
            StoreCode = storeCode,
            ProductCode = productCode,
            StoreProductCode = $"{storeCode}-{productCode}-{updatedMinutes}",
            StoreRetailPriceValue = price,
            DiscountRate = discountRate,
            IsSpecialProduct = special,
            IsActive = isActive,
            IsDeleted = isDeleted,
            CreatedAt = At(0),
            UpdatedAt = At(updatedMinutes)
        }).ExecuteCommandAsync();
    }

    public static Task AddMultiCodeAsync(
        ISqlSugarClient db,
        string storeCode,
        string productCode,
        string multiCodeProductCode,
        string? multiBarcode,
        decimal? price,
        int updatedMinutes,
        bool isActive = true,
        bool isDeleted = false,
        decimal? discountRate = null)
    {
        return db.Insertable(new StoreMultiCodeProduct
        {
            UUID = $"MULTI-{storeCode}-{productCode}-{multiCodeProductCode}-{multiBarcode}-{updatedMinutes}",
            StoreCode = storeCode,
            ProductCode = productCode,
            MultiCodeProductCode = multiCodeProductCode,
            MultiBarcode = multiBarcode,
            MultiCodeRetailPrice = price,
            DiscountRate = discountRate,
            IsActive = isActive,
            IsDeleted = isDeleted,
            CreatedAt = At(0),
            UpdatedAt = At(updatedMinutes)
        }).ExecuteCommandAsync();
    }

    public static Task AddClearanceAsync(
        ISqlSugarClient db,
        string storeCode,
        string productCode,
        string? clearanceBarcode,
        decimal? price,
        int updatedMinutes,
        bool isDeleted = false)
    {
        return db.Insertable(new StoreClearancePrice
        {
            UUID = $"CLR-{storeCode}-{productCode}-{clearanceBarcode}-{updatedMinutes}",
            StoreCode = storeCode,
            ProductCode = productCode,
            ClearanceBarcode = clearanceBarcode,
            ClearancePrice = price,
            IsDeleted = isDeleted,
            CreatedAt = At(0),
            UpdatedAt = At(updatedMinutes)
        }).ExecuteCommandAsync();
    }

    public static Task AddSetCodeAsync(
        ISqlSugarClient db,
        string productCode,
        string setProductCode,
        string? setBarcode,
        decimal? price,
        int updatedMinutes,
        bool isActive = true,
        bool isDeleted = false)
    {
        return db.Insertable(new ProductSetCode
        {
            SetCodeId = $"SET-{productCode}-{setProductCode}-{setBarcode}-{updatedMinutes}",
            ProductCode = productCode,
            SetProductCode = setProductCode,
            SetItemNumber = $"{setProductCode}-ITEM",
            SetBarcode = setBarcode,
            SetRetailPrice = price,
            SetQuantity = 1,
            SetType = 1,
            IsActive = isActive,
            IsDeleted = isDeleted,
            CreatedAt = At(0),
            UpdatedAt = At(updatedMinutes)
        }).ExecuteCommandAsync();
    }

    /// <summary>
    /// 覆盖特殊商品进入/退出列表的各种路径：门店价特殊标记（含多行取最新）、价格为空、停用/已删除商品、
    /// 同码被清货码/多码/套装码/其他商品压过（含大小写、首尾空白）、同码但竞争者更旧/已删除/已停用/他店、
    /// 两个特殊商品同码、条码即货号、无任何查询码。S01、S02 两个门店各有自己的特殊商品。
    /// </summary>
    public static async Task SeedRichCatalogAsync(ISqlSugarClient db)
    {
        // 一个事务写完全部数据：SQLite 文件库逐条提交很慢，且避免读到半截造数。
        await db.Ado.BeginTranAsync();
        try
        {
            await SeedRichCatalogCoreAsync(db);
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
    }

    private static async Task SeedRichCatalogCoreAsync(ISqlSugarClient db)
    {
        await AddStoreAsync(db, "S01");
        await AddStoreAsync(db, "S02");
        await AddStoreAsync(db, "S03", isActive: false);
        await AddStoreAsync(db, "S04", isDeleted: true);

        // 竞争者商品：只作为同码对手出现，自己不是特殊商品。
        await AddProductAsync(db, "OT01", "OT01-BAR", "OT01-ITEM", 1m, 1);
        await AddProductAsync(db, "OT02", "OT02-BAR", "OT02-ITEM", 1m, 1);
        await AddProductAsync(db, "OT03", "OT03-BAR", "OT03-ITEM", 1m, 1);
        await AddProductAsync(db, "OT04", "6900000000011", null, 1m, 1);
        await AddProductAsync(db, "OT05", "6900000000017", null, 1m, 1);
        await AddProductAsync(db, "OT06", "6900000000018", null, 1m, 1, isDeleted: true);
        await AddStorePriceAsync(db, "S01", "OT04", 3m, 30, special: false);
        await AddStorePriceAsync(db, "S01", "OT05", 3m, 30, special: false);
        await AddProductAsync(db, "OT08", "6900000000002", null, 1m, 1);
        await AddStorePriceAsync(db, "S01", "OT08", 3m, 5, special: false);

        // SP01：门店价 8.50 + 折扣率 90（归一为 0.9）。S02 里同一商品有另一个门店价。
        await AddProductAsync(db, "SP01", "6900000000001", "SP01-ITEM", 10m, 1, image: "sp01.png");
        await AddStorePriceAsync(db, "S01", "SP01", 8.5m, 10, special: true, discountRate: 90m);
        await AddStorePriceAsync(db, "S02", "SP01", 7.7m, 11, special: true, discountRate: 0.85m);
        // SP02：特殊标记但门店价为空 -> 价格来源 ProductBase，仍是特殊商品；条码被 OT08 的分店零售价行压过（来源等级高于更新时间），列表里查询码是货号。
        await AddProductAsync(db, "SP02", "6900000000002", "SP02-ITEM", 20m, 1);
        await AddStorePriceAsync(db, "S01", "SP02", null, 10, special: true);
        // SP03：停用商品，门店仍可售，应包含。
        await AddProductAsync(db, "SP03", "6900000000003", "SP03-ITEM", 30m, 1, isActive: false);
        await AddStorePriceAsync(db, "S01", "SP03", 25m, 10, special: true);
        // SP04：已删除商品，应排除。
        await AddProductAsync(db, "SP04", "6900000000004", "SP04-ITEM", 40m, 1, isDeleted: true);
        await AddStorePriceAsync(db, "S01", "SP04", 35m, 10, special: true);
        // SP05：特殊标记行已停用，应排除。
        await AddProductAsync(db, "SP05", "6900000000005", "SP05-ITEM", 50m, 1);
        await AddStorePriceAsync(db, "S01", "SP05", 45m, 10, special: true, isActive: false);
        // SP06：旧行是特殊、新行不是 -> 以最新行为准，不是特殊商品。
        await AddProductAsync(db, "SP06", "6900000000006", "SP06-ITEM", 60m, 1);
        await AddStorePriceAsync(db, "S01", "SP06", 4m, 5, special: true);
        await AddStorePriceAsync(db, "S01", "SP06", 4.5m, 20, special: false);
        // SP07：旧行不是特殊、新行是 -> 是特殊商品，价格取新行。
        await AddProductAsync(db, "SP07", "6900000000007", "SP07-ITEM", 70m, 1);
        await AddStorePriceAsync(db, "S01", "SP07", 5m, 5, special: false);
        await AddStorePriceAsync(db, "S01", "SP07", 6m, 20, special: true);
        // SP08：条码被清货码压过，货号行仍胜出 -> 列表里该商品的查询码是货号。
        await AddProductAsync(db, "SP08", "6900000000008", "SP08-ITEM", 80m, 1);
        await AddStorePriceAsync(db, "S01", "SP08", 6m, 10, special: true);
        await AddClearanceAsync(db, "S01", "OT01", "6900000000008", 1m, 12);
        // SP09：条码被清货码压过，货号被多码压过 -> 整个商品不在列表里。
        await AddProductAsync(db, "SP09", "6900000000009", "SP09-ITEM", 90m, 1);
        await AddStorePriceAsync(db, "S01", "SP09", 7m, 10, special: true);
        await AddClearanceAsync(db, "S01", "OT01", "6900000000009", 1m, 12);
        await AddMultiCodeAsync(db, "S01", "OT02", "OT02-MULTI", "SP09-ITEM", 2m, 13);
        // SP10：唯一的条码被他品套装码压过 -> 不在列表里。
        await AddProductAsync(db, "SP10", "6900000000010", null, 100m, 1);
        await AddStorePriceAsync(db, "S01", "SP10", 8m, 10, special: true);
        await AddSetCodeAsync(db, "OT03", "SETP-10", "6900000000010", 2m, 14);
        // SP11：条码与他品条码相同，对方门店价更新更晚 -> 条码行输给 OT04，列表里该商品的查询码变成货号。
        await AddProductAsync(db, "SP11", "6900000000011", "SP11-ITEM", 110m, 1);
        await AddStorePriceAsync(db, "S01", "SP11", 9m, 10, special: true);
        // SP12：条码即货号，只产生一个查询码。
        await AddProductAsync(db, "SP12", "6900000000012", "6900000000012", 120m, 1);
        await AddStorePriceAsync(db, "S01", "SP12", 10m, 10, special: true);
        // SP13：条码小写，被大写的清货码压过（查询码不区分大小写）-> 不在列表里。
        await AddProductAsync(db, "SP13", "abc-sp13", null, 130m, 1);
        await AddStorePriceAsync(db, "S01", "SP13", 11m, 10, special: true);
        await AddClearanceAsync(db, "S01", "OT01", "ABC-SP13", 1m, 12);
        // SP14：自己带一个多码（非特殊行），条码行仍是特殊商品。
        await AddProductAsync(db, "SP14", "6900000000014", "SP14-ITEM", 140m, 1);
        await AddStorePriceAsync(db, "S01", "SP14", 12m, 10, special: true);
        await AddMultiCodeAsync(db, "S01", "SP14", "SP14-MULTI", "6900000000114", 3m, 11);
        // SP15：没有条码也没有货号，目录里没有任何查询码 -> 不在列表里。
        await AddProductAsync(db, "SP15", string.Empty, null, 150m, 1);
        await AddStorePriceAsync(db, "S01", "SP15", 13m, 10, special: true);
        // SP16：条码首尾带空白，清货码是去空白后的写法 -> 被压过。
        await AddProductAsync(db, "SP16", " 6900000000016 ", null, 160m, 1);
        await AddStorePriceAsync(db, "S01", "SP16", 14m, 10, special: true);
        await AddClearanceAsync(db, "S01", "OT01", "6900000000016", 1m, 12);
        // SP17：与他品同码且自己更新更晚 -> 胜出。
        await AddProductAsync(db, "SP17", "6900000000017", "SP17-ITEM", 170m, 1);
        await AddStorePriceAsync(db, "S01", "SP17", 15m, 40, special: true);
        // SP18：同码的对手商品已删除 -> 不参与，SP18 胜出。
        await AddProductAsync(db, "SP18", "6900000000018", null, 180m, 1);
        await AddStorePriceAsync(db, "S01", "SP18", 16m, 10, special: true);
        // SP19：同码的多码已停用、清货码已删除、套装码已停用 -> 都不参与。
        await AddProductAsync(db, "SP19", "6900000000019", null, 190m, 1);
        await AddStorePriceAsync(db, "S01", "SP19", 17m, 10, special: true);
        await AddMultiCodeAsync(db, "S01", "OT02", "OT02-MULTI-OFF", "6900000000019", 2m, 13, isActive: false);
        await AddClearanceAsync(db, "S01", "OT01", "6900000000019", 1m, 12, isDeleted: true);
        await AddSetCodeAsync(db, "OT03", "SETP-19", "6900000000019", 2m, 14, isActive: false);
        // SP20：只在 S02 是特殊商品，S01 是普通商品。
        await AddProductAsync(db, "SP20", "6900000000020", "SP20-ITEM", 200m, 1);
        await AddStorePriceAsync(db, "S01", "SP20", 9m, 10, special: false);
        await AddStorePriceAsync(db, "S02", "SP20", 7m, 10, special: true);
        // SP21：S02 的清货码同码，不影响 S01 的特殊商品。
        await AddProductAsync(db, "SP21", "6900000000021", null, 210m, 1);
        await AddStorePriceAsync(db, "S01", "SP21", 18m, 10, special: true);
        await AddClearanceAsync(db, "S02", "OT01", "6900000000021", 1m, 12);
        // SP22A/SP22B：两个特殊商品条码相同，更新更晚的 SP22B 胜出，SP22A 不在列表里。
        await AddProductAsync(db, "SP22A", "6900000000022", null, 220m, 1);
        await AddStorePriceAsync(db, "S01", "SP22A", 1m, 10, special: true);
        await AddProductAsync(db, "SP22B", "6900000000022", null, 221m, 1);
        await AddStorePriceAsync(db, "S01", "SP22B", 2m, 20, special: true);
        // SP23：货号被“套装码 + 本店多码”压过（价格来自多码），条码行仍在。
        await AddProductAsync(db, "SP23", "6900000000023", "SET-SP23", 230m, 1);
        await AddStorePriceAsync(db, "S01", "SP23", 19m, 10, special: true);
        await AddSetCodeAsync(db, "OT03", "SETP-23", "SET-SP23", 9m, 14);
        await AddMultiCodeAsync(db, "S01", "OT03", "SETP-23", "SETP-23-BAR", 8m, 50);
        // SP24：只在 S02。
        await AddProductAsync(db, "SP24", "6900000000024", "SP24-ITEM", 240m, 1);
        await AddStorePriceAsync(db, "S02", "SP24", 20m, 10, special: true);
        // SP25：条码带单引号（拼进 IN 列表时必须被正确转义），条码被清货码压过，列表里的查询码是货号。
        await AddProductAsync(db, "SP25", "Q'25", "Q25-ITEM", 250m, 1);
        await AddStorePriceAsync(db, "S01", "SP25", 21m, 10, special: true);
        await AddClearanceAsync(db, "S01", "OT01", "Q'25", 1m, 12);

        // 普通商品：有门店价但不是特殊；Product.IsSpecialProduct 为真也不算（目录只看门店价上的标记）。
        for (var i = 1; i <= 5; i++)
        {
            await AddProductAsync(db, $"N0{i}", $"NORMAL-BAR-{i}", $"NORMAL-ITEM-{i}", 5m + i, 1);
            await AddStorePriceAsync(db, "S01", $"N0{i}", 4m + i, 10, special: false);
        }

        await AddProductAsync(db, "N06", "NORMAL-BAR-6", "NORMAL-ITEM-6", 6m, 1, productFlagSpecial: true);
        await AddStorePriceAsync(db, "S01", "N06", 6m, 10, special: false);
    }
}

/// <summary>
/// 特殊商品实时查询测试用的 SQLite 库（文件库，允许多个客户端并发读取）。
/// 与 CatalogServiceTests 里的私有替身写法一致：绕过构造函数直接设置 MainDb/PosmDb。
/// </summary>
internal sealed class SpecialProductsSqliteDatabase : IAsyncDisposable
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-special-live-{Guid.NewGuid():N}.db");
    private readonly List<SqlSugarClient> extraClients = [];

    public SpecialProductsSqliteDatabase()
    {
        Client = CreateClient();
        Client.CodeFirst.InitTables<Store, Product, StoreRetailPrice>();
        Client.CodeFirst.InitTables<StoreMultiCodeProduct, StoreClearancePrice, ProductSetCode>();
        DbContext = CreateDbContext(Client);
    }

    public SqlSugarClient Client { get; }

    public HbposSqlSugarContext DbContext { get; }

    /// <summary>在同一个库文件上另开一个客户端/上下文，模拟多个请求各自持有自己的 scope。</summary>
    public HbposSqlSugarContext CreateExtraDbContext()
    {
        var client = CreateClient();
        extraClients.Add(client);
        return CreateDbContext(client);
    }

    public CatalogService CreateService(
        ICatalogIndexCache? cache = null,
        HbposSqlSugarContext? context = null)
    {
        return new CatalogService(
            context ?? DbContext,
            new PriceIndexBuilder(),
            cache ?? new CatalogIndexCache(),
            new CatalogBaseDataCache());
    }

    public ValueTask DisposeAsync()
    {
        foreach (var extra in extraClients)
        {
            extra.Dispose();
        }

        Client.Dispose();
        if (File.Exists(databasePath))
        {
            try
            {
                File.Delete(databasePath);
            }
            catch (IOException)
            {
            }
        }

        return ValueTask.CompletedTask;
    }

    private SqlSugarClient CreateClient() => new(new ConnectionConfig
    {
        ConnectionString = $"Data Source={databasePath}",
        DbType = DbType.Sqlite,
        InitKeyType = InitKeyType.Attribute,
        IsAutoCloseConnection = true
    });

    private static HbposSqlSugarContext CreateDbContext(ISqlSugarClient mainDb)
    {
        var context = (HbposSqlSugarContext)RuntimeHelpers
            .GetUninitializedObject(typeof(HbposSqlSugarContext));
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
