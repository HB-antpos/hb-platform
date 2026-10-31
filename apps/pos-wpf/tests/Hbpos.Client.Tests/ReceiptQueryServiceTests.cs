using System.Globalization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

[Collection(CultureSensitiveTestCollection.Name)]
public sealed class ReceiptQueryServiceTests
{
    [Fact]
    public async Task Receipt_query_service_maps_receipt_preview_from_order()
    {
        using var culture = new CultureScope(CultureInfo.GetCultureInfo("en-US"));
        var order = CreateOrder(
            Guid.NewGuid(),
            soldAt: new DateTimeOffset(2026, 5, 24, 9, 30, 0, TimeSpan.Zero),
            actualAmount: 8.4m);
        var service = new ReceiptQueryService(new StubOrderRepository([order]));

        var receipt = await service.GetReceiptAsync(order.OrderGuid);

        Assert.NotNull(receipt);
        Assert.Equal(order.OrderGuid, receipt.OrderGuid);
        Assert.Equal(order.StoreCode, receipt.StoreCode);
        Assert.Equal(order.DeviceCode, receipt.DeviceCode);
        Assert.Equal(order.CashierName, receipt.CashierName);
        Assert.Equal(order.TotalAmount, receipt.TotalAmount);
        Assert.Equal(order.DiscountAmount, receipt.DiscountAmount);
        Assert.Equal(order.ActualAmount, receipt.ActualAmount);
        Assert.Equal(order.TenderedAmount, receipt.TenderedAmount);
        Assert.Equal(order.ChangeAmount, receipt.ChangeAmount);
        Assert.Equal("#" + ReceiptOrderShortCode.Create(order.OrderGuid), receipt.TransactionIdDisplay);
        Assert.Equal(order.SoldAt.ToLocalTime().ToString("MMM dd, yyyy HH:mm"), receipt.SoldAtDisplay);
        Assert.Equal(2, receipt.Lines.Count);
        Assert.Equal("SKU-401", receipt.Lines[0].ProductCode);
        Assert.Equal("ITEM-401", receipt.Lines[0].ItemNumber);
        Assert.Equal("SKU-402", receipt.Lines[1].ProductCode);
        Assert.Equal("ITEM-402", receipt.Lines[1].ItemNumber);
        Assert.Equal(PaymentMethodKind.Cash, Assert.Single(receipt.Payments).Method);
    }

    [Fact]
    public async Task Remote_order_history_preserves_product_identity_for_order_details()
    {
        var orderGuid = Guid.NewGuid();
        var details = CreateRemoteDetails(
            orderGuid,
            12m,
            [new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Card, 12m, "MASKED-REF")],
            [
                new OrderHistoryLineDto(
                    Guid.NewGuid(),
                    "SKU-REMOTE",
                    null,
                    "Remote Tea",
                    "9300001234567",
                    "ITEM-REMOTE",
                    1m,
                    12m,
                    0m,
                    12m)
            ]);
        var service = new RemoteOrderHistoryService(new StubOrderHistoryApiClient(details));

        var receipt = await service.GetDetailsAsync(orderGuid);

