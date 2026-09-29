namespace Hbpos.Updater;

/// <summary>
/// 更新窗口是独立进程，读不到收银端的资源文件，这里只保留收银端支持的中英文两套文案。
/// </summary>
internal sealed record UpdaterStrings
{
    public required string WindowTitle { get; init; }
    public required string UpdatingTitle { get; init; }
    public required string UpdatingSubtitle { get; init; }
    public required string CompletedTitle { get; init; }
    public required string CompletedSubtitle { get; init; }
    public required string FailedTitle { get; init; }
    public required string FailedSubtitleWithVersion { get; init; }
    public required string FailedSubtitle { get; init; }
    public required string VersionLabel { get; init; }
    public required string ClosingStageTitle { get; init; }
    public required string ClosingStageDetail { get; init; }
    public required string PreparingPercent { get; init; }
    public required string InstallingStageTitle { get; init; }
    public required string InstallerStartingDetail { get; init; }
    public required string InstallerPreparingDetail { get; init; }
    public required string InstallingFilesDetail { get; init; }
    public required string FinishingDetail { get; init; }
    public required string CompletedStageTitle { get; init; }
    public required string OpeningVersionDetail { get; init; }
    public required string OpeningDetail { get; init; }
    public required string ElapsedFormat { get; init; }
    public required string TotalElapsedFormat { get; init; }
    public required string ClosingHint { get; init; }
    public required string ElevationHint { get; init; }
    public required string EstimatingHint { get; init; }
    public required string RemainingSecondsFormat { get; init; }
    public required string RemainingMinutesFormat { get; init; }
    public required string AutoCloseHint { get; init; }
    public required string StepDownload { get; init; }
    public required string StepInstall { get; init; }
    public required string StepLaunch { get; init; }
    public required string FailureTitleWithVersion { get; init; }
    public required string FailureTitle { get; init; }
    public required string InstallerFailedMessage { get; init; }
    public required string InstallerCancelledMessage { get; init; }
    public required string ElevationCancelledMessage { get; init; }
    public required string RestartRequiredMessage { get; init; }
    public required string InstallerMissingMessage { get; init; }
    public required string LaunchFailedMessage { get; init; }
    public required string UnexpectedFailureMessage { get; init; }
    public required string OpenAppFailedMessage { get; init; }
    public required string ViewLogButton { get; init; }
    public required string OpenCurrentButton { get; init; }
    public required string RetryButton { get; init; }

    public static UpdaterStrings ForCulture(string? cultureName)
    {
        return cultureName?.Trim().StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true
            ? ChineseSimplified
            : English;
    }

    public static UpdaterStrings ChineseSimplified { get; } = new()
    {
        WindowTitle = "HB POS 更新",
        UpdatingTitle = "正在更新 HB POS",
        UpdatingSubtitle = "完成后会自动重新打开，请勿关机或断开电源",
        CompletedTitle = "更新完成",
        CompletedSubtitle = "马上为你打开新版本，无需任何操作",
        FailedTitle = "更新未完成",
        FailedSubtitleWithVersion = "当前版本 {0} 保持不变，收银数据不受影响",
        FailedSubtitle = "当前版本保持不变，收银数据不受影响",
        VersionLabel = "版本",
        ClosingStageTitle = "正在关闭旧版本…",
        ClosingStageDetail = "正在保存本机数据并退出收银程序",
        PreparingPercent = "准备中",
        InstallingStageTitle = "正在安装新版本…",
        InstallerStartingDetail = "正在启动安装程序",
        InstallerPreparingDetail = "正在检查安装环境",
        InstallingFilesDetail = "正在替换程序文件",
        FinishingDetail = "正在完成安装",
        CompletedStageTitle = "新版本已安装",
        OpeningVersionDetail = "正在打开 HB POS {0}…",
        OpeningDetail = "正在打开 HB POS…",
        ElapsedFormat = "已用时 {0}",
        TotalElapsedFormat = "总用时 {0}",
        ClosingHint = "通常只需几秒",
        ElevationHint = "如出现授权窗口，请选择「是」",
        EstimatingHint = "正在估算剩余时间",
        RemainingSecondsFormat = "预计还需约 {0} 秒",
        RemainingMinutesFormat = "预计还需约 {0} 分钟",
        AutoCloseHint = "本窗口将自动关闭",
        StepDownload = "下载更新包",
        StepInstall = "安装新版本",
        StepLaunch = "重新打开收银",
        FailureTitleWithVersion = "新版本 {0} 没有安装成功",
        FailureTitle = "新版本没有安装成功",
        InstallerFailedMessage = "安装程序返回错误代码 {0}。可能被杀毒软件拦截或权限不足，可以先重试一次；仍失败请联系管理员。",
        InstallerCancelledMessage = "安装被取消，可能是在授权窗口中选择了「否」。点击「重试更新」可重新安装。",
        ElevationCancelledMessage = "安装需要管理员授权，授权窗口被取消。点击「重试更新」后，请在授权窗口中选择「是」。",
        RestartRequiredMessage = "需要先重启电脑才能完成安装。重启后再打开 HB POS 即可。",
        InstallerMissingMessage = "找不到已下载的安装包。请打开当前版本，系统会重新下载更新。",
        LaunchFailedMessage = "无法启动安装程序：{0}",
        UnexpectedFailureMessage = "更新窗口出现意外错误：{0}",
        OpenAppFailedMessage = "无法打开收银程序，请从桌面图标打开 HB POS。",
        ViewLogButton = "查看更新日志",
        OpenCurrentButton = "打开当前版本",
        RetryButton = "重试更新"
    };

