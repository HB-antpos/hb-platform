using Microsoft.AspNetCore.DataProtection;

namespace BlazorApp.Api.Security;

/// <summary>
/// MYOB 对接的两类敏感数据：refresh token（落库，必须可跨重启 / 部署解密）与 OAuth state（10 分钟内有效的一次性令牌）。
/// 复用主后端持久化 key ring，仅以独立 purpose 隔离，不同用途的密文互相解不开。
/// </summary>
public static class MyobDataProtection
{
    public const string RefreshTokenPurpose = "HB.Platform.Myob.RefreshToken.v1";
    public const string OAuthStatePurpose = "HB.Platform.Myob.OAuthState.v1";

    public static MyobSecretProtector CreateProtector(IDataProtectionProvider provider) =>
        new(
            provider.CreateProtector(RefreshTokenPurpose),
            provider.CreateProtector(OAuthStatePurpose).ToTimeLimitedDataProtector());
}

public sealed class MyobSecretProtector(IDataProtector refreshToken, ITimeLimitedDataProtector oauthState)
{
    public string ProtectRefreshToken(string value) => refreshToken.Protect(value);

    public string UnprotectRefreshToken(string value) => refreshToken.Unprotect(value);

    /// <summary>生成带过期时间的 state；过期或被篡改时 <see cref="TryUnprotectState"/> 返回 false。</summary>
    public string ProtectState(string value, DateTimeOffset expiresAt) => oauthState.Protect(value, expiresAt);

    public bool TryUnprotectState(string protectedValue, out string value)
    {
        value = string.Empty;
        try
        {
            value = oauthState.Unprotect(protectedValue);
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // 过期（CryptographicException 的子类）、被篡改、purpose 不符都按无效 state 处理，不向外区分原因。
            return false;
        }
    }
}
