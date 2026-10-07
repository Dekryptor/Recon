#!/usr/bin/env bash
# Source this to get the .NET SDK on PATH:  . tools/env.sh
#
# The SDK lives outside the workspace on purpose — it is a few hundred megabytes that no clone of
# this repository needs to carry — so where it is depends on the machine. The first place below
# that holds a usable `dotnet` wins, and an existing DOTNET_ROOT is always respected.
if [ -z "${DOTNET_ROOT:-}" ] || [ ! -x "${DOTNET_ROOT}/dotnet" ]; then
    for candidate in /opt/dotnet /usr/share/dotnet /usr/lib/dotnet \
                     "${HOME}/.dotnet" "${HOME}/.cache/dotnet" /var/tmp/dotnet /tmp/dotnet; do
        if [ -x "${candidate}/dotnet" ]; then
            export DOTNET_ROOT="${candidate}"
            break
        fi
    done
fi

if [ -n "${DOTNET_ROOT:-}" ] && [ -x "${DOTNET_ROOT}/dotnet" ]; then
    export PATH="${DOTNET_ROOT}:${PATH}"
fi

# Package cache: next to the SDK when that can be written, otherwise the user's own cache. A
# read-only NUGET_PACKAGES makes every build fail with "access to the path is denied".
#
# Both caches go beside the SDK rather than under $HOME, and that matters when the working copy *is*
# $HOME: `~/.nuget/packages` and `~/.local/share/NuGet/http-cache` are a few hundred megabytes of
# downloads that have nothing to do with this repository, and a workspace that is snapshotted or
# copied wholesale pays for them. The SDK is already outside the tree for the same reason.
if [ -z "${NUGET_PACKAGES:-}" ] || [ ! -w "${NUGET_PACKAGES}" ]; then
    for candidate in "${DOTNET_ROOT:-/nonexistent}/packages" "${HOME}/.cache/nuget" "${HOME}/.nuget/packages"; do
        if { [ -d "${candidate}" ] && [ -w "${candidate}" ]; } || { mkdir -p "${candidate}" 2>/dev/null && [ -w "${candidate}" ]; }; then
            export NUGET_PACKAGES="${candidate}"
            break
        fi
    done
fi

# The download cache is a separate setting from NUGET_PACKAGES, and it defaults to
# $HOME/.local/share/NuGet/http-cache whatever NUGET_PACKAGES says — which is how a build run from the
# workspace root leaves 90 MB of .nupkg bodies in it.
if [ -z "${NUGET_HTTP_CACHE_PATH:-}" ] && [ -n "${NUGET_PACKAGES:-}" ]; then
    export NUGET_HTTP_CACHE_PATH="${NUGET_PACKAGES%/packages}/http-cache"
fi

# Build output. `out/` inside the checkout is right for a checkout anywhere but here; when the
# working copy is the workspace itself (a checkout under $HOME, SDK outside it), a full build —
# above all a test build — leaves over a thousand files and a few hundred megabytes inside the tree
# that is snapshotted and has a budget, which is exactly how it went over that budget. So in that
# shape the output moves to a scratch directory beside the SDK and the package cache; a `dotnet`
# command that was not run through this script still builds into `out/`, and
# `Directory.Build.props` is where that default lives.
_recon_root="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/.." && pwd)"
case "${_recon_root}/" in
    "${HOME}/"*)
        if [ -z "${DOTNET_ROOT:-}" ] || [ "${DOTNET_ROOT#${HOME}/}" != "${DOTNET_ROOT}" ]; then
            : # SDK inside the tree as well: not the shape this is about, leave `out/` alone.
        else
            export RECON_ARTIFACTS="${RECON_ARTIFACTS:-${TMPDIR:-/tmp}/recon-out}"
            export RECON_REPO_ROOT="${RECON_REPO_ROOT:-${_recon_root}}"
        fi
        ;;
esac

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
