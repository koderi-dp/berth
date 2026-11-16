using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Berth.Tests.Transport;

/// <summary>A throwaway CA and certificates it signs, for TLS servers and clients in tests.</summary>
internal static class TestCertificates
{
    public static X509Certificate2 CreateCa(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now.AddDays(2));
    }

    /// <summary>Creates a certificate signed by <paramref name="ca"/>, re-imported so Windows TLS can use its private key.</summary>
    public static X509Certificate2 CreateLeaf(X509Certificate2 ca, string commonName, bool serverAuth, out string keyPem)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(serverAuth ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")], critical: false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        if (serverAuth)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName(commonName);
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
        }

        var now = DateTimeOffset.UtcNow;
        using var signed = request.Create(ca, now.AddHours(-1), now.AddDays(1), RandomNumberGenerator.GetBytes(16));
        using var withKey = signed.CopyWithPrivateKey(key);
        keyPem = key.ExportPkcs8PrivateKeyPem();
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
    }
}
