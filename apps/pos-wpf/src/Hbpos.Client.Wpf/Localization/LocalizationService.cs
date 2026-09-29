using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace Hbpos.Client.Wpf.Localization;

public interface ILocalizationService : INotifyPropertyChanged
{
    IReadOnlyList<CultureInfo> AvailableCultures { get; }

    CultureInfo CurrentCulture { get; }

    event EventHandler? CultureChanged;

    void SetCulture(string cultureName);

    void SetCulture(CultureInfo culture);

    Task SetCultureAsync(string cultureName, CancellationToken cancellationToken = default);

    string T(string key);
}

public sealed class LocalizationService : ILocalizationService
{
    public const string DefaultCultureName = "en-US";
    public const string ChineseCultureName = "zh-CN";

    private static readonly ResourceManager[] ResourceManagers =
    [
        new("Hbpos.Client.Wpf.Resources.Strings", typeof(LocalizationService).Assembly),
        new("Hbpos.Client.Wpf.Resources.SettingsStrings", typeof(LocalizationService).Assembly)
    ];

    private static readonly IReadOnlyDictionary<string, CultureInfo> SupportedCultures =
        new[]
        {
            CreateSupportedCulture(DefaultCultureName),
            CreateSupportedCulture(ChineseCultureName)
        }.ToDictionary(culture => culture.Name, StringComparer.OrdinalIgnoreCase);

    private CultureInfo _currentCulture = SupportedCultures[DefaultCultureName];

    public LocalizationService()
    {
        ApplyThreadCulture(_currentCulture);
    }

    public event EventHandler? CultureChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<CultureInfo> AvailableCultures { get; } = SupportedCultures.Values.ToArray();

    public CultureInfo CurrentCulture => _currentCulture;

    public void SetCulture(string cultureName)
    {
        SetCulture(CultureInfo.GetCultureInfo(cultureName));
    }

    public void SetCulture(CultureInfo culture)
    {
        if (!SupportedCultures.TryGetValue(culture.Name, out var supportedCulture))
        {
            throw new ArgumentException($"Unsupported culture '{culture.Name}'.", nameof(culture));
        }

        if (Equals(_currentCulture, supportedCulture))
        {
            ApplyThreadCulture(supportedCulture);
            return;
        }

        _currentCulture = supportedCulture;
        ApplyThreadCulture(_currentCulture);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentCulture)));
        CultureChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task SetCultureAsync(string cultureName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetCulture(cultureName);
        return Task.CompletedTask;
    }

    public string T(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "[[]]";
        }

        foreach (var resourceManager in ResourceManagers)
        {
            try
            {
                var value = resourceManager.GetString(key, _currentCulture);
                if (value is not null)
                {
                    return value;
                }
            }
            catch (MissingManifestResourceException)
            {
            }
        }

        return $"[[{key}]]";
    }

    /// <summary>
    /// 把界面语言名规范成受支持的语言；启动页在服务容器建立前就要按本机上次的语言显示。
    /// </summary>
    internal static bool TryGetSupportedCulture(string? cultureName, out CultureInfo culture)
    {
        if (!string.IsNullOrWhiteSpace(cultureName) &&
            SupportedCultures.TryGetValue(cultureName.Trim(), out var supported))
        {
            culture = supported;
            return true;
        }

        culture = SupportedCultures[DefaultCultureName];
        return false;
    }

    /// <summary>不依赖实例状态的查词，供启动页线程按固定语言取文案。</summary>
    internal static string Translate(string key, CultureInfo culture)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "[[]]";
        }

        foreach (var resourceManager in ResourceManagers)
        {
            try
            {
                var value = resourceManager.GetString(key, culture);
                if (value is not null)
                {
                    return value;
                }
            }
            catch (MissingManifestResourceException)
            {
            }
        }

        return $"[[{key}]]";
    }

    private static void ApplyThreadCulture(CultureInfo culture)
    {
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
    }

    private static CultureInfo CreateSupportedCulture(string cultureName)
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo(cultureName).Clone();
        // 中文说明：界面语言可以切换，但 POS 金额统一使用美元符号显示。
        culture.NumberFormat.CurrencySymbol = "$";
        return CultureInfo.ReadOnly(culture);
    }
}
