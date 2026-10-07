namespace BlazorApp.Api.Services.StoreReceiptProfiles;

/// <summary>
/// 下发前的字符守卫，口径与 Hbpos.Api 的 StoreReceiptProfileGuard 完全一致（HBweb 不能引用 Hbpos.Api，故在此保留一份等价实现）：
/// 仅 Address 与 ReturnPolicy 需要 CR/LF/TAB 排版；其余字段（含 StoreCode/StoreName/BrandName/Phone/Abn）
/// 任何控制字符都会污染小票草稿。如果这里放行了 Hbpos.Api 会拒绝的内容，下发后收银端的资料接口会整体 400，
/// 所以两边口径必须同步修改。
/// </summary>
public static class StoreReceiptProfileGuard
{
    /// <summary>返回第一个含非法字符的字段名；全部合法返回 null。</summary>
    public static string? FindInvalidField(
        string? storeCode,
        string? storeName,
        string? brandName,
        string? address,
        string? phone,
        string? abn,
        string? returnPolicy)
    {
        if (!NoControlCharacters(storeCode)) return "StoreCode";
        if (!NoControlCharacters(storeName)) return "StoreName";
        if (!NoControlCharacters(brandName)) return "BrandName";
        if (!NoControlCharacters(phone)) return "Phone";
        if (!NoControlCharacters(abn)) return "ABN";
        if (!AllowedMultiline(address)) return "Address";
        if (!AllowedMultiline(returnPolicy)) return "ReturnPolicy";
        return null;
    }

    private static bool NoControlCharacters(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        foreach (var ch in value)
        {
            if (char.IsControl(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AllowedMultiline(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        foreach (var ch in value)
        {
            if (char.IsControl(ch) && ch is not '\r' and not '\n' and not '\t')
            {
                return false;
            }
        }

        return true;
    }
}
