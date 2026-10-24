using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Instella.Core.Platform.Windows;

/// <summary>
/// <see cref="ICredentialChecker"/> over <c>LogonUserW</c> with a network logon: the token is closed
/// at once. <c>ERROR_LOGON_FAILURE</c> is a bad user name or password; every other failure is a
/// refusal. The password goes to Windows from a pinned, zeroed-after copy, never as a string.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsCredentialChecker : ICredentialChecker
{
    private const int LOGON32_LOGON_NETWORK = 3;
    private const int LOGON32_PROVIDER_DEFAULT = 0;
    private const int ERROR_LOGON_FAILURE = 1326;

    public Task<CredentialCheckResult> CheckAsync(string user, char[] password, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(user);
        ArgumentNullException.ThrowIfNull(password);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Check(user, password));
    }

    private static unsafe CredentialCheckResult Check(string user, char[] password)
    {
        // DOMAIN\name and .\name split; a UPN (name@domain) goes whole with no domain.
        string? domain = null;
        var name = user;
        var slash = user.IndexOf('\\', StringComparison.Ordinal);
        if (slash >= 0)
        {
            domain = user[..slash];
            name = user[(slash + 1)..];
        }

        var terminated = new char[password.Length + 1];
        try
        {
            password.CopyTo(terminated, 0);
            int ok, error;
            nint token;
            fixed (char* pPassword = terminated)
            {
                ok = NativeMethods.LogonUserW(name, domain, pPassword, LOGON32_LOGON_NETWORK, LOGON32_PROVIDER_DEFAULT, out token);
                error = ok == 0 ? Marshal.GetLastPInvokeError() : 0;
            }
            if (ok != 0)
            {
                NativeMethods.CloseHandle(token);
                return CredentialCheckResult.Valid;
            }
            var message = $"{new Win32Exception(error).Message} (error {error})";
            return new CredentialCheckResult(
                error == ERROR_LOGON_FAILURE ? CredentialCheckOutcome.BadCredentials : CredentialCheckOutcome.Refused, message);
        }
        finally
        {
            Array.Clear(terminated);
        }
    }

    private static unsafe partial class NativeMethods
    {
        [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial int LogonUserW(string userName, string? domain, char* password, int logonType, int logonProvider, out nint token);

        [LibraryImport("kernel32.dll")]
        public static partial int CloseHandle(nint handle);
    }
}
