using System;
using System.Runtime.InteropServices;
using Instella.Installer.Runtime.UI.Linux.Widgets;
using Instella.Installer.Runtime.UI.Widgets;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.UI.Linux.Widgets;

/// <summary>
/// Exercises <see cref="GtkWidgetInstance"/> lifecycle without
/// calling into libgtk. Signal-handler disconnects swallow
/// <see cref="DllNotFoundException"/> on non-Linux hosts so Dispose
/// is safe anywhere; that contract is what matters for portability
/// of the tests.
/// </summary>
[TestFixture]
public sealed class GtkWidgetInstanceTests
{
    [Test]
    public void Dispose_clearsSignalHandleList()
    {
        var widget = new CheckBox("Accept") { Id = "acc" };
        var instance = new GtkWidgetInstance(widget, primaryWidget: 0, rootWidget: 0);

        instance.SignalHandles.Add(GCHandle.Alloc("context-1"));
        instance.SignalHandles.Add(GCHandle.Alloc("context-2"));
        Assert.That(instance.SignalHandles.Count, Is.EqualTo(2));

        instance.Dispose();

        // GCHandle is a struct; freeing a copy doesn't zero the original's
        // internal field, so a direct IsAllocated check on the test's
        // handle reference is unreliable. What we can verify is that
        // Dispose iterated the list and cleared it — the Free() calls on
        // the struct copies do invalidate the underlying GC slots.
        Assert.That(instance.SignalHandles.Count, Is.EqualTo(0));
    }

    [Test]
    public void Dispose_isIdempotent()
    {
        var widget = new Heading("Hi");
        var instance = new GtkWidgetInstance(widget, primaryWidget: 0, rootWidget: 0);

        instance.Dispose();
        Assert.DoesNotThrow(() => instance.Dispose());
    }

    [Test]
    public void Properties_exposeConstructorArgs()
    {
        var widget = new TextInput("Name");
        var bindings = new[] { new GtkRadioBinding(42, "a") };
        var instance = new GtkWidgetInstance(
            widget,
            primaryWidget: 10,
            rootWidget: 20,
            browseButtonWidget: 30,
            radioOptions: bindings,
            pixbuf: 40);

        Assert.That(instance.Widget, Is.SameAs(widget));
        Assert.That(instance.PrimaryWidget, Is.EqualTo((nint)10));
        Assert.That(instance.RootWidget, Is.EqualTo((nint)20));
        Assert.That(instance.BrowseButtonWidget, Is.EqualTo((nint)30));
        Assert.That(instance.RadioOptions, Is.EquivalentTo(bindings));
        Assert.That(instance.Pixbuf, Is.EqualTo((nint)40));

        // Clean up without expecting the pointers to be real.
        instance.Dispose();
    }

    [Test]
    public void Default_radioOptions_isEmpty()
    {
        var widget = new Paragraph("Body");
        var instance = new GtkWidgetInstance(widget, 0, 0);

        Assert.That(instance.RadioOptions.Count, Is.EqualTo(0));
        Assert.That(instance.BrowseButtonWidget, Is.EqualTo((nint)0));
        Assert.That(instance.Pixbuf, Is.EqualTo((nint)0));
    }
}
