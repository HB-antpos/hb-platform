namespace BlazorApp.Api.Models;

public sealed class BrowserExtensionOptions
{
    public const string SectionName = "BrowserExtension";

    // 扩展 1.5.0 上架商店后再通过配置把 LatestVersion/ReleaseNotes 提升，避免提示用户升级到尚不存在的版本。
    public string LatestVersion { get; set; } = "1.2.0";
    public string MinimumVersion { get; set; } = "1.1.0";
    public string ChromeStoreUrl { get; set; } = string.Empty;
    public string EdgeStoreUrl { get; set; } =
        "https://microsoftedge.microsoft.com/addons/detail/eeggjfaljfdkoanlaonfiodmljkmpfhn";
    public string SafariStoreUrl { get; set; } = string.Empty;
    public string ReleaseNotesZh { get; set; } = "新增 Jemark、GFA、TXK 和 Boom Up 供应商支持";
    public string ReleaseNotesEn { get; set; } = "Adds Jemark, GFA, TXK and Boom Up supplier support";
    public string ConfigVersion { get; set; } = "7";
    public bool UseBuiltInDatsProfile { get; set; } = true;
    public bool UseBuiltInSupplierProfiles { get; set; } = true;
    public List<BrowserExtensionSupplierProfileOptions> SupplierProfiles { get; set; } = new();

    /// <summary>
    /// 供应商分类采集总开关；关闭后不下发分类配置，采集接口返回 FEATURE_DISABLED（热生效，无需发版）。
    /// </summary>
    public bool CategoryCaptureEnabled { get; set; } = true;
}

/// <summary>
/// 供应商分类采集的声明式配置。选择器与路径模式只作为数据下发，扩展绝不执行远程代码。
/// 注：登录后才可见的站点选择器按平台惯例推测，可通过 appsettings 覆盖并递增 ConfigVersion 热修正。
/// </summary>
public sealed class BrowserExtensionSupplierCategoryOptions
{
    public bool Enabled { get; set; } = true;
    public bool PassiveEnabled { get; set; } = true;
    public bool CrawlEnabled { get; set; } = true;
    public List<string> CategoryPagePatterns { get; set; } = new();
    public List<string> CategoryExcludePatterns { get; set; } = new();
    public string? BreadcrumbSelector { get; set; }
    public int BreadcrumbSkip { get; set; } = 1;
    public string? TitleSelector { get; set; } = "h1";
    public string KeySource { get; set; } = "pathname";
    public List<string> KeyQueryParams { get; set; } = new();
    public string? NavRootUrl { get; set; }
    public string? NavSelector { get; set; }
    public string? SubcategoryLinkSelector { get; set; }
    public string? PaginationNextSelector { get; set; } = "a[rel=\"next\"]";
    public int MaxPages { get; set; } = 20;
    public int MaxDepth { get; set; } = 4;
    public int MaxCategories { get; set; } = 400;
    public int CrawlDelayMs { get; set; } = 1500;
    public List<string> PromotionalPatterns { get; set; } = new();

    // DATS 与 Yatsal 同一电商平台：分类页公开，路径形如 /office-stationery，面包屑 Home > 分类。
    internal static BrowserExtensionSupplierCategoryOptions CreateDatsPlatformDefault() =>
        new()
        {
            CategoryPagePatterns = new List<string> { "/*" },
            CategoryExcludePatterns = new List<string> { "/product/*", "/products/*", "/clearance*" },
            BreadcrumbSelector = ".breadcrumb a, .breadcrumbs a, nav[aria-label='breadcrumb'] a",
            NavSelector = "nav a[href^='/'], .navbar a[href^='/'], .menu a[href^='/']",
            SubcategoryLinkSelector = ".subcategories a, .category-list a, .widget-categorylist a",
            PaginationNextSelector = ".pagination a.next, a[rel='next']",
        };

    // WooCommerce 站点（Windragon、Boom Up）：/product-category/父/子/，分页 /page/N/。
    internal static BrowserExtensionSupplierCategoryOptions CreateWooCommerceDefault() =>
        new()
        {
            CategoryPagePatterns = new List<string> { "/product-category/*" },
            BreadcrumbSelector = ".woocommerce-breadcrumb a",
            TitleSelector = "h1.page-title, h1.woocommerce-products-header__title, h1",
            NavSelector = "ul.product-categories a, nav a[href*='/product-category/']",
            SubcategoryLinkSelector = "ul.products li.product-category a",
            PaginationNextSelector = ".woocommerce-pagination a.next, a[rel='next']",
        };

