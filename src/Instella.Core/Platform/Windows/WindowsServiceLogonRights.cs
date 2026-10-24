using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Instella.Core.Platform.Windows;

/// <summary>
/// <see cref="IServiceLogonRights"/> over the Local Security Authority: <c>LsaOpenPolicy</c>,
/// <c>LookupAccountNameW</c>, then <c>LsaEnumerateAccountRights</c> / <c>LsaAddAccountRights</c> /
/// <c>LsaRemoveAccountRights</c> with <c>SeServiceLogonRight</c>. Granting and revoking need an
/// elevated caller.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsServiceLogonRights : IServiceLogonRights
{
    internal const string Right = "SeServiceLogonRight";

    private const uint POLICY_VIEW_LOCAL_INFORMATION = 0x0001;
    private const uint POLICY_CREATE_ACCOUNT = 0x0010;
    private const uint POLICY_LOOKUP_NAMES = 0x0800;
    private const uint STATUS_OBJECT_NAME_NOT_FOUND = 0xC0000034;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    public Task<LogonRightQuery> HasAsync(string account, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(account);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Has(account));
    }

    public Task<PlatformResult> GrantAsync(string account, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(account);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Change(account, grant: true));
    }

    public Task<PlatformResult> RevokeAsync(string account, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(account);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Change(account, grant: false));
    }

    private static unsafe LogonRightQuery Has(string account)
    {
        var sid = LookupSid(account, out var error);
        if (sid is null) return LogonRightQuery.Fail($"Could not find the account '{account}': {Describe(error)}");
        var status = OpenPolicy(POLICY_LOOKUP_NAMES | POLICY_VIEW_LOCAL_INFORMATION, out var policy);
        if (status != 0) return LogonRightQuery.Fail($"Could not open the local security policy: {DescribeStatus(status)}");
        try
        {
            fixed (byte* pSid = sid)
            {
                status = NativeMethods.LsaEnumerateAccountRights(policy, (nint)pSid, out var rights, out var count);
                if (status == STATUS_OBJECT_NAME_NOT_FOUND) return LogonRightQuery.Of(false);
                if (status != 0)
                    return LogonRightQuery.Fail($"Could not read the rights of '{account}': {DescribeStatus(status)}");
                try
                {
                    var entries = (LsaUnicodeString*)rights;
                    for (var i = 0; i < count; i++)
                    {
                        var name = new string((char*)entries[i].Buffer, 0, entries[i].Length / 2);
                        if (string.Equals(name, Right, StringComparison.OrdinalIgnoreCase)) return LogonRightQuery.Of(true);
                    }
                    return LogonRightQuery.Of(false);
                }
                finally
                {
                    NativeMethods.LsaFreeMemory(rights);
                }
            }
        }
        finally
        {
            NativeMethods.LsaClose(policy);
        }
    }

    private static unsafe PlatformResult Change(string account, bool grant)
    {
        var verb = grant ? "grant" : "revoke";
        var sid = LookupSid(account, out var error);
        if (sid is null) return PlatformResult.Fail($"Could not {verb} '{Right}' for '{account}': {Describe(error)}");
        var status = OpenPolicy(POLICY_LOOKUP_NAMES | POLICY_CREATE_ACCOUNT, out var policy);
        if (status != 0) return PlatformResult.Fail($"Could not open the local security policy: {DescribeStatus(status)}");
        try
        {
            fixed (byte* pSid = sid)
            fixed (char* pRight = Right)
            {
                var right = new LsaUnicodeString
                {
                    Length = (ushort)(Right.Length * 2),
                    MaximumLength = (ushort)(Right.Length * 2),
                    Buffer = (nint)pRight,
                };
                status = grant
                    ? NativeMethods.LsaAddAccountRights(policy, (nint)pSid, &right, 1)
                    : NativeMethods.LsaRemoveAccountRights(policy, (nint)pSid, 0, &right, 1);
                // Removing a right the account never had is not an error.
                if (status == 0 || (!grant && status == STATUS_OBJECT_NAME_NOT_FOUND)) return PlatformResult.Ok;
                return PlatformResult.Fail($"Could not {verb} '{Right}' for '{account}': {DescribeStatus(status)}");
            }
        }
        finally
        {
            NativeMethods.LsaClose(policy);
        }
    }

    private static unsafe uint OpenPolicy(uint access, out nint policy)
    {
        var attributes = new LsaObjectAttributes { Length = (uint)sizeof(LsaObjectAttributes) };
        return NativeMethods.LsaOpenPolicy(0, &attributes, access, out policy);
    }

    /// <summary>The account's SID, or null with the Win32 error. <c>.\name</c> is looked up on this machine.</summary>
    private static byte[]? LookupSid(string account, out int error)
    {
        var name = account.StartsWith(@".\", StringComparison.Ordinal) ? Environment.MachineName + account[1..] : account;
        uint sidSize = 0, domainSize = 0;
        NativeMethods.LookupAccountNameW(null, name, null, ref sidSize, null, ref domainSize, out _);
        error = Marshal.GetLastPInvokeError();
        if (error != ERROR_INSUFFICIENT_BUFFER || sidSize == 0) return null;
        var sid = new byte[sidSize];
        var domain = new char[domainSize];
        if (NativeMethods.LookupAccountNameW(null, name, sid, ref sidSize, domain, ref domainSize, out _) == 0)
        {
            error = Marshal.GetLastPInvokeError();
            return null;
        }
        error = 0;
        return sid;
    }

    private static string DescribeStatus(uint status) => Describe((int)NativeMethods.LsaNtStatusToWinError(status));

    private static string Describe(int error) => $"{new Win32Exception(error).Message} (error {error})";

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaObjectAttributes
    {
        public uint Length;
        public nint RootDirectory;
        public nint ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor;
        public nint SecurityQualityOfService;
    }

    private static unsafe partial class NativeMethods
    {
        private const string Advapi32 = "advapi32.dll";

        [LibraryImport(Advapi32)]
        public static partial uint LsaOpenPolicy(nint systemName, LsaObjectAttributes* objectAttributes, uint desiredAccess, out nint policyHandle);

        [LibraryImport(Advapi32)]
        public static partial uint LsaEnumerateAccountRights(nint policyHandle, nint accountSid, out nint userRights, out uint countOfRights);

        [LibraryImport(Advapi32)]
        public static partial uint LsaAddAccountRights(nint policyHandle, nint accountSid, LsaUnicodeString* userRights, uint countOfRights);

        [LibraryImport(Advapi32)]
        public static partial uint LsaRemoveAccountRights(nint policyHandle, nint accountSid, byte allRights, LsaUnicodeString* userRights, uint countOfRights);

        [LibraryImport(Advapi32)]
        public static partial uint LsaFreeMemory(nint buffer);

        [LibraryImport(Advapi32)]
        public static partial uint LsaClose(nint objectHandle);

        [LibraryImport(Advapi32)]
        public static partial uint LsaNtStatusToWinError(uint status);

        [LibraryImport(Advapi32, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial int LookupAccountNameW(
            string? systemName, string accountName, byte[]? sid, ref uint sidSize,
            char[]? referencedDomainName, ref uint domainSize, out int use);
    }
}
