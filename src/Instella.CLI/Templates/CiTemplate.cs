using System.Text;
using Instella.CLI.Services;

namespace Instella.CLI.Templates;

/// <summary>Where the workflow runs.</summary>
public enum CiHost
{
    /// <summary>GitHub Actions (<c>.github/workflows</c>).</summary>
    GitHub,

    /// <summary>Gitea Actions (<c>.gitea/workflows</c>).</summary>
    Gitea,
}

/// <summary>How the release is signed.</summary>
public enum CiSigning
{
    /// <summary>Azure Key Vault key, used through <c>--sign-command</c>.</summary>
    KmsAzure,

    /// <summary>AWS KMS key, used through <c>--sign-command</c>.</summary>
    KmsAws,

    /// <summary>Google Cloud KMS key, used through <c>--sign-command</c>.</summary>
    KmsGcp,

    /// <summary>CI uploads a draft; a person signs it with <c>instella publish</c>.</summary>
    Draft,

    /// <summary>The PEM key as a CI secret (protected environment).</summary>
    Secret,
}

/// <summary>What <c>instella ci init</c> generates from.</summary>
public sealed record CiOptions
{
    /// <summary>CI host.</summary>
    public required CiHost Host { get; init; }

    /// <summary>Signing method.</summary>
    public required CiSigning Signing { get; init; }

    /// <summary>The app project, relative to the repository root (forward slashes).</summary>
    public required string AppProject { get; init; }

    /// <summary>The installer project, relative to the repository root (forward slashes).</summary>
    public required string InstallerProject { get; init; }

    /// <summary>Package id on the server (the installer's app id).</summary>
    public required string PackageId { get; init; }

    /// <summary>Update server URL.</summary>
    public required string ServerUrl { get; init; }

    /// <summary>Name used in the installer file names (<c>{Name}-WebSetup-{version}.exe</c>).</summary>
    public required string AppName { get; init; }

    /// <summary>Runtime identifier the installers are built for.</summary>
    public string Rid { get; init; } = "win-x64";
}

/// <summary>
/// Generates a release workflow for <c>instella ci init</c>: a <c>build</c> job without any
/// publishing credentials (app, online and offline installer from a <c>v*</c> tag) and a
/// <c>publish</c> job that uploads them, signed the chosen way. See <c>docs/publishing.md</c>.
/// </summary>
public static class CiTemplate
{
    /// <summary>
    /// Derives the version and channel from the tag: <c>v1.3.0</c> is 1.3.0 on stable;
    /// <c>v1.4.0-beta</c> (or <c>-beta.2</c>) is 1.4.0 on beta. Writes VERSION and CHANNEL to the job
    /// environment and to the step outputs.
    /// </summary>
    internal static readonly string[] TagParseBash =
    [
        "TAG=\"${GITHUB_REF_NAME#v}\"                                  # 1.3.0-beta.2",
        "VERSION=\"${TAG%%-*}\"                                        # 1.3.0",
        "SUFFIX=\"\"; [ \"$TAG\" != \"$VERSION\" ] && SUFFIX=\"${TAG#*-}\"    # beta.2",
        "CHANNEL=\"${SUFFIX%%.*}\"; CHANNEL=\"${CHANNEL:-stable}\"        # beta (\".2\" only keeps tags unique)",
        "echo \"$VERSION\" | grep -Eq '^[0-9]+\\.[0-9]+\\.[0-9]+(\\.[0-9]+)?$' || { echo \"::error::tag $GITHUB_REF_NAME: version must be like 1.2.3 or 1.2.3.4\"; exit 1; }",
        "echo \"$CHANNEL\" | grep -Eq '^[a-z0-9]([a-z0-9-]{0,30}[a-z0-9])?$' || { echo \"::error::tag suffix '$CHANNEL' is not a channel name\"; exit 1; }",
        "echo \"VERSION=$VERSION\" >> \"$GITHUB_ENV\"; echo \"CHANNEL=$CHANNEL\" >> \"$GITHUB_ENV\"",
        "echo \"version=$VERSION\" >> \"$GITHUB_OUTPUT\"; echo \"channel=$CHANNEL\" >> \"$GITHUB_OUTPUT\"",
    ];

