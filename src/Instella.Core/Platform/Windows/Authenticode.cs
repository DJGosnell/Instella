using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace Instella.Core.Platform.Windows;

/// <summary>A file's Authenticode status.</summary>
/// <param name="IsValid">The signature verifies and chains to a trusted root.</param>
/// <param name="SignerSubject">The leaf signer certificate's subject distinguished name, when the file carries a signature (also when <paramref name="IsValid"/> is false).</param>
/// <param name="Status">The <c>WinVerifyTrust</c> result (0 = valid).</param>
internal readonly record struct AuthenticodeInfo(bool IsValid, string? SignerSubject, int Status);

/// <summary>Reads a file's Authenticode signature.</summary>
internal interface IAuthenticodeReader
{
    AuthenticodeInfo Read(string path);
}

/// <summary>Off Windows there is no Authenticode: every file reads as unsigned, so checks are skipped.</summary>
internal sealed class NoAuthenticodeReader : IAuthenticodeReader
{
    public static NoAuthenticodeReader Instance { get; } = new();

    public AuthenticodeInfo Read(string path) => new(false, null, -1);
}

/// <summary>
/// <c>WinVerifyTrust</c> with <c>WINTRUST_ACTION_GENERIC_VERIFY_V2</c>, no UI, and revocation
/// checked from the cache only (chain excluding the root), so an offline machine never stalls for
/// minutes on CRL downloads. Revocation that cannot be determined from the cache counts as valid;
/// a certificate the cache knows is revoked does not.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class Authenticode : IAuthenticodeReader
{
    public static Authenticode Instance { get; } = new();

    private static Guid s_verifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT = 0x80;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

    // Revocation could not be checked (offline, nothing cached): not a verdict.
    private const int CERT_E_REVOCATION_FAILURE = unchecked((int)0x800B010E);
    private const int CRYPT_E_REVOCATION_OFFLINE = unchecked((int)0x80092013);
    private const int CRYPT_E_NO_REVOCATION_CHECK = unchecked((int)0x80092012);

    public unsafe AuthenticodeInfo Read(string path)
    {
        fixed (char* pathPtr = path)
        {
            var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)sizeof(WINTRUST_FILE_INFO), pcwszFilePath = (nint)pathPtr };
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)sizeof(WINTRUST_DATA),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = (nint)(&fileInfo),
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT | WTD_CACHE_ONLY_URL_RETRIEVAL,
            };
            var status = WinVerifyTrust(0, ref s_verifyV2, ref data);
            try
            {
                var valid = status == 0 || status is CERT_E_REVOCATION_FAILURE or CRYPT_E_REVOCATION_OFFLINE or CRYPT_E_NO_REVOCATION_CHECK;
                // The subject is reported even when the chain is not trusted (for diagnostics and
                // the Signing-stage test's self-signed certificates); callers decide on IsValid.
                return new AuthenticodeInfo(valid, SignerSubject(data.hWVTStateData), status);
            }
            finally
            {
                // Always release the state WinVerifyTrust kept for the helpers above.
                data.dwStateAction = WTD_STATEACTION_CLOSE;
                WinVerifyTrust(0, ref s_verifyV2, ref data);
            }
        }
    }

    /// <summary>The leaf signer's subject: the publisher's identity, stable across certificate renewals.</summary>
    private static string? SignerSubject(nint state)
    {
        if (state == 0) return null;
        var provData = WTHelperProvDataFromStateData(state);
        if (provData == 0) return null;
        var signer = WTHelperGetProvSignerFromChain(provData, 0, 0, 0);
        if (signer == 0) return null;
        var provCert = WTHelperGetProvCertFromChain(signer, 0);
        if (provCert == 0) return null;
        // CRYPT_PROVIDER_CERT { DWORD cbStruct; PCCERT_CONTEXT pCert; ... }: pCert is pointer-aligned.
        var certContext = Marshal.ReadIntPtr(provCert, nint.Size);
        if (certContext == 0) return null;
        using var certificate = new X509Certificate2(certContext);   // duplicates the context
        return certificate.SubjectName.Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public nint pcwszFilePath;
        public nint hFile;
        public nint pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public nint pPolicyCallbackData;
        public nint pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public nint pFile;
        public uint dwStateAction;
        public nint hWVTStateData;
        public nint pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public nint pSignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(nint hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    [LibraryImport("wintrust.dll")]
    private static partial nint WTHelperProvDataFromStateData(nint hStateData);

    [LibraryImport("wintrust.dll")]
    private static partial nint WTHelperGetProvSignerFromChain(nint pProvData, uint idxSigner, int fCounterSigner, uint idxCounterSigner);

    [LibraryImport("wintrust.dll")]
    private static partial nint WTHelperGetProvCertFromChain(nint pSgnr, uint idxCert);
}
