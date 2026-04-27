#!/bin/sh
# ----------------------------------------------------------------------------
# AgentStationHub container entrypoint
#
# Problem it solves: when the main app (running INSIDE this container)
# spawns deployment sandboxes as *siblings* via the Docker socket, it
# passes `-v <path>:/workspace` to docker. The host daemon interprets
# <path> as a HOST filesystem path, not a container one — so any path
# that exists only inside this container image fails the bind mount.
#
# Solution: share two directories between HOST and this container at
# THE SAME absolute path via docker-compose bind mounts:
#   /var/agentichub-work   : scratch area for cloned repos (grows+shrinks)
#   /var/agentichub-tools  : pre-published SandboxRunner binaries
#
# /var/agentichub-tools starts empty (the bind mount hides whatever was
# in the image layer), so we seed it from /runner-src on first boot.
# The copy is cheap (<40 MB), idempotent (skipped if already populated),
# and unlocks sibling-container access to the runner dll.
# ----------------------------------------------------------------------------
set -eu

TOOLS_DIR="${AGENTICHUB_RUNNER_PATH:-/var/agentichub-tools}"
WORK_DIR="${Deployment__WorkRootDir:-/var/agentichub-work}"

mkdir -p "$TOOLS_DIR" "$WORK_DIR"

# Seed (or re-seed) the tools volume so the sandbox always runs the
# SandboxRunner binaries that match the current app container image.
#
# Previous behaviour: if the dll was already present (first boot populated
# /var/agentichub-tools and the directory is bind-mounted from the host),
# we skipped the copy — so when the app image was rebuilt with a new
# runner, the SANDBOX kept running the stale dll from a past image. That
# caused bugs fixed in the source to appear unchanged in production for
# the user. Classic "but I updated the code!" confusion.
#
# Current behaviour: compare a checksum marker between /runner-src
# (baked into the image at build time) and /var/agentichub-tools
# (bind-mount, survives container restarts). If they differ, wipe and
# re-seed. Cost is ~40 MB copy, well under a second. The named volume
# for agent memory (agentichub-state) is elsewhere, so nothing important
# lives under $TOOLS_DIR that we might lose.
RUNNER_DLL="AgentStationHub.SandboxRunner.dll"
if [ -f "$TOOLS_DIR/$RUNNER_DLL" ] && [ -f "/runner-src/$RUNNER_DLL" ]; then
    src_sum=$(md5sum "/runner-src/$RUNNER_DLL" | awk '{print $1}')
    dst_sum=$(md5sum "$TOOLS_DIR/$RUNNER_DLL" | awk '{print $1}')
    if [ "$src_sum" != "$dst_sum" ]; then
        echo "[entrypoint] $TOOLS_DIR runner ($dst_sum) differs from image ($src_sum), re-seeding..."
        rm -rf "${TOOLS_DIR:?}"/*
        cp -r /runner-src/. "$TOOLS_DIR/"
        echo "[entrypoint] re-seed done."
    else
        echo "[entrypoint] $TOOLS_DIR already matches current image runner, skipping seed."
    fi
else
    echo "[entrypoint] seeding $TOOLS_DIR from /runner-src ..."
    cp -r /runner-src/. "$TOOLS_DIR/"
    echo "[entrypoint] done."
fi

# Permissions: the sandbox containers run as root by default, so 0755 is
# enough. Harden by narrowing if your security posture requires it.
chmod -R 0755 "$TOOLS_DIR"

# Seed /root/.azure from the read-only host stash. Azure.Identity's
# AzureCliCredential and the 'az' CLI itself both need to write session
# data (az.sess, refreshed tokens, telemetry) to /root/.azure at runtime.
# If we bound-mount the host's ~/.azure directly at :ro, the first 'az'
# subprocess errors out with "[Errno 30] Read-only file system" and the
# DefaultAzureCredential chain cascades into a full failure — breaking
# every Azure OpenAI / Azure ARM call the app makes without an explicit
# API key.
#
# So the stash lives at /host-azure-readonly (read-only bind mount, host
# remains immutable) and we COPY its contents into /root/.azure (writable
# image path) on every boot. Size is typically <1 MB, copy is near-
# instant, and running on every boot keeps the container in sync with
# fresh 'az login' sessions on the host without requiring a rebuild.
STASH_DIR="/host-azure-readonly"
AZURE_DIR="/root/.azure"
if [ -d "$STASH_DIR" ]; then
    # Clear the stale content the image laid down during build so our
    # copy doesn't fight directory conflicts.
    rm -rf "$AZURE_DIR"
    mkdir -p "$AZURE_DIR"

    # Copy ONLY the auth-relevant files — NOT cliextensions/ because
    # host-installed extensions are architecture/OS specific (e.g. a
    # Windows host's cliextensions/containerapp contains pywin32 wheels
    # that crash 'az' on Linux with "Expected 1 module to load starting
    # with 'azext_': got []").  The shapes we need are the *.json config
    # files (azureProfile, clouds.config, commandIndex) plus the MSAL
    # token caches (msal_token_cache.json, msal_http_cache.bin) and the
    # 'az.sess' session. Everything else is best left to the fresh
    # Linux-native extensions that come with the 'az' binary in the image.
    for f in azureProfile.json clouds.config config commandIndex.json \
             az.json az.sess versionCheck.json \
             msal_token_cache.json msal_http_cache.bin; do
        if [ -f "$STASH_DIR/$f" ]; then
            cp "$STASH_DIR/$f" "$AZURE_DIR/$f" 2>/dev/null || true
        fi
    done
    chmod -R u+w "$AZURE_DIR" 2>/dev/null || true
    file_count=$(find "$AZURE_DIR" -maxdepth 1 -type f | wc -l)
    echo "[entrypoint] seeded $AZURE_DIR from $STASH_DIR ($file_count auth file(s)). Skipped cliextensions (host-OS-specific)."

    # Also synchronise the SANDBOX's shared Azure-profile volume
    # (mounted here at /sandbox-azure-profile, mounted by every deploy
    # sandbox at /root/.azure). We only overwrite when the sandbox
    # volume LACKS a usable MSAL token cache while the host HAS one —
    # this covers the common case where the host is freshly
    # 'az login'-ed but the sandbox volume holds a stale profile from
    # a previous session (symptom: 'az account show' returns the
    # subscription JSON but 'az account get-access-token' fails
    # because msal_token_cache.bin is missing / expired, which then
    # cascades into azd's "AzureCLICredential: signal: killed" or a
    # Doctor 'No Azure subscription found' dead-end).
    #
    # We DO NOT clobber a valid sandbox-side login (e.g. one obtained
    # via 'az login --use-device-code' at a prior deploy): if the
    # sandbox volume already has a msal_token_cache.bin AND its
    # azureProfile.json mentions a subscription, we consider it good
    # and leave it alone.
    SANDBOX_PROFILE="/sandbox-azure-profile"
    if [ -d "$SANDBOX_PROFILE" ]; then
        sandbox_has_token=0
        [ -s "$SANDBOX_PROFILE/msal_token_cache.bin" ] && sandbox_has_token=1
        [ -s "$SANDBOX_PROFILE/msal_token_cache.json" ] && sandbox_has_token=1
        host_has_token=0
        [ -s "$STASH_DIR/msal_token_cache.bin" ] && host_has_token=1
        [ -s "$STASH_DIR/msal_token_cache.json" ] && host_has_token=1

        if [ "$sandbox_has_token" = "0" ] && [ "$host_has_token" = "1" ]; then
            echo "[entrypoint] Sandbox Azure profile lacks a token cache; " \
                 "syncing from host stash so the next deploy doesn't fail auth."
            for f in azureProfile.json clouds.config config commandIndex.json \
                     az.json az.sess versionCheck.json \
                     msal_token_cache.json msal_token_cache.bin \
                     msal_http_cache.bin service_principal_entries.json; do
                if [ -f "$STASH_DIR/$f" ]; then
                    cp "$STASH_DIR/$f" "$SANDBOX_PROFILE/$f" 2>/dev/null || true
                fi
            done
            chmod -R u+w "$SANDBOX_PROFILE" 2>/dev/null || true
            synced=$(find "$SANDBOX_PROFILE" -maxdepth 1 -type f | wc -l)
            echo "[entrypoint] sandbox profile synced ($synced file(s))."
        elif [ "$sandbox_has_token" = "1" ]; then
            echo "[entrypoint] Sandbox Azure profile already has a token cache; not overwriting."
        else
            echo "[entrypoint] Neither sandbox volume nor host stash has a token cache; " \
                 "the first deploy will trigger 'az login --use-device-code' inside the sandbox."
        fi
    fi
else
    echo "[entrypoint] WARN: $STASH_DIR not present. 'az' inside the container" \
         "will have no cached login; set AzureOpenAI__ApiKey in .env or rely on" \
         "a managed identity / device-code flow."
fi

exec dotnet AgentStationHub.dll "$@"
