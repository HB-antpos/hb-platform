using System.Security.Cryptography.X509Certificates;
using Hbpos.Client.Wpf.Services.NetworkDiagnostics;

namespace Hbpos.Client.Tests;

public sealed class TrustedRootCertificatesTests
{
    [Fact]
    public void The_built_in_root_matches_its_pinned_fingerprints_and_is_a_self_signed_ca()
    {
        var root = TrustedRootCertificates.IsrgRootX1;

        // 这两个指纹是 Let's Encrypt 公布的 ISRG Root X1 的公开值，写死在测试里，防止内置证书被悄悄换掉。
        Assert.Equal("96BCEC06264976F37460779ACF28C5A7CFE8A3C0AAE11A8FFCEE05C0BDDF08C6", root.Sha256);
        Assert.Equal("CABD2A79A1076A31F21D253635CB039D4329A5E8", root.Sha1);
        Assert.True(root.MatchesPinnedFingerprint());

        using var certificate = new X509Certificate2(root.GetDer());
        Assert.Equal("CN=ISRG Root X1, O=Internet Security Research Group, C=US", certificate.Subject);
        Assert.Equal(certificate.Subject, certificate.Issuer);
        Assert.Equal(root.Sha1, certificate.Thumbprint, ignoreCase: true);
        Assert.True(certificate.NotAfter > DateTime.UtcNow.AddYears(5), "built-in root should stay valid for years");
        Assert.True(certificate.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
    }

    [Fact]
    public void A_tampered_certificate_no_longer_matches_the_pinned_fingerprint()
    {
        var tampered = TrustedRootCertificates.IsrgRootX1 with { Base64Der = "AAAA" + TrustedRootCertificates.IsrgRootX1.Base64Der[4..] };

        Assert.False(tampered.MatchesPinnedFingerprint());
    }

    [Fact]
    public void The_list_only_holds_the_built_in_roots_and_the_install_check_does_not_throw()
    {
        Assert.Equal(["ISRG Root X1"], TrustedRootCertificates.All.Select(root => root.Name).ToArray());
        Assert.All(TrustedRootCertificates.All, root => Assert.True(root.MatchesPinnedFingerprint(), root.Name));

        // 在 CI / 开发机上结果可能是 true 或 false，这里只要求能正常读取证书库、不抛异常。
        _ = TrustedRootCertificates.IsInstalled(TrustedRootCertificates.IsrgRootX1);
    }
}