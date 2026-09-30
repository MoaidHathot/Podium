# Podium

Serve your [Slidev](https://sli.dev) and [presenterm](https://github.com/mfontanini/presenterm) decks straight from
GitHub, from anywhere, without your laptop. Push to a repository, Podium builds the deck in an isolated container and
serves the result behind your login, with presenter view, PDF/PPTX exports, share links, and a searchable library.

```
GitHub repos (private or public)
   │  GitHub App: contents:read + push webhooks
   ▼
Podium web (ASP.NET Core, Azure Container Apps, scale-to-zero)
   ├─ Library UI: search, recently presented/updated, pinned, build status
   ├─ /d/<slug>/            built deck (SPA), presenter view at /d/<slug>/presenter/
   ├─ /d/<slug>.pdf|.pptx   exports, each with its own visibility
   └─ Builder job (Container Apps Job, ephemeral per build):
        clone @sha → npm ci --ignore-scripts → slidev build/export → upload → report
Storage: Blob (artifacts, one container per build) + Table (index). Managed identity everywhere; no shared keys.
```

## What you get

- **Rendering identical to local**: `slidev build` output, served as-is (presenter mode, overview, notes, drawings, click animations).
- **Live updates**: pushes trigger rebuilds via webhook; open decks detect the new build and reload on the same slide.
- **Access control** per deck *and* per artifact: Private, Link (revocable signed URLs), Shared (specific GitHub users), Public.
- **Discovery**: one library across all connected repositories; `/` focuses search.
- **External decks**: public repositories you do not own can be added; they build in the same sandbox and are served with a CSP sandbox (opaque origin).
- **Security**: single owner pinned by GitHub user id; untrusted deck code only runs inside a throwaway container with a write-only SAS scoped to its own blob container; installation tokens never touch disk; CSRF header + SameSite cookies; secrets in Key Vault.

## Deck detection

| Kind | Detected by | Build |
|---|---|---|
| Slidev | `slides.md` in a directory | `npm ci` (if `package.json`) + `slidev build --base /d/<slug>/` + `slidev export` |
| presenterm | `config.yaml` + a `.md` (prefers `main.md`) | `presenterm --export-html`; PDF served if one is committed next to the deck |
| Static HTML | committed `.html` with no source deck | copied as-is |

`node_modules`, `dist`, `.slidev`, `bin`, `obj` are ignored. Legacy GitPitch decks are skipped.

Optional per-deck `.podium.yml` (trusted repositories only): `npmScripts: true` allows lifecycle scripts during install.

## Deploying (Azure, ~$1-5/month)

Prerequisites: `az` logged in, `gh` logged in, PowerShell 7.

```pwsh
./infra/deploy.ps1 -Phase foundation                 # identities, storage, key vault, environment, builder job, budget
./infra/deploy.ps1 -Phase secrets                    # signing key
./scripts/github-app-manifest.ps1 -KeyVaultName <kv> # creates the GitHub App in your browser, stores creds in Key Vault
./infra/deploy.ps1 -Phase app -GitHubAppId <id> -GitHubAppSlug <slug>
# add the printed CNAME + TXT records at your DNS provider, then:
./infra/deploy.ps1 -Phase domain
```

Set repository variables `AZURE_CLIENT_ID` (deploy identity), `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`,
`AZURE_RESOURCE_GROUP` and the GitHub Actions workflow builds both images to GHCR and rolls them out with OIDC.
No deployment secrets exist anywhere.

Finally install the GitHub App on the repositories holding your slides (Sources page has the link).

## Local development

```pwsh
npm i -g azurite && azurite --silent --location $env:TEMP/azurite   # or set Storage:ConnectionString to "memory"
cd builder && npm install
$env:GitHub__Token = (gh auth token)     # dev-only PAT instead of the GitHub App
dotnet run --project src/Podium.Web      # http://localhost:5187, /dev-login signs you in as the owner
```

`appsettings.Development.json` uses the LocalProcess builder (runs `builder/build.mjs` with the local Node) and Azurite.

## Configuration

| Key | Purpose |
|---|---|
| `Podium:PublicBaseUrl` | public origin, used for builder callbacks and links |
| `Podium:OwnerGitHubId` | numeric GitHub id of the single owner |
| `Podium:SigningKey` | base64 key for HMAC tokens (builder callbacks) |
| `GitHub:AppId`, `GitHub:AppSlug`, `GitHub:PrivateKeyPem`, `GitHub:ClientId`, `GitHub:ClientSecret`, `GitHub:WebhookSecret` | GitHub App |
| `GitHub:Token` | development only: PAT used instead of installation tokens |
| `Storage:AccountName` / `Storage:ConnectionString` | managed identity (prod) / Azurite or `memory` (dev) |
| `Builder:Mode` | `ContainerAppsJob` or `LocalProcess`; `Builder:JobResourceId`, `Builder:LocalScriptPath` |

## Remote limitations

- Code that needs a server at runtime (custom runners, server-side execution) is not available; Monaco runs in the browser and works.
- presenterm PDF export requires weasyprint, which the builder does not ship; commit the PDF next to the deck to serve it.
- Presenter/audience sync across *devices* is on the roadmap (a sync addon injected at build time); within one machine it works out of the box.
