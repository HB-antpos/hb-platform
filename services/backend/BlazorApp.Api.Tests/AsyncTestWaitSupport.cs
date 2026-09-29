using System.Diagnostics;
using System.Runtime.CompilerServices;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 后端测试项目共享的异步等待工具，判断标准与 POS 客户端测试的
/// <c>apps/pos-wpf/tests/Hbpos.Client.Tests/AsyncTestWaitSupport.cs</c> 保持一致。
/// 通过 csproj 中的 <c>global using static</c> 引入；各测试文件不要再复制私有的轮询副本，
/// 也不要各自写一个几百毫秒到几秒的"防挂死"超时，否则 CI runner 负载抖动时会误报
/// （历史上 <c>WaitAsync(TimeSpan.FromSeconds(2))</c> 曾在 Linux runner 上偶发超时）。
/// </summary>
/// <remarks>
/// 等待对象应当是"已被触发、只差线程调度"的异步工作，而不是生产代码的真实超时。
/// 如果测试需要让生产代码"超时"，应向生产代码注入 <see cref="TimeProvider"/>
/// 或超时参数直接推进虚拟时间，而不是靠这里的墙钟预算去硬等。
/// </remarks>
internal static class AsyncTestWaitSupport
{
    /// <summary>
    /// 默认等待预算。条件满足时立即返回，所以放宽预算不会拖慢通过的测试；
    /// 它只决定 CI runner 负载抖动（线程池饥饿、SQLite 冷启动、忙等锁）时的容忍上限。
    /// </summary>
    /// <remarks>
    /// 也用于 <c>task.WaitAsync(...)</c>、<c>ManualResetEventSlim.Wait(...)</c>、测试 SQLite 连接的
    /// <c>Default Timeout</c> 这类"防挂死"的保险等待——判断标准是这个超时的用途，而不是它原来设了几秒：
    /// 只要被等待的工作已被触发、失败时由其它断言或测试一直不放开的闸门兜底，就应使用本预算。
    /// 被测行为自身的时间参数——传给生产代码的超时、业务等待预算、耗时上限断言、虚拟时钟推进，
    /// 以及"在短时间内不应完成"的反向等待——保持各自原值，不要改用本常量。
    /// </remarks>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>轮询间隔。</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// 轮询直到 <paramref name="condition"/> 为真；超出预算则以断言失败，
    /// 并把条件表达式原文与可选的诊断信息一起输出，方便直接从 CI 日志定位。
    /// </summary>
    /// <param name="condition">被等待的条件。</param>
    /// <param name="timeout">等待预算，缺省为 <see cref="DefaultTimeout"/>。</param>
    /// <param name="diagnostics">失败时追加到断言消息的诊断内容。</param>
    /// <param name="conditionExpression">由编译器填入的条件表达式原文，调用方不需要传。</param>
    public static Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan? timeout = null,
        Func<string>? diagnostics = null,
        [CallerArgumentExpression(nameof(condition))] string? conditionExpression = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return WaitUntilAsync(() => Task.FromResult(condition()), timeout, diagnostics, conditionExpression);
    }

    /// <summary>
    /// <see cref="WaitUntilAsync(Func{bool}, TimeSpan?, Func{string}?, string?)"/> 的异步条件版本，
    /// 用于条件本身需要 I/O（例如读 SQLite）的场景。
    /// </summary>
    public static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan? timeout = null,
        Func<string>? diagnostics = null,
        [CallerArgumentExpression(nameof(condition))] string? conditionExpression = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        await WaitForValueAsync(
            condition,
            satisfied => satisfied,
            timeout,
            diagnostics is null ? null : _ => diagnostics(),
            conditionExpression);
    }

    /// <summary>
    /// 反复调用 <paramref name="read"/>，直到读到的值满足 <paramref name="isSatisfied"/> 并返回该值；
    /// 用于轮询后台 job 状态这类"读一次、判断一次"的场景。超出预算则以断言失败。
    /// </summary>
    /// <param name="read">读取当前值。</param>
    /// <param name="isSatisfied">判断读到的值是否已满足。</param>
    /// <param name="timeout">等待预算，缺省为 <see cref="DefaultTimeout"/>。</param>
    /// <param name="describeLast">失败时描述最后一次读到的值，例如当前 job 状态。</param>
    /// <param name="conditionExpression">由编译器填入的条件表达式原文，调用方不需要传。</param>
    public static async Task<T> WaitForValueAsync<T>(
        Func<Task<T>> read,
        Func<T, bool> isSatisfied,
        TimeSpan? timeout = null,
        Func<T, string>? describeLast = null,
        [CallerArgumentExpression(nameof(isSatisfied))] string? conditionExpression = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(isSatisfied);

        var budget = timeout ?? DefaultTimeout;
        // 使用 Stopwatch 而非 DateTime.UtcNow：单调时钟不受系统时间回拨影响。
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            // 先读再看预算：预算耗尽后的最后一次读取仍有机会命中，不会因最后一轮 Delay 误报。
            var value = await read();
            if (isSatisfied(value))
            {
                return value;
            }

            if (stopwatch.Elapsed >= budget)
            {
                throw new Xunit.Sdk.XunitException(BuildTimeoutMessage(
                    "等待条件",
                    conditionExpression,
                    budget,
                    describeLast is null ? null : () => describeLast(value)));
            }

            await Task.Delay(PollInterval);
        }
    }

    /// <summary>
    /// <see cref="WaitUntilAsync(Func{bool}, TimeSpan?, Func{string}?, string?)"/> 的同步版本，
    /// 仅供无法改为 async 的同步测试使用。
    /// </summary>
    public static void WaitUntil(
        Func<bool> condition,
        TimeSpan? timeout = null,
        Func<string>? diagnostics = null,
        [CallerArgumentExpression(nameof(condition))] string? conditionExpression = null)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var budget = timeout ?? DefaultTimeout;
        if (!SpinWait.SpinUntil(condition, budget))
        {
            Assert.Fail(BuildTimeoutMessage("等待条件", conditionExpression, budget, diagnostics));
        }
    }

    private static string BuildTimeoutMessage(
        string what,
        string? expression,
        TimeSpan budget,
        Func<string>? diagnostics)
    {
        var message = $"{what}在 {budget.TotalSeconds:0.##}s 内未满足：{expression}";
        if (diagnostics is not null)
        {
            message += Environment.NewLine + "诊断信息：" + Environment.NewLine + diagnostics();
        }

        return message;
    }
}
