using Hbpos.Api.Data;
using Hbpos.Api.Services;
using Hbpos.Contracts.Stores;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hbpos.Api.Tests;

public sealed class StoreReceiptProfileServiceTests
{
    [Fact]
    public async Task GetCurrentAsync_returns_profile_when_loader_finds_store()
    {
        var profile = new StoreReceiptProfileDto(
            "S001", "旗舰店", "Hot Bargain", "1 Queen St", "07 3000 0000", "ABN", "30 天无理由退换");
        var service = new StoreReceiptProfileService(
            (_, _) => Task.FromResult<StoreReceiptProfileDto?>(profile));

        var result = await service.GetCurrentAsync("S001", CancellationToken.None);

        Assert.NotNull(result.Profile);
        Assert.Same(profile, result.Profile);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public async Task GetCurrentAsync_returns_store_not_found_when_loader_returns_null()
    {
        var service = new StoreReceiptProfileService(
            (_, _) => Task.FromResult<StoreReceiptProfileDto?>(null));

        var result = await service.GetCurrentAsync("S-MISSING", CancellationToken.None);

        Assert.Null(result.Profile);
        Assert.Equal(StoreReceiptProfileService.StoreNotFoundCode, result.ErrorCode);
    }

    [Fact]
    public async Task GetCurrentAsync_requires_non_blank_store_code()
    {
        var service = new StoreReceiptProfileService(
            (_, _) => Task.FromResult<StoreReceiptProfileDto?>(null));

        var result = await service.GetCurrentAsync("  ", CancellationToken.None);

        Assert.Null(result.Profile);
        Assert.Equal(StoreReceiptProfileService.StoreCodeRequiredCode, result.ErrorCode);
    }

    [Fact]
    public async Task GetCurrentAsync_allows_cr_lf_tab_in_address_and_return_policy()
    {
        var profile = new StoreReceiptProfileDto(
            "S001",
            "旗舰店",
            "Hot Bargain",
            "Line1\r\nLine2\tEnd",
            "07 3000 0000",
            "ABN",
            "Line1\r\nLine2");
        var service = new StoreReceiptProfileService(
            (_, _) => Task.FromResult<StoreReceiptProfileDto?>(profile));

        var result = await service.GetCurrentAsync("S001", CancellationToken.None);

        Assert.NotNull(result.Profile);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public async Task GetCurrentAsync_allows_null_and_empty_fields()
    {
        var profile = new StoreReceiptProfileDto("S001", "旗舰店", null, null, null, null, "");
        var service = new StoreReceiptProfileService(
            (_, _) => Task.FromResult<StoreReceiptProfileDto?>(profile));

        var result = await service.GetCurrentAsync("S001", CancellationToken.None);

        Assert.NotNull(result.Profile);
        Assert.Null(result.ErrorCode);
    }

    [Theory]
    [InlineData('\u0001')]
    [InlineData('\u0008')]
    [InlineData('\u007F')]
    [InlineData('\u009F')]
    public async Task GetCurrentAsync_rejects_disallowed_control_characters(char controlChar)
    {
        var profile = new StoreReceiptProfileDto(
            "S001",
            "旗舰店",
            "Hot Bargain",
            $"Line1{controlChar}Line2",
            "07 3000 0000",
            "ABN",
            "30 天无理由退换");
        var service = new StoreReceiptProfileService(
            (_, _) => Task.FromResult<StoreReceiptProfileDto?>(profile));

        var result = await service.GetCurrentAsync("S001", CancellationToken.None);

        Assert.Null(result.Profile);
        Assert.Equal(StoreReceiptProfileService.InvalidCharactersCode, result.ErrorCode);
    }

    [Fact]
    public async Task GetCurrentAsync_rejects_control_characters_in_return_policy()
    {
        var profile = new StoreReceiptProfileDto(
            "S001", "旗舰店", "Hot Bargain", "1 Queen St", "07 3000 0000", "ABN", "Line1\u0001Line2");
        var service = new StoreReceiptProfileService(
            (_, _) => Task.FromResult<StoreReceiptProfileDto?>(profile));

        var result = await service.GetCurrentAsync("S001", CancellationToken.None);

        Assert.Null(result.Profile);
        Assert.Equal(StoreReceiptProfileService.InvalidCharactersCode, result.ErrorCode);
    }

    [Fact]
    public async Task GetCurrentAsync_passes_voucher_and_installment_terms_through_unchanged()
    {
        // 多行、首尾空白、空行都原样透传：归一与取行是收银端的职责，接口不改写内容。
        var profile = BuildProfile() with
        {
            VoucherTerms = "Use at the issuing store only.\r\n\r\n  Not redeemable for cash.  ",
            InstallmentTerms = "Order total: $50.00 minimum.\tFirst payment: $20.00 minimum."
        };
        var service = new StoreReceiptProfileService(
            (_, _) => Task.FromResult<StoreReceiptProfileDto?>(profile));

        var result = await service.GetCurrentAsync("S001", CancellationToken.None);

        Assert.Null(result.ErrorCode);
        Assert.Equal(profile.VoucherTerms, result.Profile!.VoucherTerms);
        Assert.Equal(profile.InstallmentTerms, result.Profile.InstallmentTerms);
    }

    [Fact]
    public async Task GetCurrentAsync_old_rows_without_terms_stay_valid_with_null_terms()
    {
        var service = new StoreReceiptProfileService(
            (_, _) => Task.FromResult<StoreReceiptProfileDto?>(BuildProfile()));

        var result = await service.GetCurrentAsync("S001", CancellationToken.None);

        Assert.Null(result.ErrorCode);
        Assert.Null(result.Profile!.VoucherTerms);
        Assert.Null(result.Profile.InstallmentTerms);
    }

    [Theory]
    [InlineData("VoucherTerms", '\r')]
    [InlineData("VoucherTerms", '\n')]
    [InlineData("VoucherTerms", '\t')]
    [InlineData("InstallmentTerms", '\r')]
    [InlineData("InstallmentTerms", '\n')]
    [InlineData("InstallmentTerms", '\t')]
    public async Task GetCurrentAsync_allows_cr_lf_tab_in_voucher_and_installment_terms(string field, char whitespace)
    {
        var value = $"Line1{whitespace}Line2";
        var profile = field == "VoucherTerms"
            ? BuildProfile() with { VoucherTerms = value }
            : BuildProfile() with { InstallmentTerms = value };
        var service = new StoreReceiptProfileService(
            (_, _) => Task.FromResult<StoreReceiptProfileDto?>(profile));

        var result = await service.GetCurrentAsync("S001", CancellationToken.None);

        Assert.NotNull(result.Profile);
        Assert.Null(result.ErrorCode);
    }

    [Theory]
    [InlineData("VoucherTerms", '\u0001')]
    [InlineData("VoucherTerms", '\u0008')]
    [InlineData("VoucherTerms", '\u007F')]
    [InlineData("VoucherTerms", '\u009F')]
    [InlineData("InstallmentTerms", '\u0001')]
    [InlineData("InstallmentTerms", '\u0008')]
    [InlineData("InstallmentTerms", '\u007F')]
    [InlineData("InstallmentTerms", '\u009F')]
    public async Task GetCurrentAsync_rejects_disallowed_control_characters_in_voucher_and_installment_terms(
        string field,
        char controlChar)
    {
        var value = $"Line1{controlChar}Line2";
        var profile = field == "VoucherTerms"
            ? BuildProfile() with { VoucherTerms = value }
            : BuildProfile() with { InstallmentTerms = value };
        var service = new StoreReceiptProfileService(
            (_, _) => Task.FromResult<StoreReceiptProfileDto?>(profile));

        var result = await service.GetCurrentAsync("S001", CancellationToken.None);

        // 整份拒绝且不返回任何数据（与退货政策同口径）。
        Assert.Null(result.Profile);
        Assert.Equal(StoreReceiptProfileService.InvalidCharactersCode, result.ErrorCode);
    }

    [Fact]
    public void StoreReceiptProfileRow_carries_the_new_term_columns_for_the_sql_mapping()
    {
        var row = new StoreReceiptProfileRow { VoucherTerms = "v", InstallmentTerms = "i" };

        Assert.Equal("v", row.VoucherTerms);
        Assert.Equal("i", row.InstallmentTerms);
        Assert.Null(new StoreReceiptProfileRow().VoucherTerms);
        Assert.Null(new StoreReceiptProfileReleaseRow().InstallmentTerms);
    }

    [Theory]
    [InlineData("StoreCode", '\r')]
    [InlineData("StoreCode", '\n')]
    [InlineData("StoreCode", '\t')]
    [InlineData("StoreCode", '\u0001')]
    [InlineData("StoreCode", '\u007F')]
    [InlineData("StoreName", '\r')]
    [InlineData("StoreName", '\n')]
    [InlineData("StoreName", '\t')]
    [InlineData("StoreName", '\u0001')]
    [InlineData("StoreName", '\u007F')]
    [InlineData("BrandName", '\r')]
    [InlineData("BrandName", '\n')]
    [InlineData("BrandName", '\t')]
    [InlineData("BrandName", '\u0001')]
    [InlineData("BrandName", '\u007F')]
    [InlineData("Phone", '\r')]
    [InlineData("Phone", '\n')]
    [InlineData("Phone", '\t')]
    [InlineData("Phone", '\u0001')]
    [InlineData("Phone", '\u007F')]
    [InlineData("Abn", '\r')]
    [InlineData("Abn", '\n')]
    [InlineData("Abn", '\t')]
    [InlineData("Abn", '\u0001')]
    [InlineData("Abn", '\u007F')]
    public async Task GetCurrentAsync_rejects_control_characters_outside_address_and_return_policy(
        string field,
        char controlChar)
    {
        var value = $"X{controlChar}Y";
        var profile = field switch
        {
            "StoreCode" => BuildProfile(storeCode: value),
            "StoreName" => BuildProfile(storeName: value),
            "BrandName" => BuildProfile(brandName: value),
            "Phone" => BuildProfile(phone: value),
            "Abn" => BuildProfile(abn: value),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "不支持的测试字段")
        };
        var service = new StoreReceiptProfileService(
            (_, _) => Task.FromResult<StoreReceiptProfileDto?>(profile));

        var result = await service.GetCurrentAsync("S001", CancellationToken.None);

        Assert.Null(result.Profile);
        Assert.Equal(StoreReceiptProfileService.InvalidCharactersCode, result.ErrorCode);
    }

    [Fact]
    public void Service_registration_resolves_database_backed_service()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:MainConnection"] =
                    "Server=localhost;Database=hbpos;Trusted_Connection=True;",
                ["ConnectionStrings:PosmConnection"] =
                    "Server=localhost;Database=hbposm;Trusted_Connection=True;"
            })
            .Build());
        services.AddScoped<HbposSqlSugarContext>();
        services.AddScoped<IStoreReceiptProfileService, StoreReceiptProfileService>();

        using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IStoreReceiptProfileService>();

        Assert.IsType<StoreReceiptProfileService>(service);
    }

    private static StoreReceiptProfileDto BuildProfile(
        string storeCode = "S001",
        string storeName = "旗舰店",
        string? brandName = "Hot Bargain",
        string? address = "1 Queen St",
        string? phone = "07 3000 0000",
        string? abn = "ABN",
        string? returnPolicy = "30 天无理由退换")
    {
        return new StoreReceiptProfileDto(
            storeCode,
            storeName,
            brandName,
            address,
            phone,
            abn,
            returnPolicy);
    }
}
