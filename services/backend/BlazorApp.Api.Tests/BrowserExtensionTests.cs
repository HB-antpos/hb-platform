using System.Reflection;
using System.Security.Claims;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Models;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class BrowserExtensionAccessServiceTests
{
    [Fact]
    public async Task WarehouseStaff_RequiresExplicitOrdersCreate_AndBypassesStoreScope()
    {
        var scope = new Mock<ICurrentUserManageableStoreScopeService>(MockBehavior.Strict);
        var userService = new Mock<IUserService>(MockBehavior.Strict);
        var service = new BrowserExtensionAccessService(
            CreateAuthorizationService(Permissions.Orders.Create),
            scope.Object,
            userService.Object
        );

        var allowed = await service.CanAccessAsync(
            CreateUser("user-1", "WarehouseStaff"),
            "1004"
        );

        Assert.True(allowed);
        scope.VerifyNoOtherCalls();
        userService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WarehouseStaff_WithoutOrdersCreate_IsDeniedEvenWhenOrderFrontViewExists()
    {
        var service = new BrowserExtensionAccessService(
            CreateAuthorizationService(Permissions.OrderFront.View),
            Mock.Of<ICurrentUserManageableStoreScopeService>(),
            Mock.Of<IUserService>()
        );

        var allowed = await service.CanAccessAsync(
            CreateUser("user-1", "仓库员工"),
            "1004"
        );

        Assert.False(allowed);
    }

    [Fact]
    public async Task OrderFrontUser_MustHaveSelectedStoreScope()
    {
        var scope = new Mock<ICurrentUserManageableStoreScopeService>(MockBehavior.Strict);
        scope.Setup(item => item.CanAccessStoreCodeAsync(It.IsAny<string>())).ReturnsAsync(false);
        var userService = new Mock<IUserService>(MockBehavior.Strict);
        userService
            .Setup(item => item.GetUserStoresAsync("user-1"))
            .ReturnsAsync(
                ApiResponse<List<UserStoreDto>>.OK(
                    new List<UserStoreDto>
                    {
                        new() { StoreCode = "1004", StoreName = "Kawana", IsActive = true },
                    }
                )
            );
        var service = new BrowserExtensionAccessService(
            CreateAuthorizationService(Permissions.OrderFront.View),
            scope.Object,
            userService.Object
        );

        Assert.True(await service.CanAccessAsync(CreateUser("user-1", "Order"), "1004"));
        Assert.False(await service.CanAccessAsync(CreateUser("user-1", "Order"), "1006"));
    }

    [Fact]
    public async Task OrdersCreateAlone_DoesNotGrantOrdinaryUserAccess()
    {
        var service = new BrowserExtensionAccessService(
            CreateAuthorizationService(Permissions.Orders.Create),
            Mock.Of<ICurrentUserManageableStoreScopeService>(),
            Mock.Of<IUserService>()
        );

        Assert.False(await service.CanAccessAsync(CreateUser("user-1", "Order"), null));
    }

    [Fact]
    public async Task Admin_CanAccessBaseAndAnySelectedStoreWithoutAdditionalPolicy()
    {
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        var scope = new Mock<ICurrentUserManageableStoreScopeService>(MockBehavior.Strict);
        var userService = new Mock<IUserService>(MockBehavior.Strict);
        var service = new BrowserExtensionAccessService(
            authorization.Object,
            scope.Object,
            userService.Object
        );
        var user = CreateUser("admin-1", "SuperAdmin");

        Assert.True(await service.CanAccessAsync(user));
        Assert.True(await service.CanAccessAsync(user, "9999"));
        authorization.VerifyNoOtherCalls();
        scope.VerifyNoOtherCalls();
        userService.VerifyNoOtherCalls();
    }

    private static ClaimsPrincipal CreateUser(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new("userId", userId) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static IAuthorizationService CreateAuthorizationService(params string[] allowedPolicies)
    {
        var allowed = allowedPolicies.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var service = new Mock<IAuthorizationService>(MockBehavior.Strict);
        service
            .Setup(item =>
                item.AuthorizeAsync(
                    It.IsAny<ClaimsPrincipal>(),
                    It.IsAny<object?>(),
                    It.IsAny<string>()
                )
            )
            .ReturnsAsync(
                (ClaimsPrincipal _, object? _, string policy) =>
                    allowed.Contains(policy)
                        ? AuthorizationResult.Success()
                        : AuthorizationResult.Failed()
            );
        return service.Object;
    }
}

public sealed class BrowserExtensionPurchaseCycleTests
{
    [Fact]
    public void Build_GroupsSameDayInvoices_UsesWeightedPrices_AndSplitsSalesAtNextPurchase()
    {
        var today = new DateOnly(2026, 8, 19);
        var purchases = new[]
        {
            Purchase(new DateOnly(2026, 8, 1), "INV-1", 4m, 2m, "P1"),
            Purchase(new DateOnly(2026, 8, 1), "INV-2", 6m, 3m, "P1"),
            Purchase(new DateOnly(2026, 8, 10), "INV-3", 5m, 4m, "P1"),
        };
        var sales = new[]
        {
            Sale(new DateOnly(2026, 8, 1), 2m, 6m, "P1"),
            Sale(new DateOnly(2026, 8, 9), 3m, 12m, "P1"),
            Sale(new DateOnly(2026, 8, 10), 4m, 20m, "P1"),
            Sale(today, 1m, 7m, "P1"),
        };

        var cycles = BrowserExtensionPurchaseCycleCalculator.Build(purchases, sales, today);

        Assert.Equal(2, cycles.Count);
        var latest = cycles[0];
        Assert.Equal(new DateOnly(2026, 8, 10), latest.PurchaseDate);
        Assert.Equal(today, latest.SalesEndDate);
        Assert.Equal(5m, latest.SalesQuantity);
        Assert.Equal(5.4m, latest.AverageSalePrice);

        var previous = cycles[1];
        Assert.Equal(new[] { "INV-1", "INV-2" }, previous.InvoiceNumbers);
        Assert.Equal(10m, previous.PurchaseQuantity);
        Assert.Equal(2.6m, previous.AveragePurchasePrice);
        Assert.Equal(new DateOnly(2026, 8, 9), previous.SalesEndDate);
        Assert.Equal(5m, previous.SalesQuantity);
    }

    [Fact]
    public void Build_ReturnsAtMostSixEventsWithinPreviousTwelveMonths()
    {
        var today = new DateOnly(2026, 8, 19);
        var purchases = Enumerable.Range(0, 8)
            .Select(index =>
                Purchase(today.AddMonths(-index * 2), $"INV-{index}", 1m, 1m, "P1")
            )
            .ToList();

        var cycles = BrowserExtensionPurchaseCycleCalculator.Build(purchases, [], today);

        Assert.Equal(6, cycles.Count);
        Assert.All(cycles, cycle => Assert.True(cycle.PurchaseDate >= today.AddMonths(-12)));
        Assert.Equal(today, cycles[0].PurchaseDate);
    }

    private static BrowserExtensionPurchaseLine Purchase(
        DateOnly date,
        string invoiceNumber,
        decimal quantity,
        decimal price,
        string productCode
    ) =>
        new()
        {
            PurchaseDate = date,
            InvoiceNumber = invoiceNumber,
            Quantity = quantity,
            PurchasePrice = price,
            Amount = quantity * price,
            ProductCode = productCode,
        };

    private static BrowserExtensionSalesLine Sale(
        DateOnly date,
        decimal quantity,
        decimal amount,
        string productCode
    ) =>
        new()
        {
            Date = date,
            Quantity = quantity,
            Amount = amount,
            ProductCode = productCode,
        };
}

public sealed class BrowserExtensionSqlBuilderTests
{
    [Fact]
    public void SummaryQuery_IsReadOnlyParameterized_AndUsesExpectedPurchaseDateFallback()
    {
        var query = BrowserExtensionPurchaseCycleSqlBuilder.BuildSummary(
            "1004",
            "DATS",
            new[] { "ABC-1", "X'; DROP TABLE Product;--" },
            new DateOnly(2026, 8, 19)
        );

        Assert.Contains("COALESCE(h.InboundDate, h.OrderDate, h.CreatedAt)", query.Sql);
        Assert.Contains("@Cutoff", query.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            query.Parameters,
            parameter => Equals(parameter.Value, new DateTime(2025, 8, 19))
        );
        Assert.Contains("COALESCE(d.ItemNumber, p.ItemNumber)", query.Sql);
        Assert.Contains("COALESCE(h.IsDeleted, 0) = 0", query.Sql);
        Assert.Contains("COALESCE(d.IsDeleted, 0) = 0", query.Sql);
        Assert.Contains("LEFT JOIN [StoreRetailPrice] storeProduct", query.Sql);
        Assert.Contains("storeProduct.StoreCode", query.Sql);
        Assert.Contains("storeProduct.SupplierCode", query.Sql);
        Assert.DoesNotContain("DROP TABLE", query.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(query.Parameters, parameter => Equals(parameter.Value, "ABC-1"));
        Assert.Contains("s.SupplierCode = @SupplierCode", query.Sql);
        Assert.Contains(
            query.Parameters,
            parameter => Equals(parameter.Value, "X'; DROP TABLE PRODUCT;--")
        );
        Assert.False(BrowserExtensionPurchaseCycleSqlBuilder.ContainsWriteKeyword(query.Sql));
    }

    [Fact]
    public void GfaQueries_MatchNormalizedPageCodeAgainstProductCodeOnlyForSupplier236()
    {
        var today = new DateOnly(2026, 9, 3);
        var gfaSummary = BrowserExtensionPurchaseCycleSqlBuilder.BuildSummary(
            "1024",
            "236",
            new[] { "HO/BPK2FL" },
            today
        );
        var otherSummary = BrowserExtensionPurchaseCycleSqlBuilder.BuildSummary(
            "1024",
            "240",
            new[] { "HO/BPK2FL" },
            today
        );
        var gfaPurchases = BrowserExtensionPurchaseCycleSqlBuilder.BuildPurchaseLines(
            "1024",
            "236",
            "HO/BPK2FL",
            today.AddMonths(-12),
            today
        );
        var otherPurchases = BrowserExtensionPurchaseCycleSqlBuilder.BuildPurchaseLines(
            "1024",
            "240",
            "HO/BPK2FL",
            today.AddMonths(-12),
            today
        );
        var compactSummarySql = System.Text.RegularExpressions.Regex.Replace(gfaSummary.Sql, @"\s+", " ");
        var compactPurchaseSql = System.Text.RegularExpressions.Regex.Replace(gfaPurchases.Sql, @"\s+", " ");

        Assert.Contains("@MatchByProductCode = 1", gfaSummary.Sql);
        Assert.Contains(
            "@MatchByProductCode = 0 AND UPPER(LTRIM(RTRIM(p.ItemNumber))) = requested.ItemNumber",
            compactSummarySql
        );
        Assert.Contains(
            "UPPER(LTRIM(RTRIM(COALESCE(p.ProductCode, N'')))) = requested.ItemNumber",
            gfaSummary.Sql
        );
        Assert.Contains("@MatchByProductCode = 1", gfaPurchases.Sql);
        Assert.Contains(
            "@MatchByProductCode = 0 AND UPPER(LTRIM(RTRIM(p.ItemNumber))) = @ItemNumber",
            compactPurchaseSql
        );
        Assert.Contains(
            "UPPER(LTRIM(RTRIM(COALESCE(NULLIF(d.ProductCode, N''), p.ProductCode, N'')))) = @ItemNumber",
            gfaPurchases.Sql
        );
        Assert.Equal(
            true,
            Assert.Single(gfaSummary.Parameters, parameter => parameter.ParameterName == "@MatchByProductCode").Value
        );
        Assert.Equal(
            false,
            Assert.Single(otherSummary.Parameters, parameter => parameter.ParameterName == "@MatchByProductCode").Value
        );
        Assert.Equal(
            true,
            Assert.Single(gfaPurchases.Parameters, parameter => parameter.ParameterName == "@MatchByProductCode").Value
        );
        Assert.Equal(
            false,
            Assert.Single(otherPurchases.Parameters, parameter => parameter.ParameterName == "@MatchByProductCode").Value
        );
    }

    [Fact]
    public void SalesQuery_FiltersBySupplierStoreAndProductCodes()
    {
        var query = BrowserExtensionPurchaseCycleSqlBuilder.BuildSales(
            "1004",
            "DATS",
            new[] { "P-1" },
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 19)
        );

        Assert.Contains("s.BranchCode = @StoreCode", query.Sql);
        Assert.Contains("s.SupplierCode = @SupplierCode", query.Sql);
        Assert.Contains(query.Parameters, parameter => Equals(parameter.Value, "DATS"));
        Assert.False(BrowserExtensionPurchaseCycleSqlBuilder.ContainsWriteKeyword(query.Sql));
    }

    [Fact]
    public void SalesQuery_RejectsMoreThanMaximumProductCodesInsteadOfSilentlyTruncating()
    {
        var productCodes = Enumerable.Range(0, 101).Select(index => $"P-{index}");

        Assert.Throws<ArgumentException>(() =>
            BrowserExtensionPurchaseCycleSqlBuilder.BuildSales(
                "1004",
                "DATS",
                productCodes,
                new DateOnly(2026, 8, 1),
                new DateOnly(2026, 8, 19)
            )
        );
    }

    [Fact]
    public void NormalizeItemNumbers_DeduplicatesAndLimitsBatchSize()
    {
        var normalized = BrowserExtensionPurchaseCycleSqlBuilder.NormalizeItemNumbers(
            new[] { " abc ", "ABC", "xyz" }
        );

        Assert.Equal(new[] { "ABC", "XYZ" }, normalized);
        Assert.Throws<ArgumentException>(() =>
            BrowserExtensionPurchaseCycleSqlBuilder.NormalizeItemNumbers(
                Enumerable.Range(0, 101).Select(index => $"ITEM-{index}")
            )
        );
    }
}

public sealed class BrowserExtensionControllerContractTests
{
    [Fact]
    public void Controller_UsesAuthenticatedBrowserExtensionRoute()
    {
        var type = typeof(ReactBrowserExtensionController);
        var route = type.GetCustomAttribute<RouteAttribute>();

        Assert.Equal("api/react/v1/browser-extension", route?.Template);
        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());
        Assert.NotNull(type.GetMethod(nameof(ReactBrowserExtensionController.GetRelease)));
        Assert.NotNull(type.GetMethod(nameof(ReactBrowserExtensionController.GetSupplierProfiles)));
        Assert.NotNull(type.GetMethod(nameof(ReactBrowserExtensionController.GetProductSummaries)));
        Assert.NotNull(type.GetMethod(nameof(ReactBrowserExtensionController.GetPurchaseCycles)));
    }
}

public sealed class BrowserExtensionProfileCatalogTests
{
    [Fact]
    public void Profiles_BrazcoSupportsCategoryHtmlPages()
    {
        var result = BrowserExtensionProfileCatalog.BuildProfiles(new BrowserExtensionOptions());

        var brazco = Assert.Single(result.Profiles, profile => profile.SupplierCode == "243");
        Assert.Contains("https://www.brazcoint.com.au/*.html*", brazco.ListPagePatterns);
    }

    [Fact]
    public void Profiles_MnbSupportsCategoryHtmlPagesWithoutHyphens()
    {
        var result = BrowserExtensionProfileCatalog.BuildProfiles(new BrowserExtensionOptions());

        var mnb = Assert.Single(result.Profiles, profile => profile.SupplierCode == "225");
        Assert.Contains("https://www.mnb.com.au/*.html*", mnb.ListPagePatterns);
    }

    [Fact]
    public void Profiles_MalmarSupportsProductsHtmCategoryPages()
    {
        var result = BrowserExtensionProfileCatalog.BuildProfiles(new BrowserExtensionOptions());

        var malmar = Assert.Single(result.Profiles, profile => profile.SupplierCode == "227");
        Assert.Contains("https://www.malmar.com.au/products/*.htm*", malmar.ListPagePatterns);
    }

    [Fact]
    public void Profiles_MeteorPartySupportsCurrentCategoryTrees()
    {
        var result = BrowserExtensionProfileCatalog.BuildProfiles(new BrowserExtensionOptions());

        var meteor = Assert.Single(result.Profiles, profile => profile.SupplierCode == "226");
        Assert.Contains("https://www.meteorparty.com.au/Party*", meteor.ListPagePatterns);
        Assert.Contains("https://www.meteorparty.com.au/Events*", meteor.ListPagePatterns);
        Assert.Contains("https://www.meteorparty.com.au/Seasonal*", meteor.ListPagePatterns);
        Assert.Contains("https://www.meteorparty.com.au/Tableware*", meteor.ListPagePatterns);
        Assert.Contains("https://www.meteorparty.com.au/Candles*", meteor.ListPagePatterns);
    }

    [Fact]
    public void Profiles_DefaultCatalogContainsAllSupportedSuppliers()
    {
        var result = BrowserExtensionProfileCatalog.BuildProfiles(new BrowserExtensionOptions());

        Assert.Equal("7", result.ConfigVersion);
        Assert.Equal(
            new[] { "240", "243", "227", "226", "201", "203", "225", "218", "267", "236", "SP2502280001", "SP0101" },
            result.Profiles.Select(profile => profile.SupplierCode)
        );
        Assert.Equal(
            new[] { "DATS", "Brazco", "Malmar", "Meteor Party", "Yatsal", "Windragon", "MNB", "PJ SAS", "Jemark", "GFA", "TXK", "Boom Up" },
            result.Profiles.Select(profile => profile.DisplayName)
        );

        var brazco = Assert.Single(result.Profiles, profile => profile.SupplierCode == "243");
        Assert.Equal(new[] { "after-colon", "trim", "uppercase" }, brazco.ItemNumber.Transforms);

        var yatsal = Assert.Single(result.Profiles, profile => profile.SupplierCode == "201");
        Assert.Equal(
            new[] { "https://yatsal.com.au/*", "https://www.yatsal.com.au/*" },
            yatsal.Origins
        );

        var pjsas = Assert.Single(result.Profiles, profile => profile.SupplierCode == "218");
        Assert.Equal(new[] { "https://www.pjsas.com.au/*" }, pjsas.Origins);
        Assert.Equal(new[] { "after-colon", "trim", "uppercase" }, pjsas.ItemNumber.Transforms);

        (string Code, string Card, string Source, string? Selector, string? Attribute, string Mount, string Pattern)[] expectedContracts =
        {
            ("243", ".product-listing-record", "text", ".product-listing-code", null, ".product-listing-code", "https://www.brazcoint.com.au/*.html*"),
            ("227", "li.item-thumbs", "text", ".p-spec strong", null, ".p-spec", "https://www.malmar.com.au/Products.aspx*"),
            ("226", ".facets-item-cell-grid[data-sku]", "attribute", null, "data-sku", ".facets-item-cell-grid-title", "https://www.meteorparty.com.au/balloons/*"),
            ("201", ".product[data-product-code]", "attribute", null, "data-product-code", ".widget-productlist-code", "https://yatsal.com.au/*"),
            ("203", "li.product", "text", ".sku", null, ".sku", "https://windragon.com.au/product-category/*"),
            ("225", ".product-listing-record", "text", ".product-listing-code", null, ".product-listing-code", "https://www.mnb.com.au/*.html*"),
            ("218", ".product-listing-record", "text", ".product-listing-code", null, ".product-listing-code", "https://www.pjsas.com.au/*.html*"),
            ("267", "ul.products li.product", "text", ".model", null, ".model", "https://www.jemark.com.au/category/*"),
            ("236", ".list-row[data-product]", "attribute", null, "data-product", ".content > a[href*='/product/view?id=']", "https://gfa.opmetrix.store/products/view*"),
            ("SP2502280001", ".single-product.grid-view", "text", ".sku", null, ".price-box", "http://txkorders.inzantsales.com/shop*"),
            ("SP0101", "main ul.products li.product", "text", ".custom_sku", null, "h2.woocommerce-loop-product__title", "https://boomup.com.au/shop*"),
        };
        foreach (var expected in expectedContracts)
        {
            var profile = Assert.Single(result.Profiles, item => item.SupplierCode == expected.Code);
            Assert.Equal(expected.Card, profile.CardSelector);
            Assert.Equal(expected.Source, profile.ItemNumber.Source);
            Assert.Equal(expected.Selector, profile.ItemNumber.Selector);
            Assert.Equal(expected.Attribute, profile.ItemNumber.Attribute);
            Assert.Equal(expected.Mount, profile.MountSelector);
            Assert.Contains(expected.Pattern, profile.ListPagePatterns);
        }
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("1.0.0", 1)]
    [InlineData("1.1.0", 10)]
    [InlineData("1.2.0", 12)]
    [InlineData("2.0.0", 12)]
    public void Profiles_FilterForClientProtectsLegacyExtensions(
        string? extensionVersion,
        int expectedCount
    )
    {
        var all = BrowserExtensionProfileCatalog.BuildProfiles(new BrowserExtensionOptions());

        var result = BrowserExtensionProfileCatalog.FilterProfilesForClient(all, extensionVersion);

        Assert.Equal(expectedCount, result.Profiles.Count);
        Assert.Equal("240", result.Profiles[0].SupplierCode);
        Assert.DoesNotContain(
            result.Profiles.SelectMany(profile => profile.ItemNumber.Transforms),
            transform => transform == "after-colon" && expectedCount == 1
        );
        if (expectedCount < 12)
        {
            Assert.DoesNotContain(result.Profiles, profile => profile.SupplierCode == "236");
            Assert.DoesNotContain(result.Profiles, profile => profile.SupplierCode == "SP2502280001");
        }
        if (expectedCount == 10)
        {
            Assert.Equal(
                new[] { "240", "243", "227", "226", "201", "203", "225", "218", "267", "SP0101" },
                result.Profiles.Select(profile => profile.SupplierCode)
            );
        }
    }

    [Fact]
    public void Profiles_ConfiguredDisabledRuleSuppressesMatchingBuiltInSupplier()
    {
        var result = BrowserExtensionProfileCatalog.BuildProfiles(
            new BrowserExtensionOptions
            {
                SupplierProfiles = new List<BrowserExtensionSupplierProfileOptions>
                {
                    new() { SupplierCode = "243", Enabled = false },
                },
            }
        );

        Assert.DoesNotContain(result.Profiles, profile => profile.SupplierCode == "243");
        Assert.Equal(11, result.Profiles.Count);
    }

    [Fact]
    public void Profiles_CanDisableBuiltInDatsWithoutPublishingExtension()
    {
        var result = BrowserExtensionProfileCatalog.BuildProfiles(
            new BrowserExtensionOptions
            {
                UseBuiltInDatsProfile = false,
                UseBuiltInSupplierProfiles = false,
                SupplierProfiles = new List<BrowserExtensionSupplierProfileOptions>(),
            }
        );

        Assert.Empty(result.Profiles);
    }

    [Fact]
    public void Profiles_OnlyReturnEnabledDeclarativeConfiguration()
    {
        var options = new BrowserExtensionOptions
        {
            UseBuiltInSupplierProfiles = false,
            SupplierProfiles = new List<BrowserExtensionSupplierProfileOptions>
            {
                BrowserExtensionSupplierProfileOptions.CreateDatsDefault(),
                new()
                {
                    SupplierCode = "UNSAFE",
                    DisplayName = "Unsafe",
                    Origins = new List<string> { "javascript:alert(1)" },
                    ListPagePatterns = new List<string> { "https://example.com/*" },
                    CardSelector = ".product",
                    ItemNumberSource = "attribute",
                    ItemNumberAttribute = "data-code",
                    MountSelector = ".code",
                },
                new()
                {
                    SupplierCode = "OFF",
                    Enabled = false,
                    Origins = new List<string> { "https://example.com/*" },
                    ListPagePatterns = new List<string> { "https://example.com/*" },
                    CardSelector = ".product",
                    ItemNumberSource = "text",
                    MountSelector = ".code",
                },
            },
        };

        var result = BrowserExtensionProfileCatalog.BuildProfiles(options);

        var profile = Assert.Single(result.Profiles);
        Assert.Equal("240", profile.SupplierCode);
        Assert.Equal("DATS", profile.DisplayName);
        Assert.Equal(new[] { "trim", "uppercase" }, profile.ItemNumber.Transforms);
    }

    [Fact]
    public void Profiles_OnlyAllowExactTxkHttpOrigin()
    {
        BrowserExtensionSupplierProfileOptions TxkWithOrigin(string origin) =>
            new()
            {
                SupplierCode = "SP2502280001",
                DisplayName = "TXK",
                Origins = new List<string> { origin },
                ListPagePatterns = new List<string> { origin.Replace("/*", "/shop*") },
                CardSelector = ".single-product.grid-view",
                ItemNumberSource = "text",
                ItemNumberSelector = ".sku",
                ItemNumberTransforms = new List<string> { "after-sku", "trim", "uppercase" },
                MountSelector = ".price-box",
            };

        var accepted = BrowserExtensionProfileCatalog.BuildProfiles(
            new BrowserExtensionOptions
            {
                UseBuiltInDatsProfile = false,
                UseBuiltInSupplierProfiles = false,
                SupplierProfiles = new List<BrowserExtensionSupplierProfileOptions>
                {
                    TxkWithOrigin("http://txkorders.inzantsales.com/*"),
                },
            }
        );
        var rejected = BrowserExtensionProfileCatalog.BuildProfiles(
            new BrowserExtensionOptions
            {
                UseBuiltInDatsProfile = false,
                UseBuiltInSupplierProfiles = false,
                SupplierProfiles = new List<BrowserExtensionSupplierProfileOptions>
                {
                    TxkWithOrigin("http://example.com/*"),
                },
            }
        );

        var profile = Assert.Single(accepted.Profiles);
        Assert.Equal(new[] { "after-sku", "trim", "uppercase" }, profile.ItemNumber.Transforms);
        Assert.Empty(rejected.Profiles);

        foreach (
            var unsafeOrigin in new[]
            {
                "http://txkorders.inzantsales.com.evil.example/*",
                "http://txkorders.inzantsales.com:8080/*",
                "http://user@txkorders.inzantsales.com/*",
            }
        )
        {
            var unsafeResult = BrowserExtensionProfileCatalog.BuildProfiles(
                new BrowserExtensionOptions
                {
                    UseBuiltInDatsProfile = false,
                    UseBuiltInSupplierProfiles = false,
                    SupplierProfiles = new List<BrowserExtensionSupplierProfileOptions>
                    {
                        TxkWithOrigin(unsafeOrigin),
                    },
                }
            );
            Assert.Empty(unsafeResult.Profiles);
        }
    }

    [Fact]
    public void Profiles_ConfiguredDatsBusinessCodeOverridesBuiltInProfile()
    {
        var result = BrowserExtensionProfileCatalog.BuildProfiles(
            new BrowserExtensionOptions
            {
                UseBuiltInSupplierProfiles = false,
                SupplierProfiles = new List<BrowserExtensionSupplierProfileOptions>
                {
                    new()
                    {
                        SupplierCode = "240",
                        DisplayName = "Configured DATS",
                        Origins = new List<string> { "https://www.dats.com.au/*" },
                        ListPagePatterns = new List<string> { "https://www.dats.com.au/office-stationery*" },
                        CardSelector = ".configured-product",
                        ItemNumberSource = "attribute",
                        ItemNumberAttribute = "data-product-code",
                        MountSelector = ".configured-code",
                    },
                },
            }
        );

        var profile = Assert.Single(result.Profiles);
        Assert.Equal("240", profile.SupplierCode);
        Assert.Equal("Configured DATS", profile.DisplayName);
        Assert.Equal(".configured-product", profile.CardSelector);
    }

    [Fact]
    public void Release_DefaultsExposePublishedEdgeHiddenStoreLinkOnly()
    {
        var release = BrowserExtensionProfileCatalog.BuildRelease(new BrowserExtensionOptions());

        Assert.Equal("1.2.0", release.LatestVersion);
        Assert.Equal("1.1.0", release.MinimumVersion);
        Assert.Empty(release.ChromeStoreUrl);
        Assert.Empty(release.SafariStoreUrl);
        Assert.Equal(
            "https://microsoftedge.microsoft.com/addons/detail/eeggjfaljfdkoanlaonfiodmljkmpfhn",
            release.EdgeStoreUrl
        );
    }

    [Fact]
    public void Release_StripsNonHttpsStoreLinksAndInvalidVersions()
    {
        var release = BrowserExtensionProfileCatalog.BuildRelease(
            new BrowserExtensionOptions
            {
                LatestVersion = "not-a-version",
                MinimumVersion = "1.0.0",
                ChromeStoreUrl = "javascript:alert(1)",
                EdgeStoreUrl = "https://microsoftedge.microsoft.com/addons/detail/example",
                SafariStoreUrl = "javascript:alert(2)",
            }
        );

        Assert.Equal("1.0.0", release.LatestVersion);
        Assert.Empty(release.ChromeStoreUrl);
        Assert.Empty(release.SafariStoreUrl);
        Assert.StartsWith("https://", release.EdgeStoreUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_ExposesConfiguredHttpsSafariStoreUrl()
    {
        var release = BrowserExtensionProfileCatalog.BuildRelease(
            new BrowserExtensionOptions
            {
                SafariStoreUrl = "https://apps.apple.com/au/app/hb-supplier-order/id1234567890",
            }
        );

        Assert.Equal(
            "https://apps.apple.com/au/app/hb-supplier-order/id1234567890",
            release.SafariStoreUrl
        );
    }

    [Fact]
    public void DockerCompose_MapsSafariStoreUrlFromServerEnvironment()
    {
        var composePath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../docker-compose.yml")
        );
        var compose = File.ReadAllText(composePath);

        Assert.Contains(
            "BrowserExtension__SafariStoreUrl=${BROWSER_EXTENSION_SAFARI_STORE_URL:-https://apps.apple.com/au/app/hb-supplier-order/id6803740010}",
            compose,
            StringComparison.Ordinal
        );
    }
}
