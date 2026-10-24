using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Tests;

/// <summary>设置页「扫码枪灵敏度」（Raw Input 重复读码过滤窗口）：存储、扫码服务接线、设置页 VM 与界面契约。</summary>
public sealed partial class SettingsViewModelTests
{
    // ---------- 存储 ----------

    [Fact]
    public async Task ScannerSettingsStore_defaults_to_600_and_persists_changes_within_range()
    {
        var settings = new InMemoryScannerSettings();
        var store = new ScannerSettingsStore(settings);
        Assert.Equal(600, store.RepeatWindowMs);

        await store.LoadAsync();
        Assert.Equal(600, store.RepeatWindowMs);

        await store.SetRepeatWindowMsAsync(300);
        Assert.Equal(300, store.RepeatWindowMs);
        Assert.Equal("300", settings.Values[ScannerSettingsStore.RepeatWindowKey]);

        // 重启后从本机读回。
        var reloaded = new ScannerSettingsStore(settings);
        await reloaded.LoadAsync();
        Assert.Equal(300, reloaded.RepeatWindowMs);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(99, 100)]
    [InlineData(1001, 1000)]
    [InlineData(5000, 1000)]
    public async Task ScannerSettingsStore_clamps_saved_values_into_100_to_1000(int requested, int expected)
    {
        var store = new ScannerSettingsStore(new InMemoryScannerSettings());

        await store.SetRepeatWindowMsAsync(requested);

        Assert.Equal(expected, store.RepeatWindowMs);
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("99")]
    [InlineData("1001")]
    [InlineData("-5")]
    public async Task ScannerSettingsStore_falls_back_to_default_for_garbage_or_out_of_range_stored_value(string stored)
    {
        var settings = new InMemoryScannerSettings();
        settings.Values[ScannerSettingsStore.RepeatWindowKey] = stored;
        var store = new ScannerSettingsStore(settings);

        await store.LoadAsync();

        Assert.Equal(ScannerSettingsStore.DefaultRepeatWindowMs, store.RepeatWindowMs);
    }

    [Fact]
    public async Task ScannerSettingsStore_keeps_the_effective_value_when_saving_fails()
    {
        var settings = new InMemoryScannerSettings();
        var store = new ScannerSettingsStore(settings);
        await store.SetRepeatWindowMsAsync(400);
        settings.FailWrites = true;

        await Assert.ThrowsAsync<IOException>(() => store.SetRepeatWindowMsAsync(900));

        Assert.Equal(400, store.RepeatWindowMs);
    }

    // ---------- 守卫默认窗口 ----------

    [Fact]
    public void ScannerInputDuplicateGuard_default_window_is_600ms()
    {
        var guard = new ScannerInputDuplicateGuard();
        var now = DateTimeOffset.UtcNow;

        Assert.True(guard.TryAccept("ABC123", "raw", now));
        Assert.False(guard.TryAccept("ABC123", "raw", now.AddMilliseconds(600)));
        Assert.True(guard.TryAccept("ABC123", "raw", now.AddMilliseconds(1300)));
    }

    // ---------- 设置页 VM ----------

    [Fact]
    public async Task Scanner_setting_defaults_to_600_loads_stored_value_and_does_not_write_while_loading()
    {
        var store = new FakeScannerSettingsStore { Stored = 300 };
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            scannerSettingsStore: store);
        Assert.True(viewModel.IsScannerSettingVisible);
        Assert.Equal(600, viewModel.ScannerRepeatWindowMs);
        Assert.Equal(100, viewModel.ScannerRepeatWindowMinMs);
        Assert.Equal(1000, viewModel.ScannerRepeatWindowMaxMs);
        Assert.Equal(100, viewModel.ScannerRepeatWindowStepMs);

        await viewModel.LoadAsync();

        Assert.Equal(300, viewModel.ScannerRepeatWindowMs);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public void Scanner_setting_card_is_hidden_without_a_store()
    {
        using var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService());

