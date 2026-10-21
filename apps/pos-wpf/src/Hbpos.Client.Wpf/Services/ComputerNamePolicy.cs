using System.Security.Cryptography;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 电脑名规则与自动取名（纯逻辑，不依赖 Windows）。
/// 硬件码 = SHA256(电脑名|域名|平台|64位|MachineGuid)，克隆系统镜像的两台电脑只要改了电脑名，硬件码就不再相同。
/// 取名结果会拼进以管理员身份执行的脚本，所以必须严格限制字符集，不接受任何其他输入。
/// </summary>
public static class ComputerNamePolicy
{
    /// <summary>NetBIOS 电脑名上限 15 个字符。</summary>
    public const int MaxLength = 15;

    // 去掉容易看错的 0/O、1/I/L，现场口头报名字时不会混淆。
    private const string RandomAlphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
    private const int RandomLength = 4;
    private const int MaxStoreCodeLength = 6;

    /// <summary>1–15 个字母/数字/短横线，不以短横线开头或结尾，且不能全是数字（Windows 不接受纯数字电脑名）。</summary>
    public static bool IsValid(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxLength)
        {
            return false;
        }

        if (name[0] == '-' || name[^1] == '-')
        {
            return false;
        }

        var hasLetterOrHyphen = false;
        foreach (var ch in name)
        {
            var isAsciiLetter = ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
            var isDigit = ch is >= '0' and <= '9';
            if (!isAsciiLetter && !isDigit && ch != '-')
            {
                return false;
            }

            hasLetterOrHyphen |= !isDigit;
        }

        return hasLetterOrHyphen;
    }

    /// <summary>
    /// 生成形如 <c>HB-2010-A7K3</c> 的新电脑名（店号 + 4 位随机码）；保证合法，且和当前电脑名不同。
    /// <paramref name="randomIndex"/> 只给测试注入确定的随机序列用。
    /// </summary>
    public static string Generate(string? storeCode, string? currentName, Func<int, int>? randomIndex = null)
    {
        var pick = randomIndex ?? RandomNumberGenerator.GetInt32;
        var store = new string((storeCode ?? string.Empty)
            .Where(char.IsAsciiLetterOrDigit)
            .Take(MaxStoreCodeLength)
            .ToArray())
            .ToUpperInvariant();
        var prefix = store.Length == 0 ? "HB-" : $"HB-{store}-";

        // 随机码撞上当前名字的概率极低；仍然循环保证「新名字 ≠ 旧名字」，否则硬件码不会变。
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var suffix = new char[RandomLength];
            for (var i = 0; i < suffix.Length; i++)
            {
                suffix[i] = RandomAlphabet[pick(RandomAlphabet.Length)];
            }

            var candidate = prefix + new string(suffix);
            if (IsValid(candidate)
                && !string.Equals(candidate, currentName, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not generate a new computer name.");
    }
}
