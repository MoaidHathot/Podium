# Podium builder image: Node 22 + Chromium (Playwright) + presenterm + LibreOffice (pptx -> pdf) + poppler + git.
# Runs one build per container execution and exits. See build.mjs for the contract.
FROM mcr.microsoft.com/playwright:v1.63.0-noble

ARG PRESENTERM_VERSION=0.16.1

# Note: NODE_ENV is deliberately NOT set. A production value makes `npm ci` skip devDependencies and breaks
# Slidev's dev-server-based exporter (define constants are not injected).
ENV DEBIAN_FRONTEND=noninteractive \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    NPM_CONFIG_UPDATE_NOTIFIER=false \
    NPM_CONFIG_FUND=false \
    NPM_CONFIG_AUDIT=false \
    CI=1

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      git ca-certificates curl \
      poppler-utils \
      libreoffice-impress \
      fonts-liberation fonts-dejavu-core fonts-noto-core fonts-crosextra-carlito fonts-crosextra-caladea \
 && rm -rf /var/lib/apt/lists/* \
 && curl -fsSL "https://github.com/mfontanini/presenterm/releases/download/v${PRESENTERM_VERSION}/presenterm-${PRESENTERM_VERSION}-x86_64-unknown-linux-gnu.tar.gz" \
      | tar -xz -C /tmp \
 && install -m 0755 "$(find /tmp -type f -name presenterm | head -n1)" /usr/local/bin/presenterm \
 && rm -rf /tmp/presenterm* \
 && presenterm --version && soffice --version && pdftoppm -v 2>&1 | head -n1

# The builder's own tooling lives outside the workspace so deck code cannot tamper with it.
WORKDIR /opt/podium
COPY package.json package-lock.json* ./
RUN npm ci --ignore-scripts --omit=dev --no-audit --no-fund --loglevel error \
 && chown -R pwuser:pwuser /opt/podium /ms-playwright
COPY build.mjs ./
COPY addon ./addon

# Workspace for clone + output. Ephemeral per execution.
RUN mkdir -p /work && chown pwuser:pwuser /work
USER pwuser
ENV PODIUM_WORKDIR=/work \
    HOME=/home/pwuser \
    GIT_TERMINAL_PROMPT=0

ENTRYPOINT ["node", "/opt/podium/build.mjs"]
