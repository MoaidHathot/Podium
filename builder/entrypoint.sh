#!/bin/sh
# Podium builder entrypoint (runs as root).
#
# Container Apps can only pass configuration through environment variables, and a process's original environment
# stays readable in /proc/<pid>/environ for its whole life even after unsetenv(). The orchestrator therefore:
#   1. copies every PODIUM_* variable into a root-only file,
#   2. re-executes itself with an empty environment (so /proc/1/environ holds nothing sensitive),
#   3. runs every deck-controlled step (git, npm, slidev, presenterm, soffice) as the unprivileged `pwuser`
#      via setpriv (see build.mjs), which cannot read root's files or memory.
set -eu

cfg_dir=/run/podium
cfg=$cfg_dir/config.json
mkdir -p "$cfg_dir"
chmod 700 "$cfg_dir"

# JSON-encode the PODIUM_* variables with node (no jq in the image); values may contain quotes/newlines.
node -e '
  const out = {};
  for (const [k, v] of Object.entries(process.env)) if (k.startsWith("PODIUM_")) out[k] = v;
  require("fs").writeFileSync(process.argv[1], JSON.stringify(out), { mode: 0o600 });
' "$cfg"

exec env -i \
  PATH="$PATH" \
  HOME=/root \
  LANG=C.UTF-8 \
  CI=1 \
  PLAYWRIGHT_BROWSERS_PATH="${PLAYWRIGHT_BROWSERS_PATH:-/ms-playwright}" \
  PODIUM_CONFIG_FILE="$cfg" \
  PODIUM_DECK_USER=pwuser \
  node /opt/podium/build.mjs
