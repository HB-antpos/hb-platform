using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class PaymentMethodSettingsServiceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("broken")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"UseManualCard\":\"true\"}")]
    public async Task Missing_or_invalid_settings_keep_both_switches_off(string? value)
    {
        var service = new PaymentMethodSettingsService(new SettingsRepository { Value = value });
        Assert.Equal(new PaymentMethodSettings(), service.Current);
        Assert.Equal(new PaymentMethodSettings(), await service.LoadAsync());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Saved_settings_round_trip_in_a_new_service(bool manual, bool voucher)
    {
        var repository = new SettingsRepository();
        var service = new PaymentMethodSettingsService(repository);
        var notifications = 0;
        service.Changed += (_, _) => notifications++;
        var settings = new PaymentMethodSettings(manual, voucher);
        await service.SaveAsync(settings);
        await service.SaveAsync(settings);

        Assert.Equal(settings, service.Current);
        Assert.Equal(1, notifications);
        Assert.Equal(settings, await new PaymentMethodSettingsService(repository).LoadAsync());
        Assert.Equal(PaymentMethodSettingsService.SettingsKey, repository.LastWrittenKey);
    }

    [Fact]
    public void Order_list_is_shown_by_default()
    {
        // 付款页订单列表默认显示：record 默认值、服务初始值都必须是 true。
        Assert.True(new PaymentMethodSettings().ShowOrderList);
        Assert.True(new PaymentMethodSettings(UseManualCard: true).ShowOrderList);
        Assert.True(new PaymentMethodSettingsService(new SettingsRepository()).Current.ShowOrderList);
    }

    [Theory]
    [InlineData("{\"UseManualCard\":true,\"VoucherEnabled\":false}", true, false)]
    [InlineData("{\"UseManualCard\":false,\"VoucherEnabled\":true}", false, true)]
    [InlineData("{\"UseManualCard\":true,\"VoucherEnabled\":true}", true, true)]
    public async Task Legacy_json_without_order_list_field_loads_as_shown(string legacyJson, bool manual, bool voucher)
    {
        // 升级前保存的 JSON 没有 ShowOrderList；System.Text.Json 对缺失的构造参数取参数默认值（true），
        // 其余两个开关保持原值，升级不会让付款页订单列表突然消失。
        var service = new PaymentMethodSettingsService(new SettingsRepository { Value = legacyJson });
        var notifications = 0;
        service.Changed += (_, _) => notifications++;

        var loaded = await service.LoadAsync();

        Assert.True(loaded.ShowOrderList);
        Assert.Equal(manual, loaded.UseManualCard);
        Assert.Equal(voucher, loaded.VoucherEnabled);
        Assert.Equal(loaded, service.Current);
        // 三组数据里至少有一个旧开关为开，加载结果与初始默认值不同，会通知一次。
        Assert.Equal(1, notifications);
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"UseManualCard\":\"true\"}")]
    public async Task Missing_or_corrupt_json_falls_back_to_order_list_shown(string value)
    {
        var service = new PaymentMethodSettingsService(new SettingsRepository { Value = value });

        var loaded = await service.LoadAsync();

        Assert.True(loaded.ShowOrderList);
        Assert.True(service.Current.ShowOrderList);
    }

    [Fact]
    public async Task Saved_order_list_off_persists_reloads_and_notifies_once()
    {
        var repository = new SettingsRepository();
        var service = new PaymentMethodSettingsService(repository);
        var notifications = 0;
        service.Changed += (_, _) => notifications++;
        var hidden = new PaymentMethodSettings(UseManualCard: true, VoucherEnabled: false, ShowOrderList: false);

        await service.SaveAsync(hidden);

        Assert.False(service.Current.ShowOrderList);
        Assert.Equal(1, notifications);
        Assert.Contains("\"ShowOrderList\":false", repository.Value, StringComparison.Ordinal);

        // 重复保存同一份配置不再通知；重新创建服务（模拟重启）后读回仍是 false，其余开关不变。
        await service.SaveAsync(hidden);
        Assert.Equal(1, notifications);
        var reloaded = await new PaymentMethodSettingsService(repository).LoadAsync();
        Assert.False(reloaded.ShowOrderList);
        Assert.True(reloaded.UseManualCard);
        Assert.False(reloaded.VoucherEnabled);

        // 再改回显示：同样会保存并通知。
        await service.SaveAsync(hidden with { ShowOrderList = true });
        Assert.True(service.Current.ShowOrderList);
        Assert.Equal(2, notifications);
        Assert.True((await new PaymentMethodSettingsService(repository).LoadAsync()).ShowOrderList);
    }

    [Fact]
    public async Task Loading_saved_order_list_off_replaces_default_and_notifies()
    {
        var service = new PaymentMethodSettingsService(new SettingsRepository { Value = "{\"ShowOrderList\":false}" });
        var notifications = 0;
        service.Changed += (_, _) => notifications++;

        var loaded = await service.LoadAsync();

        Assert.False(loaded.ShowOrderList);
        Assert.False(service.Current.ShowOrderList);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task Failed_save_keeps_last_applied_settings_and_does_not_notify()
    {
        var repository = new SettingsRepository();
        var service = new PaymentMethodSettingsService(repository);
        await service.SaveAsync(new(true, false));
        var savedValue = repository.Value;
        var notifications = 0;
        service.Changed += (_, _) => notifications++;
        repository.FailSave = true;

        await Assert.ThrowsAsync<IOException>(() => service.SaveAsync(new(false, true)));

        Assert.Equal(new PaymentMethodSettings(true, false), service.Current);
        Assert.Equal(savedValue, repository.Value);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task Read_failure_propagates_without_replacing_applied_settings()
    {
        var repository = new SettingsRepository();
        var service = new PaymentMethodSettingsService(repository);
        await service.SaveAsync(new(true, true));
        repository.FailRead = true;
        await Assert.ThrowsAsync<IOException>(() => service.LoadAsync());
        Assert.Equal(new PaymentMethodSettings(true, true), service.Current);
    }

    [Fact]
    public async Task Concurrent_save_waits_for_load_and_remains_current()
    {
        var repository = new SettingsRepository { BlockRead = true };
        var service = new PaymentMethodSettingsService(repository);
        var load = service.LoadAsync();
        await repository.ReadStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        var save = service.SaveAsync(new(true, true));
        Assert.False(save.IsCompleted);
        repository.ReadRelease.SetResult();
        await load;
        await save;
        Assert.Equal(new PaymentMethodSettings(true, true), service.Current);
    }

    private sealed class SettingsRepository : ILocalAppSettingsRepository
    {
        public string? Value { get; set; }
        public string? LastWrittenKey { get; private set; }
        public bool FailSave { get; set; }
        public bool FailRead { get; set; }
        public bool BlockRead { get; init; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            if (FailRead) throw new IOException("read failed");
            var snapshot = Value;
            ReadStarted.TrySetResult();
            if (BlockRead) await ReadRelease.Task.WaitAsync(cancellationToken);
            return snapshot;
        }
        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailSave) throw new IOException("save failed");
            LastWrittenKey = key;
            Value = value;
            return Task.CompletedTask;
        }
        public Task SetValuesAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
