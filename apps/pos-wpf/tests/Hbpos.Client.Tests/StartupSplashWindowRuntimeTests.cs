using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Hbpos.Client.Wpf;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class StartupSplashWindowRuntimeTests
{
    [Fact]
    public Task Splash_window_renders_bound_text_progress_and_step_names()
    {
        return RunOnStaDispatcherAsync(() =>
        {
            EnsureWpfResourceAssembly();
            Assert.True(LocalizationService.TryGetSupportedCulture("zh-CN", out var culture));
            var state = new StartupProgressState(key => LocalizationService.Translate(key, culture), culture, "Morayfield · POS-03");
            state.SetVersion("1.9.0", new AppLaunchVersionNotice("1.9.0", false));
            for (var frame = 0; frame < 60; frame++)
            {
                state.Apply(
                    new StartupProgressSnapshot(0.5, StartupPhase.Device, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1), false),
                    TimeSpan.FromMilliseconds(33));
            }

            var window = new StartupSplashWindow(state);
            try
            {
                var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                content.Measure(new Size(window.Width, window.Height));
                content.Arrange(new Rect(0, 0, window.Width, window.Height));
                content.UpdateLayout();

                // 回归：窗口改了 InheritanceBehavior 后若事后才设 DataContext，文字和进度条全部为空，只剩空卡片。
                Assert.Equal("HB POS", TextOf(content, "StartupTitle"));
                Assert.Equal("Morayfield · POS-03", TextOf(content, "StartupSubtitle"));
                Assert.Equal("正在验证设备", TextOf(content, "StartupStageTitle"));
                Assert.Equal("确认本机收银授权", TextOf(content, "StartupStageDetail"));
                Assert.Equal("1.9.0", TextOf(content, "StartupVersionText"));
                Assert.StartsWith("已用", TextOf(content, "StartupElapsed"), StringComparison.Ordinal);
                Assert.Equal("50%", TextOf(content, "StartupPercent"));
                Assert.Equal(50, Find<ProgressBar>(content, "StartupProgressBar").Value, precision: 6);
                Assert.Equal(Visibility.Visible, Find<FrameworkElement>(content, "StartupUpdateNotice").Visibility);
                Assert.Equal(
                    new[] { "启动服务", "加载界面", "检查更新", "验证设备", "加载商品", "打开收银台" },
                    Descendants<TextBlock>(Find<ItemsControl>(content, "StartupStepList")).Select(text => text.Text).ToArray());
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static string TextOf(DependencyObject root, string automationId)
    {
        var text = Find<TextBlock>(root, automationId);
        return text.Inlines.Count > 0 && string.IsNullOrEmpty(text.Text)
            ? string.Concat(text.Inlines.OfType<Run>().Select(run => run.Text))
            : text.Text;
    }

    private static T Find<T>(DependencyObject root, string automationId)
        where T : DependencyObject
    {
        return Assert.Single(Descendants<T>(root), element =>
            string.Equals(AutomationProperties.GetAutomationId(element), automationId, StringComparison.Ordinal));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// 测试进程的入口程序集是 testhost，pack://application 会去那里找图标；改指向 WPF 客户端程序集，与真实运行一致。
    /// </summary>
    private static void EnsureWpfResourceAssembly()
    {
        var wpfAssembly = typeof(StartupSplashWindow).Assembly;
        var field = typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(null, wpfAssembly);
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
    }

    private static async Task RunOnStaDispatcherAsync(Action action)
    {
        var dispatcherReady = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                dispatcherReady.TrySetResult(dispatcher);
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                dispatcherReady.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Hbpos.Client.Tests.StartupSplashWindowDispatcher"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var dispatcher = await dispatcherReady.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        try
        {
            await dispatcher.InvokeAsync(action, DispatcherPriority.Normal).Task;
        }
        finally
        {
            if (!dispatcher.HasShutdownStarted)
            {
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }

            Assert.True(thread.Join(AsyncTestWaitSupport.DefaultTimeout), "WPF Dispatcher thread did not shut down.");
        }
    }
}
