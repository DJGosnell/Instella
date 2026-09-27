# Instella.Core

Shared types for the Instella packages: the build and installed manifests, the signed release
manifest, platform abstractions (`IPlatformServices`, `IFileSystem`), exit codes, logging
interfaces and update results.

You normally do not reference this package directly: `Instella.Installer.Runtime`,
`Instella.Sdk` and `Instella.Installer.Testing` bring it in. See the [repository]({{RepositoryUrl}})
and [compatibility]({{DocsUrl}}/compatibility.md) (format versions and the public API policy).
