namespace Hbpos.Updater;

/// <summary>
/// 更新窗口眼中的“代理安装进程”：轮询代理写的状态文件，等到成功或失败后给出退出码，
/// 让更新会话沿用与直接运行安装器相同的等待和进度显示逻辑。
/// </summary>
internal sealed class AgentInstallProcess(
    string requestId,
    string statusFilePath,
    Func<string, string?> readAllText,
    TimeProvider timeProvider) : IInstallerProcess
{
    internal const int AgentFailedExitCode = -1;
    internal const int AgentNotRespondingExitCode = -2;

    // 中文注释：任务计划正常情况下几秒内就会写“已受理”；一分钟都没有，多半是任务被删或权限不对，交给会话决定是否退回直接安装。
    internal static readonly TimeSpan AcceptTimeout = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan CompletionTimeout = TimeSpan.FromMinutes(45);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    public int ExitCode { get; private set; }

    public string? FailureDetail { get; private set; }

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetTimestamp();
        var accepted = false;
        while (true)
        {
            if (UpdateAgentStatus.TryParse(readAllText(statusFilePath), requestId, out var status) && status is not null)
            {
                accepted = true;
                if (status.IsFinal)
                {
                    ExitCode = status.State == UpdateAgentState.Succeeded
                        ? 0
                        : status.ExitCode is int exitCode and not 0 ? exitCode : AgentFailedExitCode;
                    FailureDetail = status.Error;
                    return;
                }
            }

            var elapsed = timeProvider.GetElapsedTime(startedAt);
            if (!accepted && elapsed >= AcceptTimeout)
            {
                ExitCode = AgentNotRespondingExitCode;
                FailureDetail = "update-agent-not-responding";
                return;
            }

            if (elapsed >= CompletionTimeout)
            {
                ExitCode = AgentFailedExitCode;
                FailureDetail = "update-agent-timeout";
                return;
            }

            await Task.Delay(PollInterval, timeProvider, cancellationToken);
        }
    }

    public void Dispose()
    {
    }
}
