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
