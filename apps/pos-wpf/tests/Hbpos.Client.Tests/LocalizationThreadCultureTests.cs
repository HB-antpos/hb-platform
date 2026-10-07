using System.Globalization;
using Hbpos.Client.Wpf.Localization;

namespace Hbpos.Client.Tests;

[Collection(CultureSensitiveTestCollection.Name)]
public sealed class LocalizationThreadCultureTests
{
    [Fact]
    public void Culture_switched_inside_async_method_is_reapplied_after_the_ui_callback()
    {
        using var scope = new CultureScope();
        var previousContext = SynchronizationContext.Current;
        var uiContext = new QueuedSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(uiContext);
        try
        {
            // 模拟中文启动：启动恢复语言发生在 await 之后，区域性留在了界面线程上。
            var localization = new LocalizationService();
            localization.SetCulture(LocalizationService.ChineseCultureName);
            uiContext.RunPending();
            Assert.Equal(LocalizationService.ChineseCultureName, CultureInfo.CurrentCulture.Name);

            // 与 ShellCultureService.ApplyAsync 相同：在 async 方法的同步段里切到英文。
            _ = SwitchInsideAsyncMethod(localization, LocalizationService.DefaultCultureName);

            // 根因：CurrentCulture 存在 AsyncLocal 里，async 方法返回时被还原，界面线程仍是中文。
            Assert.Equal(LocalizationService.ChineseCultureName, CultureInfo.CurrentCulture.Name);

            // 修复：SetCulture 投递到界面线程同步上下文的回调把英文重新写回线程。
            uiContext.RunPending();
            Assert.Equal(LocalizationService.DefaultCultureName, CultureInfo.CurrentCulture.Name);
            Assert.Equal(LocalizationService.DefaultCultureName, CultureInfo.CurrentUICulture.Name);
            Assert.Equal("$", CultureInfo.CurrentCulture.NumberFormat.CurrencySymbol);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [Fact]
    public void Reapplied_culture_uses_the_latest_selection_when_switched_twice()
    {
        using var scope = new CultureScope();
        var previousContext = SynchronizationContext.Current;
        var uiContext = new QueuedSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(uiContext);
        try
        {
            var localization = new LocalizationService();
            _ = SwitchInsideAsyncMethod(localization, LocalizationService.ChineseCultureName);
            _ = SwitchInsideAsyncMethod(localization, LocalizationService.DefaultCultureName);

            // 两次投递按顺序执行，最终都以最新选择为准，不会被先投递的旧语言覆盖。
            uiContext.RunPending();
            Assert.Equal(LocalizationService.DefaultCultureName, CultureInfo.CurrentCulture.Name);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static async Task SwitchInsideAsyncMethod(LocalizationService localization, string cultureName)
    {
        localization.SetCulture(cultureName);
        await Task.CompletedTask;
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _pending = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            _pending.Enqueue((d, state));
        }

        public void RunPending()
        {
            while (_pending.TryDequeue(out var item))
            {
                item.Callback(item.State);
            }
        }
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _originalUiCulture = CultureInfo.CurrentUICulture;
        private readonly CultureInfo? _originalDefaultCulture = CultureInfo.DefaultThreadCurrentCulture;
        private readonly CultureInfo? _originalDefaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _originalCulture;
            CultureInfo.CurrentUICulture = _originalUiCulture;
            CultureInfo.DefaultThreadCurrentCulture = _originalDefaultCulture;
            CultureInfo.DefaultThreadCurrentUICulture = _originalDefaultUiCulture;
        }
    }
}
