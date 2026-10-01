namespace Hbpos.Client.Tests;

public sealed class LinklyBusinessWaitTimerContractTests
{
    // 中文注释：Cloud / Backend 两个客户端的 PurchaseAsync_does_not_use_short_configured_timeout_before_linkly_business_wait
    // 用 FakeTimeProvider 推进虚拟时间来判断"业务等待前不会提前结束"，只能观察到注入的 TimeProvider 上的计时器。
    // 若有人绕过它直接 CancelAfter（真实计时器）或改用终端超时设置，那两个用例看不见，因此用源码契约兜底。
    [Theory]
    [InlineData("LinklyBackendTerminalClient.cs")]
    [InlineData("LinklyCloudTerminalClient.cs")]
    public void Business_wait_timer_uses_time_provider_and_ignores_configured_terminal_timeout(string fileName)
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "apps", "pos-wpf", "src", "Hbpos.Client.Wpf", "Services", fileName));

        Assert.DoesNotContain("TerminalTimeout", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?i)CancelAfter\([^)]*businessWait", source);
        Assert.Contains("_timeProvider = timeProvider ?? TimeProvider.System;", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "apps", "pos-wpf")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("Unable to find repository root.");
    }
}
