using System.Globalization;
using Hbpos.Client.Wpf.Localization;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 分期（Lay-by）服务层提示的中英文切换。分期的操作服务、订单服务里有大量静态方法和后台线程，
/// 所以这里用一个进程级入口，而不是给每个调用点传语言服务。
/// </summary>
/// <remarks>
/// 中文注释：只在应用启动时（App.OnStartup）配置一次。没有配置时返回调用处写的中文原文——
/// 单元测试不配置，断言里的中文提示保持不变；也保证资源缺失时提示不会变成 [[key]]。
/// 写进本地记录的失败原因按写入时的语言保存，不回头翻译。
/// </remarks>
public static class InstallmentText
{
    private static ILocalizationService? _localization;

    public static void Configure(ILocalizationService? localization) => _localization = localization;

    /// <param name="key">资源键（Strings.resx / Strings.zh-CN.resx）。</param>
    /// <param name="fallback">中文原文；带参数时是 string.Format 模板。</param>
    public static string L(string key, string fallback, params object?[] args) =>
        Format(_localization, key, fallback, args);

    /// <summary>不依赖进程级配置的实现，便于测试。</summary>
    internal static string Format(ILocalizationService? localization, string key, string fallback, object?[] args)
    {
        var template = fallback;
        var localized = localization?.T(key);
        if (!string.IsNullOrWhiteSpace(localized) && !localized.StartsWith("[[", StringComparison.Ordinal))
        {
            template = localized;
        }

        return args.Length == 0 ? template : string.Format(CultureInfo.CurrentCulture, template, args);
    }
}
