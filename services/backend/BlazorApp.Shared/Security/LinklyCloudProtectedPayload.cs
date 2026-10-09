namespace BlazorApp.Shared.Security;

/// <summary>
/// 只读解析 ASP.NET Core Data Protection 密文头部，用于在解密失败时报告“缺的是哪把 key”。
/// 格式：4 字节魔数 0x09F0C9F0（大端）+ 16 字节 key id（Guid 内存布局）+ 载荷。只读头部，不触碰也不记录载荷内容。
/// </summary>
public static class LinklyCloudProtectedPayload
{
    private static readonly byte[] MagicHeader = [0x09, 0xF0, 0xC9, 0xF0];

    /// <summary>密文是合法的 Data Protection 载荷头时返回其 key id；无法解析（例如被截断/手工改写）时返回 false。</summary>
    public static bool TryReadKeyId(string? protectedValue, out Guid keyId)
    {
        keyId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return false;
        }

        var normalized = protectedValue.Trim().Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(normalized);
        }
        catch (FormatException)
        {
            return false;
        }

        if (bytes.Length < MagicHeader.Length + 16)
        {
            return false;
        }

        for (var index = 0; index < MagicHeader.Length; index++)
        {
            if (bytes[index] != MagicHeader[index])
            {
                return false;
            }
        }

        keyId = new Guid(bytes.AsSpan(MagicHeader.Length, 16));
        return true;
    }
}
