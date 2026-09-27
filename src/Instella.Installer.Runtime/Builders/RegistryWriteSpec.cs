using System;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Installation;
using RegistryHive = Instella.Core.Platform.RegistryHive;

namespace Instella.Installer.Runtime.Builders;

/// <summary>
/// Frozen single-value registry write. Produced by
/// <see cref="RegistryKeyBuilder"/> and consumed by
/// <c>WriteRegistrySpecsStep</c> + the <c>IPlatformServices.WriteRegistryValueAsync</c>
/// surface. <see cref="ValueFactory"/> resolves the concrete value at install
/// time (so values can reference <see cref="InstallContext.InstallPath"/>
/// etc.).
/// </summary>
internal sealed record RegistryWriteSpec(
    RegistryHive Hive,
    string KeyPath,
    string ValueName,
    InstellaRegistryValueKind Kind,
    Func<InstallContext, object> ValueFactory);
