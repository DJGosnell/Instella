using Instella.Installer.Runtime.Runners;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests;

/// <summary>No test may open a real message box: nobody would close it and the run would hang.</summary>
[SetUpFixture]
public sealed class NoDialogsSetUp
{
    [OneTimeSetUp]
    public void DisableDialogs() => UserMessages.DialogsDisabled = true;
}
