# Instella.Installer.Build

MSBuild targets that turn `dotnet publish` of your `*.Installer` project into a single installer
executable: the app projects marked as payload are published, zipped and appended to the
installer together with the manifest emitted from your fluent-builder code.

```xml
<ItemGroup>
  <PackageReference Include="Instella.Installer.Runtime" Version="0.1.0" />
  <PackageReference Include="Instella.Installer.Build" Version="0.1.0" />

  <!-- The application being installed. -->
  <ProjectReference Include="..\QuickNotes\QuickNotes.csproj">
    <InstellaPayload>true</InstellaPayload>
    <ReferenceOutputAssembly>false</ReferenceOutputAssembly>
    <Private>false</Private>
  </ProjectReference>
</ItemGroup>

<PropertyGroup>
  <!-- Optional: Authenticode-sign the installed stub and the finished installer
       (with InstellaEnabled=false, the online installer). -->
  <InstellaSignCommand>signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /n "Example Corp" "{0}"</InstellaSignCommand>
  <!-- `dotnet run` dev loop without payload work. -->
  <InstellaEnabled Condition="'$(Configuration)' == 'Debug'">false</InstellaEnabled>
</PropertyGroup>
```

```bash
dotnet publish -r win-x64 -c Release
```

Do not sign the exe yourself before the payload is appended (`INSTELLA0201`); use
`InstellaSignCommand` or sign the finished installer. Publishing for a non-Windows RID warns
with `INSTELLA0001` (experimental platform). See [distribution and signing]({{DocsUrl}}/distribution-and-signing.md).
