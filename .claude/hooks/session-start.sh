#!/bin/bash
# Prepares a Claude Code cloud session to build and test DevicePulse: installs the .NET 10 SDK
# and a Node release the Angular 22 CLI accepts, then restores both dependency trees.
# Idempotent: every step is skipped when its result is already in place, so a cached container
# starts quickly.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

# Same major as CI (NODE_VERSION in .github/workflows/ci.yml). The image ships Node 22.22.0,
# which Angular 22 rejects (it needs >=22.22.3, 24.15 or 26).
NODE_MAJOR=24
NODE_ROOT=/opt/node

# .NET 10 SDK from Ubuntu's archive. Microsoft's own download host is not reachable from cloud
# sessions, so dotnet-install.sh cannot be used here.
if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks | grep -q '^10\.'; then
  apt-get update -qq
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq dotnet-sdk-10.0
fi

if [ ! -x "$NODE_ROOT/bin/node" ] || [ "$("$NODE_ROOT/bin/node" -p 'process.versions.node.split(".")[0]')" != "$NODE_MAJOR" ]; then
  version=$(curl -fsSL https://nodejs.org/dist/index.json \
    | python3 -c "import json,sys; print(next(r['version'] for r in json.load(sys.stdin) if r['version'].startswith('v$NODE_MAJOR.')))")
  tmp=$(mktemp -d)
  curl -fsSL "https://nodejs.org/dist/$version/node-$version-linux-x64.tar.xz" -o "$tmp/node.tar.xz"
  rm -rf "$NODE_ROOT"
  mkdir -p "$NODE_ROOT"
  tar -xJf "$tmp/node.tar.xz" -C "$NODE_ROOT" --strip-components=1
  rm -rf "$tmp"
fi

export PATH="$NODE_ROOT/bin:$PATH"
export DOTNET_NOLOGO=true DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true DOTNET_CLI_TELEMETRY_OPTOUT=true
if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  cat >> "$CLAUDE_ENV_FILE" <<ENV
export PATH="$NODE_ROOT/bin:\$PATH"
export DOTNET_NOLOGO=true
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true
export DOTNET_CLI_TELEMETRY_OPTOUT=true
ENV
fi

cd "$CLAUDE_PROJECT_DIR"
dotnet restore backend/DevicePulse.slnx
# npm ci, not npm install: npm install rewrites the committed lockfile on this npm release.
# Skipped when node_modules already matches the lockfile, so a cached container keeps its tree.
cd frontend/devicepulse-web
if ! cmp -s package-lock.json node_modules/.claude-installed-lock.json; then
  npm ci --no-audit --no-fund
  cp package-lock.json node_modules/.claude-installed-lock.json
fi
