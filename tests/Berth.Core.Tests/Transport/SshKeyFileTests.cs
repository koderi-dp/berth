using Berth.Transport;

namespace Berth.Tests.Transport;

public sealed class SshKeyFileTests
{
    [Theory]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nb3Blbg==\n-----END OPENSSH PRIVATE KEY-----\n")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nProc-Type: 4,ENCRYPTED\n\nabc\n-----END RSA PRIVATE KEY-----")]
    [InlineData("-----BEGIN EC PRIVATE KEY-----\nabc\n-----END EC PRIVATE KEY-----")]
    [InlineData("-----BEGIN ENCRYPTED PRIVATE KEY-----\nabc\n-----END ENCRYPTED PRIVATE KEY-----")]
    [InlineData("PuTTY-User-Key-File-3: ssh-ed25519\nEncryption: none\n")]
    public void A_private_key_is_recognised(string text) => Assert.True(SshKeyFile.LooksLikePrivateKey(text));

    [Theory]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI berth-check")]
    [InlineData("-----BEGIN CERTIFICATE-----\nabc\n-----END CERTIFICATE-----")]
    [InlineData("")]
    [InlineData("hello")]
    public void A_public_key_a_certificate_or_anything_else_is_not(string text) => Assert.False(SshKeyFile.LooksLikePrivateKey(text));
}