    /// <summary>The PowerShell twin of <see cref="TagParseBash"/>, for Windows runners.</summary>
    internal static readonly string[] TagParsePwsh =
    [
        "$tag = $env:GITHUB_REF_NAME -replace '^v', ''",
        "$version, $suffix = $tag -split '-', 2",
        "$channel = if ($suffix) { ($suffix -split '\\.', 2)[0] } else { 'stable' }",
        "if ($version -notmatch '^[0-9]+\\.[0-9]+\\.[0-9]+(\\.[0-9]+)?$') { Write-Output \"::error::tag $($env:GITHUB_REF_NAME): version must be like 1.2.3 or 1.2.3.4\"; exit 1 }",
        "if ($channel -cnotmatch '^[a-z0-9]([a-z0-9-]{0,30}[a-z0-9])?$') { Write-Output \"::error::tag suffix '$channel' is not a channel name\"; exit 1 }",
        "\"VERSION=$version\" >> $env:GITHUB_ENV; \"CHANNEL=$channel\" >> $env:GITHUB_ENV",
        "\"version=$version\" >> $env:GITHUB_OUTPUT; \"channel=$channel\" >> $env:GITHUB_OUTPUT",
    ];

    /// <summary>The workflow file, relative to the repository root.</summary>
    public static string WorkflowPath(CiHost host) =>
        host == CiHost.GitHub ? ".github/workflows/instella-release.yml" : ".gitea/workflows/instella-release.yml";

    /// <summary>The <c>--signing</c> value for a method.</summary>
    public static string Name(CiSigning signing) => signing switch
    {
        CiSigning.KmsAzure => "kms-azure",
        CiSigning.KmsAws => "kms-aws",
        CiSigning.KmsGcp => "kms-gcp",
        CiSigning.Draft => "draft",
        _ => "secret",
    };

    /// <summary>Parses a <c>--signing</c> value.</summary>
    public static bool TryParseSigning(string value, out CiSigning signing)
    {
        foreach (var s in Enum.GetValues<CiSigning>())
        {
            if (string.Equals(Name(s), value, StringComparison.OrdinalIgnoreCase))
            {
                signing = s;
                return true;
            }
        }
        signing = default;
        return false;
    }

