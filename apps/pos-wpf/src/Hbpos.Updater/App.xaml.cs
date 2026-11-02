using System.Globalization;
using System.IO;
using System.Windows;

namespace Hbpos.Updater;

public partial class App : Application
{
    private const string ProgressFileName = "installer-progress.txt";
    private const int InvalidArgumentsExitCode = 2;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 更新窗口从临时目录运行，工作目录也切过去，避免占住收银程序的安装目录。
        TrySetWorkingDirectory(AppContext.BaseDirectory);
        if (UpdateAgentEntryPoint.TryStart(e.Args, out var agentCommand))
        {
            // 注册/注销代理任务和 SYSTEM 代理本身都没有界面，执行完按退出码结束。
            _ = RunAgentCommandAsync(agentCommand);
            return;
        }

        if (!UpdaterOptions.TryParse(e.Args, out var options) || options is null)
        {
            Shutdown(InvalidArgumentsExitCode);
            return;
        }

        ApplyCulture(options.Culture);
        var viewModel = new UpdaterViewModel(
            UpdaterStrings.ForCulture(options.Culture),
            options.FromVersion,
            options.ToVersion);
        var session = new UpdateSession(
            options,
            new WindowsUpdaterSystem(),
            TimeProvider.System,
            viewModel,
            Path.Combine(AppContext.BaseDirectory, ProgressFileName));
        session.ExitRequested += (_, _) => Shutdown();
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            session.ReportUnexpectedFailure(args.Exception);
        };

        var window = new UpdaterWindow(viewModel);
        // 失败页允许收银员直接关窗；窗口关掉就结束进程，不在后台残留。
        window.Closed += (_, _) => Shutdown();
        window.Show();
        _ = session.RunAsync();
    }

    private async Task RunAgentCommandAsync(Func<Task<int>> command)
    {
        var exitCode = 1;
        try
        {
            exitCode = await command();
        }
        catch (Exception)
        {
            // 无界面命令不能停在未处理异常上，统一以失败退出码结束。
            exitCode = 1;
        }
        finally
        {
            Shutdown(exitCode);
        }
    }

    private static void TrySetWorkingDirectory(string directory)
    {
        try
        {
            Environment.CurrentDirectory = directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void ApplyCulture(string cultureName)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
        }
    }
}
