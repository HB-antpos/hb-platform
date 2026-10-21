using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Hbpos.Contracts.Cashiers;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 小票上的员工显示名：「昵称 + 姓首字母」；没有昵称时用「用户名 + 姓首字母」；没有姓则只显示昵称 / 用户名。
/// 所有小票入口（打印、预览、重打、退货、分期）都经 <see cref="ReceiptTextFormatter"/> 走 <see cref="ReceiptStaffDirectory.ResolveDisplayName"/>，
/// 这里是唯一的格式化实现，不要在别处另写一份。
/// </summary>
public static class ReceiptStaffName
{
    /// <summary>
    /// 按规则格式化；昵称和用户名都为空白时返回 null（调用方自行降级）。
    /// 姓首字母按「文本元素」取（不拆 emoji / 组合字符 / 中文），英文字母转大写。
    /// </summary>
    public static string? Format(string? nickname, string? username, string? familyName)
    {
        var baseName = FirstNonBlank(nickname, username);
        if (baseName is null)
        {
            return null;
        }

        var initial = FamilyNameInitial(familyName);
        return initial is null ? baseName : $"{baseName} {initial}";
    }

    public static string? Format(CashierSessionDto? session)
    {
        return session is null || session.IsEmergencyOverride
            ? null
            : Format(session.Nickname, session.Username, session.FamilyName);
    }

    private static string? FirstNonBlank(string? first, string? second)
    {
        var trimmedFirst = first?.Trim();
        if (!string.IsNullOrEmpty(trimmedFirst))
        {
            return trimmedFirst;
        }

        var trimmedSecond = second?.Trim();
        return string.IsNullOrEmpty(trimmedSecond) ? null : trimmedSecond;
    }

    private static string? FamilyNameInitial(string? familyName)
    {
        var trimmed = familyName?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var first = StringInfo.GetNextTextElement(trimmed);
        return first.ToUpperInvariant();
    }
}

/// <summary>
/// 本机已知员工的小票显示名目录，键是订单 / 会话里存的 CashierName（全名，没有全名时为用户名）。
/// 订单和远程历史只存全名字符串，小票渲染处没有员工 ID，所以重打历史订单时靠它把全名换成新格式；
/// 查不到（该员工从未在本机登录过、同名多人无法区分、登录时服务端还没返回新字段）则降级为订单里存的原文。
/// </summary>
public sealed class ReceiptStaffDirectory
{
    public const string SettingsKey = "receipt.staffDisplayNames.v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public static ReceiptStaffDirectory Shared { get; } = new();

    /// <summary>记录一个登录会话；返回目录是否发生变化（调用方据此决定要不要落盘）。</summary>
    public bool Remember(CashierSessionDto? session)
    {
        var key = session?.CashierName?.Trim();
        var display = ReceiptStaffName.Format(session);
        if (session is null || string.IsNullOrEmpty(key) || display is null)
        {
            return false;
        }

        var candidate = new Entry(session.CashierId, display, Ambiguous: false);
        var changed = false;
        _entries.AddOrUpdate(
            key,
            _ =>
            {
                changed = true;
                return candidate;
            },
            (_, existing) =>
            {
                // 同一员工改了昵称 / 姓：以最新为准；不同员工撞了同一个全名：标记为歧义，宁可降级也不张冠李戴。
                var next = string.Equals(existing.CashierId, candidate.CashierId, StringComparison.OrdinalIgnoreCase)
                    ? candidate
                    : existing with { Ambiguous = true };
                changed = next != existing;
                return next;
            });
        return changed;
    }

    /// <summary>
    /// 把订单里存的员工名换成小票显示名；目录里没有或有歧义时原样返回（包含空白 / null → 空串）。
    /// </summary>
    public string ResolveDisplayName(string? storedCashierName)
    {
        var key = storedCashierName?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        return _entries.TryGetValue(key, out var entry) && !entry.Ambiguous
            ? entry.Display
            : key;
    }

    public string Serialize()
    {
        return JsonSerializer.Serialize(
            _entries.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            JsonOptions);
    }

    /// <summary>合并落盘内容；已在内存里的（更新鲜的）条目优先。内容损坏时忽略，不影响登录和打印。</summary>
    public void Load(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        Dictionary<string, Entry>? stored;
        try
        {
            stored = JsonSerializer.Deserialize<Dictionary<string, Entry>>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return;
        }

        foreach (var (key, entry) in stored ?? [])
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(entry?.Display))
            {
                _entries.TryAdd(key, entry);
            }
        }
    }

    internal sealed record Entry(string CashierId, string Display, bool Ambiguous);
}