    /// <summary>The workflow YAML.</summary>
    public static string Generate(CiOptions o)
    {
        var (os, arch) = SplitRid(o.Rid);
        var gitHub = o.Host == CiHost.GitHub;
        var oidc = gitHub && o.Signing is CiSigning.KmsAzure or CiSigning.KmsAws or CiSigning.KmsGcp;
        var artifactVersion = gitHub ? "v4" : "v3";
        var buildRunner = gitHub
            ? os switch { "windows" => "windows-latest", "macos" => "macos-latest", _ => "ubuntu-latest" }
            : os switch { "windows" => "windows", "macos" => "macos", _ => "ubuntu-latest" };
        var y = new StringBuilder();

        y.AppendLine($"# Generated by `instella ci init --host {(gitHub ? "github" : "gitea")} --signing {Name(o.Signing)}`.");
        y.AppendLine("# Push a tag such as v1.2.3 to build the app and both installers and publish them to the update server.");
        y.AppendLine("# v1.2.3 publishes 1.2.3 on stable; v1.3.0-beta (or -beta.2) publishes 1.3.0 on the beta channel.");
        y.AppendLine($"# Setup (secrets, variables, key service) and the security notes: {InstellaDocs.Page("publishing.md")}");
        y.AppendLine("# Pin every `uses:` to the full commit SHA of the version you reviewed (the tags below are only a starting point).");
        y.AppendLine("name: Release");
        y.AppendLine();
        y.AppendLine("on:");
        y.AppendLine("  push:");
        y.AppendLine("    tags: ['v[0-9]*']");
        y.AppendLine();
        y.AppendLine("permissions:");
        y.AppendLine("  contents: read");
        y.AppendLine();
        y.AppendLine("env:");
        y.AppendLine("  DOTNET_NOLOGO: true");
        y.AppendLine($"  INSTELLA_SERVER: {Q(o.ServerUrl)}");
        y.AppendLine($"  INSTELLA_PACKAGE: {Q(o.PackageId)}");
        y.AppendLine($"  APP_NAME: {Q(o.AppName)}");
        y.AppendLine($"  APP_PROJECT: {Q(o.AppProject)}");
        y.AppendLine($"  INSTALLER_PROJECT: {Q(o.InstallerProject)}");
        y.AppendLine($"  RID: {Q(o.Rid)}");
        y.AppendLine();
        y.AppendLine("jobs:");
        y.AppendLine("  build:");
        y.AppendLine("    # No publishing credentials here: this job only compiles.");
        if (!gitHub) y.AppendLine("    # The label of an act_runner that can build for this platform (Native AOT needs the target OS).");
        y.AppendLine($"    runs-on: {buildRunner}");
        y.AppendLine("    outputs:");
        y.AppendLine("      version: ${{ steps.tag.outputs.version }}");
        y.AppendLine("      channel: ${{ steps.tag.outputs.channel }}");
        y.AppendLine("    steps:");
        y.AppendLine("      - uses: actions/checkout@v4");
        y.AppendLine("      - uses: actions/setup-dotnet@v4");
        y.AppendLine("        with:");
        y.AppendLine("          dotnet-version: 10.0.x");
        var windows = os == "windows";
        Step(y, windows, "Version and channel from the tag", TagParseBash, TagParsePwsh, id: "tag");
        Step(y, windows, "Publish the app (exactly the files installations receive)",
            ["dotnet publish \"$APP_PROJECT\" -c Release -r \"$RID\" -p:Version=\"$VERSION\" -o out/app"],
            ["dotnet publish $env:APP_PROJECT -c Release -r $env:RID -p:Version=$env:VERSION -o out/app"]);
        y.AppendLine("      # Authenticode: to sign the installers, pass your signing command to both installer builds, e.g.");
        y.AppendLine($"      #   -p:InstellaSignCommand=\"...\"  (a cloud signing service; see {InstellaDocs.Page("distribution-and-signing.md")}).");
        y.AppendLine("      # The online installer is built first: the offline build appends the app to its own copy.");
        Step(y, windows, "Build the online installer",
            ["dotnet publish \"$INSTALLER_PROJECT\" -c Release -r \"$RID\" -p:Version=\"$VERSION\" -p:InstellaEnabled=false -o out/online"],
            ["dotnet publish $env:INSTALLER_PROJECT -c Release -r $env:RID -p:Version=$env:VERSION -p:InstellaEnabled=false -o out/online"]);
        Step(y, windows, "Build the offline installer",
            ["dotnet publish \"$INSTALLER_PROJECT\" -c Release -r \"$RID\" -p:Version=\"$VERSION\" -o out/offline"],
            ["dotnet publish $env:INSTALLER_PROJECT -c Release -r $env:RID -p:Version=$env:VERSION -o out/offline"]);
        // The installer's own file name: its assembly name, as the template sets it from the project name.
        var installerAssembly = Path.GetFileNameWithoutExtension(o.InstallerProject.Replace('\\', '/'));
        Step(y, windows, "Name the installers",
            [
                "mkdir -p out/installers",
                $"cp \"out/online/{installerAssembly}\" \"out/installers/$APP_NAME-WebSetup-$VERSION\"",
                $"cp \"out/offline/{installerAssembly}\" \"out/installers/$APP_NAME-Setup-$VERSION\"",
            ],
            [
                "New-Item -ItemType Directory -Force out/installers | Out-Null",
                $"Copy-Item \"out/online/{installerAssembly}.exe\" \"out/installers/$env:APP_NAME-WebSetup-$env:VERSION.exe\"",
                $"Copy-Item \"out/offline/{installerAssembly}.exe\" \"out/installers/$env:APP_NAME-Setup-$env:VERSION.exe\"",
            ]);
        y.AppendLine($"      - uses: actions/upload-artifact@{artifactVersion}");
        y.AppendLine("        with:");
        y.AppendLine("          name: release");
        // v4 leaves out dot-files (a .instella folder in the app, for example); Gitea's v3 keeps them.
        if (gitHub) y.AppendLine("          include-hidden-files: true");
        y.AppendLine("          path: |");
        y.AppendLine("            out/app");
        y.AppendLine("            out/installers");
        y.AppendLine();
        y.AppendLine("  publish:");
        y.AppendLine("    needs: build");
        y.AppendLine("    runs-on: ubuntu-latest");
        if (o.Signing != CiSigning.Draft)
        {
            if (gitHub)
            {
                y.AppendLine("    # Settings > Environments > release: add required reviewers and allow only v* tags,");
                y.AppendLine("    # so nothing is signed without a person approving this run.");
                y.AppendLine("    environment: release");
            }
            else
            {
                y.AppendLine("    # Gitea has no deployment environments: protect the v* tags (Settings > Tags) so only");
                y.AppendLine("    # maintainers can start this workflow, and keep the secrets below at repository level.");
            }
        }
        if (oidc)
        {
            y.AppendLine("    permissions:");
            y.AppendLine("      contents: read");
            y.AppendLine("      id-token: write   # OIDC sign-in to the key service; no cloud secret is stored");
        }
        y.AppendLine("    steps:");
        y.AppendLine($"      - uses: actions/download-artifact@{artifactVersion}");
        y.AppendLine("        with:");
        y.AppendLine("          name: release");
        y.AppendLine("          path: out");
        y.AppendLine("      - uses: actions/setup-dotnet@v4");
        y.AppendLine("        with:");
        y.AppendLine("          dotnet-version: 10.0.x");
        y.AppendLine("      - name: Install the Instella CLI");
        y.AppendLine("        run: |");
        y.AppendLine($"          dotnet tool install --global instella-cli --version {ProjectTemplate.PackageVersion}");
        y.AppendLine("          echo \"$HOME/.dotnet/tools\" >> \"$GITHUB_PATH\"");
        AppendKeyServiceLogin(y, o.Signing, gitHub);
        y.AppendLine("      - name: Upload the release");
        y.AppendLine("        env:");
        y.AppendLine("          INSTELLA_API_KEY: ${{ secrets.INSTELLA_API_KEY }}");
        y.AppendLine("          # From the build job, not parsed again: one source for both jobs.");
        y.AppendLine("          VERSION: ${{ needs.build.outputs.version }}");
        y.AppendLine("          CHANNEL: ${{ needs.build.outputs.channel }}");
        AppendSigningEnv(y, o.Signing, gitHub);
        y.AppendLine("        run: |");
        y.AppendLine("          ext=\"\"; [ \"${RID%%-*}\" = \"win\" ] && ext=\".exe\"");
        y.AppendLine("          instella upload --server \"$INSTELLA_SERVER\" --package \"$INSTELLA_PACKAGE\" --version \"$VERSION\" --channel \"$CHANNEL\" \\");
        y.AppendLine($"            --os {os} --arch {arch} --path out/app \\");
        y.AppendLine("            --installer \"out/installers/$APP_NAME-WebSetup-$VERSION$ext\" \\");
        y.AppendLine("            --offline-installer \"out/installers/$APP_NAME-Setup-$VERSION$ext\"" + (o.Signing == CiSigning.Draft ? " \\" : ""));
        if (o.Signing == CiSigning.Draft)
        {
            y.AppendLine("            --draft");
            y.AppendLine($"          echo \"Draft uploaded. A maintainer publishes it with 'instella publish' (see {InstellaDocs.Page("publishing.md")}).\"");
        }
        return y.ToString();
    }

