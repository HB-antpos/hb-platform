using System.Globalization;

namespace Hbpos.Updater;

internal enum InstallerProgressStage
{
    Preparing,
    Installing,
    Finishing
}

/// <summary>
/// Inno 安装脚本写入进度文件的一行内容，格式为 "阶段 百分比"，例如 "install 42"。
/// </summary>
internal readonly record struct InstallerProgressSnapshot(InstallerProgressStage Stage, int Percent)
{
    public static bool TryParse(string? content, out InstallerProgressSnapshot snapshot)
    {
        snapshot = default;
        var parts = content?.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: 2 } ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var percent))
        {
            // 安装器写文件与这里读取可能交错，读到半截内容时保留上一次的进度即可。
            return false;
        }

        InstallerProgressStage? stage = parts[0].ToLowerInvariant() switch
        {
            "prepare" => InstallerProgressStage.Preparing,
            "install" => InstallerProgressStage.Installing,
            "finish" => InstallerProgressStage.Finishing,
            _ => null
        };
        if (stage is null)
        {
            return false;
        }

        snapshot = new InstallerProgressSnapshot(stage.Value, Math.Clamp(percent, 0, 100));
        return true;
    }
}
