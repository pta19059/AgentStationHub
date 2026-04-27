# =============================================================================
# Long-lived sidecar container that ships the GitHub Copilot CLI behind
# `ttyd`, an HTTP/WebSocket terminal server. The Blazor UI just embeds an
# <iframe> pointing at this container's port 7681 -- no custom xterm glue,
# no hand-rolled ANSI parser, no SignalR PTY pumping.
#
# Why ttyd: GitHub Copilot CLI is built on Ink (React for terminals), which
# uses DEC alt-screen + heavy CSI traffic that proved fragile inside our
# bespoke xterm.js wrapper. ttyd ships a battle-tested xterm.js bundle
# patched to handle exactly this kind of TUI (the same upstream client the
# project tests against btop, vim, htop, etc.) and runs Copilot under a
# real forkpty(), so PTY sizing, REP, mouse and bracketed-paste all work
# without us having to reinvent any of it.
#
# Auth tokens, history and shell state live under /root, persisted by a
# named volume ('agentichub-copilot-home') so 'gh auth login' / Copilot's
# device-code flow only happens the first time.
# =============================================================================
FROM node:20-bookworm-slim

ENV DEBIAN_FRONTEND=noninteractive \
    HOME=/root \
    TERM=xterm-256color

# - git + ca-certificates + curl: standard Copilot CLI prerequisites.
# - gh: companion CLI; also the recommended way to do the initial
#   `gh auth login` which Copilot CLI then reuses transparently.
# - locales: avoid the "Setting locale failed" warnings inside the PTY.
# - tini: PID 1 reaper so ttyd's child processes (one per browser tab) get
#   their SIGCHLDs handled cleanly when the operator closes a panel.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        ca-certificates curl gnupg git locales less tini \
 && sed -i 's/^# *\(en_US.UTF-8\)/\1/' /etc/locale.gen \
 && locale-gen \
 && mkdir -p -m 755 /etc/apt/keyrings \
 && curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg \
        -o /etc/apt/keyrings/githubcli-archive-keyring.gpg \
 && chmod go+r /etc/apt/keyrings/githubcli-archive-keyring.gpg \
 && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" \
        > /etc/apt/sources.list.d/github-cli.list \
 && apt-get update \
 && apt-get install -y --no-install-recommends gh \
 && rm -rf /var/lib/apt/lists/*

# ttyd v1.7.7 static binary. Single ~2 MB executable; we pick the right
# arch off Debian's `dpkg --print-architecture` and download from the
# official GitHub release. If supply-chain hardening becomes a concern,
# mirror the binary into our own storage and pin a SHA here.
RUN ARCH="$(dpkg --print-architecture)" \
 && case "$ARCH" in \
        amd64) TTYD_ARCH=x86_64 ;; \
        arm64) TTYD_ARCH=aarch64 ;; \
        *)     echo "Unsupported arch $ARCH" >&2; exit 1 ;; \
    esac \
 && curl -fsSL "https://github.com/tsl0922/ttyd/releases/download/1.7.7/ttyd.${TTYD_ARCH}" \
        -o /usr/local/bin/ttyd \
 && chmod +x /usr/local/bin/ttyd \
 && ttyd --version

ENV LANG=en_US.UTF-8 \
    LC_ALL=en_US.UTF-8

# GitHub Copilot CLI. Loose pin on purpose: the CLI auto-updates and we
# rebuild the image rarely.
RUN npm install -g @github/copilot

WORKDIR /root

# Expose ttyd inside the container; CopilotCliService publishes this on
# 127.0.0.1 of the host so the Blazor app's iframe can reach it without
# leaking to the network.
EXPOSE 7681

# ttyd flags:
#   -W                 writable terminal (forward keystrokes into the PTY).
#   -p 7681            listen port.
#   -b /copilot        base path. The Hub reverse-proxies /copilot/ on
#                      port 8080 to this service, and ttyd needs to know
#                      its own base path so the static assets and the
#                      websocket URL it emits are prefixed correctly.
#                      Without this, ttyd's bundle would request
#                      /auth_token.js, /ws, etc. -- which on the Hub's
#                      port 8080 would 404 against Razor routes.
#   -t titleFixed=...  static window title in the embedded UI.
#   -t fontSize=13     readable monospace size matching the panel chrome.
#   -t theme=...       dark theme matching the AgentStationHub palette.
#   -t disableResizeOverlay=true
#                      hide the "<cols>x<rows>" pill xterm.js flashes on
#                      every resize -- distracting and the operator never
#                      needs to see the grid size.
# --check-origin is intentionally OMITTED: the Hub's reverse proxy
# rewrites Origin/Host so the browser's outer origin (e.g.
# http://20.12.9.164:8080) never matches ttyd's inner Host
# (agentichub-copilot-cli:7681). The reverse proxy itself is the trust
# boundary -- ttyd is only reachable through it on the docker network.
# No `script` PTY trick: ttyd does forkpty() itself.
ENTRYPOINT ["/usr/bin/tini", "--"]
CMD ["ttyd", "-W", "-p", "7681", "-b", "/copilot", \
     "-t", "titleFixed=GitHub Copilot CLI", \
     "-t", "fontSize=13", \
     "-t", "disableResizeOverlay=true", \
     "-t", "theme={\"background\":\"#0b1220\",\"foreground\":\"#e5e7eb\",\"cursor\":\"#7FBA00\"}", \
     "copilot"]