    /// <summary>A build step in PowerShell on Windows runners and bash elsewhere.</summary>
    private static void Step(StringBuilder y, bool windows, string name, string[] bash, string[] pwsh, string? id = null)
    {
        var lines = windows ? pwsh : bash;
        y.AppendLine($"      - name: {name}");
        if (id is not null) y.AppendLine($"        id: {id}");
        y.AppendLine($"        shell: {(windows ? "pwsh" : "bash")}");
        y.AppendLine("        run: |");
        foreach (var line in lines) y.AppendLine("          " + line);
    }

    /// <summary>What to set up before the first run: secrets, variables, the key service.</summary>
    public static string SetupNotes(CiOptions o)
    {
        var gitHub = o.Host == CiHost.GitHub;
        var n = new StringBuilder();
        n.AppendLine($"Wrote {WorkflowPath(o.Host)}. Before the first release:");
        n.AppendLine();
        n.AppendLine("  Secret INSTELLA_API_KEY: a server API key limited to package " + o.PackageId + " with upload permission.");
        switch (o.Signing)
        {
            case CiSigning.KmsAzure:
                n.AppendLine("  Azure Key Vault: an EC P-256 key the pipeline identity may only 'sign' with.");
                n.AppendLine("  Variables INSTELLA_KEY_VAULT_KEY_ID (the key's URL) and INSTELLA_SIGNING_PUBLIC_KEY (base64 SPKI).");
                n.AppendLine(gitHub
                    ? "  Variables AZURE_CLIENT_ID and AZURE_TENANT_ID of an app registration with a federated credential for this repository's 'release' environment."
                    : "  Variables AZURE_CLIENT_ID, AZURE_TENANT_ID and secret AZURE_CLIENT_SECRET (Gitea has no OIDC tokens); the runner image needs the Azure CLI.");
                break;
            case CiSigning.KmsAws:
                n.AppendLine("  AWS KMS: an ECC_NIST_P256 SIGN_VERIFY key; the pipeline role may only kms:Sign with it.");
                n.AppendLine("  Variables INSTELLA_KMS_KEY_ID, AWS_REGION and INSTELLA_SIGNING_PUBLIC_KEY (base64 SPKI: aws kms get-public-key).");
                n.AppendLine(gitHub
                    ? "  Variable AWS_ROLE_ARN: a role whose trust policy allows this repository's 'release' environment through GitHub OIDC."
                    : "  Secrets AWS_ACCESS_KEY_ID and AWS_SECRET_ACCESS_KEY of a user that may only kms:Sign with that key (Gitea has no OIDC tokens); the runner image needs the AWS CLI.");
                break;
            case CiSigning.KmsGcp:
                n.AppendLine("  Google Cloud KMS: an EC_SIGN_P256_SHA256 key; the service account may only use it to sign.");
                n.AppendLine("  Variables GCP_KMS_LOCATION, GCP_KMS_KEYRING, GCP_KMS_KEY, GCP_KMS_KEY_VERSION and INSTELLA_SIGNING_PUBLIC_KEY.");
                n.AppendLine(gitHub
                    ? "  Variables GCP_WORKLOAD_IDENTITY_PROVIDER and GCP_SERVICE_ACCOUNT (workload identity federation for this repository)."
                    : "  Secret GCP_CREDENTIALS_JSON: a key of that service account (Gitea has no OIDC tokens).");
                break;
            case CiSigning.Draft:
                n.AppendLine("  No signing key in CI. After each run a maintainer publishes the draft on their own machine:");
                n.AppendLine($"    instella publish --server {o.ServerUrl} --package {o.PackageId} --version <version> --os {SplitRid(o.Rid).Os} --arch {SplitRid(o.Rid).Arch} \\");
                n.AppendLine("        --path <the run's app artifact> --installer <...> --offline-installer <...> --signing-key <key.pem>");
                break;
            default:
                n.AppendLine("  Secret INSTELLA_SIGNING_KEY: the publisher key's PEM text; INSTELLA_SIGNING_KEY_PASSWORD if it is encrypted.");
                n.AppendLine(gitHub
                    ? "  Put both in the 'release' environment (not repository secrets) and give it required reviewers."
                    : "  Anyone who can change workflows can read these secrets: prefer a KMS or draft publishing.");
                break;
        }
        n.AppendLine();
        n.AppendLine("  The installer must be built with the same public key (WithPublisherKey) and the same server (WithServer).");
        n.AppendLine($"  Pin each action to a commit SHA. Full guide: {InstellaDocs.Page("publishing.md")}.");
        return n.ToString();
    }

