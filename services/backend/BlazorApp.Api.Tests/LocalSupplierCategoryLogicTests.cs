using System.ComponentModel.DataAnnotations;
using System.Reflection;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Models;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.LocalSupplierCategories;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class LocalSupplierCategoryLogicTests
{
    [Theory]
    [InlineData("/Office-Stationery/", "/office-stationery")]
    [InlineData("https://www.dats.com.au/office-stationery?page=2&sort=name", "/office-stationery")]
    [InlineData("/product-category/bags/page/3/", "/product-category/bags")]
    [InlineData("//category//841-kitchenware", "/category/841-kitchenware")]
    [InlineData("/Party%20Supplies#top", "/party supplies")]
    public void KeyNormalizer_统一大小写尾斜杠分页与查询串(string raw, string expected)
    {
        Assert.Equal(expected, LocalSupplierCategoryKeyNormalizer.Normalize(raw));
    }

    [Fact]
    public void KeyNormalizer_只保留白名单查询参数并按名排序()
    {
        var key = LocalSupplierCategoryKeyNormalizer.Normalize(
            "/products/view?page=2&Cat=12&group=A&sort=x",
            new[] { "cat", "group" }
        );

        Assert.Equal("/products/view?cat=12&group=a", key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("https://www.dats.com.au/")]
    public void KeyNormalizer_空键与首页被拒绝(string raw)
    {
        Assert.Throws<ArgumentException>(() => LocalSupplierCategoryKeyNormalizer.Normalize(raw));
    }

    [Fact]
    public void KeyNormalizer_超长键被拒绝()
    {
        Assert.Throws<ArgumentException>(() =>
            LocalSupplierCategoryKeyNormalizer.Normalize("/" + new string('a', 450))
        );
    }

    [Theory]
    [InlineData("Clearance", "/clearance", true)]
    [InlineData("Shop By Colour", "/shop-by-colour", true)]
    [InlineData("New", "/new_releases", true)]
    [InlineData("What's New", "/whats-new", true)]
    [InlineData("Specials", "/category/12-specials", true)]
    [InlineData("Kitchenware", "/category/841-kitchenware", false)]
    [InlineData("Wholesale Packs", "/wholesale-packs", false)]
    [InlineData("Office Stationery", "/office-stationery", false)]
    public void PromotionRule_默认模式识别促销分类且不误伤正常分类(string name, string key, bool expected)
    {
        var patterns = LocalSupplierCategoryPromotionRule.Combine(null);

        Assert.Equal(expected, LocalSupplierCategoryPromotionRule.IsPromotional(name, key, patterns));
    }

    [Fact]
    public void PromotionRule_供应商附加模式生效且非法模式被丢弃()
    {
        var patterns = LocalSupplierCategoryPromotionRule.Combine(new[] { "seasonal*", "bad(pattern", "" });

        Assert.Contains("seasonal*", patterns);
        Assert.DoesNotContain("bad(pattern", patterns);
        Assert.True(LocalSupplierCategoryPromotionRule.IsPromotional("Seasonal Christmas", "/seasonal/christmas", patterns));
    }

    [Fact]
    public void Resolver_取最深的非促销分类()
    {
        var now = new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc);
        var candidates = new[]
        {
            new LocalSupplierCategoryCandidate("root", 0, false, true, false, now),
            new LocalSupplierCategoryCandidate("leaf", 2, false, true, false, now.AddDays(-5)),
            new LocalSupplierCategoryCandidate("promo-deep", 3, true, true, false, now),
            new LocalSupplierCategoryCandidate("inactive-deep", 4, false, false, false, now),
            new LocalSupplierCategoryCandidate("deleted-deep", 5, false, true, true, now),
        };

        Assert.Equal("leaf", LocalSupplierCategoryResolver.Resolve(candidates));
    }

    [Fact]
    public void Resolver_同深度取最近看到的并按GUID定序()
    {
        var now = new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(
            "recent",
            LocalSupplierCategoryResolver.Resolve(new[]
            {
                new LocalSupplierCategoryCandidate("older", 1, false, true, false, now.AddHours(-1)),
                new LocalSupplierCategoryCandidate("recent", 1, false, true, false, now),
            })
        );
        Assert.Equal(
            "a",
            LocalSupplierCategoryResolver.Resolve(new[]
            {
                new LocalSupplierCategoryCandidate("b", 1, false, true, false, now),
                new LocalSupplierCategoryCandidate("a", 1, false, true, false, now),
            })
        );
    }

    [Fact]
    public void Resolver_全部是促销或空候选时不归类()
    {
        var now = DateTime.UtcNow;
        Assert.Null(LocalSupplierCategoryResolver.Resolve(Array.Empty<LocalSupplierCategoryCandidate>()));
        Assert.Null(
            LocalSupplierCategoryResolver.Resolve(new[]
            {
                new LocalSupplierCategoryCandidate("promo", 1, true, true, false, now),
            })
        );
    }

    [Fact]
    public void Resolver_完整路径超长时保留叶子()
    {
        var names = Enumerable.Range(0, 8).Select(index => new string((char)('a' + index), 200)).ToList();

        var path = LocalSupplierCategoryResolver.BuildFullPath(names);

        Assert.True(path.Length <= LocalSupplierCategoryConstants.MaxFullPathLength);
        Assert.EndsWith(names[^1], path, StringComparison.Ordinal);
        Assert.StartsWith("…", path, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("  Office   Stationery (123) ", "Office Stationery")]
    [InlineData("Kitchen", "Kitchen")]
    public void Resolver_分类名清洗空白与商品计数(string raw, string expected)
    {
        Assert.Equal(expected, LocalSupplierCategoryResolver.NormalizeName(raw));
    }

    [Fact]
    public void AssignmentMatchKey_GFA按商品编码其余按货号()
    {
        Assert.Equal("ABC/123", LocalSupplierCategoryAssignmentService.BuildMatchKey("236", " abc/123 ", "ITEM-1"));
        Assert.Equal("ITEM-1", LocalSupplierCategoryAssignmentService.BuildMatchKey("240", "P-1", " item-1 "));
        Assert.Null(LocalSupplierCategoryAssignmentService.BuildMatchKey("240", "P-1", "  "));
        Assert.Equal("200", LocalSupplierCategoryAssignmentService.NormalizeSupplierCode(null));
    }

    [Fact]
    public void CaptureRequest_路径层级与货号数量受限()
    {
        var tooDeep = new BrowserExtensionCategoryCaptureRequestDto
        {
            SupplierCode = "240",
            PageUrl = "https://www.dats.com.au/a",
            CategoryPath = Enumerable.Range(0, 9)
                .Select(index => new BrowserExtensionCategoryPathNodeDto { Name = $"N{index}", Key = $"/n{index}" })
                .ToList(),
            ItemNumbers = new List<string> { "A" },
            Mode = "passive",
        };
        var tooManyItems = new BrowserExtensionCategoryCaptureRequestDto
        {
            SupplierCode = "240",
            PageUrl = "https://www.dats.com.au/a",
            CategoryPath = new List<BrowserExtensionCategoryPathNodeDto> { new() { Name = "A", Key = "/a" } },
            ItemNumbers = Enumerable.Range(0, 101).Select(index => $"I{index}").ToList(),
            Mode = "passive",
        };

        Assert.False(Validator.TryValidateObject(tooDeep, new ValidationContext(tooDeep), null, true));
        Assert.False(Validator.TryValidateObject(tooManyItems, new ValidationContext(tooManyItems), null, true));
    }
}

public sealed class LocalSupplierCategoryProfileCatalogTests
{
    [Fact]
    public void Profiles_默认目录为除200外的供应商下发分类配置且Meteor只被动采集()
    {
        var result = BrowserExtensionProfileCatalog.BuildProfiles(new BrowserExtensionOptions());

        Assert.All(result.Profiles, profile => Assert.NotNull(profile.Category));
        var dats = Assert.Single(result.Profiles, profile => profile.SupplierCode == "240");
        Assert.True(dats.Category!.Enabled);
        Assert.Contains("/*", dats.Category.CategoryPagePatterns);
        var meteor = Assert.Single(result.Profiles, profile => profile.SupplierCode == "226");
        Assert.False(meteor.Category!.CrawlEnabled);
        Assert.True(meteor.Category.PassiveEnabled);
        var gfa = Assert.Single(result.Profiles, profile => profile.SupplierCode == "236");
        Assert.Contains("category", gfa.Category!.KeyQueryParams);
    }

    [Fact]
    public void Profiles_供应商200永不下发分类采集配置()
    {
        var category = BrowserExtensionProfileCatalog.TryBuildCategory(
            new BrowserExtensionSupplierCategoryOptions(),
            "200",
            new[] { "https://hotbargain.example/*" }
        );

        Assert.Null(category);
    }

    [Fact]
    public void Profiles_分类配置非法时只关闭分类不影响按钮注入Profile()
    {
        var dats = BrowserExtensionSupplierProfileOptions.CreateDatsDefault();
        dats.Category!.BreadcrumbSelector = ".breadcrumb\na";
        var result = BrowserExtensionProfileCatalog.BuildProfiles(
            new BrowserExtensionOptions
            {
                UseBuiltInDatsProfile = false,
                UseBuiltInSupplierProfiles = false,
                SupplierProfiles = new List<BrowserExtensionSupplierProfileOptions> { dats },
            }
        );

        var profile = Assert.Single(result.Profiles);
        Assert.Equal("240", profile.SupplierCode);
        Assert.Null(profile.Category);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60000)]
    public void Profiles_限速超出范围时关闭分类采集(int crawlDelayMs)
    {
        var options = BrowserExtensionSupplierCategoryOptions.CreateWooCommerceDefault();
        options.CrawlDelayMs = crawlDelayMs;

        Assert.Null(
            BrowserExtensionProfileCatalog.TryBuildCategory(options, "203", new[] { "https://windragon.com.au/*" })
        );
    }

    [Fact]
    public void Profiles_导航入口不能指向其他站点()
    {
        var options = BrowserExtensionSupplierCategoryOptions.CreateWooCommerceDefault();
        options.NavRootUrl = "https://evil.example/shop";

        Assert.Null(
            BrowserExtensionProfileCatalog.TryBuildCategory(options, "203", new[] { "https://windragon.com.au/*" })
        );

        options.NavRootUrl = "https://windragon.com.au/shop";
        Assert.NotNull(
            BrowserExtensionProfileCatalog.TryBuildCategory(options, "203", new[] { "https://windragon.com.au/*" })
        );
    }

    [Fact]
    public void Profiles_总开关关闭时不下发任何分类配置()
    {
        var result = BrowserExtensionProfileCatalog.BuildProfiles(
            new BrowserExtensionOptions { CategoryCaptureEnabled = false }
        );

        Assert.Equal(12, result.Profiles.Count);
        Assert.All(result.Profiles, profile => Assert.Null(profile.Category));
    }

    [Theory]
    [InlineData("1.4.1", false)]
    [InlineData("1.5.0", true)]
    [InlineData("2.0.0", true)]
    public void Profiles_只对1点5及以上客户端下发分类配置(string version, bool expectCategory)
    {
        var all = BrowserExtensionProfileCatalog.BuildProfiles(new BrowserExtensionOptions());

        var result = BrowserExtensionProfileCatalog.FilterProfilesForClient(all, version);

        Assert.Equal(12, result.Profiles.Count);
        Assert.All(result.Profiles, profile => Assert.Equal(expectCategory, profile.Category != null));
        // 剥离分类配置不能改动服务端缓存的原始目录。
        Assert.All(all.Profiles, profile => Assert.NotNull(profile.Category));
    }

    [Theory]
    [InlineData("https://www.dats.com.au/office-stationery", true)]
    [InlineData("https://www.dats.com.au.evil.example/office", false)]
    [InlineData("http://www.dats.com.au/office", false)]
    [InlineData("not-a-url", false)]
    public void UrlMatchesProfileOrigins_只接受声明的供应商源(string url, bool expected)
    {
        var dats = Assert.Single(
            BrowserExtensionProfileCatalog.BuildProfiles(new BrowserExtensionOptions()).Profiles,
            profile => profile.SupplierCode == "240"
        );

        Assert.Equal(expected, BrowserExtensionProfileCatalog.UrlMatchesProfileOrigins(dats, url));
    }

    [Fact]
    public void Controller_分类回传接口挂在扩展前缀下并启用限流()
    {
        var type = typeof(ReactBrowserExtensionController);
        var capture = type.GetMethod(nameof(ReactBrowserExtensionController.CaptureSupplierCategory));
        var snapshot = type.GetMethod(nameof(ReactBrowserExtensionController.SubmitSupplierCategoryTreeSnapshot));

        Assert.Equal("supplier-categories/captures", capture?.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Equal("supplier-categories/tree-snapshot", snapshot?.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Equal(
            BrowserExtensionCaptureRateLimits.PolicyName,
            capture?.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName
        );
        Assert.Equal(
            BrowserExtensionCaptureRateLimits.PolicyName,
            snapshot?.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName
        );
    }

    [Fact]
    public void Controller_分类管理读用商品查看权限写用商品管理权限()
    {
        var type = typeof(ReactLocalSupplierCategoriesController);
        Assert.Equal("api/react/v1/local-supplier-categories", type.GetCustomAttribute<RouteAttribute>()?.Template);

        string? PolicyOf(string method) =>
            type.GetMethod(method)?.GetCustomAttribute<AuthorizeAttribute>()?.Policy;

        Assert.Equal(Permissions.PosProducts.View, PolicyOf(nameof(ReactLocalSupplierCategoriesController.GetSummary)));
        Assert.Equal(Permissions.PosProducts.View, PolicyOf(nameof(ReactLocalSupplierCategoriesController.GetTree)));
        Assert.Equal(Permissions.PosProducts.Manage, PolicyOf(nameof(ReactLocalSupplierCategoriesController.SetPromotional)));
        Assert.Equal(Permissions.PosProducts.Manage, PolicyOf(nameof(ReactLocalSupplierCategoriesController.Resolve)));
    }
}
