namespace BlazorApp.Shared.Security;

/// <summary>
/// Linkly Cloud 多终端凭据的跨进程保护契约。
/// Username 是用于唯一匹配的登录标识，Password 与配对后的 Secret 必须使用独立 purpose 加密保存。
/// </summary>
public static class LinklyCloudTerminalCredentialDataProtection
{
    public const string ApplicationName = "HB.Linkly.CloudTerminalCredentials";
    public const string PasswordPurpose = "HB.Linkly.CloudTerminalCredentials.Password.v1";
    public const string SecretPurpose = "HB.Linkly.CloudTerminalCredentials.Secret.v1";
    public const byte LegacyPlaintextVersion = 0;
    public const byte CurrentVersion = 1;

    /// <summary>
    /// 非生产环境未显式配置密钥目录时，Admin 与 POS API 共用的默认目录。
    /// 两个进程的内容根目录不同，各自回落到“程序目录/App_Data”会让两侧密钥环不一致，
    /// 表现为本机后台录入成功、POS 却永远解不开。取不到用户目录时返回 null，调用方沿用各自的旧默认值。
    /// </summary>
    public static string? ResolveSharedDevelopmentKeysPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(root)
            ? null
            : Path.Combine(root, "HbPlatform", "LinklyCloudCredentialDataProtectionKeys");
    }
}

public interface ILinklyCloudTerminalCredentialProtector
{
    string ProtectPassword(string password);

    string UnprotectPassword(string protectedPassword);

    string ProtectSecret(string secret);

    string UnprotectSecret(string protectedSecret);
}
