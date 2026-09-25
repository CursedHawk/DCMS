FROM registry-gitlab.highgeek.eu/cursedhawk/baas-dcms/mirror/dotnet-sdk:10.0.401 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/ src/
RUN dotnet publish src/Services/Dcms.SiteBuilder/Dcms.SiteBuilder.csproj -c Release -o /app

# Pyroscope .NET profiler (TODO/PYROSCOPE-INTGERATION-PLAN.MD). The release ships the native
# library with debug info -- 181 MB; stripped it is 13 MB, and only the stripped copy reaches
# the runtime image. glibc x86_64, matching the aspnet base (Ubuntu noble) and vps1.
FROM registry-gitlab.highgeek.eu/cursedhawk/baas-dcms/mirror/dotnet-sdk:10.0.401 AS pyroscope
ARG PYROSCOPE_DOTNET_VERSION=1.5.1
ARG PYROSCOPE_DOTNET_SHA256=06bc3888e45d53cecefc3263db957833b1c0cb082f9129d1b1e5f63c84b88d18
RUN apt-get update && apt-get install -y --no-install-recommends binutils && apt-get clean \
    && curl -fsSL -o /tmp/pyroscope.tar.gz \
       "https://github.com/grafana/pyroscope-dotnet/releases/download/pyroscope-${PYROSCOPE_DOTNET_VERSION}/pyroscope.${PYROSCOPE_DOTNET_VERSION}-glibc-x86_64.tar.gz" \
    && echo "${PYROSCOPE_DOTNET_SHA256}  /tmp/pyroscope.tar.gz" | sha256sum -c - \
    && mkdir /pyroscope && tar -xzf /tmp/pyroscope.tar.gz -C /pyroscope \
    && strip --strip-unneeded /pyroscope/Pyroscope.Profiler.Native.so

FROM registry-gitlab.highgeek.eu/cursedhawk/baas-dcms/mirror/dotnet-aspnet:10.0.12 AS runtime
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

# Loaded into the CLR at startup; pushes to PYROSCOPE_SERVER_ADDRESS (x-otel-env in compose).
# Kill switch without a rebuild: CORECLR_ENABLE_PROFILING=0 in the service's environment.
COPY --from=pyroscope /pyroscope/Pyroscope.Profiler.Native.so /pyroscope/Pyroscope.Linux.ApiWrapper.x64.so /opt/pyroscope/
ENV CORECLR_ENABLE_PROFILING=1 \
    CORECLR_PROFILER={BD1A650D-AC5D-4896-B64F-D6FA25D6B26A} \
    CORECLR_PROFILER_PATH=/opt/pyroscope/Pyroscope.Profiler.Native.so \
    LD_PRELOAD=/opt/pyroscope/Pyroscope.Linux.ApiWrapper.x64.so \
    LD_LIBRARY_PATH=/opt/pyroscope \
    PYROSCOPE_PROFILING_ENABLED=1 \
    PYROSCOPE_APPLICATION_NAME=site-builder
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Dcms.SiteBuilder.dll"]
