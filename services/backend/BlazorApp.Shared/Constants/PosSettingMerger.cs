using System.Security.Cryptography;
using System.Text;

namespace BlazorApp.Shared.Constants;

/// <summary>某一项最终生效的值与来源。Value 为 null = 三层都没设置，收银机沿用本机值。</summary>
public sealed record PosSettingEffective(bool? Value, string Source);

/// <summary>
/// 三层合并与指纹。放在 BlazorApp.Shared，是为了让主后端（算「设备是否已应用」）与 POS API（给收银机下发）
/// 用同一份代码，不会因为两边各写一遍而对不上。
/// 规则：设备 → 门店 → 全局，取最具体那层「已设置」的值；三层都没设置时，有缺省值的项（现金存入 / 取出）按缺省值，
/// 其余为 null（收银机沿用本机值）。传入 clientKind 时只保留该端适用的项。
/// </summary>
public static class PosSettingMerger
{
    /// <summary>设备系统（Windows / iPadOS / iOS / Android）→ 端类型；POS 设备表只有设备系统，没有回执过的设备靠它推导。</summary>
    public static string ClientKindFromDeviceSystem(string? deviceSystem)
    {
        var value = deviceSystem?.Trim();
        if (string.Equals(value, "Windows", StringComparison.OrdinalIgnoreCase))
        {
            return PosSettingClientKinds.Wpf;
        }

        if (string.Equals(value, "iPadOS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "iOS", StringComparison.OrdinalIgnoreCase))
        {
            return PosSettingClientKinds.Ipad;
        }

        return string.Equals(value, "Android", StringComparison.OrdinalIgnoreCase)
            ? PosSettingClientKinds.Handheld
            : PosSettingClientKinds.Other;
    }

    /// <summary>
    /// 合并三层。layer 字典里只放「已设置」的项；不认识的键一律忽略（清单以后删项时，库里的老数据不会让下发出错）。
    /// </summary>
    public static Dictionary<string, PosSettingEffective> Merge(
        IReadOnlyDictionary<string, bool>? global,
        IReadOnlyDictionary<string, bool>? store,
        IReadOnlyDictionary<string, bool>? device,
        string? clientKind = null)
    {
        var result = new Dictionary<string, PosSettingEffective>(StringComparer.Ordinal);
        foreach (var definition in PosSettingCatalog.All)
        {
            if (clientKind is not null && !PosSettingCatalog.IsSupportedBy(definition.Key, clientKind))
            {
                continue;
            }

            if (device is not null && device.TryGetValue(definition.Key, out var deviceValue))
            {
                result[definition.Key] = new PosSettingEffective(deviceValue, PosSettingScopes.Device);
            }
            else if (store is not null && store.TryGetValue(definition.Key, out var storeValue))
            {
                result[definition.Key] = new PosSettingEffective(storeValue, PosSettingScopes.Store);
            }
            else if (global is not null && global.TryGetValue(definition.Key, out var globalValue))
            {
                result[definition.Key] = new PosSettingEffective(globalValue, PosSettingScopes.Global);
            }
            else if (definition.DefaultWhenUnset is { } fallback)
            {
                result[definition.Key] = new PosSettingEffective(fallback, PosSettingSources.Default);
            }
            else
            {
                result[definition.Key] = new PosSettingEffective(null, PosSettingSources.None);
            }
        }

        return result;
    }

    /// <summary>只保留有值的项（下发给收银机、参与指纹的就是这部分）。</summary>
    public static Dictionary<string, PosSettingEffective> Managed(
        IReadOnlyDictionary<string, PosSettingEffective> effective) =>
        effective
            .Where(item => item.Value.Value is not null)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

    /// <summary>
    /// 指纹：把「键=值|来源」按键排序拼成文本再做 SHA-256（小写十六进制）。
    /// 来源也参与：同一个值从「全局」变成「门店」设置，收银机设置页上的来源标签要跟着变，所以也要重新同步。
    /// 没有任何已管理项时是固定值（空文本的 SHA-256），客户端首次同步（knownHash 为空）一定会拿到完整响应。
    /// </summary>
    public static string ComputeHash(IReadOnlyDictionary<string, PosSettingEffective> managed)
    {
        var builder = new StringBuilder();
        foreach (var item in managed.Where(item => item.Value.Value is not null).OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            builder.Append(item.Key).Append('=').Append(item.Value.Value == true ? '1' : '0')
                .Append('|').Append(item.Value.Source).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}
