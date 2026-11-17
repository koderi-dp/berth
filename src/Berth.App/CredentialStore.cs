using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Berth.Connections;

namespace Berth;

/// <summary>
/// SSH passwords and passphrases in Windows Credential Manager: one generic credential per connection and kind, named
/// <c>Koderi.Berth/&lt;connection id&gt;/password</c> or <c>…/passphrase</c>, kept for this user on this computer (not roamed). The
/// secret is the credential's blob (UTF-16, as <c>cmdkey</c> stores one); its user name is "Berth". Nothing here is logged, and a
/// failure's exception carries Windows' own message, never the secret.
/// </summary>
internal sealed class CredentialStore : ISecretStore
{
    private const uint GenericType = 1;
    private const uint PersistLocalMachine = 2;
    private const int NotFound = 1168;

    /// <summary>The credential's name in Credential Manager (shown by <c>cmdkey /list</c>).</summary>
    public static string TargetName(string connectionId, SecretKind kind) =>
        $"Koderi.Berth/{connectionId}/{(kind == SecretKind.Password ? "password" : "passphrase")}";

    public string? Get(string connectionId, SecretKind kind)
    {
        if (!CredRead(TargetName(connectionId, kind), GenericType, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            return error == NotFound ? null : throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            var bytes = new byte[credential.CredentialBlobSize];
            try
            {
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                return Encoding.Unicode.GetString(bytes);
            }
            finally
            {
                Array.Clear(bytes);
            }
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Set(string connectionId, SecretKind kind, string secret)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
        var target = Marshal.StringToHGlobalUni(TargetName(connectionId, kind));
        var user = Marshal.StringToHGlobalUni("Berth");
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = GenericType,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = user,
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Array.Clear(bytes);
            // Overwrite the unmanaged copy before freeing it.
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
            Marshal.FreeHGlobal(target);
            Marshal.FreeHGlobal(user);
        }
    }

    public void Delete(string connectionId, SecretKind kind)
    {
        if (!CredDelete(TargetName(connectionId, kind), GenericType, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != NotFound)
                throw new Win32Exception(error);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
