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
- **Cross-device presenter sync**: a tiny Slidev addon is injected at build time; any instance opened by you (or a *Present* grantee) drives every other open instance through a WebSocket relay. Viewers follow. Clickers work as plain keyboard input.
- **Phone remote** at `/d/<slug>/remote` (prev/next, counter, timer) and a **QR code** (`/d/<slug>/qr.svg`, shown on the deck page) so the room can open the deck and follow live.
- **Live updates without surprises**: a new build is announced over the socket; hidden tabs reload silently, visible viewers get a "Reload" notice, presenter views are never reloaded automatically.
- **Self-updating builds**: when the builder image changes, decks built by the previous builder are rebuilt automatically (failed ones are retried).
- **External decks**: public repositories you do not own can be added. They build in the same isolated job and are served from a *separate origin* (`Podium:ExternalBaseUrl`, by default the platform FQDN) with a short-lived view token instead of your session, so their code can never read your session or private decks.
- **PowerPoint decks** can be viewed as a PDF rendition (default, nothing leaves Podium) or through Microsoft's Office Online viewer (faithful rendering; Microsoft's service fetches the file via a 20-minute signed link). Per-deck setting under *Manage*.
- **Library**: group by repository/type/visibility/year, sort, grid or an aligned compact list, filters (including tags), collapsible Pinned and Recently-presented shelves (decks can be removed from recents until presented again); preferences are remembered per browser.
- **Short aliases**: `/d/<alias>/` redirects to the canonical deck URL (set under *Manage* or in `.podium.yml`); deep links and query strings are preserved.
- **Frozen decks**: pin the build you rehearsed with while pushes keep building in the background; roll forward whenever you choose.
- **View analytics** on each deck's page: views in the last 7/30 days, distinct viewers and the most recent viewers.
- **Link previews**: public decks carry Open Graph / Twitter card tags (title, description, first-slide thumbnail) so links unfurl in chat and social clients; tags the deck already declares are left alone.
- **Clean-up**: decks that disappear from their repository are archived (artifacts purged after 30 days) and listed in an *Archived* shelf with a *Delete permanently* action for immediate removal of builds, grants and share links.
- **GitHub check runs** (optional): grant the app *Checks: write* and every build reports back on the commit with a link to the deck.
- **Security**: single owner pinned by GitHub user id; untrusted deck code only runs inside a throwaway container with a write-only SAS scoped to its own blob container, and is served from a separate origin; installation tokens never touch disk; CSRF header + SameSite cookies; per-IP rate limits on login, webhook and deck entry; secrets in Key Vault, data-protection keys wrapped by a Key Vault key.

## Deck detection

| Kind | Detected by | Build |
|---|---|---|
| Slidev | `slides.md` in a directory | `npm ci` (if `package.json`) + `slidev build --base /d/<slug>/` + `slidev export` |
| presenterm | `config.yaml` + a `.md` (prefers `main.md`) | `presenterm --export-html` + `--export-pdf` (weasyprint); a committed PDF is the fallback |
| PowerPoint | any `.pptx` file (one deck per file) | converted to PDF with LibreOffice for in-browser viewing; original offered for download. A committed `.pdf` with the same name is used instead of converting |
| PDF | any standalone `.pdf` | served in the browser's PDF viewer |
| Static HTML | committed `.html` with no source deck | copied as-is |

Every build also captures a first-slide thumbnail for the library. `node_modules`, `dist`, `.slidev`, `bin`, `obj` are ignored. Legacy GitPitch decks are skipped.

### Adding a deck

Create a folder anywhere in a connected repository and push:

- **Slidev**: `my-talk/slides.md` (plus `package.json`/`package-lock.json` if you need specific versions, themes or addons; without one Podium builds with its bundled Slidev). `npm create slidev@latest` produces a suitable folder. Set `title:` in the headmatter; the URL becomes `/d/<repo>-<folder>/`.
- **presenterm**: `my-talk/main.md` + `config.yaml`.
- **PowerPoint / PDF**: just commit the file; the URL becomes `/d/<repo>-<file-name>/`.

New decks appear in the library within a couple of minutes (webhook) as **Private**; open *Manage* to change visibility or share.

Optional per-deck `.podium.yml` (trusted repositories only), next to the deck entry:

```yaml
title: How I ended up with 75+ AI agents   # overrides the headmatter title
alias: agents                               # /d/agents/ redirects to the deck
tags: [ai, agents, conference]
exportPdf: true
exportPptx: false
stripNotes: true                            # notes-free copy for viewers
visibility: private                         # seeds a *new* deck only; later changes in the UI win
npmScripts: false                           # allow npm lifecycle scripts during install
```

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

The web app scales to zero after 30 idle minutes (`scaleToZeroAfterSeconds` in `infra/app.bicep`; the platform default is 5).
A cold start takes roughly 20-25 s, almost all of it Azure scheduling the replica; set `minReplicas: 1` if you would rather
pay a few dollars a month for an always-warm instance.

Set repository variables `AZURE_CLIENT_ID` (deploy identity), `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`,
`AZURE_RESOURCE_GROUP` and the GitHub Actions workflow builds both images to GHCR and rolls them out with OIDC.
No deployment secrets exist anywhere.

Finally install the GitHub App on the repositories holding your slides (Sources page has the link).

## Tests and CI

`dotnet test tests/Podium.Tests` runs the Core unit tests and the web integration tests. The latter boot the real
pipeline (authentication, rate limiting, external-origin isolation, serving, owner API) on in-memory stores with a fake
artifact store, so they cover the security boundaries without Azure, GitHub or a builder. Pull requests run `ci.yml`
(build + tests, builder lint, both Dockerfiles assembled without pushing); pushes to `main` run `deploy.yml`.
Dependabot keeps NuGet, npm (Slidev grouped separately), Docker base images and GitHub Actions current.

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
| `Podium:SigningKey` | base64 key for HMAC tokens (builder callbacks, view tokens, viewer file links) |
| `Podium:ExternalBaseUrl` | second origin of the same app used to serve decks from repositories you do not control |
| `GitHub:AppId`, `GitHub:AppSlug`, `GitHub:PrivateKeyPem`, `GitHub:ClientId`, `GitHub:ClientSecret`, `GitHub:WebhookSecret` | GitHub App |
| `GitHub:Token` | development only: PAT used instead of installation tokens |
| `Storage:AccountName` / `Storage:ConnectionString` | managed identity (prod) / Azurite or `memory` (dev) |
| `Builder:Mode` | `ContainerAppsJob` or `LocalProcess`; `Builder:JobResourceId`, `Builder:LocalScriptPath` |

## Remote limitations

- Code that needs a server at runtime (custom runners, server-side execution) is not available; Monaco runs in the browser and works.
- presenterm PDF exports are rendered with weasyprint (shipped in the builder image, so the output is text-based rather than terminal screenshots); if the export fails, a PDF committed next to the deck is served instead.
- Speaker notes: by default viewers receive a second build made with `--without-notes`; only you and grantees with the *Present* right get the full bundle. Per-deck toggle under *Manage → Exports*.
- Cross-device sync relays only Slidev shared state (slide, clicks, drawings, presenter cursor); it does not stream video.