    private static void AppendKeyServiceLogin(StringBuilder y, CiSigning signing, bool gitHub)
    {
        switch (signing)
        {
            case CiSigning.KmsAzure when gitHub:
                y.AppendLine("      - uses: azure/login@v2");
                y.AppendLine("        with:");
                y.AppendLine("          client-id: ${{ vars.AZURE_CLIENT_ID }}");
                y.AppendLine("          tenant-id: ${{ vars.AZURE_TENANT_ID }}");
                y.AppendLine("          allow-no-subscriptions: true");
                break;
            case CiSigning.KmsAzure:
                y.AppendLine("      - name: Sign in to Azure (a service principal that may only sign with the one key)");
                y.AppendLine("        run: az login --service-principal --username \"${{ vars.AZURE_CLIENT_ID }}\" --password \"${{ secrets.AZURE_CLIENT_SECRET }}\" --tenant \"${{ vars.AZURE_TENANT_ID }}\" --allow-no-subscriptions");
                break;
            case CiSigning.KmsAws when gitHub:
                y.AppendLine("      - uses: aws-actions/configure-aws-credentials@v4");
                y.AppendLine("        with:");
                y.AppendLine("          role-to-assume: ${{ vars.AWS_ROLE_ARN }}");
                y.AppendLine("          aws-region: ${{ vars.AWS_REGION }}");
                break;
            case CiSigning.KmsGcp:
                y.AppendLine("      - uses: google-github-actions/auth@v2");
                y.AppendLine("        with:");
                if (gitHub)
                {
                    y.AppendLine("          workload_identity_provider: ${{ vars.GCP_WORKLOAD_IDENTITY_PROVIDER }}");
                    y.AppendLine("          service_account: ${{ vars.GCP_SERVICE_ACCOUNT }}");
                }
                else
                {
                    y.AppendLine("          credentials_json: ${{ secrets.GCP_CREDENTIALS_JSON }}");
                }
                y.AppendLine("      - uses: google-github-actions/setup-gcloud@v2");
                break;
        }
    }

