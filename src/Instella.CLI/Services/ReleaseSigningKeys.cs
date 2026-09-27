using System.Diagnostics;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Instella.Core.Trust;

namespace Instella.CLI.Services;

/// <summary>Something that can sign a release manifest: a local private key or an external command.</summary>
internal interface IReleaseSigningKey
{
    /// <summary>The public key the signature verifies against.</summary>
    PublisherKey PublicKey { get; }

    /// <summary>Signs the exact <paramref name="manifestUtf8"/> bytes.</summary>
    Task<SignedRelease> SignAsync(byte[] manifestUtf8, CancellationToken ct);
}

/// <summary>A PEM private key held by this process (<c>--signing-key</c>).</summary>
internal sealed class LocalSigningKey(ECDsa key) : IReleaseSigningKey
{
    public PublisherKey PublicKey { get; } = ReleaseKeys.PublicKeyOf(key);

    public Task<SignedRelease> SignAsync(byte[] manifestUtf8, CancellationToken ct) =>
        Task.FromResult(ReleaseSigner.Sign(manifestUtf8, key));
}

/// <summary>
/// <c>--sign-command</c>: a key this process never sees (a cloud KMS or HSM key, a hardware
/// token). The command signs the SHA-256 digest of the signed message and prints the signature;
/// the signature is checked against <c>--signing-public-key</c> before anything is uploaded.
/// </summary>
/// <remarks>
/// Placeholders replaced in the command: <c>{digest-hex}</c>, <c>{digest-base64}</c>,
/// <c>{digest-base64url}</c>, <c>{digest-file}</c> (the 32 raw digest bytes) and
/// <c>{message-file}</c> (the whole message, for tools that hash it themselves with SHA-256).
/// Output accepted on standard output: the signature as base64, base64url or hex, either raw
/// (64 bytes, r‖s) or DER; or a JSON object whose <c>result</c>, <c>signature</c> or
/// <c>Signature</c> property holds it (as <c>az keyvault key sign</c> and <c>aws kms sign</c> print).
/// The command runs through <c>cmd /c</c> on Windows and <c>/bin/sh -c</c> elsewhere.
/// </remarks>
internal sealed class CommandSigningKey(string command, PublisherKey publicKey, TimeSpan? timeout = null) : IReleaseSigningKey
{
    public const string CommandEnvironmentVariable = "INSTELLA_SIGN_COMMAND";
    public const string PublicKeyEnvironmentVariable = "INSTELLA_SIGNING_PUBLIC_KEY";

    public PublisherKey PublicKey { get; } = publicKey;

    public async Task<SignedRelease> SignAsync(byte[] manifestUtf8, CancellationToken ct)
    {
        var message = ReleaseSigner.MessageFor(manifestUtf8);
        var digest = SHA256.HashData(message);
        var dir = Directory.CreateTempSubdirectory("instella-sign-").FullName;
        try
        {
            var digestFile = Path.Combine(dir, "digest.bin");
            var messageFile = Path.Combine(dir, "message.bin");
            await File.WriteAllBytesAsync(digestFile, digest, ct);
            await File.WriteAllBytesAsync(messageFile, message, ct);

            var expanded = command
                .Replace("{digest-hex}", Convert.ToHexStringLower(digest), StringComparison.Ordinal)
                .Replace("{digest-base64url}", Base64Url(digest), StringComparison.Ordinal)
                .Replace("{digest-base64}", Convert.ToBase64String(digest), StringComparison.Ordinal)
                // Quoted: the temporary folder can contain spaces.
                .Replace("{digest-file}", QuoteForShell(digestFile), StringComparison.Ordinal)
                .Replace("{message-file}", QuoteForShell(messageFile), StringComparison.Ordinal);

            var output = await RunAsync(expanded, digestFile, messageFile, ct);
            var signature = ParseSignature(output);
            if (!ReleaseSigner.Verify(manifestUtf8, signature, PublicKey))
                throw new InvalidOperationException(
                    $"the sign command's signature does not verify against the signing public key {PublicKey.KeyId} " +
                    "(wrong key, or the command signed something other than the SHA-256 digest)");
            return new SignedRelease(Convert.ToBase64String(manifestUtf8), Convert.ToBase64String(signature), PublicKey.KeyId);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>A path quoted for the shell the command runs in (cmd or /bin/sh).</summary>
    internal static string QuoteForShell(string path) =>
        OperatingSystem.IsWindows() ? $"\"{path}\"" : "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// Runs the command, with <c>INSTELLA_DIGEST_FILE</c> and <c>INSTELLA_MESSAGE_FILE</c> in its
    /// environment for commands that need the path inside another argument (<c>fileb://…</c>).
    /// </summary>
    private async Task<string> RunAsync(string expanded, string digestFile, string messageFile, CancellationToken ct)
    {
        // cmd does not understand the \" escaping ArgumentList uses: pass its command line raw
        // (with /s, cmd strips exactly the outer quotes and runs the rest as typed).
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", $"/d /s /c \"{expanded}\"")
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", expanded } };
        psi.UseShellExecute = false;
        psi.Environment["INSTELLA_DIGEST_FILE"] = digestFile;
        psi.Environment["INSTELLA_MESSAGE_FILE"] = messageFile;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start the sign command");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
        var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = process.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* exited */ }
            throw new InvalidOperationException("the sign command did not finish within the time limit");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"the sign command exited {process.ExitCode}: {(await stderr).Trim()}");
        return await stdout;
    }

    /// <summary>Decodes the command's output into a 64-byte IEEE P1363 signature.</summary>
    internal static byte[] ParseSignature(string output)
    {
        var text = output.Trim();
        if (text.StartsWith('{'))
        {
            using var json = JsonDocument.Parse(text);
            text = new[] { "result", "signature", "Signature", "value" }
                .Select(name => json.RootElement.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null)
                .FirstOrDefault(v => v is not null)
                ?? throw new InvalidOperationException("the sign command printed JSON without a result/signature property");
            text = text.Trim();
        }

        byte[] bytes;
        if (text.Length >= 128 && text.Length % 2 == 0 && text.All(char.IsAsciiHexDigit))
            bytes = Convert.FromHexString(text);
        else
        {
            var b64 = text.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            try { bytes = Convert.FromBase64String(b64); }
            catch (FormatException) { throw new InvalidOperationException($"the sign command printed something that is not a signature: '{Shorten(text)}'"); }
        }

        return bytes.Length == 64 ? bytes : DerToP1363(bytes);
    }

    private static byte[] DerToP1363(byte[] der)
    {
        try
        {
            var reader = new AsnReader(der, AsnEncodingRules.DER);
            var sequence = reader.ReadSequence();
            var r = sequence.ReadIntegerBytes().ToArray();
            var s = sequence.ReadIntegerBytes().ToArray();
            sequence.ThrowIfNotEmpty();
            reader.ThrowIfNotEmpty();
            var result = new byte[64];
            Fit(r, result.AsSpan(0, 32));
            Fit(s, result.AsSpan(32, 32));
            return result;
        }
        catch (AsnContentException ex)
        {
            throw new InvalidOperationException($"the sign command's signature is neither 64 raw bytes nor DER: {ex.Message}");
        }
    }

    private static void Fit(byte[] integer, Span<byte> target)
    {
        var trimmed = integer.AsSpan().TrimStart((byte)0);
        if (trimmed.Length > target.Length) throw new InvalidOperationException("the sign command's signature is not a P-256 signature");
        trimmed.CopyTo(target[(target.Length - trimmed.Length)..]);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Shorten(string s) => s.Length <= 40 ? s : s[..40] + "…";
}
