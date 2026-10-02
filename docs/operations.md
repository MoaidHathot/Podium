# Operations

Day-two tasks for a Podium deployment. Everything below uses your `az` and `gh` logins; no secret ever needs to be
copied out of Key Vault by hand.

## Where things live

| What | Where |
|---|---|
| Index (sources, decks, builds, grants, share links, views) | Table storage in the Podium storage account (`stpodium…`) |
| Build artifacts | one blob container per build, `b-<buildId>-<hash>`; retention keeps the served, pinned and rollback builds |
| Secrets | Key Vault `kv-podium-…`: `podium-signing-key`, `github-private-key`, `github-client-id`, `github-client-secret`, `github-webhook-secret` |
| Cookie / antiforgery keys | DataProtection key ring in blob storage, wrapped with the Key Vault key `podium-dataprotection` |
| Logs | Log Analytics: `ContainerAppConsoleLogs_CL` (web + builder output), `ContainerAppSystemLogs_CL` (revisions, probes, restarts) |

The web app reads secrets through Key Vault references; Container Apps re-syncs them roughly every 30 minutes
(`SyncingSecretFromAzureKeyVault…` in the system logs). To apply a rotated secret immediately, restart the active
revision:

```pwsh
az containerapp revision list -g rg-podium -n podium-web --query "[?properties.active].name" -o tsv |
  ForEach-Object { az containerapp revision restart -g rg-podium -n podium-web --revision $_ }
```

## Rotating secrets

Rotate in this order when you suspect a leak; each item is independent otherwise.

### Signing key (`podium-signing-key`)

Signs builder callback tokens, view tokens for the external origin and signed viewer file links.
Rotating it invalidates all of them at once: open deck tabs on the external origin will bounce through the library
once, and **a build in flight will fail to report** (it is retried by the next push or a manual rebuild).

```pwsh
$bytes = [byte[]]::new(32); [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
az keyvault secret set --vault-name <kv> -n podium-signing-key --value ([Convert]::ToBase64String($bytes)) -o none
# then restart the revision (above)
```

Wait for `Running` builds to finish first (library shows them; or `az containerapp job execution list -g rg-podium -n podium-builder`).

### GitHub App private key (`github-private-key`)

GitHub App settings → *Private keys* → *Generate a private key*. Store the downloaded PEM, then revoke the old key
in GitHub once the app has restarted:

```pwsh
az keyvault secret set --vault-name <kv> -n github-private-key --file podium-slides.YYYY-MM-DD.private-key.pem -o none
```

Installation tokens are minted per request from this key and are never persisted; nothing else changes.

### OAuth client secret (`github-client-secret`)

GitHub App settings → *Client secrets* → *Generate a new client secret*. Store it, restart, then delete the old
secret in GitHub. Signed-in sessions are unaffected (they are cookies protected by DataProtection, not by this secret).

### Webhook secret (`github-webhook-secret`)

Generate a random value, set it in Key Vault **and** in the GitHub App's webhook settings, restart. Deliveries sent
between the two changes fail signature verification (401) and show up as failed deliveries in the App's *Advanced*
tab; redeliver them from there, or push again.

### DataProtection key (`podium-dataprotection` in Key Vault)

Create a new key version (`az keyvault key rotate --vault-name <kv> -n podium-dataprotection`). New key-ring entries
are wrapped with the new version; existing ones keep unwrapping with the version they were created under, so sessions
survive. Disabling old versions logs everyone out.

### Storage access

There are no storage keys in use (shared-key access is disabled on the account; everything goes through managed
identities and short-lived, container-scoped SAS for the builder, signed with a user delegation key). Nothing to rotate.

### GitHub Actions deploy identity

OIDC federated credentials on the `id-podium-deploy` user-assigned identity; there is no secret. If the repository is
renamed or forked, re-run `infra/deploy.ps1 -Phase foundation` from the new repository (`-GitHubRepository owner/repo`):
it registers the branch, environment and id-form subjects GitHub presents.

## Backups and export

```pwsh
./scripts/export-data.ps1 -ResourceGroup rg-podium -Out ./export            # tables as JSON
./scripts/export-data.ps1 -ResourceGroup rg-podium -Out ./export -IncludeArtifacts   # plus served builds
```

Requires *Storage Table Data Reader* (and *Storage Blob Data Reader* for artifacts) on the storage account for the
signed-in user. Artifacts are reproducible from the repositories at any time (a rebuild recreates them), so the
tables are the part worth keeping.

## Scaling

- `infra/deploy.ps1 -Phase app -MinReplicas 1` keeps one replica warm (no ~20 s cold start) for a few dollars a month;
  `-MinReplicas 0` (default) scales to zero after `scaleToZeroAfterSeconds` (30 min) of no traffic.
- Builds run `Builder:MaxConcurrentBuilds` (3) at a time; the rest wait and start as slots free up. Raise it for large
  installations; each build is one job execution (2 vCPU / 4 GiB).

## Builder image updates

The builder image is identified by the git tree hash of `builder/`. A deploy whose `builder/` is unchanged reuses the
existing image, so web-only deploys do not rebuild decks. When the image does change, every deck is rebuilt (bounded by
the concurrency limit) on the next hourly check or app start; failures are listed on each deck's page.

## Checking on things

```kusto
// builds finished in the last day
ContainerAppConsoleLogs_CL
| where ContainerAppName_s == "podium-web" and Log_s has "finished:"
| project TimeGenerated, Log_s | order by TimeGenerated desc

// web restarts / crash loops
ContainerAppSystemLogs_CL
| where ContainerAppName_s == "podium-web" and Reason_s in ("ContainerTerminated", "ProbeFailed")
| project TimeGenerated, Reason_s, Log_s
```
