using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf;

public sealed partial class StartupProgressState : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int stagePercent;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private string versionText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateNotice))]
    private string updateNoticeText = string.Empty;

    private AppLaunchVersionNotice? _updateNotice;

    public string ProgressText => $"{StagePercent}%";

    public bool HasUpdateNotice => !string.IsNullOrWhiteSpace(UpdateNoticeText);

    public void SetStage(int percent, string? status = null)
    {
        StagePercent = Math.Clamp(percent, 0, 100);
        if (status is not null)
        {
            StatusText = status;
        }
    }

    public void SetVersion(
        string version,
        AppLaunchVersionNotice? notice,
        Func<string, string> localize,
        CultureInfo culture)
    {
        VersionText = version;
        _updateNotice = notice;
        LocalizeUpdateNotice(localize, culture);
    }

    // 启动页先按默认语言显示；宿主读到本机语言设置后要重新格式化，否则提示会停留在默认语言。
    public void LocalizeUpdateNotice(Func<string, string> localize, CultureInfo culture)
    {
        UpdateNoticeText = _updateNotice is null
            ? string.Empty
            : string.Format(
                culture,
                localize(_updateNotice.IsRollback ? "startup.rolledBackTo" : "startup.updatedTo"),
                _updateNotice.Version);
    }
}
