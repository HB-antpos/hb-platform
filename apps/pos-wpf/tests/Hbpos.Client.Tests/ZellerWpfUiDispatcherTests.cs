using System.Windows.Threading;
using Hbpos.Client.Wpf.Services.Zeller;

namespace Hbpos.Client.Tests;

/// <summary>Zeller 网关用的 WPF 界面线程调度器：SDK 的窗口和 WebView2 必须在界面线程上调用。</summary>
public sealed class ZellerWpfUiDispatcherTests
{
    [Fact]
    public async Task Calls_from_a_background_thread_run_on_the_ui_thread()
    {
        using var ui = UiThread.Start();
        var dispatcher = new WpfZellerUiDispatcher(ui.Dispatcher);

        var threadId = await Task.Run(() => dispatcher.InvokeAsync(() => Environment.CurrentManagedThreadId));

        Assert.Equal(ui.ThreadId, threadId);
    }

    [Fact]
    public async Task Action_overload_runs_on_the_ui_thread()
    {
        using var ui = UiThread.Start();
        var dispatcher = new WpfZellerUiDispatcher(ui.Dispatcher);
        var ranOn = 0;

        await Task.Run(() => dispatcher.InvokeAsync(() => ranOn = Environment.CurrentManagedThreadId));

        Assert.Equal(ui.ThreadId, ranOn);
    }

    [Fact]
    public async Task Exceptions_come_back_through_the_task()
    {
        using var ui = UiThread.Start();
        var dispatcher = new WpfZellerUiDispatcher(ui.Dispatcher);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task.Run(() => dispatcher.InvokeAsync<int>(() => throw new InvalidOperationException("no WebView2"))));

        Assert.Equal("no WebView2", ex.Message);
    }

    [Fact]
    public async Task Calls_already_on_the_ui_thread_run_inline()
    {
        using var ui = UiThread.Start();
        var dispatcher = new WpfZellerUiDispatcher(ui.Dispatcher);

        var completedInline = await ui.Dispatcher.InvokeAsync(() => dispatcher.InvokeAsync(() => 42).IsCompletedSuccessfully);
        var failedInline = await ui.Dispatcher.InvokeAsync(() =>
        {
            var task = dispatcher.InvokeAsync<int>(() => throw new IOException());
            _ = task.Exception; // 观察掉异常，避免终结器线程报未观察的任务异常。
            return task.IsFaulted;
        });

        Assert.True(completedInline);
        Assert.True(failedInline);
    }

    [Fact]
    public async Task Calls_after_the_dispatcher_shut_down_fail_instead_of_hanging()
    {
        var ui = UiThread.Start();
        var dispatcher = new WpfZellerUiDispatcher(ui.Dispatcher);
        ui.Dispose();

        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.InvokeAsync(() => 1));
    }

    /// <summary>一个跑着 WPF Dispatcher 的 STA 线程，相当于收银端的界面线程。</summary>
    private sealed class UiThread : IDisposable
    {
        private readonly Thread _thread;

        private UiThread(Thread thread, Dispatcher dispatcher)
        {
            _thread = thread;
            Dispatcher = dispatcher;
        }

        public Dispatcher Dispatcher { get; }

        public int ThreadId => _thread.ManagedThreadId;

        public static UiThread Start()
        {
            var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "Zeller UI dispatcher test"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return new UiThread(thread, ready.Task.GetAwaiter().GetResult());
        }

        public void Dispose()
        {
            Dispatcher.InvokeShutdown();
            _thread.Join(TimeSpan.FromSeconds(10));
        }
    }
}
