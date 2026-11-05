namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 节日销售报告（目前只有万圣节）：KFC / Brazco 节日商品在一个节日季内的销售、进货、卖断与滞销。
    /// 全部分店合计与单店共用同一结构，单店时 <see cref="Stores"/> 为空、<see cref="Benchmark"/> 有值。
    /// </summary>
    public class HolidaySalesReportDto
    {
        /// <summary>节日代号，目前固定 Halloween。</summary>
        public string Holiday { get; set; } = "Halloween";

        public int Year { get; set; }

        /// <summary>节日当天（yyyy-MM-dd），卖断天数与节前 / 节后都以它为界。</summary>
        public string HolidayDate { get; set; } = string.Empty;

        /// <summary>销售统计的起止日（含），止日为已出数的最后一天（今天还在滚动，不计入）。</summary>
        public string SalesFrom { get; set; } = string.Empty;
        public string SalesThrough { get; set; } = string.Empty;

        /// <summary>参与统计的节日商品款数（含只有进货没有销售的）。</summary>
        public int ProductCount { get; set; }

        /// <summary>单店视图时的分店；全部分店合计为 null。</summary>
        public string? BranchCode { get; set; }
        public string? BranchName { get; set; }

        public HolidaySalesTotalsDto Totals { get; set; } = new();
        public List<HolidaySalesSupplierDto> Suppliers { get; set; } = new();
        public List<HolidaySalesDailyDto> Daily { get; set; } = new();
        public List<HolidaySalesCategoryDto> Categories { get; set; } = new();

        /// <summary>卖得好：按销售额。</summary>
        public List<HolidaySalesProductDto> BestSellers { get; set; } = new();

        /// <summary>库存不够：节前卖完 ≥95% 进货，按估算少卖金额。合计视图要求至少 2 家店卖断。</summary>
        public List<HolidaySalesProductDto> EarlySoldOut { get; set; } = new();

        /// <summary>卖得不好：进货 ≥12 件且售罄率 &lt;35%，按剩余进价金额。</summary>
        public List<HolidaySalesProductDto> SlowSellers { get; set; } = new();

        /// <summary>库存很多：今年进货减季内销量，按剩余件数。</summary>
        public List<HolidaySalesProductDto> Overstock { get; set; } = new();

        /// <summary>分店排行，仅全部分店合计视图返回。</summary>
        public List<HolidaySalesStoreDto> Stores { get; set; } = new();

        /// <summary>有进货单但本季没有任何收银销售的分店（合计视图提示用）。</summary>
        public List<HolidaySalesStoreDto> StoresWithoutSales { get; set; } = new();

        /// <summary>单店视图时与全部分店对比的基准。</summary>
        public HolidaySalesBenchmarkDto? Benchmark { get; set; }
    }

    public class HolidaySalesTotalsDto
    {
        /// <summary>销售额（含 GST，退款按净额）。</summary>
        public decimal Amount { get; set; }
        public decimal Quantity { get; set; }

        /// <summary>有销售的款数。</summary>
        public int SkuCount { get; set; }

        /// <summary>今年进货件数与进价金额（按分店进货单）。</summary>
        public decimal PurchaseQuantity { get; set; }
        public decimal PurchaseCost { get; set; }

        /// <summary>售罄率 = 售出件数 ÷ 今年进货件数；没有进货为 null。</summary>
        public decimal? SellThrough { get; set; }

        /// <summary>节前卖完 ≥95% 进货的「分店 × 商品」数（店次）。</summary>
        public int EarlySoldOutCount { get; set; }

        /// <summary>其中不重复的商品款数：一款在 5 家店卖断，店次是 5、款数是 1。</summary>
        public int EarlySoldOutProductCount { get; set; }

        /// <summary>卖断少卖（估算）：卖完前 14 天日均销售额 × 提前天数。</summary>
        public decimal EstimatedLostAmount { get; set; }

        /// <summary>剩余库存（估算）：今年进货减季内销量，不含去年剩货。</summary>
        public decimal LeftoverQuantity { get; set; }
        public decimal LeftoverCost { get; set; }

        /// <summary>节日当天与之前最后 7 天（含当天）、节日之后的销售额。</summary>
        public decimal HolidayDayAmount { get; set; }
        public decimal LastWeekAmount { get; set; }
        public decimal AfterHolidayAmount { get; set; }
    }

    public class HolidaySalesSupplierDto
    {
        public string SupplierCode { get; set; } = string.Empty;
        public string SupplierName { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public decimal Quantity { get; set; }
        public decimal PurchaseQuantity { get; set; }
    }

    public class HolidaySalesDailyDto
    {
        /// <summary>yyyy-MM-dd，销售起止日之间每天一行，没有销售的日子是 0。</summary>
        public string Date { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }

    public class HolidaySalesCategoryDto
    {
        /// <summary>大类代号（稳定英文键，前端翻译显示名）。</summary>
        public string Key { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public decimal Quantity { get; set; }
        public int SkuCount { get; set; }
        public decimal PurchaseQuantity { get; set; }
        public decimal? SellThrough { get; set; }
        public decimal EstimatedLostAmount { get; set; }
        public decimal LeftoverQuantity { get; set; }
        public decimal LeftoverCost { get; set; }
    }

    public class HolidaySalesProductDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public string ItemNumber { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string? ImageUrl { get; set; }
        public string SupplierCode { get; set; } = string.Empty;
        public string CategoryKey { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public decimal Quantity { get; set; }
        public decimal PurchaseQuantity { get; set; }
        public decimal? SellThrough { get; set; }

        /// <summary>有进货的分店数 / 其中节前卖断的分店数（单店视图分别是 1 或 0）。</summary>
        public int StoreCount { get; set; }
        public int SoldOutStoreCount { get; set; }

        /// <summary>节前提前几天卖完（多家店取中位数）；没有卖断为 null。</summary>
        public int? DaysEarly { get; set; }

        /// <summary>单店视图的卖完日期（yyyy-MM-dd）。</summary>
        public string? SoldOutDate { get; set; }
        public decimal EstimatedLostAmount { get; set; }
        public decimal LeftoverQuantity { get; set; }
        public decimal LeftoverCost { get; set; }
    }

    public class HolidaySalesStoreDto
    {
        public string BranchCode { get; set; } = string.Empty;
        public string BranchName { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public decimal Quantity { get; set; }
        public decimal PurchaseQuantity { get; set; }
        public decimal? SellThrough { get; set; }
        public int EarlySoldOutCount { get; set; }
        public decimal EstimatedLostAmount { get; set; }
        public decimal LeftoverCost { get; set; }
        public decimal AfterHolidayAmount { get; set; }
    }

    public class HolidaySalesBenchmarkDto
    {
        /// <summary>有销售的分店数与本店按销售额的名次（1 起）。</summary>
        public int StoreCount { get; set; }
        public int Rank { get; set; }

        public decimal AverageAmount { get; set; }
        public decimal? SellThrough { get; set; }
        public decimal AverageEarlySoldOutCount { get; set; }
        public decimal AverageLostAmount { get; set; }
        public decimal AverageLeftoverCost { get; set; }
        public decimal AverageAfterHolidayAmount { get; set; }

        /// <summary>全部分店各大类的售罄率，key 为大类代号。</summary>
        public Dictionary<string, decimal?> CategorySellThrough { get; set; } = new();
    }

    /// <summary>
    /// 单个商品的分店明细（榜单里点商品钻取）：每家有进货或有销售的分店一行，按销售额降序。
    /// 口径与榜单一致：售罄率只在该店有进货时才有值，卖断按累计销量达进货 95% 判定。
    /// </summary>
    public class HolidaySalesProductStoresDto
    {
        public int Year { get; set; }
        public string HolidayDate { get; set; } = string.Empty;
        public string ProductCode { get; set; } = string.Empty;
        public string ItemNumber { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string? ImageUrl { get; set; }
        public string SupplierCode { get; set; } = string.Empty;
        public string CategoryKey { get; set; } = string.Empty;

        /// <summary>全部分店合计（BranchCode 为空）。</summary>
        public HolidaySalesProductStoreDto Total { get; set; } = new();

        /// <summary>有进货的分店数 / 其中节前卖断的分店数。</summary>
        public int StoreCount { get; set; }
        public int SoldOutStoreCount { get; set; }
        public List<HolidaySalesProductStoreDto> Stores { get; set; } = new();
    }

    public class HolidaySalesProductStoreDto
    {
        public string BranchCode { get; set; } = string.Empty;
        public string BranchName { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public decimal Quantity { get; set; }
        public decimal PurchaseQuantity { get; set; }
        public decimal? SellThrough { get; set; }

        /// <summary>卖断日（yyyy-MM-dd，不晚于节日当天）；没有卖断为 null。0 天提前表示节日当天才卖完。</summary>
        public string? SoldOutDate { get; set; }
        public int? DaysEarly { get; set; }
        public decimal EstimatedLostAmount { get; set; }
        public decimal LeftoverQuantity { get; set; }
        public decimal LeftoverCost { get; set; }

        /// <summary>这家店这款第一次有销售的日期；没有销售为 null。</summary>
        public string? FirstSaleDate { get; set; }
    }

    /// <summary>
    /// 大类明细（大类表里点大类钻取）：该大类的合计、各分店表现与完整商品清单。
    /// 单店视图（BranchCode 有值）时只含该店，Stores 为空。商品清单不截断（一个大类最多几百款），页面里再排序 / 筛选 / 分批显示。
    /// </summary>
    public class HolidaySalesCategoryDetailDto
    {
        public int Year { get; set; }
        public string HolidayDate { get; set; } = string.Empty;
        public string? BranchCode { get; set; }
        public string? BranchName { get; set; }
        public HolidaySalesCategoryDto Category { get; set; } = new();
        public List<HolidaySalesStoreDto> Stores { get; set; } = new();
        public List<HolidaySalesProductDto> Products { get; set; } = new();
    }

    /// <summary>大类归并对照：供应商自己的分类落到哪个大类，右侧是款数与季内销售额。</summary>
    public class HolidaySalesCategoryMapDto
    {
        public int Year { get; set; }
        public List<HolidaySalesCategoryMapGroupDto> Groups { get; set; } = new();
    }

    public class HolidaySalesCategoryMapGroupDto
    {
        public string Key { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public List<HolidaySalesCategoryMapEntryDto> Entries { get; set; } = new();
    }

    public class HolidaySalesCategoryMapEntryDto
    {
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>供应商网站上的分类名；商品没有归类时为空，页面显示「未归类，按品名」。</summary>
        public string? SupplierCategory { get; set; }
        public int SkuCount { get; set; }
        public decimal Amount { get; set; }
    }
}
