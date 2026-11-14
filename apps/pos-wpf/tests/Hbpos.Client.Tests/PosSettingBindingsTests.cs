using BlazorApp.Shared.Constants;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

/// <summary>
/// 9 个可下发开关与各自「真正的本机存储」之间的映射。用真实的各存储类跑在内存仓储上：
/// 经适配写入后，再用原存储自己的读法读回来，这样「映射到哪个字段」是端到端验证的，
/// 不会出现把「只开钱箱再扫码」与「退货再扫码」接反这类静默错误。
/// </summary>
public sealed class PosSettingBindingsTests
{
    [Fact]
    public void Bindings_cover_every_catalog_key_in_catalog_order()
    {
        var bindings = CreateBindings(new MemoryRepository(), out _);

        Assert.Equal(
            PosSettingCatalog.All.Select(definition => definition.Key),
            bindings.Select(binding => binding.Key));
    }

    [Fact]
    public async Task Defaults_match_what_each_local_store_reads_when_nothing_was_ever_saved()
    {
        var bindings = CreateBindings(new MemoryRepository(), out _);

        // 现金存入 / 取出是新功能，没存过按关；自动重启、订单列表、银行收据本机缺省是开，其余缺省是关。
        Assert.Equal(
            new Dictionary<string, bool>
            {
                [PosSettingKeys.CashMovementEnabled] = false,
                [PosSettingKeys.NoSaleRescan] = false,
                [PosSettingKeys.ReturnsRescan] = false,
                [PosSettingKeys.BankReceipt] = true,
                [PosSettingKeys.ManualCard] = false,
                [PosSettingKeys.Voucher] = false,
                [PosSettingKeys.OrderList] = true,
                [PosSettingKeys.AutoRestart] = true,
                [PosSettingKeys.ZellerUnmatchedRefund] = false,
            },
            await ReadAllAsync(bindings));
    }

    [Theory]
    [InlineData(PosSettingKeys.CashMovementEnabled)]
    [InlineData(PosSettingKeys.NoSaleRescan)]
    [InlineData(PosSettingKeys.ReturnsRescan)]
    [InlineData(PosSettingKeys.BankReceipt)]
    [InlineData(PosSettingKeys.ManualCard)]
    [InlineData(PosSettingKeys.Voucher)]
    [InlineData(PosSettingKeys.OrderList)]
    [InlineData(PosSettingKeys.AutoRestart)]
    [InlineData(PosSettingKeys.ZellerUnmatchedRefund)]
    public async Task Writing_one_key_flips_only_that_key_and_round_trips_both_ways(string key)
    {
        var bindings = CreateBindings(new MemoryRepository(), out _);
        var before = await ReadAllAsync(bindings);
        var binding = bindings.Single(item => item.Key == key);

        await binding.WriteAsync(!before[key]);
        var flipped = await ReadAllAsync(bindings);

        // 只有目标键变了：任何接错字段、顺带改了别的开关的映射都会在这里露出来。
        foreach (var other in before.Keys)
        {
            Assert.Equal(other == key ? !before[key] : before[other], flipped[other]);
        }

        await binding.WriteAsync(before[key]);
        Assert.Equal(before, await ReadAllAsync(bindings));
    }

    [Fact]
    public async Task Receipt_flags_are_stored_where_the_receipt_store_reads_them()
    {
        var bindings = CreateBindings(new MemoryRepository(), out var stores);

        await bindings.Single(item => item.Key == PosSettingKeys.NoSaleRescan).WriteAsync(true);
        var loaded = await stores.Receipt.LoadAsync();

        Assert.True(loaded.NoSaleCashDrawerRequiresRescan);
        Assert.False(loaded.ReturnsRequireRescan);
        Assert.True(loaded.PrintBankReceiptText);
    }

    [Fact]
    public async Task Payment_flags_are_written_as_one_configuration_and_notify_listeners()
    {
        var bindings = CreateBindings(new MemoryRepository(), out var stores);
        var raised = 0;
        stores.Payment.Changed += (_, _) => raised++;

        await bindings.Single(item => item.Key == PosSettingKeys.ManualCard).WriteAsync(true);
        await bindings.Single(item => item.Key == PosSettingKeys.OrderList).WriteAsync(false);

        // 三个开关是同一份配置：改其中两项后另一项（代金券）不受影响，付款页能通过 Changed 立即刷新。
        Assert.Equal(new PaymentMethodSettings(UseManualCard: true, VoucherEnabled: false, ShowOrderList: false), stores.Payment.Current);
        Assert.Equal(2, raised);
    }

    [Fact]
    public async Task Writing_the_value_that_is_already_there_does_not_touch_the_store()
    {
        var repository = new MemoryRepository();
        var bindings = CreateBindings(repository, out _);
        await ReadAllAsync(bindings);
        var writesBefore = repository.WriteCount;

        foreach (var binding in bindings)
        {
            await binding.WriteAsync(await binding.ReadAsync());
        }

        // 同步每分钟都可能「写一遍」：值没变就不能产生任何落盘，否则白白磨损磁盘并触发界面刷新。
        Assert.Equal(writesBefore, repository.WriteCount);
    }

    [Theory]
    [InlineData("True", true)]
    [InlineData("true", true)]
    [InlineData("False", false)]
    [InlineData("1", false)]
    [InlineData("garbage", false)]
    [InlineData("", false)]
    public async Task Cash_movement_reads_only_an_explicit_true_as_on(string stored, bool expected)
    {
        var repository = new MemoryRepository();
        await repository.SetValueAsync(CashMovementEnabledBinding.StorageKey, stored);
        var binding = new CashMovementEnabledBinding(repository);

        Assert.Equal(expected, await binding.ReadAsync());
    }

    private static async Task<Dictionary<string, bool>> ReadAllAsync(IReadOnlyList<IPosSettingBinding> bindings)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            result[binding.Key] = await binding.ReadAsync();
        }

        return result;
    }

    private static IReadOnlyList<IPosSettingBinding> CreateBindings(MemoryRepository repository, out Stores stores)
    {
        var receipt = new ReceiptPrinterSettingsStore(repository);
        var payment = new PaymentMethodSettingsService(repository);
        var cardTerminal = new CardTerminalSettingsStore(repository, new PassthroughProtector());
        var restart = new SystemRestartSettingsStore(repository);
        stores = new Stores(receipt, payment);
        return PosSettingBindings.Create(repository, receipt, payment, cardTerminal, restart);
    }

    private sealed record Stores(ReceiptPrinterSettingsStore Receipt, PaymentMethodSettingsService Payment);

    private sealed class PassthroughProtector : IDeviceAuthorizationProtector
    {
        public string? Protect(string? value) => value;

        public string? Unprotect(string? protectedValue) => protectedValue;
    }

    private sealed class MemoryRepository : ILocalAppSettingsRepository
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public int WriteCount { get; private set; }

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task SetValuesAsync(
            IReadOnlyDictionary<string, string> values,
            CancellationToken cancellationToken = default)
        {
            WriteCount++;
            foreach (var entry in values)
            {
                _values[entry.Key] = entry.Value;
            }

            return Task.CompletedTask;
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            _values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
