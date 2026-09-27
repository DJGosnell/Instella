# Instella.Sdk

Reference it from your **application** (not the installer) to check for updates, hand over to
the installed updater, and react to the first start after an update.

```csharp
using Instella.Sdk;

// At startup: finish an update that a crash or power loss interrupted.
if (InstellaClient.GetInstallationHealth() == InstallationHealth.InterruptedUpdate)
{
    var exit = await InstellaClient.StartRecoveryAsync();   // 0 = recovered
    return;                                                 // exit, then start again
}

// The first start after an update, once per Windows user.
if (InstellaClient.PostUpdate is { } update)
    MigrateUserData(update.FromVersion);

// Later, e.g. from a "Check for updates" menu item. Never throws for network errors.
var result = await InstellaClient.CheckForUpdateAsync();
if (result.UpdateAvailable && AskTheUser(result.Update!))              // version, changelog, size
    await InstellaClient.LaunchUpdaterAndExitAsync(result.Update!);   // or StartUpdaterAsync + your own shutdown
```

Ask before updating: `UpdateInfo` has the `Version`, the `Changelog` and the download size
(`PatchSize` when `PatchAvailable`, else `FullSize`). The sample's `UpdateDialog` shows one way.

The updater accepts an update only if it is signed by one of the publisher keys the installer
was built with, targets this app, OS and architecture, and is newer than the installed version.
It then shows "{App} was updated", counts down (`UpdateOptions.RestartCountdown`, default 5 s)
and restarts the app **without administrator rights and without arguments**. What the app
passed as `UpdateOptions.AdditionalArgs` arrives in `InstellaClient.PostUpdateArguments`.
The SDK is trimming- and NativeAOT-compatible.
