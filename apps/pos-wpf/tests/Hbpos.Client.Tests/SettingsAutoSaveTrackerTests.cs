using Hbpos.Client.Wpf.ViewModels;
using Xunit;

namespace Hbpos.Client.Tests;

/// <summary>设置页自动保存状态机：防抖、保存中再改要补存、失败重试、草稿、离开前冲刷。</summary>
public sealed class SettingsAutoSaveTrackerTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Repeated_changes_within_debounce_save_once_after_the_delay()
    {
        var delay = new ManualDelay();
        var tracker = new SettingsAutoSaveTracker(delay.DelayAsync);
        var saves = 0;
        tracker.Register("receipt", SettingsCategory.ReceiptPrinter, "label", _ =>
        {
            saves++;
            return Task.FromResult(SettingsSaveResult.Success);
        });

        tracker.MarkChanged("receipt", Debounce);
        tracker.MarkChanged("receipt", Debounce);

        Assert.Equal(SettingsSaveState.Pending, tracker.GetState("receipt"));
        Assert.Equal(0, saves);
        // 第一次计时已被第二次修改取消，只剩一个有效计时。
        await delay.ReleaseAllAsync();
        await WaitForStateAsync(tracker, "receipt", SettingsSaveState.Saved);

        Assert.Equal(1, saves);
        Assert.Empty(tracker.Unsaved());
    }

    [Fact]
    public async Task Zero_debounce_saves_immediately()
    {
        var tracker = new SettingsAutoSaveTracker(new ManualDelay().DelayAsync);
        var saves = 0;
        tracker.Register("methods", SettingsCategory.PaymentTerminal, "label", _ =>
        {
            saves++;
            return Task.FromResult(SettingsSaveResult.Success);
        });

        tracker.MarkChanged("methods", TimeSpan.Zero);
        await WaitForStateAsync(tracker, "methods", SettingsSaveState.Saved);

        Assert.Equal(1, saves);
    }

    [Fact]
    public async Task Failed_save_is_reported_and_retry_clears_it()
    {
        var tracker = new SettingsAutoSaveTracker(new ManualDelay().DelayAsync);
        var fail = true;
        tracker.Register("receipt", SettingsCategory.ReceiptPrinter, "receipt.label", _ =>
            Task.FromResult(fail ? SettingsSaveResult.Failure("disk full") : SettingsSaveResult.Success));

        tracker.MarkChanged("receipt", TimeSpan.Zero);
        await WaitForStateAsync(tracker, "receipt", SettingsSaveState.Failed);

        var unsaved = Assert.Single(tracker.Unsaved());
        Assert.Equal("receipt", unsaved.Key);
        Assert.Equal(SettingsCategory.ReceiptPrinter, unsaved.Category);
        Assert.Equal("receipt.label", unsaved.LabelKey);
        Assert.Equal("disk full", unsaved.Message);

        fail = false;
        Assert.True(await tracker.RetryFailedAsync());
        Assert.Equal(SettingsSaveState.Saved, tracker.GetState("receipt"));
        Assert.Empty(tracker.Unsaved());
    }

    [Fact]
    public async Task Exception_from_save_counts_as_failure()
    {
        var tracker = new SettingsAutoSaveTracker(new ManualDelay().DelayAsync);
        tracker.Register("receipt", SettingsCategory.ReceiptPrinter, "label", _ =>
            throw new InvalidOperationException("boom"));

        tracker.MarkChanged("receipt", TimeSpan.Zero);
        await WaitForStateAsync(tracker, "receipt", SettingsSaveState.Failed);

        Assert.Equal("boom", tracker.GetMessage("receipt"));
    }

    [Fact]
    public async Task Change_while_saving_triggers_one_more_save_with_latest_content()
    {
        var tracker = new SettingsAutoSaveTracker(new ManualDelay().DelayAsync);
        var firstSave = new TaskCompletionSource<SettingsSaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var saves = 0;
        tracker.Register("receipt", SettingsCategory.ReceiptPrinter, "label", _ =>
        {
            saves++;
            return saves == 1 ? firstSave.Task : Task.FromResult(SettingsSaveResult.Success);
        });

        tracker.MarkChanged("receipt", TimeSpan.Zero);
        await WaitForStateAsync(tracker, "receipt", SettingsSaveState.Saving);
        tracker.MarkChanged("receipt", Debounce);
        Assert.Equal(SettingsSaveState.Saving, tracker.GetState("receipt"));

        // 第一次写完（即使成功）也不能停在「已保存」：写入期间的修改还没落盘。
        firstSave.SetResult(SettingsSaveResult.Success);
        await WaitForStateAsync(tracker, "receipt", SettingsSaveState.Saved);

        Assert.Equal(2, saves);
    }

    [Fact]
    public async Task Explicit_section_becomes_draft_and_is_cleared_by_mark_saved()
    {
        var tracker = new SettingsAutoSaveTracker(new ManualDelay().DelayAsync);
        tracker.RegisterExplicit("linkly", SettingsCategory.PaymentTerminal, "linkly.label");

        tracker.MarkChanged("linkly", Debounce);

        Assert.Equal(SettingsSaveState.Draft, tracker.GetState("linkly"));
        Assert.Equal("linkly", Assert.Single(tracker.Unsaved()).Key);
        Assert.False(tracker.IsBusy);

        tracker.MarkSaved("linkly");

        Assert.Equal(SettingsSaveState.Saved, tracker.GetState("linkly"));
        Assert.Empty(tracker.Unsaved());
        // 草稿没有保存委托：重试不会改变它。
        Assert.True(await tracker.RetryFailedAsync());
    }

    [Fact]
    public async Task Flush_runs_pending_saves_without_waiting_for_the_debounce()
    {
        var delay = new ManualDelay();
        var tracker = new SettingsAutoSaveTracker(delay.DelayAsync);
        var saves = 0;
        tracker.Register("receipt", SettingsCategory.ReceiptPrinter, "label", _ =>
        {
            saves++;
            return Task.FromResult(SettingsSaveResult.Success);
        });

        tracker.MarkChanged("receipt", Debounce);
        Assert.True(await tracker.FlushAsync(Wait));

        Assert.Equal(1, saves);
        Assert.Equal(SettingsSaveState.Saved, tracker.GetState("receipt"));
        // 被冲刷取消的计时之后再放行，也不能多存一次。
        await delay.ReleaseAllAsync();
        await Task.Delay(50);
        Assert.Equal(1, saves);
    }

    [Fact]
    public async Task Flush_gives_up_after_the_timeout_when_a_save_hangs()
    {
        var tracker = new SettingsAutoSaveTracker(new ManualDelay().DelayAsync);
        var hang = new TaskCompletionSource<SettingsSaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.Register("receipt", SettingsCategory.ReceiptPrinter, "label", _ => hang.Task);

        tracker.MarkChanged("receipt", TimeSpan.Zero);

        Assert.False(await tracker.FlushAsync(TimeSpan.FromMilliseconds(100)));
        Assert.True(tracker.IsBusy);
        hang.SetResult(SettingsSaveResult.Success);
        await WaitForStateAsync(tracker, "receipt", SettingsSaveState.Saved);
    }

    [Fact]
    public async Task Mark_saved_cancels_a_pending_debounced_save()
    {
        var delay = new ManualDelay();
        var tracker = new SettingsAutoSaveTracker(delay.DelayAsync);
        var saves = 0;
        tracker.Register("receipt", SettingsCategory.ReceiptPrinter, "label", _ =>
        {
            saves++;
            return Task.FromResult(SettingsSaveResult.Success);
        });

        tracker.MarkChanged("receipt", Debounce);
        tracker.MarkSaved("receipt");
        await delay.ReleaseAllAsync();
        await Task.Delay(50);

        Assert.Equal(0, saves);
        Assert.Equal(SettingsSaveState.Saved, tracker.GetState("receipt"));
    }

    [Fact]
    public async Task Reset_all_clears_failed_and_draft_sections()
    {
        var tracker = new SettingsAutoSaveTracker(new ManualDelay().DelayAsync);
        tracker.Register("receipt", SettingsCategory.ReceiptPrinter, "label", _ =>
            Task.FromResult(SettingsSaveResult.Failure("x")));
        tracker.RegisterExplicit("server", SettingsCategory.DeviceRegistration, "label");

        tracker.MarkChanged("receipt", TimeSpan.Zero);
        tracker.MarkChanged("server", TimeSpan.Zero);
        await WaitForStateAsync(tracker, "receipt", SettingsSaveState.Failed);
        Assert.Equal(2, tracker.Unsaved().Count);

        tracker.ResetAll();

        Assert.Empty(tracker.Unsaved());
    }

    [Fact]
    public void Changed_event_fires_on_state_changes()
    {
        var tracker = new SettingsAutoSaveTracker(new ManualDelay().DelayAsync);
        tracker.RegisterExplicit("server", SettingsCategory.DeviceRegistration, "label");
        var raised = 0;
        tracker.Changed += (_, _) => raised++;

        tracker.MarkChanged("server", TimeSpan.Zero);
        tracker.MarkSaved("server");

        Assert.Equal(2, raised);
    }

    [Fact]
    public void Unknown_keys_are_ignored()
    {
        var tracker = new SettingsAutoSaveTracker(new ManualDelay().DelayAsync);

        tracker.MarkChanged("nope", Debounce);
        tracker.MarkSaved("nope");

        Assert.Equal(SettingsSaveState.Saved, tracker.GetState("nope"));
        Assert.Empty(tracker.Snapshot());
    }

    // 共享等待工具（AsyncTestWaitSupport.WaitUntilAsync，30 秒预算），失败时带出当前状态。
    private static Task WaitForStateAsync(SettingsAutoSaveTracker tracker, string key, SettingsSaveState expected) =>
        WaitUntilAsync(() => tracker.GetState(key) == expected, diagnostics: () => $"{key}={tracker.GetState(key)}");

    /// <summary>可控的假延时：测试主动放行，不依赖真实时间。</summary>
    private sealed class ManualDelay
    {
        private readonly List<(TaskCompletionSource Source, CancellationToken Token)> _pending = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
            lock (_pending)
            {
                _pending.Add((source, cancellationToken));
            }

            return source.Task;
        }

        public async Task ReleaseAllAsync()
        {
            List<(TaskCompletionSource Source, CancellationToken Token)> pending;
            lock (_pending)
            {
                pending = [.. _pending];
                _pending.Clear();
            }

            foreach (var (source, _) in pending)
            {
                source.TrySetResult();
            }

            await Task.Delay(20);
        }
    }
}
