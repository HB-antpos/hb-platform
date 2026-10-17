using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Hbpos.Client.Wpf.Services.NetworkDiagnostics;

/// <summary>
/// 一键修复可以安装的受信任根证书。只收 Let's Encrypt 自己的公共根（所有主流系统的信任库里都有），
/// 证书内容编进程序，安装前再用写死的 SHA-256 指纹逐个校验，指纹对不上就拒绝，不接受任何外部来源的证书。
/// 服务器发出的证书链用 ISRG Root X1 交叉签名，所以信任 X1 就够了，不需要再装 X2 等其他根。
/// </summary>
public sealed record TrustedRootCertificate(string Name, string Sha256, string Sha1, string Base64Der)
{
    public byte[] GetDer() => Convert.FromBase64String(Base64Der);

    /// <summary>证书内容的 SHA-256 是否等于写死的指纹。</summary>
    public bool MatchesPinnedFingerprint()
    {
        return string.Equals(
            Convert.ToHexString(SHA256.HashData(GetDer())),
            Sha256,
            StringComparison.OrdinalIgnoreCase);
    }
}

public static class TrustedRootCertificates
{
    public static readonly TrustedRootCertificate IsrgRootX1 = new(
        "ISRG Root X1",
        "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" +
        "m+kXQ99b21/+jh5Xos1AnX5iItreGCc=");

    public static IReadOnlyList<TrustedRootCertificate> All { get; } = [IsrgRootX1];

    /// <summary>本机（本机或当前用户的受信任根 / 自动更新根）里有没有这张根证书。</summary>
    public static bool IsInstalled(TrustedRootCertificate root)
    {
        var expected = Convert.FromHexString(root.Sha256);
        foreach (var (name, location) in new[]
                 {
                     (StoreName.Root, StoreLocation.LocalMachine),
                     (StoreName.AuthRoot, StoreLocation.LocalMachine),
                     (StoreName.Root, StoreLocation.CurrentUser)
                 })
        {
            using var store = new X509Store(name, location);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            foreach (var certificate in store.Certificates)
            {
                if (certificate.GetCertHash(HashAlgorithmName.SHA256).AsSpan().SequenceEqual(expected))
                {
                    return true;
                }
            }
        }

        return false;
    }
}