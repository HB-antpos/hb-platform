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

    // 收银端手持/iPad 本机校验上限（apps/pos-handheld|pos-ipad 的 pos-settings-repository.ts），超限整份丢弃且永远不更新；
    // HBweb 的 Store 列宽更大（地址 500、电话 200），总部能保存超限内容，所以必须在发布时拦截。
    // 长度口径是 UTF-16 码元：JS 的 string.length 与 .NET 的 string.Length 一致。
    public const int MaxBrandNameLength = 120;
    public const int MaxStoreNameLength = 120;
    public const int MaxAddressLength = 240;
    public const int MaxPhoneLength = 60;
    public const int MaxAbnLength = 32;
    public const int MaxReturnPolicyLength = 500;

    /// <summary>某个字段超过收银端上限：中文字段名、上限与当前长度。</summary>
    public readonly record struct TooLongField(string DisplayName, int Limit, int Length);

    /// <summary>
    /// 按收银端上限检查 6 个字段，返回全部超限的字段（按「品牌、店名、地址、电话、ABN、退货政策」固定顺序）；全部合法返回空列表。
    /// 传入的必须是归一后的值（trim、空白变 null，与快照写入口径相同）：首尾空白不会被写进快照，也就不该计入长度。
    /// 品牌/店名/ABN/退货政策在现有列宽下不可能超限，仍统一检查，防止以后 HBweb 列宽改大后悄悄放行。
    /// </summary>
    public static IReadOnlyList<TooLongField> FindTooLongFields(
        string? brandName,
        string? storeName,
        string? address,
        string? phone,
        string? abn,
        string? returnPolicy)
    {
        var result = new List<TooLongField>();
        Check(result, "品牌", brandName, MaxBrandNameLength);
        Check(result, "店名", storeName, MaxStoreNameLength);
        Check(result, "地址", address, MaxAddressLength);
        Check(result, "电话", phone, MaxPhoneLength);
        Check(result, "ABN", abn, MaxAbnLength);
        Check(result, "退货政策", returnPolicy, MaxReturnPolicyLength);
        return result;
    }

    private static void Check(List<TooLongField> result, string displayName, string? value, int limit)
    {
        if (value is not null && value.Length > limit)
        {
            result.Add(new TooLongField(displayName, limit, value.Length));
        }
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
