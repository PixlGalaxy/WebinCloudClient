using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CloudClient.Auth;

/// <summary>The device's tokens; the access token is short-lived, the refresh token changes on each use.</summary>
public sealed record TokenSet(string AccessToken, string RefreshToken, DateTimeOffset AccessExpiresAt);

/// <summary>
/// Keeps the tokens in the Windows Credential Manager, encrypted for the
/// signed-in Windows user, under "{Brand.Id}:{server}". They never touch the
/// settings file or the log.
/// </summary>
public static class TokenStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    private static string Target(string serverUrl) => $"{Brand.Id}:{serverUrl}";

    public static void Save(string serverUrl, TokenSet tokens)
    {
        var blob = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokens));
        var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
        try
        {
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = Target(serverUrl),
                CredentialBlobSize = blob.Length,
                CredentialBlob = handle.AddrOfPinnedObject(),
                Persist = CredPersistLocalMachine,
                UserName = Brand.Id,
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            handle.Free();
        }
    }

    public static TokenSet? Load(string serverUrl)
    {
        if (!CredRead(Target(serverUrl), CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new Win32Exception(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            var blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            return JsonSerializer.Deserialize<TokenSet>(blob);
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public static void Delete(string serverUrl) => CredDelete(Target(serverUrl), CredTypeGeneric, 0);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);
}