    // Brazco、MNB、PJ SAS 同一平台：分类页为 *.html。
    internal static BrowserExtensionSupplierCategoryOptions CreateListingHtmlDefault() =>
        new()
        {
            CategoryPagePatterns = new List<string> { "/*.html*" },
            CategoryExcludePatterns = new List<string> { "/home.html*", "/product/*" },
            BreadcrumbSelector = ".breadcrumb a, #breadcrumb a, .breadcrumbs a",
            NavSelector = "nav a[href$='.html'], .menu a[href$='.html']",
            SubcategoryLinkSelector = ".category-listing a, .subcategory-listing a",
            PaginationNextSelector = ".pager a.next, .pagination a.next, a[rel='next']",
        };
}

public sealed class BrowserExtensionSupplierProfileOptions
{
    public string SupplierCode { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public List<string> Origins { get; set; } = new();
    public List<string> ListPagePatterns { get; set; } = new();
    public string CardSelector { get; set; } = string.Empty;
    public string ItemNumberSource { get; set; } = "attribute";
    public string? ItemNumberSelector { get; set; }
    public string? ItemNumberAttribute { get; set; }
    public List<string> ItemNumberTransforms { get; set; } = new();
    public string MountSelector { get; set; } = string.Empty;
    public string MountPosition { get; set; } = "afterend";

    /// <summary>
    /// 分类采集配置；为空表示该供应商不采集分类。
    /// </summary>
    public BrowserExtensionSupplierCategoryOptions? Category { get; set; }

    public static BrowserExtensionSupplierProfileOptions CreateDatsDefault() =>
        new()
        {
            // DATS 是显示名称；HB 的供应商业务代码是 240。
            SupplierCode = "240",
            DisplayName = "DATS",
            Origins = new List<string> { "https://www.dats.com.au/*" },
            ListPagePatterns = new List<string> { "https://www.dats.com.au/*" },
            CardSelector = ".product[data-product-code]",
            ItemNumberSource = "attribute",
            ItemNumberAttribute = "data-product-code",
            ItemNumberTransforms = new List<string> { "trim", "uppercase" },
            MountSelector = ".widget-productlist-code",
            MountPosition = "afterend",
            Category = BrowserExtensionSupplierCategoryOptions.CreateDatsPlatformDefault(),
        };

    public static IReadOnlyList<BrowserExtensionSupplierProfileOptions> CreateSupplierDefaults() =>
        new List<BrowserExtensionSupplierProfileOptions>
        {
            new()
            {
                SupplierCode = "243",
                DisplayName = "Brazco",
                Origins = new List<string> { "https://www.brazcoint.com.au/*" },
                ListPagePatterns = new List<string>
                {
                    "https://www.brazcoint.com.au/*.html*",
                },
                CardSelector = ".product-listing-record",
                ItemNumberSource = "text",
                ItemNumberSelector = ".product-listing-code",
                ItemNumberTransforms = new List<string> { "after-colon", "trim", "uppercase" },
                MountSelector = ".product-listing-code",
                MountPosition = "afterend",
                Category = BrowserExtensionSupplierCategoryOptions.CreateListingHtmlDefault(),
            },
            new()
            {
                SupplierCode = "227",
                DisplayName = "Malmar",
                Origins = new List<string> { "https://www.malmar.com.au/*" },
                ListPagePatterns = new List<string>
                {
                    "https://www.malmar.com.au/Products.aspx*",
                    "https://www.malmar.com.au/products/*.htm*",
                },
                CardSelector = "li.item-thumbs",
                ItemNumberSource = "text",
                ItemNumberSelector = ".p-spec strong",
                ItemNumberTransforms = new List<string> { "trim", "uppercase" },
                MountSelector = ".p-spec",
                MountPosition = "afterend",
                Category = new BrowserExtensionSupplierCategoryOptions
                {
                    CategoryPagePatterns = new List<string> { "/Products.aspx*", "/products/*.htm*" },
                    KeyQueryParams = new List<string> { "cat", "category", "id" },
                    BreadcrumbSelector = ".breadcrumb a, .breadcrumbs a",
                    NavSelector = "nav a[href*='/products/'], nav a[href*='Products.aspx']",
                    PaginationNextSelector = ".pager a.next, .pagination a.next, a[rel='next']",
                },
            },
            new()
            {
                SupplierCode = "226",
                DisplayName = "Meteor Party",
                Origins = new List<string> { "https://www.meteorparty.com.au/*" },
                ListPagePatterns = new List<string>
                {
                    "https://www.meteorparty.com.au/balloons/*",
                    "https://www.meteorparty.com.au/Party*",
                    "https://www.meteorparty.com.au/Events*",
                    "https://www.meteorparty.com.au/Seasonal*",
                    "https://www.meteorparty.com.au/Tableware*",
                    "https://www.meteorparty.com.au/Candles*",
                },
                CardSelector = ".facets-item-cell-grid[data-sku]",
                ItemNumberSource = "attribute",
                ItemNumberAttribute = "data-sku",
                ItemNumberTransforms = new List<string> { "trim", "uppercase" },
                MountSelector = ".facets-item-cell-grid-title",
                MountPosition = "afterend",
                Category = new BrowserExtensionSupplierCategoryOptions
                {
                    // SuiteCommerce 单页应用：直接抓取分类 URL 只得到应用壳，先只做被动采集。
                    CrawlEnabled = false,
                    BreadcrumbSelector = ".global-views-breadcrumb a",
                    TitleSelector = ".facets-facet-browse-title, h1",
                    NavSelector = ".header-menu-level1 a, .header-menu-level2 a, .header-menu-level3 a",
                    PaginationNextSelector = ".global-views-pagination-next a",
                },
            },
            new()
            {
                SupplierCode = "201",
                DisplayName = "Yatsal",
                Origins = new List<string>
                {
                    "https://yatsal.com.au/*",
                    "https://www.yatsal.com.au/*",
                },
                ListPagePatterns = new List<string>
                {
                    "https://yatsal.com.au/*",
                    "https://www.yatsal.com.au/*",
                },
                CardSelector = ".product[data-product-code]",
                ItemNumberSource = "attribute",
                ItemNumberAttribute = "data-product-code",
                ItemNumberTransforms = new List<string> { "trim", "uppercase" },
                MountSelector = ".widget-productlist-code",
                MountPosition = "afterend",
                Category = BrowserExtensionSupplierCategoryOptions.CreateDatsPlatformDefault(),
            },
            new()
            {
                SupplierCode = "203",
                DisplayName = "Windragon",
                Origins = new List<string> { "https://windragon.com.au/*" },
                ListPagePatterns = new List<string>
                {
                    "https://windragon.com.au/product-category/*",
                },
                CardSelector = "li.product",
                ItemNumberSource = "text",
                ItemNumberSelector = ".sku",
                ItemNumberTransforms = new List<string> { "trim", "uppercase" },
                MountSelector = ".sku",
                MountPosition = "afterend",
                Category = BrowserExtensionSupplierCategoryOptions.CreateWooCommerceDefault(),
            },
            new()
            {
                SupplierCode = "225",
                DisplayName = "MNB",
                Origins = new List<string> { "https://www.mnb.com.au/*" },
                ListPagePatterns = new List<string>
                {
                    "https://www.mnb.com.au/*.html*",
                },
                CardSelector = ".product-listing-record",
                ItemNumberSource = "text",
                ItemNumberSelector = ".product-listing-code",
                ItemNumberTransforms = new List<string> { "after-colon", "trim", "uppercase" },
                MountSelector = ".product-listing-code",
                MountPosition = "afterend",
                Category = BrowserExtensionSupplierCategoryOptions.CreateListingHtmlDefault(),
            },
            new()
            {
                SupplierCode = "218",
                DisplayName = "PJ SAS",
                Origins = new List<string> { "https://www.pjsas.com.au/*" },
                ListPagePatterns = new List<string>
                {
                    "https://www.pjsas.com.au/*.html*",
                },
                CardSelector = ".product-listing-record",
                ItemNumberSource = "text",
                ItemNumberSelector = ".product-listing-code",
                ItemNumberTransforms = new List<string> { "after-colon", "trim", "uppercase" },
                MountSelector = ".product-listing-code",
                MountPosition = "afterend",
                Category = BrowserExtensionSupplierCategoryOptions.CreateListingHtmlDefault(),
            },
            new()
            {
                SupplierCode = "267",
                DisplayName = "Jemark",
                Origins = new List<string> { "https://www.jemark.com.au/*" },
                ListPagePatterns = new List<string>
                {
                    "https://www.jemark.com.au/category/*",
                },
                CardSelector = "ul.products li.product",
                ItemNumberSource = "text",
                ItemNumberSelector = ".model",
                ItemNumberTransforms = new List<string> { "trim", "uppercase" },
                MountSelector = ".model",
                MountPosition = "afterend",
                Category = new BrowserExtensionSupplierCategoryOptions
                {
                    CategoryPagePatterns = new List<string> { "/category/*" },
                    BreadcrumbSelector = ".breadcrumb a, ul.breadcrumb li a",
                    NavSelector = "nav a[href*='/category/'], .categories a[href*='/category/']",
                    SubcategoryLinkSelector = ".subcategories a[href*='/category/']",
                    PaginationNextSelector = ".pagination a.next, a[rel='next']",
                },
            },
            new()
            {
                SupplierCode = "236",
                DisplayName = "GFA",
                Origins = new List<string> { "https://gfa.opmetrix.store/*" },
                ListPagePatterns = new List<string>
                {
                    "https://gfa.opmetrix.store/products/view*",
                },
                CardSelector = ".list-row[data-product]",
                ItemNumberSource = "attribute",
                ItemNumberAttribute = "data-product",
                ItemNumberTransforms = new List<string>
                {
                    "trim",
                    "uppercase",
                    "underscore-to-slash",
                },
                MountSelector = ".content > a[href*='/product/view?id=']",
                MountPosition = "afterend",
                Category = new BrowserExtensionSupplierCategoryOptions
                {
                    CategoryPagePatterns = new List<string> { "/products/view*" },
                    KeyQueryParams = new List<string> { "category", "cat", "group", "id" },
                    BreadcrumbSelector = ".breadcrumb a",
                    TitleSelector = "h1, .page-title",
                    NavRootUrl = "/products/view",
                    NavSelector = "a[href*='/products/view?']",
                    PaginationNextSelector = ".pagination a.next, a[rel='next']",
                },
            },
            new()
            {
                SupplierCode = "SP2502280001",
                DisplayName = "TXK",
                Origins = new List<string> { "http://txkorders.inzantsales.com/*" },
                ListPagePatterns = new List<string>
                {
                    "http://txkorders.inzantsales.com/shop*",
                },
                CardSelector = ".single-product.grid-view",
                ItemNumberSource = "text",
                ItemNumberSelector = ".sku",
                ItemNumberTransforms = new List<string> { "after-sku", "trim", "uppercase" },
                MountSelector = ".price-box",
                MountPosition = "afterend",
                Category = new BrowserExtensionSupplierCategoryOptions
                {
                    CategoryPagePatterns = new List<string> { "/shop*" },
                    KeyQueryParams = new List<string> { "category", "cat", "c" },
                    BreadcrumbSelector = ".breadcrumb a",
                    NavRootUrl = "/shop",
                    NavSelector = ".category-list a, .sidebar a[href*='/shop']",
                    PaginationNextSelector = ".pagination a.next, a[rel='next']",
                },
            },
            new()
            {
                SupplierCode = "SP0101",
                DisplayName = "Boom Up",
                Origins = new List<string> { "https://boomup.com.au/*" },
                ListPagePatterns = new List<string>
                {
                    "https://boomup.com.au/shop*",
                    "https://boomup.com.au/product-category/*",
                },
                CardSelector = "main ul.products li.product",
                ItemNumberSource = "text",
                ItemNumberSelector = ".custom_sku",
                ItemNumberTransforms = new List<string> { "trim", "uppercase" },
                MountSelector = "h2.woocommerce-loop-product__title",
                MountPosition = "afterend",
                Category = BrowserExtensionSupplierCategoryOptions.CreateWooCommerceDefault(),
            },
        };
}
