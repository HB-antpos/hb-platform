using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hbpos.Contracts.Cashiers;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 小票（含日结单）上的员工显示名，与移动端员工码 apps/mobile/src/modules/people/person-name.ts 的
/// resolvePersonalCodeLabelName 规则一致（改一处须同步另一处）：
/// 1. base 取第一个非空的：昵称 → 名（GivenName）→ 账号（账号是邮箱时取 @ 前面的部分，保留原大小写；@ 前为空视为没有）；
/// 2. 有姓时：base 或姓含中日韩字符 → 不缩写、不加空格，按「姓 + base」拼接（王芳 / 王小芳）；
///    否则 → 「base + 空格 + 姓的首个文本元素（转大写）」，不加句点（Amy L）；没有姓只输出 base；
/// 3. 名和姓都为空（拆分字段上线前的老账号，只有全名）时，先按 splitLegacyFullName 的规则从全名拆出名 / 姓再套上面的规则；
///    全名就等于账号（服务端在没有全名时用账号填充）则不拆，避免把邮箱当成名。
/// base 全空返回 null，调用方降级为订单里存的原文。
/// 所有小票入口（打印、预览、重打、退货、分期）都经 <see cref="ReceiptTextFormatter"/> 走 <see cref="ReceiptStaffDirectory.ResolveDisplayName"/>，
/// 这里是唯一的格式化实现，不要在别处另写一份。
/// </summary>
public static class ReceiptStaffName
{
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// 按规则格式化；base 全空时返回 null（调用方自行降级）。
    /// 姓首字母按「文本元素」取（不拆 emoji / 组合字符），英文字母转大写。
    /// </summary>
    public static string? Format(
        string? nickname,
        string? givenName,
        string? username,
        string? familyName,
        string? fullName = null)
    {
        var given = CleanNamePart(givenName);
        var family = CleanNamePart(familyName);

        // 老账号兼容：没有任何结构化姓名时从全名拆；全名就是账号时不拆（与移动端一致，比较不区分大小写）。
        if (given is null && family is null)
        {
            var legacyFullName = CleanNamePart(fullName);
            var account = CleanNamePart(username);
            var fullNameIsAccount = legacyFullName is not null &&
                string.Equals(legacyFullName, account, StringComparison.OrdinalIgnoreCase);
            if (legacyFullName is not null && !fullNameIsAccount)
            {
                (given, family) = SplitLegacyFullName(legacyFullName);
            }
        }

        var baseName = CleanNamePart(nickname) ?? given ?? AccountDisplayPart(username);
        if (baseName is null)
        {
            return null;
        }

        if (family is null)
        {
            return baseName;
        }

        if (ContainsCjk(baseName) || ContainsCjk(family))
        {
            return family + baseName;
        }

        return $"{baseName} {StringInfo.GetNextTextElement(family).ToUpperInvariant()}";
    }

    public static string? Format(CashierSessionDto? session)
    {
        if (session is null || session.IsEmergencyOverride)
        {
            return null;
        }

        // 老服务端 / #911 之前的离线缓存会话不带 Username，无法判断 CashierName 是不是账号（可能是邮箱），
        // 此时不拿 CashierName 当全名拆，保持降级为订单原文的旧行为。
        var legacyFullName = string.IsNullOrWhiteSpace(session.Username) ? null : session.CashierName;
        return Format(session.Nickname, session.GivenName, session.Username, session.FamilyName, legacyFullName);
    }

    /// <summary>去首尾空白并折叠内部连续空白；空串统一成 null（对齐移动端 cleanNamePart）。</summary>
    private static string? CleanNamePart(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var text = WhitespaceRun.Replace(value, " ").Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>账号兜底：邮箱形式的账号只取第一个 @ 前面的部分（保留原大小写）；@ 前为空视为没有。</summary>
    private static string? AccountDisplayPart(string? username)
    {
        var account = CleanNamePart(username);
        if (account is null)
        {
            return null;
        }

        var at = account.IndexOf('@');
        return at >= 0 ? CleanNamePart(account[..at]) : account;
    }

    /// <summary>
    /// 只有全名的老账号拆成（名, 姓），照搬移动端 splitLegacyFullName：
    /// 含中日韩字符且无空格 → 首字当姓、其余当名，单字只当名；
    /// 西文按空格：1 个词 = 名，2 个词 = 名 姓，≥3 个词 = 首词名、末词姓（中间名小票不用）。
    /// 入参已经过 <see cref="CleanNamePart"/>，非空且只有单个空格分隔。
    /// </summary>
    private static (string? Given, string? Family) SplitLegacyFullName(string text)
    {
        if (ContainsCjk(text) && !text.Contains(' '))
        {
            // 与 JS 的 Array.from 一致按码点切分，扩展 B～F 的汉字（代理对）不会被拆坏。
            var chars = text.EnumerateRunes().Select(rune => rune.ToString()).ToArray();
            return chars.Length == 1
                ? (chars[0], null)
                : (string.Concat(chars.Skip(1)), chars[0]);
        }

        var words = text.Split(' ');
        return words.Length == 1
            ? (words[0], null)
            : (words[0], words[^1]);
    }

    /// <summary>
    /// 中日韩字符判断，范围与移动端 CJK_PATTERN 相同：
    /// CJK 统一表意文字 + 扩展 A + 兼容表意文字 + 扩展 B～F（U+20000–U+2FA1F）。
    /// </summary>
    private static bool ContainsCjk(string value)
    {
        foreach (var rune in value.EnumerateRunes())
        {
            var code = rune.Value;
            if (code is (>= 0x3400 and <= 0x4DBF)
                or (>= 0x4E00 and <= 0x9FFF)
                or (>= 0xF900 and <= 0xFAFF)
                or (>= 0x20000 and <= 0x2FA1F))
            {
                return true;
            }
        }

        return false;
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
