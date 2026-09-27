using System;
using Instella.Installer.Runtime.UI.Widgets;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.UI;

[TestFixture]
public sealed class PageStateTests
{
    [Test]
    public void Set_and_Get_roundTrip()
    {
        var state = new PageState();
        state.Set("name", "Alice");
        Assert.That(state.Text("name"), Is.EqualTo("Alice"));
    }

    [Test]
    public void Bool_returnsStoredValue()
    {
        var state = new PageState();
        state.Set("accept", true);
        Assert.That(state.Bool("accept"), Is.True);
    }

    [Test]
    public void Bool_returnsDefault_whenMissing()
    {
        var state = new PageState();
        Assert.That(state.Bool("missing", @default: true), Is.True);
    }

    [Test]
    public void Text_returnsDefault_whenMissing()
    {
        var state = new PageState();
        Assert.That(state.Text("nope", "fallback"), Is.EqualTo("fallback"));
    }

    [Test]
    public void TryGet_returnsFalse_whenMissing()
    {
        var state = new PageState();
        Assert.That(state.TryGet<int>("x", out var value), Is.False);
        Assert.That(value, Is.EqualTo(0));
    }

    [Test]
    public void TryGet_returnsFalse_whenTypeMismatch()
    {
        var state = new PageState();
        state.Set("x", "not-an-int");
        Assert.That(state.TryGet<int>("x", out _), Is.False);
    }

    [Test]
    public void StateChanged_firesOnInitialSet()
    {
        var state = new PageState();
        var count = 0;
        state.StateChanged += () => count++;
        state.Set("a", 1);
        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public void StateChanged_doesNotFire_whenValueUnchanged()
    {
        var state = new PageState();
        state.Set("a", 1);
        var count = 0;
        state.StateChanged += () => count++;
        state.Set("a", 1);
        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public void StateChanged_firesOnDifferentValue()
    {
        var state = new PageState();
        state.Set("a", 1);
        var count = 0;
        state.StateChanged += () => count++;
        state.Set("a", 2);
        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public void Clear_firesOnce_whenNonEmpty()
    {
        var state = new PageState();
        state.Set("a", 1);
        var count = 0;
        state.StateChanged += () => count++;
        state.Clear();
        Assert.That(count, Is.EqualTo(1));
        Assert.That(state.Snapshot().Count, Is.EqualTo(0));
    }

    [Test]
    public void Clear_doesNotFire_whenAlreadyEmpty()
    {
        var state = new PageState();
        var count = 0;
        state.StateChanged += () => count++;
        state.Clear();
        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public void Reentry_fromHandler_throws()
    {
        var state = new PageState();
        state.StateChanged += () => state.Set("x", "from-handler");
        Assert.Throws<InvalidOperationException>(() => state.Set("a", 1));
    }

    [Test]
    public void Snapshot_isIndependent_ofLaterMutation()
    {
        var state = new PageState();
        state.Set("a", 1);
        var snapshot = state.Snapshot();
        state.Set("a", 2);
        Assert.That(snapshot["a"], Is.EqualTo(1));
    }

    [Test]
    public void Set_rejectsEmptyKey()
    {
        var state = new PageState();
        Assert.Throws<ArgumentException>(() => state.Set("", 1));
    }

    [Test]
    public void NullValue_isValid()
    {
        var state = new PageState();
        state.Set("a", null);
        Assert.That(state.Get<string?>("a", "x"), Is.Null);
    }
}
