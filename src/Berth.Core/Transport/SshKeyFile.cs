namespace Berth.Transport;

/// <summary>Whether a key file holds a private key at all, told before it is opened, so a wrong file is never taken for a wrong passphrase.</summary>
public static class SshKeyFile
{
    /// <summary>
    /// Text with a PEM private key block (OpenSSH, PKCS#1, PKCS#8, EC, encrypted or not) or a PuTTY key file. A public key (a <c>.pub</c>
    /// file), a certificate or anything else is not.
    /// </summary>
    public static bool LooksLikePrivateKey(string text)
    {
        var begin = text.IndexOf("-----BEGIN ", StringComparison.Ordinal);
        if (begin >= 0)
        {
            var end = text.IndexOf("-----", begin + 11, StringComparison.Ordinal);
            return end > 0 && text[(begin + 11)..end].EndsWith("PRIVATE KEY", StringComparison.Ordinal);
        }

        return text.StartsWith("PuTTY-User-Key-File-", StringComparison.Ordinal);
    }
}
