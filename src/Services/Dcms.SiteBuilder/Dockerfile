FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/ src/
RUN dotnet publish src/Services/Dcms.SiteBuilder/Dcms.SiteBuilder.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# Mode A prerender is pure C# (no Node). Mode B (untrusted React) builds no longer
# run in this container at all — they run in an ephemeral, locked-down sandbox
# container (see sandbox.Dockerfile) that this service launches over a restricted
# Docker API (docker-socket-proxy). So the builder image only needs the `docker`
# CLI to start those sandboxes — never a package manager or the untrusted code.
ARG DOCKER_CLI_VERSION=27.5.1
# curl is kept: the compose healthcheck (x-service-defaults) probes /health/live with it.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
    && arch="$(dpkg --print-architecture)" \
    && case "$arch" in \
         amd64) dl=x86_64 ;; \
         arm64) dl=aarch64 ;; \
         *) echo "unsupported arch: $arch" >&2; exit 1 ;; \
       esac \
    && curl -fsSL "https://download.docker.com/linux/static/stable/${dl}/docker-${DOCKER_CLI_VERSION}.tgz" -o /tmp/docker.tgz \
    && tar -xzf /tmp/docker.tgz -C /tmp \
    && mv /tmp/docker/docker /usr/local/bin/docker \
    && rm -rf /tmp/docker.tgz /tmp/docker \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Dcms.SiteBuilder.dll"]
