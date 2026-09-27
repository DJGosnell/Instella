using System;
using System.Reflection;
using Instella.Installer.Runtime.UI.Widgets;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.UI;

[TestFixture]
public sealed class ImageSourceTests
{
    [Test]
    public void FromFile_storesPath()
    {
        var source = ImageSource.FromFile("C:/assets/logo.png");
        Assert.That(source, Is.InstanceOf<FileImageSource>());
        Assert.That(((FileImageSource)source).Path, Is.EqualTo("C:/assets/logo.png"));
    }

    [Test]
    public void FromFile_rejectsEmpty()
    {
        Assert.Throws<ArgumentException>(() => ImageSource.FromFile(""));
    }

    [Test]
    public void FromResource_defaultsToCallingAssembly()
    {
        var source = ImageSource.FromResource("MyApp.Assets.icon.png");
        Assert.That(source, Is.InstanceOf<ResourceImageSource>());
        var resource = (ResourceImageSource)source;
        Assert.That(resource.Name, Is.EqualTo("MyApp.Assets.icon.png"));
        Assert.That(resource.Assembly, Is.EqualTo(Assembly.GetExecutingAssembly()));
    }

    [Test]
    public void FromResource_respectsExplicitAssembly()
    {
        var runtimeAsm = typeof(ImageSource).Assembly;
        var source = ImageSource.FromResource("x.png", runtimeAsm);
        var resource = (ResourceImageSource)source;
        Assert.That(resource.Assembly, Is.SameAs(runtimeAsm));
    }

    [Test]
    public void FromResource_rejectsEmpty()
    {
        Assert.Throws<ArgumentException>(() => ImageSource.FromResource(""));
    }

    [Test]
    public void FromBytes_storesBytes()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var source = ImageSource.FromBytes(bytes);
        Assert.That(source, Is.InstanceOf<BytesImageSource>());
        Assert.That(((BytesImageSource)source).Bytes, Is.SameAs(bytes));
    }

    [Test]
    public void FromBytes_rejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => ImageSource.FromBytes(null!));
    }

    [Test]
    public void RecordEquality_sameFile_areEqual()
    {
        var a = ImageSource.FromFile("same.png");
        var b = ImageSource.FromFile("same.png");
        Assert.That(a, Is.EqualTo(b));
    }
}
