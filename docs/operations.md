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

## Telemetry and alerts

Requests, dependencies and exceptions go to Application Insights (`appi-podium`, workspace-based) through
OpenTelemetry. Three alert rules e-mail the budget address: web container terminated / probe failed (crash loop),
three or more failed builds in an hour, ten or more 5xx responses in 15 minutes. Alerts, the action group and App
Insights are all in `infra/foundation.bicep`.

## Letting the room in

Deck visibility applies to the plain URL: a Private or Shared deck asks visitors to sign in. The way to let a whole
room follow a talk is a **live session**: it mints a share link that admits anyone, plus a six-character join code
(`/j/ABC-123`, letters/digits that cannot be confused when read aloud, 887 million combinations) that only resolves
while the session is live. Ending the session revokes both, and every socket admitted through the link is closed
(code 4410) so the room's screens show "this session has ended" at once; revoking any share link closes its sockets
the same way. Codes and deck entry are rate limited per client address at 300/min so a conference room behind one
NAT fits; the link ids themselves are 144-bit random. View tokens on the external origin name the link that admitted
the viewer, so a revoked link ends that access immediately rather than at the token's 12-hour expiry.

## Sync protocol (v3)

Every deck window and the phone remote talk to `/ws/sync/{slug}`. The server decides who may publish (owner and
*Present* grantees); viewers only receive. Presenting sockets declare a role: `presenter` (the Slidev presenter
view), `play` (a deck window such as the projector) or `remote` (the phone). Commands that must run exactly once
(`nav`, `pointer`, `timer`) are delivered to the *primary* deck window only: the presenter view when one is
connected, otherwise the longest-connected play window; everything else follows through the relayed shared state.
The blackout (`screen`) is replayed to every newcomer and each window decides whether to go dark (deck windows do,
the presenter view and the remote do not). Session start/plan/end are pushed as `session` messages (full details to
presenters, a live flag to viewers), so nothing polls.

Who provides the window side:

- **Slidev**: the addon bundled by the builder (`builder/addon`, protocol in a `podium-addon` meta tag). It carries
  no UI; it exposes `window.__podium` to Podium's server-served `/_podium/live-ui.js`, which draws the pill, HUD,
  blackout, laser dot and toasts. UI changes therefore ship with the web app; only protocol changes rebuild decks.
  Decks built with an older addon keep their built-in pill until the hourly builder-upgrade check rebuilds them.
- **presenterm**: `/_podium/bridge.js` + `/_podium/presenterm.js`, injected when the page is served, drive the
  export's own script with synthetic key events and observe which slide is shown.
- **PowerPoint / PDF**: the builder renders pages to `site/pages/NNN.jpg` (pdftoppm, 1920 px wide) and writes a
  shell that `/_podium/pages.js` turns into a viewer. Without pdftoppm the browser's PDF viewer is used instead.

Rate limits: 40 messages/s per presenting socket (the laser sends at most 20/s), 5/s per viewer socket (handshake
only), 200 sockets per room, 256 KB per message.

## Live sessions and the deploy guard

A live session can ask Podium to hold its own deployments (per session, off by default). The workflow polls
`/healthz/live` and waits up to `DEPLOY_GUARD_MAX_WAIT_MINUTES` (repository variable, default 45; `0` disables), or
deploys immediately with the `force` input. Sessions end by themselves when no presenter has been connected for 15
minutes, 30 minutes past the planned length, or after 4 hours, so a forgotten session never blocks for long. Deck
builds are never affected by the guard.

## Audit trail and sessions

Every mutating owner API call is recorded (actor, action, target, bounded details with passcodes masked, IP) and
shown per deck and on `/activity`; `GET /api/activity` returns the newest 100. *Sign out everywhere* (Sources page)
bumps a security stamp that invalidates every session cookie issued before it.

## Backups and export

A scheduled Container Apps Job (`podium-backup`, daily at 03:15 UTC) runs the web image in `export` mode with the web
identity and writes every table as JSON to the `backups` container (`<timestamp>/<table>.json`); a lifecycle rule
deletes runs older than 30 days. Trigger one manually with `az containerapp job start -g rg-podium -n podium-backup`.
For an ad-hoc local copy:

```pwsh
./scripts/export-data.ps1 -ResourceGroup rg-podium -Out ./export            # tables as JSON
./scripts/export-data.ps1 -ResourceGroup rg-podium -Out ./export -IncludeArtifacts   # plus served builds
```

Requires *Storage Table Data Reader* (and *Storage Blob Data Reader* for artifacts) on the storage account for the
signed-in user. Artifacts are reproducible from the repositories at any time (a rebuild recreates them), so the
tables are the part worth keeping.

## Re-running infrastructure deployments

`infra/deploy.ps1` keeps whatever image the web app and builder job currently run (CI deploys pinned digests); the
`:latest` tags are only used when a resource does not exist yet. Pass `-WebImage`/`-BuilderImage` to override.
The Key Vault has purge protection on (irreversible), `main` rejects force-pushes and deletion, and every GitHub Action
is pinned to a commit SHA (Dependabot bumps the pins).

## Scaling

- `infra/deploy.ps1 -Phase app -MinReplicas 1` keeps one replica warm (no ~20 s cold start) for a few dollars a month;
  `-MinReplicas 0` (default) scales to zero after `scaleToZeroAfterSeconds` (30 min) of no traffic.
- Builds run `Builder:MaxConcurrentBuilds` (3) at a time; the rest wait and start as slots free up. Raise it for large
  installations; each build is one job execution (2 vCPU / 4 GiB).

## Builder image updates

The builder image is identified by the git tree hash of `builder/`. A deploy whose `builder/` is unchanged reuses the
existing image, so web-only deploys do not rebuild decks. When the image does change, every deck is rebuilt (bounded by
the concurrency limit) on the next hourly check or app start; failures are listed on each deck's page.

## GitHub check runs

Builds are mirrored as check runs (`Podium / <deck title>`) on the commit when the App has **Checks: read & write**
and the installation has accepted that permission (GitHub → *Settings → Applications → Installed GitHub Apps →
Podium → Configure* shows the pending request). Without it the first attempt gets a 403 and the observer disables
itself until the app restarts (`GitHub check runs disabled` in the console log; `GitHub check runs active` once it
works). Clicking **Re-run** on a check run rebuilds the deck from its current commit; the `check_run` webhook event
must be subscribed for that. New Apps created with `scripts/github-app-manifest.ps1` have both from the start.

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
