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
                // 客显广告本机默认播放：从未设置过就是开，升级后不能因为新增这一项而把广告关掉。
                [PosSettingKeys.CustomerDisplayAdvertisements] = true,
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
    [InlineData(PosSettingKeys.CustomerDisplayAdvertisements)]
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

    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("garbage", true)]
    [InlineData("", true)]
    public async Task Customer_display_advertisements_read_only_an_explicit_false_as_off(string? stored, bool expected)
    {
        var repository = new MemoryRepository();
        if (stored is not null)
        {
            await repository.SetValueAsync(CustomerDisplayWindowPreferenceStore.AdvertisementsEnabledKey, stored);
        }

        var binding = CreateBindings(repository, out _).Single(item => item.Key == PosSettingKeys.CustomerDisplayAdvertisements);

        Assert.Equal(expected, await binding.ReadAsync());
    }

    [Fact]
    public async Task Customer_display_advertisements_are_stored_in_the_window_preference_and_update_its_memory_value()
    {
        var repository = new MemoryRepository();
        var bindings = CreateBindings(repository, out var stores);
        var binding = bindings.Single(item => item.Key == PosSettingKeys.CustomerDisplayAdvertisements);

        await binding.WriteAsync(false);

        // 落在客显偏好自己的键里，窗口模式 / 位置键不受影响。
        Assert.Equal("false", repository.Values[CustomerDisplayWindowPreferenceStore.AdvertisementsEnabledKey]);
        Assert.False(repository.Values.ContainsKey(CustomerDisplayWindowPreferenceStore.ModeKey));
        // 编排器读的是偏好的内存值：下发写入后它必须立刻是新值，否则下一次刷新还会去请求广告接口。
        Assert.False(stores.CustomerDisplay.Current.AdvertisementsEnabled);
        Assert.False(await binding.ReadAsync());

        await binding.WriteAsync(true);
        Assert.True(stores.CustomerDisplay.Current.AdvertisementsEnabled);
        Assert.True((await new CustomerDisplayWindowPreferenceStore(repository).LoadAsync()).AdvertisementsEnabled);
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
        var customerDisplay = new CustomerDisplayWindowPreferenceStore(repository);
        stores = new Stores(receipt, payment, customerDisplay);
        return PosSettingBindings.Create(repository, receipt, payment, cardTerminal, restart, customerDisplay);
    }

    private sealed record Stores(
        ReceiptPrinterSettingsStore Receipt,
        PaymentMethodSettingsService Payment,
        CustomerDisplayWindowPreferenceStore CustomerDisplay);

    private sealed class PassthroughProtector : IDeviceAuthorizationProtector
    {
        public string? Protect(string? value) => value;

        public string? Unprotect(string? protectedValue) => protectedValue;
    }

    private sealed class MemoryRepository : ILocalAppSettingsRepository
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public int WriteCount { get; private set; }

        public IReadOnlyDictionary<string, string> Values => _values;

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
