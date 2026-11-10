using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AssetScope.Core.Security;

/// <summary>
/// Windows Credential Manager storage for the Plaid Secret (SPEC §13.1).
/// The secret never touches settings files, logs, or command lines; it lives
/// in the user's credential locker and is handed to the sidecar via its
/// process environment at launch.
/// </summary>
public static class WindowsCredentials
{
    public const string PlaidSecretTarget = "assetscope/PlaidSecret";
    public const string SnapTradeConsumerKeyTarget = "assetscope/SnapTradeConsumerKey";
    private const string LegacyPlaidSecretTarget = "fungible-winui/PlaidSecret";
    private const string LegacySnapTradeConsumerKeyTarget = "fungible-winui/SnapTradeConsumerKey";

    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, uint type, uint flags, out nint credentialPtr);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint credentialPtr);

    public static void Save(string target, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        var targetPtr = Marshal.StringToHGlobalUni(target);
        var userPtr = Marshal.StringToHGlobalUni(
            target == SnapTradeConsumerKeyTarget ? "snaptrade" : "plaid");
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var credential = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = targetPtr,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = userPtr,
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not write to Windows Credential Manager.");
        }
        finally
        {
            // Zero the unmanaged copy of the secret before freeing.
            for (var i = 0; i < blob.Length; i++) Marshal.WriteByte(blobPtr, i, 0);
            Marshal.FreeHGlobal(blobPtr);
            Marshal.FreeHGlobal(targetPtr);
            Marshal.FreeHGlobal(userPtr);
            Array.Clear(blob);
        }
    }

    public static string? Read(string target)
    {
        var value = ReadExact(target);
        if (value is not null) return value;

        var legacyTarget = target switch
        {
            PlaidSecretTarget => LegacyPlaidSecretTarget,
            SnapTradeConsumerKeyTarget => LegacySnapTradeConsumerKeyTarget,
            _ => null,
        };
        if (legacyTarget is null) return null;
        value = ReadExact(legacyTarget);
        if (value is not null) Save(target, value);
        return value;
    }

    private static string? ReadExact(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var ptr))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ERROR_NOT_FOUND) return null;
            throw new Win32Exception(error, "Could not read from Windows Credential Manager.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (credential.CredentialBlob == 0 || credential.CredentialBlobSize == 0) return null;
            return Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public static void Delete(string target)
    {
        if (!CredDelete(target, CRED_TYPE_GENERIC, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ERROR_NOT_FOUND)
                throw new Win32Exception(error, "Could not delete from Windows Credential Manager.");
        }
    }
}