        Assert.False(viewModel.IsScannerSettingVisible);
    }

    [Fact]
    public async Task Scanner_plus_minus_buttons_step_by_100_and_stop_at_the_bounds()
    {
        var store = new FakeScannerSettingsStore();
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            scannerSettingsStore: store);
        await viewModel.LoadAsync();

        viewModel.IncreaseScannerRepeatWindowCommand.Execute(null);
        Assert.Equal(700, viewModel.ScannerRepeatWindowMs);
        viewModel.DecreaseScannerRepeatWindowCommand.Execute(null);
        viewModel.DecreaseScannerRepeatWindowCommand.Execute(null);
        Assert.Equal(500, viewModel.ScannerRepeatWindowMs);

        for (var i = 0; i < 10; i++)
        {
            viewModel.IncreaseScannerRepeatWindowCommand.Execute(null);
        }

        Assert.Equal(1000, viewModel.ScannerRepeatWindowMs);
        Assert.False(viewModel.IncreaseScannerRepeatWindowCommand.CanExecute(null));
        Assert.True(viewModel.DecreaseScannerRepeatWindowCommand.CanExecute(null));

        for (var i = 0; i < 10; i++)
        {
            viewModel.DecreaseScannerRepeatWindowCommand.Execute(null);
        }

        Assert.Equal(100, viewModel.ScannerRepeatWindowMs);
        Assert.False(viewModel.DecreaseScannerRepeatWindowCommand.CanExecute(null));
        Assert.True(viewModel.IncreaseScannerRepeatWindowCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(640, 600)]
    [InlineData(660, 700)]
    [InlineData(0, 100)]
    [InlineData(5000, 1000)]
    public void Scanner_slider_values_snap_to_the_100ms_steps_inside_the_range(int dragged, int expected)
    {
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            scannerSettingsStore: new FakeScannerSettingsStore());

        viewModel.ScannerRepeatWindowMs = dragged;

        Assert.Equal(expected, viewModel.ScannerRepeatWindowMs);
    }

    [Fact]
    public async Task Scanner_change_is_saved_immediately_and_shows_the_value_text()
    {
        var store = new FakeScannerSettingsStore();
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            scannerSettingsStore: store);
        await viewModel.LoadAsync();

        viewModel.ScannerRepeatWindowMs = 800;

        await AsyncTestWaitSupport.WaitUntilAsync(() => store.SaveCalls == 1);
        Assert.Equal(800, store.Stored);
        Assert.Contains("800", viewModel.ScannerRepeatWindowText);
    }

    [Fact]
    public async Task Scanner_autosave_goes_through_the_tracker_and_reports_failures()
    {
        var store = new FakeScannerSettingsStore();
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            scannerSettingsStore: store,
            autoSaveSettings: true,
            autoSaveDelay: NoDelay);
        await viewModel.LoadAsync();

        viewModel.ScannerRepeatWindowMs = 400;
        await AsyncTestWaitSupport.WaitUntilAsync(() => viewModel.ScannerSaveState == "Saved");
        Assert.Equal(400, store.Stored);
        Assert.False(viewModel.HasUnsavedSettings);

        store.Fail = true;
        viewModel.ScannerRepeatWindowMs = 900;
        await AsyncTestWaitSupport.WaitUntilAsync(() => viewModel.ScannerSaveState == "Failed");
        Assert.Equal(1, viewModel.DataMaintenanceUnsavedCount);
        Assert.Equal(400, store.Stored);
    }

    // ---------- 界面契约 ----------

    [Fact]
    public void Settings_page_has_scanner_slider_with_plus_minus_buttons_and_both_languages_have_the_strings()
    {
        var wpfRoot = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ScannerSourceFilePath())!, "..", "..", "src", "Hbpos.Client.Wpf"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var view = XDocument.Load(Path.Combine(wpfRoot, "Views", "Screens", "SettingsView.xaml"));

        var slider = Assert.Single(view.Descendants(presentation + "Slider").Where(element =>
            (string?)element.Attribute(x + "Name") == "ScannerRepeatWindowSlider"));
        Assert.Equal("{Binding ScannerRepeatWindowMs, Mode=TwoWay}", (string?)slider.Attribute("Value"));
        Assert.Equal("True", (string?)slider.Attribute("IsSnapToTickEnabled"));
        Assert.Contains("ScannerRepeatWindowStepMs", (string?)slider.Attribute("TickFrequency"));
        Assert.True(double.Parse((string)slider.Attribute("MinHeight")!) >= 44);

        foreach (var (name, command) in new[]
                 {
                     ("ScannerRepeatWindowDecreaseButton", "{Binding DecreaseScannerRepeatWindowCommand}"),
                     ("ScannerRepeatWindowIncreaseButton", "{Binding IncreaseScannerRepeatWindowCommand}")
                 })
        {
            var button = Assert.Single(view.Descendants(presentation + "Button").Where(element =>
                (string?)element.Attribute(x + "Name") == name));
            Assert.Equal(command, (string?)button.Attribute("Command"));
            // 触屏最小高度。
            Assert.True(double.Parse((string)button.Attribute("MinHeight")!) >= 44);
        }

        // 整张卡跟随 IsScannerSettingVisible，且放在数据维护页（与重启提醒同页）。
        var card = slider.Ancestors(presentation + "Border")
            .First(border => ((string?)border.Attribute("Visibility") ?? string.Empty).Contains("IsScannerSettingVisible", StringComparison.Ordinal));
        Assert.NotNull(card);

        var keys = new[]
        {
            "settings.scanner.title", "settings.scanner.description", "settings.scanner.help",
            "settings.scanner.valueFormat", "settings.scanner.decrease", "settings.scanner.increase"
        };
        foreach (var file in new[] { "Strings.resx", "Strings.zh-CN.resx" })
        {
            var resx = XDocument.Load(Path.Combine(wpfRoot, "Resources", file));
            foreach (var key in keys)
            {
                Assert.Contains(resx.Descendants("data"), element => (string?)element.Attribute("name") == key);
            }
        }

        foreach (var file in new[] { "SettingsStrings.resx", "SettingsStrings.zh-CN.resx" })
        {
            var resx = XDocument.Load(Path.Combine(wpfRoot, "Resources", file));
            Assert.Contains(resx.Descendants("data"), element => (string?)element.Attribute("name") == "settings.autosave.section.scanner");
        }
    }

    private static string ScannerSourceFilePath([CallerFilePath] string path = "") => path;

    private sealed class FakeScannerSettingsStore : IScannerSettingsStore
    {
        public int Stored { get; set; } = ScannerSettingsStore.DefaultRepeatWindowMs;

        public int SaveCalls { get; private set; }

        public bool Fail { get; set; }

        public int RepeatWindowMs => Stored;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetRepeatWindowMsAsync(int milliseconds, CancellationToken cancellationToken = default)
        {
            if (Fail)
            {
                throw new IOException("disk is locked");
            }

            Stored = milliseconds;
            SaveCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryScannerSettings : ILocalAppSettingsRepository
    {
        public Dictionary<string, string> Values { get; } = [];

        public bool FailWrites { get; set; }

        public bool FailReads { get; set; }

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            if (FailReads)
            {
                throw new IOException("disk is locked");
            }

            return Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            if (FailWrites)
            {
                throw new IOException("disk is locked");
            }

            Values[key] = value;
            return Task.CompletedTask;
        }

        public Task SetValuesAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default)
        {
            foreach (var pair in values)
            {
                Values[pair.Key] = pair.Value;
            }

            return Task.CompletedTask;
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            Values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
