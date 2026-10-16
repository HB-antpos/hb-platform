using System.Text;

namespace BlazorApp.Shared.Helper
{
    /// <summary>
    /// 人名格式化工具：把「名 / 中间名 / 姓」合成为全名，并按优先级取收银、排班等场景使用的短名。
    /// 纯静态、无外部依赖，POS（Hbpos.Api）也可直接引用。
    /// </summary>
    public static class PersonNameFormatter
    {
        /// <summary>
        /// 按「名 / 中间名 / 姓」合成全名。
        /// 各段先 Trim 并把内部连续空白折叠为单个空格；任一段含 CJK 字符时按「姓 + 中间名 + 名」无空格拼接，
        /// 否则按「名 中间名 姓」用单个空格拼接；空段跳过，全空返回 null。
        /// </summary>
        public static string? Compose(string? givenName, string? middleName, string? familyName)
        {
            var given = Normalize(givenName);
            var middle = Normalize(middleName);
            var family = Normalize(familyName);

            if (given is null && middle is null && family is null)
            {
                return null;
            }

            // 关键逻辑：只要有一段是中日韩文字，就按东亚习惯「姓在前、不加空格」拼接，
            // 例如 姓 王 / 名 小明 → 王小明；混合输入（姓用汉字、名用拼音）同样走无空格路径。
            if (ContainsCjk(given) || ContainsCjk(middle) || ContainsCjk(family))
            {
                return string.Concat(family, middle, given);
            }

            var parts = new List<string>(3);
            if (given is not null) parts.Add(given);
            if (middle is not null) parts.Add(middle);
            if (family is not null) parts.Add(family);
            return string.Join(' ', parts);
        }

        /// <summary>
        /// 取显示用短名：昵称 → 名 → 全名 → 用户名，返回第一个非空白的值（已 Trim）；都为空返回 null。
        /// 供将来收银、排班等场景使用。
        /// </summary>
        public static string? ShortName(string? nickname, string? givenName, string? fullName, string? username)
        {
            return FirstNonBlank(nickname, givenName, fullName, username);
        }

        /// <summary>
        /// 把一段文本规范化：Trim、折叠内部连续空白为单个空格；空白串返回 null。
        /// </summary>
        public static string? Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var builder = new StringBuilder(value.Length);
            var pendingSpace = false;
            foreach (var ch in value)
            {
                if (char.IsWhiteSpace(ch))
                {
                    // 前导空白不输出；中间的连续空白合并成一个空格，尾部空白自然丢弃。
                    pendingSpace = builder.Length > 0;
                    continue;
                }

                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }
                builder.Append(ch);
            }

            return builder.Length == 0 ? null : builder.ToString();
        }

        /// <summary>
        /// 文本里是否含有 CJK 字符：CJK 统一表意文字（含扩展 A、扩展 B~G 经代理对）、
        /// CJK 兼容表意文字，另含日文假名与韩文音节，以便日韩姓名同样走无空格拼接。
        /// </summary>
        public static bool ContainsCjk(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                int codePoint;
                if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    codePoint = char.ConvertToUtf32(value[i], value[i + 1]);
                    i++;
                }
                else
                {
                    codePoint = value[i];
                }

                if (IsCjkCodePoint(codePoint))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsCjkCodePoint(int codePoint)
        {
            return codePoint switch
            {
                >= 0x3400 and <= 0x4DBF => true,   // CJK 统一表意文字扩展 A
                >= 0x4E00 and <= 0x9FFF => true,   // CJK 统一表意文字
                >= 0xF900 and <= 0xFAFF => true,   // CJK 兼容表意文字
                >= 0x20000 and <= 0x2A6DF => true, // 扩展 B
                >= 0x2A700 and <= 0x2EBEF => true, // 扩展 C~F
                >= 0x2F800 and <= 0x2FA1F => true, // CJK 兼容表意文字补充
                >= 0x30000 and <= 0x323AF => true, // 扩展 G~H
                >= 0x3040 and <= 0x30FF => true,   // 日文平假名、片假名
                >= 0xAC00 and <= 0xD7AF => true,   // 韩文音节
                _ => false,
            };
        }

        private static string? FirstNonBlank(params string?[] candidates)
        {
            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return candidate.Trim();
                }
            }

            return null;
        }
    }
}
