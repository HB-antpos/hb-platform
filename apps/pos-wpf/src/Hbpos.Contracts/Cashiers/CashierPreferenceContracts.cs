namespace Hbpos.Contracts.Cashiers;

/// <summary>收银员个人界面偏好的键：按客户端区分，存主库 dbo.UserPreference，换收银机也跟着人走。</summary>
public static class CashierPreferenceKeys
{
    public const string WpfColorTheme = "pos-wpf.colorTheme";
}

/// <summary>WPF 收银端可选的界面配色；名称与客户端 PosColorTheme 枚举一致，服务端只接受这些值。</summary>
public static class CashierColorThemes
{
    public static IReadOnlyList<string> All { get; } = ["Graphite", "Celadon", "Blue", "Elder", "Candy", "Sunny", "Ocean", "Rainbow"];

    /// <summary>忽略大小写匹配到规范名称；不认识的值返回 null。</summary>
    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return All.FirstOrDefault(theme => string.Equals(theme, trimmed, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// 当前收银员的配色偏好。UserGuid 是票据里的员工，客户端用它确认应答属于自己请求时的那位收银员；
/// ColorTheme 为 null 表示该员工从未选过配色。
/// </summary>
public sealed record CashierColorThemePreferenceDto(
    string UserGuid,
    string? ColorTheme);

/// <summary>
/// 保存配色偏好。UserGuid 必须与收银员票据一致：切换收银员后迟到的旧请求会被拒绝，不会写到新收银员名下。
/// </summary>
public sealed record CashierColorThemePreferenceUpdateRequest(
    string UserGuid,
    string ColorTheme);
