using System.CommandLine;
using System.Security.Cryptography;
using System.Text;
using Instella.CLI.Commands;
using Instella.CLI.Services;
using Instella.Core.Trust;
using NUnit.Framework;

namespace Instella.CLI.Tests;

/// <summary>
/// <c>--sign-command</c>: an external signer (KMS/HSM) signs the SHA-256 digest; its output is
/// decoded (base64/base64url/hex, raw or DER, or JSON) and checked against the public key before
/// anything is uploaded. The "signer" here prints a signature file prepared by the test; the file
/// is named after the digest, so a wrong placeholder expansion makes the command fail.
/// </summary>
[TestFixture]
[NonParallelizable]
public class SignCommandTests
{
    private static readonly byte[] Manifest = Encoding.UTF8.GetBytes("{\"formatVersion\":1,\"appId\":\"com.example.app\"}");
    private string _dir = null!;
    private ECDsa _key = null!;
    private PublisherKey _publicKey = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Directory.CreateTempSubdirectory("instella-signcmd-").FullName;
        _key = ReleaseKeys.Generate();
        _publicKey = ReleaseKeys.PublicKeyOf(_key);
    }

    [TearDown]
    public void TearDown()
    {
        _key.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static byte[] Digest => SHA256.HashData(ReleaseSigner.MessageFor(Manifest));

    [Test]
    public async Task DerBase64Output_IsConvertedAndProducesAVerifiableRelease()
    {
        var signature = _key.SignHash(Digest, DSASignatureFormat.Rfc3279DerSequence);
        var command = PrintFileNamedByDigest(Convert.ToBase64String(signature), "{digest-hex}");

        var release = await new CommandSigningKey(command, _publicKey).SignAsync(Manifest, CancellationToken.None);

        Assert.That(release.KeyId, Is.EqualTo(_publicKey.KeyId));
        Assert.That(ReleaseSigner.Verify(Manifest, Convert.FromBase64String(release.Signature), _publicKey), Is.True);
        Assert.That(Convert.FromBase64String(release.Signature), Has.Length.EqualTo(64), "stored as IEEE P1363");
    }

    [Test]
    public async Task AzureStyleJson_WithBase64UrlRawSignature_IsAccepted()
    {
        var raw = _key.SignHash(Digest, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var b64url = Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var command = PrintFileNamedByDigest($"{{\"kid\":\"https://vault/keys/k\",\"result\":\"{b64url}\"}}", "{digest-base64url}");

        var release = await new CommandSigningKey(command, _publicKey).SignAsync(Manifest, CancellationToken.None);
        Assert.That(ReleaseSigner.Verify(Manifest, Convert.FromBase64String(release.Signature), _publicKey), Is.True);
    }

    [Test]
    public async Task HexOutput_IsAccepted()
    {
        var raw = _key.SignHash(Digest, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var command = PrintFileNamedByDigest(Convert.ToHexString(raw), "{digest-hex}");

        var release = await new CommandSigningKey(command, _publicKey).SignAsync(Manifest, CancellationToken.None);
        Assert.That(release.Signature, Is.EqualTo(Convert.ToBase64String(raw)));
    }

    [Test]
    public void SignatureByAnotherKey_IsRefusedBeforeUpload()
    {
        using var other = ReleaseKeys.Generate();
        var command = PrintFileNamedByDigest(Convert.ToBase64String(other.SignHash(Digest)), "{digest-hex}");

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CommandSigningKey(command, _publicKey).SignAsync(Manifest, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("does not verify against the signing public key"));
    }

    [Test]
    public void FailingCommand_ReportsItsExitCodeAndError()
    {
        var command = OperatingSystem.IsWindows() ? "echo access denied 1>&2 & exit /b 3" : "echo access denied >&2; exit 3";

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CommandSigningKey(command, _publicKey).SignAsync(Manifest, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("exited 3").And.Contain("access denied"));
    }

    [Test]
    public async Task Upload_SignCommandWithoutItsPublicKey_OrWithASigningKey_IsAUsageError()
    {
        var saved = (Environment.GetEnvironmentVariable(CommandSigningKey.PublicKeyEnvironmentVariable),
            Environment.GetEnvironmentVariable(SigningKeyLoader.KeyEnvironmentVariable));
        Environment.SetEnvironmentVariable(CommandSigningKey.PublicKeyEnvironmentVariable, null);
        Environment.SetEnvironmentVariable(SigningKeyLoader.KeyEnvironmentVariable, null);
        try
        {
            File.WriteAllText(Path.Combine(_dir, "app.exe"), "x");
            string[] common = ["upload", "--server", "http://127.0.0.1:1", "--api-key", "k", "--package", "p",
                "--version", "1.0.0", "--path", _dir, "--os", "windows", "--arch", "x64", "--sign-command", "sign {digest-hex}"];
            Assert.That(await Run(common), Is.EqualTo(ExitCodes.Usage), "no --signing-public-key");

            var keyFile = Path.Combine(_dir, "k.pem");
            File.WriteAllText(keyFile, _key.ExportPkcs8PrivateKeyPem());
            Assert.That(await Run([.. common, "--signing-public-key", _publicKey.PublicKey, "--signing-key", keyFile]),
                Is.EqualTo(ExitCodes.Usage), "both kinds of key");
        }
        finally
        {
            Environment.SetEnvironmentVariable(CommandSigningKey.PublicKeyEnvironmentVariable, saved.Item1);
            Environment.SetEnvironmentVariable(SigningKeyLoader.KeyEnvironmentVariable, saved.Item2);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task TheMessageFiles_WorkFromAFolderWithASpace_QuotedOrThroughTheEnvironment(bool viaEnvironment)
    {
        // {digest-file} is inserted quoted; INSTELLA_DIGEST_FILE carries the same path.
        var spaced = Directory.CreateDirectory(Path.Combine(_dir, "with space")).FullName;
        var signatureFile = Path.Combine(_dir, "signature.txt");
        File.WriteAllText(signatureFile, Convert.ToBase64String(_key.SignHash(Digest, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        var digest = viaEnvironment
            ? OperatingSystem.IsWindows() ? "\"%INSTELLA_DIGEST_FILE%\"" : "\"$INSTELLA_DIGEST_FILE\""
            : "{digest-file}";
        var command = OperatingSystem.IsWindows()
            ? $"type {digest} >nul && type \"{signatureFile}\""
            : $"cat {digest} >/dev/null && cat '{signatureFile}'";
        var saved = (Environment.GetEnvironmentVariable("TMP"), Environment.GetEnvironmentVariable("TEMP"), Environment.GetEnvironmentVariable("TMPDIR"));
        Environment.SetEnvironmentVariable("TMP", spaced);
        Environment.SetEnvironmentVariable("TEMP", spaced);
        Environment.SetEnvironmentVariable("TMPDIR", spaced);
        try
        {
            var release = await new CommandSigningKey(command, _publicKey).SignAsync(Manifest, CancellationToken.None);

            Assert.That(ReleaseSigner.Verify(Manifest, Convert.FromBase64String(release.Signature), _publicKey), Is.True);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMP", saved.Item1);
            Environment.SetEnvironmentVariable("TEMP", saved.Item2);
            Environment.SetEnvironmentVariable("TMPDIR", saved.Item3);
        }
    }

    [Test]
    [Platform(Exclude = "Win")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void KeysGenerate_CreatesTheFileOwnerOnly_OnUnix()
    {
        var file = Path.Combine(_dir, "k.pem");

        PrivateKeyFile.Write(file, "secret");

        Assert.That(File.GetUnixFileMode(file), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
    }

    [Test]
    [Platform("Win")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void KeysGenerate_CreatesTheFileWithAProtectedAcl_OnWindows()
    {
        var file = Path.Combine(_dir, "k.pem");

        PrivateKeyFile.Write(file, "secret");

        var acl = new FileInfo(file).GetAccessControl();
        Assert.That(acl.AreAccessRulesProtected, Is.True, "nothing inherited from the folder");
        var who = acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>().Select(r => r.IdentityReference.Value).ToList();
        Assert.That(who, Is.EquivalentTo(new[]
        {
            System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value,
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null).Value,
        }));
        Assert.That(File.ReadAllText(file), Is.EqualTo("secret"));
    }

    [Test]
    public void AKeyInsideAGitWorkTree_IsNoticed()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));

        Assert.That(PrivateKeyFile.GitWorkTreeOf(Path.Combine(_dir, "keys", "k.pem")), Is.EqualTo(_dir));
    }

    [TestCase("exit 1")]
    [TestCase("echo AAAA")]   // a "signature" that does not verify
    public async Task Upload_ABrokenSignCommand_Exits4_BeforeAnythingIsSent(string signCommand)
    {
        // Signing comes first. The server here is unreachable: had the upload started, the
        // exit code would be 2 (server), not 4 (signing).
        var saved = Environment.GetEnvironmentVariable(SigningKeyLoader.KeyEnvironmentVariable);
        Environment.SetEnvironmentVariable(SigningKeyLoader.KeyEnvironmentVariable, null);
        try
        {
            File.WriteAllText(Path.Combine(_dir, "app.exe"), "x");
            var exit = await Run(["upload", "--server", "http://127.0.0.1:1", "--api-key", "k", "--package", "p",
                "--version", "1.0.0", "--path", _dir, "--os", "windows", "--arch", "x64",
                "--sign-command", signCommand, "--signing-public-key", _publicKey.PublicKey]);

            Assert.That(exit, Is.EqualTo(ExitCodes.Signing));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SigningKeyLoader.KeyEnvironmentVariable, saved);
        }
    }

    [Test]
    public async Task UploadClient_ASigningFailure_SendsNoRequest()
    {
        File.WriteAllText(Path.Combine(_dir, "app.exe"), "x");
        var recorder = new Recorder();
        using var client = new UploadClient(new HttpClient(recorder), "https://updates.example.com", "k");

        var result = await client.UploadVersionAsync(new UploadRequest
        {
            PackageId = "p", Version = new Version(1, 0, 0), SourceDirectory = _dir, Channel = "stable",
            Platform = Instella.Core.Platform.TargetPlatform.Windows, Architecture = Instella.Core.Platform.Architecture.X64,
            SignWith = (_, _) => throw new InvalidOperationException("the sign command exited 1: denied"),
        });

        Assert.That(result.SigningFailed, Is.True);
        Assert.That(result.Error, Does.Contain("signing failed: the sign command exited 1"));
        Assert.That(recorder.Requests, Is.Zero);
    }

    private sealed class Recorder : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        }
    }

    /// <summary>
    /// Writes <paramref name="output"/> to a file named after the digest (as <paramref name="placeholder"/>
    /// expands) and returns a command that prints it.
    /// </summary>
    private string PrintFileNamedByDigest(string output, string placeholder)
    {
        var name = placeholder switch
        {
            "{digest-hex}" => Convert.ToHexStringLower(Digest),
            "{digest-base64url}" => Convert.ToBase64String(Digest).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            _ => throw new ArgumentOutOfRangeException(nameof(placeholder)),
        };
        File.WriteAllText(Path.Combine(_dir, name + ".sig"), output);
        var path = Path.Combine(_dir, placeholder + ".sig");
        return OperatingSystem.IsWindows() ? $"type \"{path}\"" : $"cat '{path}'";
    }

    private static Task<int> Run(string[] args)
    {
        var root = new RootCommand { UploadCommand.Create() };
        return root.Parse(args).InvokeAsync();
    }
}