    private static void AppendSigningEnv(StringBuilder y, CiSigning signing, bool gitHub)
    {
        switch (signing)
        {
            case CiSigning.KmsAzure:
                y.AppendLine("          INSTELLA_SIGNING_PUBLIC_KEY: ${{ vars.INSTELLA_SIGNING_PUBLIC_KEY }}");
                y.AppendLine("          INSTELLA_SIGN_COMMAND: az keyvault key sign --id ${{ vars.INSTELLA_KEY_VAULT_KEY_ID }} --algorithm ES256 --digest {digest-base64} --query result -o tsv");
                break;
            case CiSigning.KmsAws:
                if (!gitHub)
                {
                    y.AppendLine("          AWS_ACCESS_KEY_ID: ${{ secrets.AWS_ACCESS_KEY_ID }}");
                    y.AppendLine("          AWS_SECRET_ACCESS_KEY: ${{ secrets.AWS_SECRET_ACCESS_KEY }}");
                    y.AppendLine("          AWS_REGION: ${{ vars.AWS_REGION }}");
                }
                y.AppendLine("          INSTELLA_SIGNING_PUBLIC_KEY: ${{ vars.INSTELLA_SIGNING_PUBLIC_KEY }}");
                y.AppendLine("          INSTELLA_SIGN_COMMAND: aws kms sign --key-id ${{ vars.INSTELLA_KMS_KEY_ID }} --message-type DIGEST --signing-algorithm ECDSA_SHA_256 --message \"fileb://$INSTELLA_DIGEST_FILE\" --query Signature --output text");
                break;
            case CiSigning.KmsGcp:
                y.AppendLine("          INSTELLA_SIGNING_PUBLIC_KEY: ${{ vars.INSTELLA_SIGNING_PUBLIC_KEY }}");
                y.AppendLine("          INSTELLA_SIGN_COMMAND: gcloud kms asymmetric-sign --location ${{ vars.GCP_KMS_LOCATION }} --keyring ${{ vars.GCP_KMS_KEYRING }} --key ${{ vars.GCP_KMS_KEY }} --version ${{ vars.GCP_KMS_KEY_VERSION }} --digest-algorithm sha256 --input-file \"$INSTELLA_MESSAGE_FILE\" --signature-file \"$INSTELLA_MESSAGE_FILE.sig\" && base64 -w0 \"$INSTELLA_MESSAGE_FILE.sig\"");
                break;
            case CiSigning.Secret:
                y.AppendLine("          INSTELLA_SIGNING_KEY: ${{ secrets.INSTELLA_SIGNING_KEY }}");
                y.AppendLine("          INSTELLA_SIGNING_KEY_PASSWORD: ${{ secrets.INSTELLA_SIGNING_KEY_PASSWORD }}");
                break;
        }
    }

    /// <summary>Canonical OS and architecture names of a RID such as <c>win-x64</c>.</summary>
    internal static (string Os, string Arch) SplitRid(string rid)
    {
        var parts = rid.Split('-', 2);
        var os = parts[0] switch { "win" => "windows", "osx" => "macos", _ => "linux" };
        return (os, parts.Length > 1 ? parts[1] : "x64");
    }

    /// <summary>A YAML scalar in single quotes.</summary>
    private static string Q(string value) => "'" + value.Replace("'", "''") + "'";
}
