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
- **Cross-device presenter sync for every kind**: Slidev decks get a tiny addon at build time; presenterm exports and the PowerPoint/PDF viewer get a server-served adapter. Any instance opened by you (or a *Present* grantee) drives every other open instance through a WebSocket relay; viewers follow. Commands run exactly once, on the presenter view when one is open, so two windows never both execute "next". Clickers work as plain keyboard input.
- **Phone remote** at `/d/<slug>/remote` (scan the QR on the deck page or in the presenter view): current and next slide thumbnails, speaker notes (adjustable size), a go-to grid, an agenda strip with an on-pace indicator, prev/next (buttons or swipe), a **laser pointer** (drag on the slide thumbnail; shows on the projector and on every viewer's screen), blackout and audience message, the deck's timer, haptic time alerts, a lock screen for the lectern, how many people are watching. Works in portrait and landscape and from the installed app (**Remote** shortcut opens the live deck or the one you presented last).
- **Presenter HUD** inside the Slidev presenter view: live state, watching count, countdown, join code with QR, Go live / End and the remote QR, so the laptop and the phone always show the same session.
- **Live sessions**: *Go live* (from the deck page, the presenter view or the phone remote) freezes the served version, gives the room a **join code** (`slides.example/j/ABC-123`, QR, copy/share/email) that admits anyone without sign-in for as long as you are live, starts a planned-length **countdown**, and records pacing; *End session* writes a recap (duration, peak viewers, seconds per slide), tells every window, and disconnects the room (their screens say the session has ended). Sessions end by themselves when no presenter is connected for 15 minutes. Optionally a live session holds Podium's own deployments until it ends.
- **Audience affordances**: a "Live · following" pill with *Browse freely* / *Jump to live*; presenter-driven blackout (darkens the projector too, never the presenter view); viewers reconnect and re-sync automatically. The live UI is served by Podium, so it evolves without rebuilding decks.
- **Offline mode**: a per-deck service worker keeps the deck working when the venue network drops (presenters precache the whole build, viewers cache what they visit); the remote shell is kept too.
- **Installable**: Podium is a PWA. Install it on your phone (Chrome/Edge show *Install*; iPhone: Share &rarr; Add to Home Screen) and the library opens full-screen with **Remote** as the lead action on every deck and a **Remote** shortcut in the app menu.
- **Live updates without surprises**: a new build is announced over the socket; hidden tabs reload silently, visible viewers get a "Reload" notice, presenter views are never reloaded automatically.
- **Self-updating builds**: when the builder image changes, decks built by the previous builder are rebuilt automatically (failed ones are retried).
- **External decks**: public repositories you do not own can be added. They build in the same isolated job and are served from a *separate origin* (`Podium:ExternalBaseUrl`, by default the platform FQDN) with a short-lived view token instead of your session, so their code can never read your session or private decks.
- **PowerPoint decks** can be viewed as a PDF rendition (default, nothing leaves Podium) or through Microsoft's Office Online viewer (faithful rendering; Microsoft's service fetches the file via a 20-minute signed link). Per-deck setting under *Manage*.
- **Library**: group by repository/type/visibility/year, sort, grid or an aligned compact list, filters (including tags), collapsible Pinned and Recently-presented shelves (decks can be removed from recents until presented again); preferences are remembered per browser.
- **Short aliases**: `/d/<alias>/` redirects to the canonical deck URL (set under *Manage* or in `.podium.yml`); deep links and query strings are preserved.
- **Frozen decks**: pin the build you rehearsed with while pushes keep building in the background; roll forward whenever you choose.
- **View analytics** on each deck's page: views in the last 7/30 days, distinct viewers and the most recent viewers.
- **Link previews**: public decks carry Open Graph / Twitter card tags (title, description, first-slide thumbnail) so links unfurl in chat and social clients; tags the deck already declares are left alone.
- **Rename-safe sources**: repositories are tracked by GitHub's numeric id, so renaming or transferring one keeps its decks, URLs, grants and share links.
- **Clean-up**: decks that disappear from their repository are archived (artifacts purged after 30 days) and listed in an *Archived* shelf with a *Delete permanently* action for immediate removal of builds, grants and share links.
- **Share links** show how often they were opened; optionally capped (max opens) or protected with a passcode (entered once per browser). Admission is a signed cookie; revoking a link cuts off cookie holders too.
- **Access requests**: a signed-in visitor who cannot open a deck can ask for access; you approve (creates the grant) or decline from the deck page. The page looks the same for unknown slugs, so it never reveals which decks exist.
- **Public gallery**: with `Podium:PublicGallery`, visitors who are not the owner see a portfolio of your Public decks at `/` (thumbnails, tags, link previews).
- **Audience interaction** through the join code while you are live: reactions (float across the projector), questions with upvotes (pin one on screen, mark answered, dismiss), live polls with results on the projector; moderated from the phone remote, bounded per person and per room, switchable per deck (or `audience:` in `.podium.yml`) and mutable mid-talk; everything lands in the session recap (also as CSV).
- **Deck page in tabs**: Present, Share, Analytics, Build; the last one you used is remembered and deep links open the right tab.
- **Slide strip** on the deck page with copy-link-to-slide; **full-text search** across slide text from the library search box ("In slides" hits jump to the slide); **embedding** opt-in for Public decks.
- **Deck health**: the builder reports missing images, oversized assets and presenterm errors with file positions, shown on the deck page and as GitHub check-run annotations on the commit.
- **Bulk actions** (visibility, tags, pin, rebuild), a **New deck** wizard that opens GitHub's editor pre-filled with a starter deck, and `?` for keyboard shortcuts.
- **GitHub check runs** (optional): grant the app *Checks: read & write* (and subscribe to the `check_run` event) and every build reports back on the commit as `Podium / <deck>` with a link to the deck or the build log; GitHub's **Re-run** button rebuilds the deck.
- **Operations**: Application Insights telemetry, e-mail alerts (crash loops, build-failure streaks, 5xx), a daily backup of the index into a `backups` container, an audit trail of every change (`/activity`), a list of signed-in devices with per-device sign-out, and *Sign out everywhere*.
- **Security**: single owner pinned by GitHub user id; untrusted deck code only runs inside a throwaway container with a write-only SAS scoped to its own blob container, and is served from a separate origin; installation tokens never touch disk; CSRF header + SameSite cookies; per-IP rate limits on login, webhook and deck entry; nonce-based Content-Security-Policy on every Podium page (deck pages are the author's HTML and are served from the external origin when untrusted); secrets in Key Vault, data-protection keys wrapped by a Key Vault key.

## Deck detection

| Kind | Detected by | Build |
|---|---|---|
| Slidev | `slides.md` in a directory | `npm ci` (if `package.json`) + `slidev build --base /d/<slug>/` + `slidev export` |
| presenterm | `config.yaml` + a `.md` (prefers `main.md`) | `presenterm --export-html` + `--export-pdf` (weasyprint); a committed PDF is the fallback |
| PowerPoint | any `.pptx` file (one deck per file) | converted to PDF with LibreOffice, then rendered page by page for Podium's viewer; original offered for download. A committed `.pdf` with the same name is used instead of converting |
| PDF | any standalone `.pdf` | rendered page by page for Podium's viewer (keys, swipe, deep links, remote, follow-along); the file itself stays downloadable |
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
A cold start takes roughly 20-25 s, almost all of it Azure scheduling the replica; run `./infra/deploy.ps1 -Phase app -MinReplicas 1`
if you would rather pay a few dollars a month for an always-warm instance.

Secret rotation, backups/export and troubleshooting queries are in [docs/operations.md](docs/operations.md).

Set repository variables `AZURE_CLIENT_ID` (deploy identity), `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`,
`AZURE_RESOURCE_GROUP` and the GitHub Actions workflow builds both images to GHCR and rolls them out with OIDC.
No deployment secrets exist anywhere.

Finally install the GitHub App on the repositories holding your slides (Sources page has the link).

## Tests and CI

`dotnet test tests/Podium.Tests` runs the Core unit tests and the web integration tests. The latter boot the real
pipeline (authentication, rate limiting, external-origin isolation, serving, owner API) on in-memory stores with a fake
artifact store, so they cover the security boundaries without Azure, GitHub or a builder. Pull requests run `ci.yml`
(build + tests, builder lint and unit tests, a real-browser smoke test against the pipeline with a seeded fixture deck,
both Dockerfiles assembled without pushing); pushes to `main` run `deploy.yml`, which waits (bounded) while a live
session that opted in is running.
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
| `Builder:MaxConcurrentBuilds` | builds running at once (default 3); the rest wait in the queue and start as slots free up |
| `Builder:TrustedMaxOutputMegabytes` / `Builder:UntrustedMaxOutputMegabytes` | upload budget per build (defaults 1024 / 256 MB); oversized builds fail and the served build stays |
| `Podium:PublicGallery`, `Podium:GalleryTitle` | portfolio of Public decks for visitors at `/` (off by default; on in the provided Bicep) |
| `Sessions:IdleTimeout` / `OvertimeGrace` / `HardCap` | when live sessions end on their own (15 min / 30 min / 4 h) |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | telemetry via OpenTelemetry (set by Bicep; empty disables) |

## Remote limitations

- Code that needs a server at runtime (custom runners, server-side execution) is not available; Monaco runs in the browser and works.
- presenterm PDF exports are rendered with weasyprint (shipped in the builder image, so the output is text-based rather than terminal screenshots); if the export fails, a PDF committed next to the deck is served instead.
- Speaker notes: by default viewers receive a second build made with `--without-notes`; only you and grantees with the *Present* right get the full bundle. Per-deck toggle under *Manage → Exports*.
- Cross-device sync relays Slidev shared state (slide, clicks, drawings, presenter cursor, timer) and, for presenterm / PowerPoint / PDF decks, the slide number, laser pointer and timer; it does not stream video. Static HTML decks are served but cannot be driven.
- The presenter view's own mouse takes the laser back from the phone as soon as it moves over the slide (Slidev shares one cursor).
- PowerPoint and PDF decks are shown as rendered page images (1920 px wide) in Podium's viewer, which adds a few megabytes per deck; the original PDF stays available for download and the Office Online viewer remains an option for PowerPoint.
