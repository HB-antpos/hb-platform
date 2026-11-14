using BlazorApp.Shared.Constants;

namespace Hbpos.Client.Wpf.Services;

/// <summary>小票设置里的三个开关（银行收据、只开钱箱再扫码、退货再扫码）：读—改—存整份小票设置，存储层负责不碰总部下发的门店资料。</summary>
internal sealed class ReceiptFlagBinding(
    string key,
    IReceiptPrinterSettingsStore store,
    Func<ReceiptPrinterSettings, bool> read,
    Func<ReceiptPrinterSettings, bool, ReceiptPrinterSettings> write) : IPosSettingBinding
{
    public string Key { get; } = key;

    public async Task<bool> ReadAsync(CancellationToken cancellationToken = default) =>
        read(await store.LoadAsync(cancellationToken));

    public async Task WriteAsync(bool value, CancellationToken cancellationToken = default)
    {
        var current = await store.LoadAsync(cancellationToken);
        if (read(current) == value)
        {
            return;
        }

        await store.SaveAsync(write(current, value), cancellationToken);
    }
}

/// <summary>付款方式设置里的三个开关（手动刷卡、代金券、付款页订单列表）：三个开关是同一份配置，写的时候只改其中一项。</summary>
internal sealed class PaymentMethodFlagBinding(
    string key,
    IPaymentMethodSettingsService service,
    Func<PaymentMethodSettings, bool> read,
    Func<PaymentMethodSettings, bool, PaymentMethodSettings> write) : IPosSettingBinding
{
    public string Key { get; } = key;

    public async Task<bool> ReadAsync(CancellationToken cancellationToken = default) =>
        read(await service.LoadAsync(cancellationToken));

    public async Task WriteAsync(bool value, CancellationToken cancellationToken = default)
    {
        var current = await service.LoadAsync(cancellationToken);
        if (read(current) == value)
        {
            return;
        }

        await service.SaveAsync(write(current, value), cancellationToken);
    }
}

/// <summary>Zeller「无关联退款」：单独的存储键，不随 Square / Linkly 保存被清掉。</summary>
internal sealed class ZellerUnmatchedRefundBinding(ICardTerminalSettingsStore store) : IPosSettingBinding
{
    public string Key => PosSettingKeys.ZellerUnmatchedRefund;

    public async Task<bool> ReadAsync(CancellationToken cancellationToken = default) =>
        (await store.LoadWithoutTokenStatusAsync(cancellationToken)).ZellerAllowUnmatchedRefund;

    public async Task WriteAsync(bool value, CancellationToken cancellationToken = default)
    {
        if (await ReadAsync(cancellationToken) == value)
        {
            return;
        }

        await store.SaveZellerOptionsAsync(value, cancellationToken);
    }
}

/// <summary>日结后凌晨自动重启：本机默认开，只有显式存成关才算关。</summary>
internal sealed class AutoRestartBinding(ISystemRestartSettingsStore store) : IPosSettingBinding
{
    public string Key => PosSettingKeys.AutoRestart;

    public Task<bool> ReadAsync(CancellationToken cancellationToken = default) =>
        store.IsPromptEnabledAsync(cancellationToken);

    public async Task WriteAsync(bool value, CancellationToken cancellationToken = default)
    {
        if (await ReadAsync(cancellationToken) == value)
        {
            return;
        }

        await store.SetPromptEnabledAsync(value, cancellationToken);
    }
}

/// <summary>
/// 现金存入 / 取出：这是新功能，本机之前没有对应设置，所以单独存一个键；没存过按「关」，
/// 与服务端「三层都没设置时缺省为关」一致。
/// </summary>
internal sealed class CashMovementEnabledBinding(ILocalAppSettingsRepository repository) : IPosSettingBinding
{
    internal const string StorageKey = "PosSettings:CashMovementEnabled";

    public string Key => PosSettingKeys.CashMovementEnabled;

    public async Task<bool> ReadAsync(CancellationToken cancellationToken = default) =>
        // 中文注释：只有明确存成 "true" 才算开启；没存过、存了别的值、读到乱码都按关闭处理。
        string.Equals(
            (await repository.GetValueAsync(StorageKey, cancellationToken))?.Trim(),
            bool.TrueString,
            StringComparison.OrdinalIgnoreCase);

    public async Task WriteAsync(bool value, CancellationToken cancellationToken = default)
    {
        if (await ReadAsync(cancellationToken) == value)
        {
            return;
        }

        await repository.SetValueAsync(StorageKey, value ? bool.TrueString : bool.FalseString, cancellationToken);
    }
}

/// <summary>按 <see cref="PosSettingCatalog"/> 里 WPF 支持的项，创建全部本机存储适配。</summary>
public static class PosSettingBindings
{
    public static IReadOnlyList<IPosSettingBinding> Create(
        ILocalAppSettingsRepository repository,
        IReceiptPrinterSettingsStore receiptStore,
        IPaymentMethodSettingsService paymentMethods,
        ICardTerminalSettingsStore cardTerminalStore,
        ISystemRestartSettingsStore restartStore)
    {
        // 顺序同 PosSettingCatalog.All，也是「业务开关」页的显示顺序。
        return
        [
            new CashMovementEnabledBinding(repository),
            new ReceiptFlagBinding(
                PosSettingKeys.NoSaleRescan, receiptStore,
                settings => settings.NoSaleCashDrawerRequiresRescan,
                (settings, value) => settings with { NoSaleCashDrawerRequiresRescan = value }),
            new ReceiptFlagBinding(
                PosSettingKeys.ReturnsRescan, receiptStore,
                settings => settings.ReturnsRequireRescan,
                (settings, value) => settings with { ReturnsRequireRescan = value }),
            new ReceiptFlagBinding(
                PosSettingKeys.BankReceipt, receiptStore,
                settings => settings.PrintBankReceiptText,
                (settings, value) => settings with { PrintBankReceiptText = value }),
            new PaymentMethodFlagBinding(
                PosSettingKeys.ManualCard, paymentMethods,
                settings => settings.UseManualCard,
                (settings, value) => settings with { UseManualCard = value }),
            new PaymentMethodFlagBinding(
                PosSettingKeys.Voucher, paymentMethods,
                settings => settings.VoucherEnabled,
                (settings, value) => settings with { VoucherEnabled = value }),
            new PaymentMethodFlagBinding(
                PosSettingKeys.OrderList, paymentMethods,
                settings => settings.ShowOrderList,
                (settings, value) => settings with { ShowOrderList = value }),
            new AutoRestartBinding(restartStore),
            new ZellerUnmatchedRefundBinding(cardTerminalStore),
        ];
    }
}
