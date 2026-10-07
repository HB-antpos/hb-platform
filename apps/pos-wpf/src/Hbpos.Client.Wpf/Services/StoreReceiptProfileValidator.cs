using Hbpos.Contracts.Stores;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 门店小票资料的客户端校验口径（与 Hbpos.Api 的 StoreReceiptProfileGuard 保持一致）。
/// 设置页「载入门店资料」和后台自动同步总部下发的资料共用这一份，保证两条写入路径口径相同。
/// </summary>
public static class StoreReceiptProfileValidator
{
    public const int ReturnPolicyMaxLength = 500;

    /// <summary>
    /// 返回 null 表示通过，否则返回不含资料内容的错误说明（可以直接写日志或展示）。
    /// <paramref name="requireStoreName"/> 只给下发同步使用：下发快照的门店名必须非空
    /// （总部下发时已校验，这里再兜底，避免把空店名写成小票抬头）；手工载入保持原有口径不要求。
    /// </summary>
    public static string? Validate(StoreReceiptProfileDto profile, bool requireStoreName = false)
    {
        // 仅地址/退货政策允许 CR、LF、TAB；其余资料字段拒绝任何控制字符（含 DEL）。
        if (ContainsRejectedControlCharacter(profile.StoreCode, allowLineBreaksAndTabs: false) ||
            ContainsRejectedControlCharacter(profile.StoreName, allowLineBreaksAndTabs: false) ||
            ContainsRejectedControlCharacter(profile.BrandName, allowLineBreaksAndTabs: false) ||
            ContainsRejectedControlCharacter(profile.Phone, allowLineBreaksAndTabs: false) ||
            ContainsRejectedControlCharacter(profile.Abn, allowLineBreaksAndTabs: false) ||
            ContainsRejectedControlCharacter(profile.Address, allowLineBreaksAndTabs: true) ||
            ContainsRejectedControlCharacter(profile.ReturnPolicy, allowLineBreaksAndTabs: true))
        {
            return "Store receipt profile contains invalid control characters.";
        }

        if (profile.ReturnPolicy is { Length: > ReturnPolicyMaxLength })
        {
            return "Return policy exceeds the maximum length of 500 characters.";
        }

        if (requireStoreName && string.IsNullOrWhiteSpace(profile.StoreName))
        {
            return "Store receipt profile has an empty store name.";
        }

        return null;
    }

    private static bool ContainsRejectedControlCharacter(string? value, bool allowLineBreaksAndTabs)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (ch is '\r' or '\n' or '\t')
            {
                if (!allowLineBreaksAndTabs)
                {
                    return true;
                }

                continue;
            }

            if (char.IsControl(ch))
            {
                return true;
            }
        }

        return false;
    }
}