    public static UpdaterStrings English { get; } = new()
    {
        WindowTitle = "HB POS Update",
        UpdatingTitle = "Updating HB POS",
        UpdatingSubtitle = "HB POS reopens automatically. Keep the computer on and plugged in.",
        CompletedTitle = "Update complete",
        CompletedSubtitle = "Opening the new version for you. Nothing else to do.",
        FailedTitle = "Update not completed",
        FailedSubtitleWithVersion = "Version {0} is unchanged and sales data is safe.",
        FailedSubtitle = "The current version is unchanged and sales data is safe.",
        VersionLabel = "Version",
        ClosingStageTitle = "Closing the current version…",
        ClosingStageDetail = "Saving local data and closing the register",
        PreparingPercent = "Preparing",
        InstallingStageTitle = "Installing the new version…",
        InstallerStartingDetail = "Starting the installer",
        InstallerPreparingDetail = "Checking the installation",
        InstallingFilesDetail = "Replacing program files",
        FinishingDetail = "Finishing installation",
        CompletedStageTitle = "New version installed",
        OpeningVersionDetail = "Opening HB POS {0}…",
        OpeningDetail = "Opening HB POS…",
        ElapsedFormat = "Elapsed {0}",
        TotalElapsedFormat = "Total {0}",
        ClosingHint = "Usually takes a few seconds",
        ElevationHint = "If Windows asks for permission, choose Yes",
        EstimatingHint = "Estimating time left",
        RemainingSecondsFormat = "About {0} s left",
        RemainingMinutesFormat = "About {0} min left",
        AutoCloseHint = "This window closes automatically",
        StepDownload = "Download",
        StepInstall = "Install",
        StepLaunch = "Reopen POS",
        FailureTitleWithVersion = "Version {0} was not installed",
        FailureTitle = "The new version was not installed",
        InstallerFailedMessage = "The installer returned error code {0}. Antivirus or missing permissions may have blocked it. Try again; if it still fails, contact your administrator.",
        InstallerCancelledMessage = "Installation was cancelled, possibly by choosing No in the permission prompt. Select Retry update to install again.",
        ElevationCancelledMessage = "Installing needs administrator permission and the prompt was cancelled. Select Retry update, then choose Yes.",
        RestartRequiredMessage = "Restart the computer to finish installing, then open HB POS.",
        InstallerMissingMessage = "The downloaded installer was not found. Open the current version and it will download the update again.",
        LaunchFailedMessage = "Could not start the installer: {0}",
        UnexpectedFailureMessage = "The updater hit an unexpected error: {0}",
        OpenAppFailedMessage = "Could not open HB POS. Open it from the desktop shortcut.",
        ViewLogButton = "View update log",
        OpenCurrentButton = "Open current version",
        RetryButton = "Retry update"
    };
}
