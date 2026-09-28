# instella-cli

Command-line tool for Instella: scaffold an installer project, manage publisher signing keys,
and publish builds to an Instella server.

```bash
dotnet tool install --global instella-cli

instella init --name "Quick Notes" --app src/QuickNotes/QuickNotes.csproj --server https://updates.example.com
instella keys generate --out publisher.key.pem --password-env KEY_PASSWORD
instella keys show --key publisher.key.pem --password-env KEY_PASSWORD

# The API key comes from --api-key or INSTELLA_API_KEY; the signing key from --signing-key or
# INSTELLA_SIGNING_KEY (PEM text or a file path), its password from INSTELLA_SIGNING_KEY_PASSWORD.
instella upload --server https://updates.example.com --package com.example.quicknotes \
    --version 1.2.0 --path ./publish --os windows --arch x64 --signing-key publisher.key.pem

# Publish the installers built for this version with it (listed in the signed release; served at
# /api/v1/installer/<package>/<version|latest>/<os>/<arch>/<online|offline>).
instella upload ... --installer ./QuickNotes-WebSetup-1.2.0.exe --offline-installer ./QuickNotes-Setup-1.2.0.exe

# Sign with a key that stays in a KMS (here AWS KMS; see {{DocsUrl}}/signing-and-keys.md for Azure/GCP).
instella upload ... --signing-public-key <base64 public key> \
    --sign-command "aws kms sign --key-id alias/instella-publisher --message-type DIGEST --signing-algorithm ECDSA_SHA_256 --message fileb://{digest-file} --query Signature --output text"

# Key rotation: installations that take this release trust exactly the listed keys afterwards.
instella upload ... --signing-key a.key.pem --trusted-key <A public key> --trusted-key <B public key>

# Hand-signed: CI uploads a draft without a key; a maintainer checks and signs.
instella upload ... --draft
instella publish --server https://updates.example.com --package com.example.quicknotes --version 1.2.0 \
    --os windows --arch x64 --path ./publish --signing-key publisher.key.pem

# Release approval (the package on the server holds releases back): list, approve, reject,
# with an API key that has the Approve releases permission (never the CI upload key).
instella pending --server https://updates.example.com --package com.example.quicknotes
instella approve --server https://updates.example.com --package com.example.quicknotes --version 1.2.0 \
    --os windows --arch x64 --path ./publish
instella reject --server https://updates.example.com --package com.example.quicknotes --version 1.2.0 \
    --os windows --arch x64 --reason "unexpected tag"

# A release workflow for GitHub or Gitea Actions (see {{DocsUrl}}/publishing.md): --signing secret,
# kms-azure, kms-aws, kms-gcp or manual; --no-environment for GitHub repositories without environments.
instella ci init --host github --signing kms-aws --app-project src/QuickNotes/QuickNotes.csproj \
    --installer-project QuickNotes.Installer/QuickNotes.Installer.csproj \
    --package com.example.quicknotes --server https://updates.example.com

instella list packages --server https://updates.example.com
instella list versions --server https://updates.example.com --package com.example.quicknotes
instella delete --server https://updates.example.com --package com.example.quicknotes --version 1.1.0
```

Server URLs must be HTTPS (plain HTTP is accepted for loopback, or anywhere with `--allow-insecure`). Keep the
private key out of the repository and generate an offline backup key: see
[signing and keys]({{DocsUrl}}/signing-and-keys.md).

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Bad arguments, missing input, or a refused operation |
| 2 | The server returned an error or could not be reached |
| 3 | The server rejected the API key (401/403); for `delete`, a key without "Manage versions" |
| 4 | Signing failed (the key, the sign command, or a signature that does not verify); nothing was uploaded |
