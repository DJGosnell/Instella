using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using Instella.Core.Platform.Windows;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Platform;

/// <summary>
/// The real <c>WinVerifyTrust</c> path of the handoff signer check: struct layout and the
/// WTHelper plumbing. Run by <c>verify.ps1 -Stage Signing</c>, which creates two throwaway
/// code-signing certificates and passes signtool and their thumbprints in the environment.
/// </summary>
[TestFixture]
[Category("Signing")]
[Platform("Win")]
[SupportedOSPlatform("windows")]
public class AuthenticodeTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp() => _dir = Directory.CreateTempSubdirectory("instella-authenticode-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_dir, recursive: true);

    [Test]
    public void SubjectsOfSignedFiles_MatchPerCertificate()
    {
        var signtool = Environment.GetEnvironmentVariable("INSTELLA_SIGNTOOL");
        var certA = Environment.GetEnvironmentVariable("INSTELLA_SIGN_CERT_A");
        var certB = Environment.GetEnvironmentVariable("INSTELLA_SIGN_CERT_B");
        Assume.That(signtool, Is.Not.Null.And.Not.Empty, "run through verify.ps1 -Stage Signing");
        Assume.That(certA, Is.Not.Null.And.Not.Empty);
        Assume.That(certB, Is.Not.Null.And.Not.Empty);

        var first = Signed("first.exe", certA!, signtool!);
        var second = Signed("second.exe", certA!, signtool!);
        var other = Signed("other.exe", certB!, signtool!);
        var unsigned = Copy("unsigned.exe");

        var a1 = Authenticode.Instance.Read(first);
        var a2 = Authenticode.Instance.Read(second);
        var b = Authenticode.Instance.Read(other);
        var none = Authenticode.Instance.Read(unsigned);

        Assert.That(a1.SignerSubject, Does.Contain("CN=Instella verify"));
        Assert.That(a2.SignerSubject, Is.EqualTo(a1.SignerSubject));
        Assert.That(b.SignerSubject, Is.Not.Null.And.Not.EqualTo(a1.SignerSubject));
        Assert.That(none.SignerSubject, Is.Null);
        Assert.That(none.IsValid, Is.False);
        // Throwaway certificates are not trusted roots: the signature is read, but not valid.
        Assert.That(a1.IsValid, Is.False, $"status 0x{a1.Status:X8}");
    }

    private string Copy(string name)
    {
        var path = Path.Combine(_dir, name);
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe"), path);
        return path;
    }

    private string Signed(string name, string thumbprint, string signtool)
    {
        var path = Copy(name);
        using var sign = Process.Start(new ProcessStartInfo(signtool)
        {
            ArgumentList = { "sign", "/fd", "SHA256", "/sha1", thumbprint, path },
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        var output = sign.StandardOutput.ReadToEnd() + sign.StandardError.ReadToEnd();
        sign.WaitForExit();
        Assert.That(sign.ExitCode, Is.Zero, output);
        return path;
    }
}
