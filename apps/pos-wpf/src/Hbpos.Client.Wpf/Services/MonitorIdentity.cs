using System.Globalization;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 用显示器整屏矩形（设备像素）标识「哪块屏」，用于跨重启记住主窗口所在的屏。
/// 系统的显示器句柄每次启动都会变，不能持久化；矩形在拔插或改分辨率后不再匹配，此时回落默认屏。
/// </summary>
public readonly record struct MonitorIdentity(int Left, int Top, int Width, int Height)
{
    public string Format() =>
        string.Join(
            ",",
            new[] { Left, Top, Width, Height }.Select(value => value.ToString(CultureInfo.InvariantCulture)));

    public static MonitorIdentity? Parse(string? value)
    {
        var parts = value?.Split(',');
        if (parts is not { Length: 4 })
        {
            return null;
        }

        var numbers = new int[4];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[index]))
            {
                return null;
            }
        }

        return numbers[2] > 0 && numbers[3] > 0
            ? new MonitorIdentity(numbers[0], numbers[1], numbers[2], numbers[3])
            : null;
    }
}
