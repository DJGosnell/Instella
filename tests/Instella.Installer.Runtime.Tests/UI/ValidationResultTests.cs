using Instella.Installer.Runtime.Builders;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.UI;

[TestFixture]
public sealed class ValidationResultTests
{
    [Test]
    public void Ok_isValid_noError()
    {
        Assert.That(ValidationResult.Ok.IsValid, Is.True);
        Assert.That(ValidationResult.Ok.Error, Is.Null);
    }

    [Test]
    public void Fail_carriesMessage()
    {
        var r = ValidationResult.Fail("install path is not writable");
        Assert.That(r.IsValid, Is.False);
        Assert.That(r.Error, Is.EqualTo("install path is not writable"));
    }
}
