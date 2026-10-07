#!/usr/bin/env bash
# Rebuild the sandbox after a reset: /tmp, /opt and out/ do not survive between agent turns,
# so the SDK, the cross toolchains and the build all have to be re-created. Safe to re-run:
# every step is skipped when it is already in place.
set -e
cd /home/user/recon

if [ ! -x /opt/dotnet/dotnet ]; then
  echo "== installing .NET SDK 10.0.401 into /opt/dotnet"
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  sudo bash /tmp/dotnet-install.sh --version 10.0.401 --install-dir /opt/dotnet --no-path >/dev/null
fi

# The SDK directory has to be writable by the user, because that is where the package cache wants to
# live (`tools/env.sh` puts NUGET_PACKAGES beside the SDK). Installed under sudo it belongs to root,
# the cache falls back into `$HOME/.cache/nuget`, and ~1,000 package files land *inside the
# snapshotted workspace* — which is what put it over its file budget once already. They are excluded
# from the snapshot, so nothing was lost, but nothing was gained either: they are a download cache
# that gets rebuilt on every restore.
if [ -d /opt/dotnet ] && [ ! -w /opt/dotnet ]; then
  sudo chown -R "$(id -u):$(id -g)" /opt/dotnet
fi

if ! command -v clang-19 >/dev/null 2>&1 || ! command -v x86_64-w64-mingw32-gcc >/dev/null 2>&1; then
  echo "== installing clang/lld/llvm/mingw (apt)"
  # The update is not optional: a restored container's package lists are older than the archive
  # index, and `apt-get install` then fails to fetch with no useful message. Without this line the
  # install quietly did nothing and four tests that need mingw stayed skipped.
  sudo apt-get update -qq >/dev/null 2>&1 || true
  sudo apt-get install -y clang-19 lld-19 llvm-19 mingw-w64 >/dev/null 2>&1 || \
    echo "   (apt install failed; corpus relink tests will skip)"
fi

. tools/env.sh
export PATH="$PATH:/opt/dotnet"
echo "== dotnet: $(dotnet --version)"

# Where the build output goes. `tools/env.sh` decides it and carries the reason: inside the workspace
# a full build — a test build above all — is over a thousand files and a few hundred megabytes of
# rebuildable output in a snapshot that has a budget, so in this shape it goes to a scratch directory
# beside the SDK and the package cache. Sourced above, so every command here agrees.
ARTIFACTS="${RECON_ARTIFACTS:-$PWD/out}"
RECON_DLL="$ARTIFACTS/bin/Recon.Cli/debug/recon.dll"

if [ ! -f "$RECON_DLL" ]; then
  echo "== building the CLI into $ARTIFACTS"
  dotnet build src/Recon.Cli/Recon.Cli.csproj -v q --nologo | tail -3
fi

# The wrapper the scratch scripts and the scratch measurements call, because `recon` is not on PATH.
mkdir -p /tmp
printf '#!/bin/sh\nexec %s/dotnet %s "$@"\n' "${DOTNET_ROOT:-/opt/dotnet}" "$RECON_DLL" > /tmp/recon
chmod +x /tmp/recon
echo "== ready: $RECON_DLL ($(ls -la "$RECON_DLL" | awk '{print $5" bytes"}')); /tmp/recon runs it"