        Assert.NotNull(receipt);
        var line = Assert.Single(receipt.Lines);
        Assert.Equal("SKU-REMOTE", line.ProductCode);
        Assert.Equal("ITEM-REMOTE", line.ItemNumber);
        Assert.Equal("9300001234567", line.LookupCode);
        Assert.Null(line.ProductImage);
    }

    [Fact]
    public void Receipt_query_service_preserves_local_voucher_refund_mapping()
    {
        var order = CreateOrder(Guid.NewGuid(), DateTimeOffset.UtcNow, actualAmount: 8m) with
        {
            TotalAmount = -8m,
            DiscountAmount = 0m,
            ActualAmount = -8m,
            Payments = [new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Voucher, -8m, " VOUCHER_REFUND:RF-LOCAL ")]
        };

        var receipt = ReceiptQueryService.CreateReceipt(order);

        var refundVoucher = Assert.IsType<RefundVoucherReceipt>(receipt.RefundVoucher);
        Assert.Equal("RF-LOCAL", refundVoucher.VoucherCode);
        Assert.Equal(8m, refundVoucher.Amount);
    }

    [Fact]
    public async Task Remote_order_history_maps_voucher_refund_to_standalone_receipt()
    {
        var orderGuid = Guid.NewGuid();
        var details = CreateRemoteDetails(
            orderGuid,
            -8m,
            [new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Voucher, -8m, " VOUCHER_REFUND:RF-REMOTE ")]);
        var service = new RemoteOrderHistoryService(new StubOrderHistoryApiClient(details));

        var receipt = await service.GetDetailsAsync(orderGuid);

        Assert.NotNull(receipt);
        var refundVoucher = Assert.IsType<RefundVoucherReceipt>(receipt.RefundVoucher);
        Assert.Equal("RF-REMOTE", refundVoucher.VoucherCode);
        Assert.Equal(8m, refundVoucher.Amount);

        var document = new ReceiptTextFormatter().Build(receipt, ReceiptPrinterSettings.Default, receipt.SoldAt);
        Assert.Contains("REFUND VOUCHER", document.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("TAX INVOICE", document.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public void Receipt_formatter_prints_refund_status_instead_of_paid_for_negative_net_order()
    {
        var order = CreateOrder(Guid.NewGuid(), DateTimeOffset.UtcNow, actualAmount: 8m) with
        {
            TotalAmount = -8m,
            DiscountAmount = 0m,
            ActualAmount = -8m,
            Payments = [new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Cash, -8m, null)]
        };

        var receipt = ReceiptQueryService.CreateReceipt(order);
        var document = new ReceiptTextFormatter().Build(receipt, ReceiptPrinterSettings.Default, receipt.SoldAt);

        Assert.Contains("*** Refund ***", document.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("Paid", document.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public void Receipt_formatter_keeps_paid_status_for_ordinary_sale()
    {
        var order = CreateOrder(Guid.NewGuid(), DateTimeOffset.UtcNow, actualAmount: 8m);

        var receipt = ReceiptQueryService.CreateReceipt(order);
        var document = new ReceiptTextFormatter().Build(receipt, ReceiptPrinterSettings.Default, receipt.SoldAt);

        Assert.Contains("*** Paid ***", document.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("Refund", document.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remote_order_history_does_not_mark_ordinary_receipt_as_refund_voucher()
    {
        var orderGuid = Guid.NewGuid();
        var details = CreateRemoteDetails(
            orderGuid,
            8m,
            [new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Cash, 8m, null)]);
        var service = new RemoteOrderHistoryService(new StubOrderHistoryApiClient(details));

        var receipt = await service.GetDetailsAsync(orderGuid);

        Assert.NotNull(receipt);
        Assert.Null(receipt.RefundVoucher);

        var document = new ReceiptTextFormatter().Build(receipt, ReceiptPrinterSettings.Default, receipt.SoldAt);
        Assert.Contains("TAX INVOICE", document.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("REFUND VOUCHER", document.PlainText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("VOUCHER_REFUND_PENDING")]
    [InlineData("VOUCHER:SALE-001")]
    [InlineData(null)]
    public async Task Remote_order_history_does_not_mark_non_refund_voucher_reference_as_refund_voucher(
        string? reference)
    {
        var orderGuid = Guid.NewGuid();
        var details = CreateRemoteDetails(
            orderGuid,
            -8m,
            [new OrderHistoryPaymentDto(Guid.NewGuid(), PaymentMethodKind.Voucher, -8m, reference)]);
        var service = new RemoteOrderHistoryService(new StubOrderHistoryApiClient(details));

        var receipt = await service.GetDetailsAsync(orderGuid);

        Assert.NotNull(receipt);
        Assert.Null(receipt.RefundVoucher);
    }

    [Fact]
    public async Task Receipt_query_service_returns_latest_receipt()
    {
        var olderOrder = CreateOrder(
            Guid.NewGuid(),
            soldAt: new DateTimeOffset(2026, 5, 24, 8, 0, 0, TimeSpan.Zero),
            actualAmount: 5m);
        var latestOrder = CreateOrder(
            Guid.NewGuid(),
            soldAt: new DateTimeOffset(2026, 5, 24, 10, 0, 0, TimeSpan.Zero),
            actualAmount: 9m);
        var service = new ReceiptQueryService(new StubOrderRepository([olderOrder, latestOrder]));

        var receipt = await service.GetLatestReceiptAsync();

        Assert.NotNull(receipt);
        Assert.Equal(latestOrder.OrderGuid, receipt.OrderGuid);
        Assert.Equal(latestOrder.ActualAmount, receipt.ActualAmount);
    }

    private static LocalOrder CreateOrder(Guid orderGuid, DateTimeOffset soldAt, decimal actualAmount)
    {
        var totalAmount = actualAmount + 0.2m;
        var discountAmount = totalAmount - actualAmount;
        var firstLineActual = decimal.Round(actualAmount - 4m, 2, MidpointRounding.AwayFromZero);
        var secondLineActual = decimal.Round(actualAmount - firstLineActual, 2, MidpointRounding.AwayFromZero);

        return new LocalOrder(
            orderGuid,
            "S001",
            "POS-01",
            "C001",
            "Alice",
            soldAt,
            totalAmount,
            discountAmount,
            actualAmount,
            [
                new LocalOrderLine(Guid.NewGuid(), "SKU-401", null, "Receipt Noodles", "930401", "ITEM-401", 1m, firstLineActual, 0m, firstLineActual, PriceSourceKind.StoreRetailPrice),
                new LocalOrderLine(Guid.NewGuid(), "SKU-402", null, "Receipt Juice", "930402", "ITEM-402", 1m, 4.2m, discountAmount, secondLineActual, PriceSourceKind.ProductBase)
            ],
            [new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Cash, actualAmount, null)],
            actualAmount + 1m,
            1m);
    }

    private static OrderHistoryDetailsDto CreateRemoteDetails(
        Guid orderGuid,
        decimal actualAmount,
        IReadOnlyList<OrderHistoryPaymentDto> payments,
        IReadOnlyList<OrderHistoryLineDto>? lines = null)
    {
        return new OrderHistoryDetailsDto(
            orderGuid,
            "S001",
            "POS-01",
            "Alice",
            DateTimeOffset.UtcNow,
            actualAmount,
            0m,
            actualAmount,
            lines ?? [],
            payments);
    }

    private sealed class StubOrderRepository(IEnumerable<LocalOrder> orders) : ILocalOrderRepository
    {
        private readonly Dictionary<Guid, LocalOrder> _orders = orders.ToDictionary(order => order.OrderGuid);
        private readonly IReadOnlyList<LocalOrderSummary> _summaries = orders
            .OrderByDescending(order => order.SoldAt)
            .Select(order => new LocalOrderSummary(
                order.OrderGuid,
                order.StoreCode,
                order.DeviceCode,
                order.CashierName,
                order.SoldAt,
                order.TotalAmount,
                order.DiscountAmount,
                order.ActualAmount,
                "Pending",
                order.Lines.Count,
                "Cash"))
            .ToList();

        public Task SavePendingOrderAsync(LocalOrder order, CancellationToken cancellationToken = default)
        {
            _orders[order.OrderGuid] = order;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalOrderSummary>>(_summaries.Take(take).ToList());
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
            LocalOrderHistoryQuery query,
            int take = 50,
            CancellationToken cancellationToken = default)
        {
            return GetRecentOrdersAsync(take, cancellationToken);
        }

        public Task<LocalOrder?> GetOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_orders.TryGetValue(orderGuid, out var order) ? order : null);
        }
    }

    private sealed class StubOrderHistoryApiClient(OrderHistoryDetailsDto? details) : IOrderHistoryApiClient
    {
        public Task<OrderHistoryQueryResponse> QueryAsync(
            OrderHistoryQueryRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OrderHistoryDetailsDto?> GetDetailsAsync(
            Guid orderGuid,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(details);

        public Task<OrderReturnContextDto?> GetReturnContextAsync(
            Guid orderGuid,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OrderReturnRecordCreateResponse> CreateReturnRecordsAsync(
            OrderReturnRecordCreateRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _originalUiCulture = CultureInfo.CurrentUICulture;
        private readonly CultureInfo? _originalDefaultCulture = CultureInfo.DefaultThreadCurrentCulture;
        private readonly CultureInfo? _originalDefaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

        public CultureScope(CultureInfo culture)
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _originalCulture;
            CultureInfo.CurrentUICulture = _originalUiCulture;
            CultureInfo.DefaultThreadCurrentCulture = _originalDefaultCulture;
            CultureInfo.DefaultThreadCurrentUICulture = _originalDefaultUiCulture;
        }
    }
}
