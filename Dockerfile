# ?????????????????????????????????????????????????????????????????????????????
# AgentStationHub — portable Docker image
#
# The app is a Blazor Server that *spawns other Docker containers* for its
# deployment sandboxes. This image therefore follows the Docker-out-of-Docker
# (DooD) pattern: we install the `docker` CLI inside the image and expect
# /var/run/docker.sock to be bind-mounted at runtime. Child sandbox containers
# are siblings of this one (not nested), which keeps auth + networking simple
# and avoids the performance / security issues of true Docker-in-Docker.
#
# Build:
#   docker build -t agentichub/app:latest .
#
# Run (see docker-compose.yml for the full variant):
#   docker run --rm -p 8080:8080 \
#     -v /var/run/docker.sock:/var/run/docker.sock \
#     -v "$HOME/.azure:/root/.azure:ro" \
#     -e AZURE_OPENAI_ENDPOINT=https://<your>.openai.azure.com/ \
#     -e AZURE_OPENAI_DEPLOYMENT=gpt-5.4 \
#     -e AZURE_OPENAI_RUNNER_DEPLOYMENT=gpt-5.3-chat \
#     agentichub/app:latest
# ?????????????????????????????????????????????????????????????????????????????

# ---- Stage 1: build both projects with the SDK ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG TARGETARCH
WORKDIR /src

# Map docker's TARGETARCH (amd64|arm64) to a .NET runtime identifier so the
# image builds natively on either architecture.
RUN case "$TARGETARCH" in \
      amd64) echo "linux-x64"   > /tmp/rid ;; \
      arm64) echo "linux-arm64" > /tmp/rid ;; \
      *)     echo "linux-x64"   > /tmp/rid ;; \
    esac

# Copy the two csproj files first so 'dotnet restore' caches independently of
# source changes. Solution file sits inside the main project folder.
COPY AgentStationHub/AgentStationHub.csproj                    AgentStationHub/
COPY AgentStationHub.SandboxRunner/AgentStationHub.SandboxRunner.csproj \
     AgentStationHub.SandboxRunner/
RUN dotnet restore AgentStationHub/AgentStationHub.csproj \
  && dotnet restore AgentStationHub.SandboxRunner/AgentStationHub.SandboxRunner.csproj

# Now copy the rest of the sources.
COPY AgentStationHub/                    AgentStationHub/
COPY AgentStationHub.SandboxRunner/      AgentStationHub.SandboxRunner/
# README.md is referenced by AgentStationHub.csproj as <Content> so it
# is embedded next to the executable and rendered at runtime by the
# /about Razor page.
COPY README.md                           README.md

# Publish the main Blazor app (framework-dependent — aspnet runtime image
# provides the shared framework).
RUN dotnet publish AgentStationHub/AgentStationHub.csproj \
      -c Release --no-restore -o /out/app

# Pre-publish the sandbox runner so the runtime image does NOT need the
# SDK. SandboxRunnerHost honours AGENTICHUB_RUNNER_PATH and skips its own
# 'dotnet publish' when set.
RUN RID="$(cat /tmp/rid)" \
 && dotnet publish AgentStationHub.SandboxRunner/AgentStationHub.SandboxRunner.csproj \
      -c Release -r "$RID" --no-self-contained \
      -o /out/runner

# ---- Stage 2: thin runtime with docker + az CLIs ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# docker CLI (client only, no daemon) + az CLI. The az login is reused at
# runtime via the user's ~/.azure bind-mount; az is primarily used by the
# in-sandbox auth logic but having it in this image simplifies debugging.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        ca-certificates curl gnupg lsb-release jq \
 && install -m 0755 -d /etc/apt/keyrings \
 && curl -fsSL https://download.docker.com/linux/debian/gpg \
      | gpg --dearmor -o /etc/apt/keyrings/docker.gpg \
 && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] \
          https://download.docker.com/linux/debian $(lsb_release -cs) stable" \
      > /etc/apt/sources.list.d/docker.list \
 && curl -sL https://aka.ms/InstallAzureCLIDeb | bash \
 && apt-get update \
 && apt-get install -y --no-install-recommends docker-ce-cli \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /out/app    /app
# The runner gets seeded into a VOLUME at startup, not used directly from
# the image. See entrypoint.sh for the rationale (DooD volume translation
# requires host?container path matching).
COPY --from=build /out/runner /runner-src

# Sidecar Dockerfiles (Copilot CLI etc.) shipped alongside the app so the
# CopilotCliService can build them at startup via the host docker daemon.
COPY Dockerfiles/ /app/Dockerfiles/

# Tell the Blazor app where the seeded runner will live. /var/agentichub-tools
# is bind-mounted from the host at the identical path via docker-compose, so
# when the app passes this path to 'docker run -v ...' the host daemon
# resolves it correctly for sandbox siblings.
ENV AGENTICHUB_RUNNER_PATH=/var/agentichub-tools
ENV Deployment__WorkRootDir=/var/agentichub-work
# Do NOT set ASPNETCORE_URLS here: the aspnet:8.0 base image already
# defines ASPNETCORE_HTTP_PORTS=8080, and when both are present Kestrel
# logs a "Overriding HTTP_PORTS ... Binding to values defined by URLS"
# warning at every startup. Deferring to the base image's HTTP_PORTS
# keeps the log clean AND lets the operator override the port with a
# single 'docker run -e ASPNETCORE_HTTP_PORTS=XYZ' without having to
# unset our variable first.
ENV DOTNET_RUNNING_IN_CONTAINER=true
# The app reads ~/.azure/azureProfile.json via Environment.SpecialFolder.
# .UserProfile which resolves to /root inside this image.
ENV HOME=/root

# Entrypoint seeds /var/agentichub-tools on first boot, then launches dotnet.
# The 'sed' strips any stray CR bytes so the shebang works regardless of
# how the file was committed (a Windows editor with default CRLF would
# otherwise break exec with "no such file or directory" — referring to
# the interpreter, not the script).
COPY entrypoint.sh /entrypoint.sh
RUN sed -i 's/\r$//' /entrypoint.sh \
 && chmod +x /entrypoint.sh

EXPOSE 8080
ENTRYPOINT ["/entrypoint.sh"]
