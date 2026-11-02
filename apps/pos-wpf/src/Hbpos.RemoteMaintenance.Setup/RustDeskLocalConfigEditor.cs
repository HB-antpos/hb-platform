namespace Hbpos.RemoteMaintenance.Setup;

/// <summary>
/// 改写 RustDesk 每个 Windows 用户自己的本地配置 <c>%APPDATA%\RustDesk\config\RustDesk_local.toml</c> 的 [options] 段。
/// RustDesk 1.4.9 的“版本更新”横幅由这里的 <c>enable-check-update</c> 控制（enable- 前缀只有值为 N 才算关），
/// 它不在服务配置里，--option 写不到，只能改文件。只按行改目标键，其余内容与换行风格原样保留。
/// </summary>
public static class RustDeskLocalConfigEditor
{
    public const string FileName = "RustDesk_local.toml";
    public const string CheckUpdateOption = "enable-check-update";
    private const string OptionsHeader = "[options]";

    /// <summary>
    /// 返回把 [options] 里 <paramref name="key"/> 设成 <paramref name="value"/> 后的内容；已经是该值、或文件结构不认识时返回 null（不写）。
    /// <paramref name="content"/> 为 null 表示文件不存在：LocalConfig 各字段都有 serde 默认值，只含 [options] 的文件能被正常读取。
    /// </summary>
    public static string? SetOption(string? content, string key, string value)
    {
        var line = $"{key} = '{value}'";
        if (string.IsNullOrWhiteSpace(content)) return OptionsHeader + "\n" + line + "\n";

        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = content.Split('\n').Select(x => x.TrimEnd('\r')).ToList();
        var header = lines.FindIndex(x => x.Trim() == OptionsHeader);
        if (header < 0)
        {
            // 顶层出现 options = {...} 内联表时再追加 [options] 会造成重复键，RustDesk 解析失败会丢掉整份本地配置；不认识就不动。
            if (lines.Any(x => KeyOf(x) == "options")) return null;
            var trimmed = content.TrimEnd('\r', '\n');
            return trimmed + newline + newline + OptionsHeader + newline + line + newline;
        }

        var end = lines.FindIndex(header + 1, x => x.TrimStart().StartsWith('['));
        if (end < 0) end = lines.Count;
        for (var index = header + 1; index < end; index++)
        {
            if (KeyOf(lines[index]) != key) continue;
            if (ValueOf(lines[index]) == value) return null;
            lines[index] = line;
            return string.Join(newline, lines);
        }

        // 插在本段最后一个非空行之后，保持段落之间原有的空行。
        var insertAt = end;
        while (insertAt > header + 1 && string.IsNullOrWhiteSpace(lines[insertAt - 1])) insertAt--;
        lines.Insert(insertAt, line);
        return string.Join(newline, lines);
    }

    // TOML 键可以是裸键或带引号的键；注释与空行返回 null。
    private static string? KeyOf(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#')) return null;
        var equals = trimmed.IndexOf('=');
        return equals <= 0 ? null : trimmed[..equals].Trim().Trim('"', '\'');
    }

    private static string? ValueOf(string line)
    {
        var equals = line.IndexOf('=');
        return equals < 0 ? null : line[(equals + 1)..].Trim().Trim('"', '\'');
    }
}
