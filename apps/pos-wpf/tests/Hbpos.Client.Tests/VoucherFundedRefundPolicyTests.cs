using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class VoucherFundedRefundPolicyTests
{
    private static readonly Guid OriginalOrder = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid OtherOrder = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");

    [Fact]
    public void Fully_voucher_paid_return_must_be_refunded_entirely_as_voucher()
    {
        var cart = CartWithReturn((OriginalOrder, 25m));
        var capacities = new[] { Capacity(PaymentMethodKind.Voucher, 50m) };

        var required = VoucherFundedRefundPolicy.GetRequiredVoucherRefundAmount(capacities, cart.Lines, cart.ActualAmount);

        Assert.Equal(25m, required);
        Assert.Equal(0m, VoucherFundedRefundPolicy.GetNonVoucherRefundCap(PaymentMethodKind.Cash, cart.ActualAmount, required, [], 25m));
        Assert.Equal(0m, VoucherFundedRefundPolicy.GetNonVoucherRefundCap(PaymentMethodKind.Card, cart.ActualAmount, required, [], 25m));
        Assert.Null(VoucherFundedRefundPolicy.GetNonVoucherRefundCap(PaymentMethodKind.Voucher, cart.ActualAmount, required, [], 25m));
    }

    [Fact]
    public void Mixed_tender_return_requires_the_voucher_share_and_caps_cash_and_card_to_the_rest()
    {
        var cart = CartWithReturn((OriginalOrder, 25m));
        var capacities = new[]
        {
            Capacity(PaymentMethodKind.Card, 30m, reference: "ANZ:TXN-1"),
            Capacity(PaymentMethodKind.Voucher, 20m)
        };

        var required = VoucherFundedRefundPolicy.GetRequiredVoucherRefundAmount(capacities, cart.Lines, cart.ActualAmount);

        // 25 × 20/50 = 10 须退代金券，其余 15 可退卡或现金。
        Assert.Equal(10m, required);
        Assert.Equal(15m, VoucherFundedRefundPolicy.GetNonVoucherRefundCap(PaymentMethodKind.Card, cart.ActualAmount, required, [], 25m));
        // 代金券应退部分补足前现金不可用（现金必须最后退）；刷卡可先退。
        Assert.Equal(0m, VoucherFundedRefundPolicy.GetNonVoucherRefundCap(PaymentMethodKind.Cash, cart.ActualAmount, required, [], 25m));
        IReadOnlyList<PaymentTender> afterCard = [new PaymentTender(PaymentMethodKind.Card, -15m)];
        Assert.Equal(0m, VoucherFundedRefundPolicy.GetNonVoucherRefundCap(PaymentMethodKind.Card, cart.ActualAmount, required, afterCard, 10m));
        IReadOnlyList<PaymentTender> afterVoucher = [new PaymentTender(PaymentMethodKind.Voucher, -10m)];
        Assert.Equal(15m, VoucherFundedRefundPolicy.GetNonVoucherRefundCap(PaymentMethodKind.Cash, cart.ActualAmount, required, afterVoucher, 15m));
    }

    [Fact]
    public void Voucher_share_rounds_up_and_cash_settles_the_rest_only_after_the_voucher_refund()
    {
        var cart = CartWithReturn((OriginalOrder, 10m));
        var capacities = new[]
        {
            Capacity(PaymentMethodKind.Cash, 20m),
            Capacity(PaymentMethodKind.Voucher, 10m)
        };

        var required = VoucherFundedRefundPolicy.GetRequiredVoucherRefundAmount(capacities, cart.Lines, cart.ActualAmount);

        Assert.Equal(3.34m, required);
        // 代金券未退前现金不可用。
        Assert.Equal(0m, VoucherFundedRefundPolicy.GetNonVoucherRefundCap(PaymentMethodKind.Cash, cart.ActualAmount, required, [], 10m));
        // 代金券先退 3.34：剩余 6.66 正好结清，现金按进位规则取整。
        IReadOnlyList<PaymentTender> afterVoucher = [new PaymentTender(PaymentMethodKind.Voucher, -3.34m)];
        Assert.Equal(6.65m, VoucherFundedRefundPolicy.GetNonVoucherRefundCap(PaymentMethodKind.Cash, cart.ActualAmount, required, afterVoucher, 6.66m));
        Assert.Equal(0m, VoucherFundedRefundPolicy.GetVoucherRefundShortfall(required, afterVoucher));
        Assert.Equal(3.34m, VoucherFundedRefundPolicy.GetVoucherRefundShortfall(required, [new PaymentTender(PaymentMethodKind.Cash, -6.65m)]));
    }

    [Fact]
    public void Exchange_caps_the_voucher_requirement_at_the_money_actually_refunded()
    {
        var cart = CartWithReturn((OriginalOrder, 50m));
        cart.AddItem(new SellableItemDto(
            StoreCode: "S001",
            ProductCode: "SKU-NEW",
            ReferenceCode: null,
            DisplayName: "New Item",
            LookupCode: "930NEW",
            ItemNumber: "SKU-NEW",
            Barcode: "930NEW",
            RetailPrice: 30m,
            PriceSource: PriceSourceKind.StoreRetailPrice,
            PriceSourceLabel: PriceSourceKind.StoreRetailPrice.ToString(),
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow));
        var capacities = new[] { Capacity(PaymentMethodKind.Voucher, 50m) };

        var required = VoucherFundedRefundPolicy.GetRequiredVoucherRefundAmount(capacities, cart.Lines, cart.ActualAmount);

        Assert.Equal(-20m, cart.ActualAmount);
        Assert.Equal(20m, required);
    }

    [Fact]
    public void Orders_without_voucher_payments_no_receipt_lines_and_unlinked_capacities_are_not_restricted()
    {
        var cart = CartWithReturn((OriginalOrder, 10m), (OtherOrder, 10m), (null, 10m));
        var capacities = new[]
        {
            Capacity(PaymentMethodKind.Cash, 10m),
            // 缺原单标识的额度无法与退货行对应，不参与限额。
            Capacity(PaymentMethodKind.Voucher, 10m) with { OriginalOrderGuid = null },
            Capacity(PaymentMethodKind.Card, 10m, OtherOrder, "ANZ:TXN-2")
        };

        Assert.Equal(0m, VoucherFundedRefundPolicy.GetRequiredVoucherRefundAmount(capacities, cart.Lines, cart.ActualAmount));
    }

    [Fact]
    public void Duplicate_capacities_and_multiple_original_orders_sum_per_order_shares()
    {
        var cart = CartWithReturn((OriginalOrder, 20m), (OtherOrder, 30m));
        var capacities = new[]
        {
            Capacity(PaymentMethodKind.Voucher, 10m),
            Capacity(PaymentMethodKind.Cash, 10m),
            // 同一原单被重复加入购物车：比例不变。
            Capacity(PaymentMethodKind.Voucher, 10m),
            Capacity(PaymentMethodKind.Cash, 10m),
            Capacity(PaymentMethodKind.Voucher, 30m, OtherOrder)
        };

        var required = VoucherFundedRefundPolicy.GetRequiredVoucherRefundAmount(capacities, cart.Lines, cart.ActualAmount);

        Assert.Equal(40m, required);
    }

    private static OrderReturnPaymentCapacityDto Capacity(
        PaymentMethodKind method,
        decimal originalAmount,
        Guid? originalOrderGuid = null,
        string? reference = null) =>
        new(method, originalAmount, 0m, originalAmount, reference, OriginalOrderGuid: originalOrderGuid ?? OriginalOrder);

    private static PosCartService CartWithReturn(params (Guid? OriginalOrder, decimal Amount)[] returns)
    {
        var cart = new PosCartService();
        var index = 0;
        foreach (var (originalOrder, amount) in returns)
        {
            index++;
            cart.AddReturnLine(new ReturnCartLineRequest(
                "S001",
                $"SKU-RET-{index}",
                null,
                $"Returned {index}",
                $"930RET{index}",
                null,
                null,
                1m,
                amount,
                PriceSourceKind.StoreRetailPrice,
                PriceSourceKind.StoreRetailPrice.ToString(),
                $"RETURN-{index}",
                originalOrder,
                originalOrder is null ? null : Guid.NewGuid()));
        }

        return cart;
    }
}
