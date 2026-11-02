using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

/// <summary>
/// 收银端手工价格输入统一限制在 9999 以内：2026-10-31 / 11-02 1014 店把开价商品输成 1 亿、10 亿，
/// 操作日志入库超出 POSM decimal(10,2) 导致整批 500 无限重传。开价商品与购物车改价此前完全没有上限。
/// </summary>
public sealed class PosPriceInputRulesTests
{
    private const string StoreCode = "S001";

    [Theory]
    [InlineData("9999", true)]
    [InlineData("9999.00", true)]
    [InlineData("9999.01", false)]
    [InlineData("10000", false)]
    [InlineData("100000000", false)]
    public void AddOpenItem_accepts_prices_up_to_9999_and_rejects_anything_above(string keypad, bool accepted)
    {
        var cart = new PosCartService();
        var service = CreateWorkflow(cart);

        var result = service.AddOpenItem(CreateSession(), keypad);

        if (accepted)
        {
            Assert.Equal("pos.status.added", result.StatusKey);
            Assert.Equal(9999m, Assert.Single(cart.Lines).UnitPrice);
            return;
        }

        Assert.Equal("pos.status.unitPriceTooHigh", result.StatusKey);
        Assert.Equal("9999", Assert.Single(result.StatusArgs));
        // 拒绝时保留小键盘内容，购物车不产生任何行（也就不会再产生超限的操作日志）。
        Assert.False(result.ClearKeypadBuffer);
        Assert.Empty(cart.Lines);
    }

    [Theory]
    [InlineData("9999", true)]
    [InlineData("9999.01", false)]
    [InlineData("1000006767", false)]
    public void ModifySelectedLinePrice_accepts_prices_up_to_9999_and_rejects_anything_above(string keypad, bool accepted)
    {
        var cart = new PosCartService();
        var line = cart.AddItem(CreateItem("SKU-SALE", "LK-SALE", 10m));
        var service = CreateWorkflow(cart);

        var result = service.ModifySelectedLinePrice(line, keypad);

        if (accepted)
        {
            Assert.Equal("pos.status.linePriceUpdated", result.StatusKey);
            Assert.Equal(9999m, line.UnitPrice);
            return;
        }

        Assert.Equal("pos.status.unitPriceTooHigh", result.StatusKey);
        Assert.False(result.ClearKeypadBuffer);
        Assert.Equal(10m, line.UnitPrice);
    }

    [Fact]
    public void Special_product_and_installment_price_limits_share_the_cashier_maximum()
    {
        Assert.Equal(9999m, PosPriceInputRules.MaxUnitPrice);
        Assert.Equal(PosPriceInputRules.MaxUnitPrice, SpecialProductPriceRules.MaxRetailPrice);
        Assert.Equal(PosPriceInputRules.MaxUnitPrice, Hbpos.Client.Wpf.ViewModels.InstallmentLinesEditor.MaxUnitPrice);
    }

    private static PosTerminalWorkflowService CreateWorkflow(PosCartService cart)
    {
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([CreateItem("G006243", "OPENITEM", 0m)]);
        return new PosTerminalWorkflowService(index, cart);
    }

    private static PosSessionState CreateSession() =>
        new("HB POS", StoreCode, "Main Store", "POS-01", "C001", "Alice", true, 0);

    private static SellableItemDto CreateItem(string productCode, string lookupCode, decimal price) =>
        new(
            StoreCode: StoreCode,
            ProductCode: productCode,
            ReferenceCode: null,
            DisplayName: $"Item {productCode}",
            LookupCode: lookupCode,
            ItemNumber: productCode,
            Barcode: lookupCode,
            RetailPrice: price,
            PriceSource: PriceSourceKind.StoreRetailPrice,
            PriceSourceLabel: PriceSourceKind.StoreRetailPrice.ToString(),
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow);
}
