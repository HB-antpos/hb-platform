using System.Collections.Concurrent;

namespace Hbpos.Client.Tests;

/// <summary>
/// 模拟 WPF Dispatcher：在一条专用线程上安装单线程 SynchronizationContext，并从该线程启动被测异步操作，
/// 被测代码里未加 ConfigureAwait(false) 的 await 续体都会排回这条线程。
/// 用于断言"本地 SQLite 读写不在调用方（UI）线程上执行、结果仍回到调用方线程应用"。
/// </summary>
internal static class SingleThreadSynchronizationContextTestSupport
{
    /// <summary>当前线程是否正运行在本工具安装的"UI"同步上下文里。</summary>
    public static bool IsOnUiContext => SynchronizationContext.Current is QueueSynchronizationContext;

    public static async Task<(T Result, int UiThreadId)> RunAsync<T>(Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiThreadId = -1;
        var thread = new Thread(() =>
        {
            var context = new QueueSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            uiThreadId = Environment.CurrentManagedThreadId;
            try
            {
                context.Post(async _ =>
                {
                    try
                    {
                        completion.TrySetResult(await action());
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }
                    finally
                    {
                        context.Complete();
                    }
                }, null);
                context.RunOnCurrentThread();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);
            }
        })
        {
            IsBackground = true,
            Name = "test-ui-context"
        };

        thread.Start();
        var result = await completion.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        Assert.True(thread.Join(AsyncTestWaitSupport.DefaultTimeout));
        return (result, uiThreadId);
    }

    public static async Task<int> RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var (_, uiThreadId) = await RunAsync(async () =>
        {
            await action();
            return true;
        });
        return uiThreadId;
    }

    private sealed class QueueSynchronizationContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                _queue.Add((d, state));
            }
            catch (InvalidOperationException)
            {
                // 上下文已结束后才到达的续体（不应出现）交给线程池，避免在线程池上抛异常拖垮测试进程。
                ThreadPool.QueueUserWorkItem(_ => d(state));
            }
        }

        public override void Send(SendOrPostCallback d, object? state) =>
            throw new NotSupportedException("测试用 UI 上下文不支持同步 Send。");

        public void RunOnCurrentThread()
        {
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        }

        public void Complete() => _queue.CompleteAdding();
    }
}
