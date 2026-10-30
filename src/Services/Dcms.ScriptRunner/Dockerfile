FROM registry-gitlab.highgeek.eu/cursedhawk/baas-dcms/mirror/dotnet-sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/ src/
RUN dotnet publish src/Services/Dcms.ScriptRunner/Dcms.ScriptRunner.csproj -c Release -o /app

# Deno runs the flow scripts (ADR 0025). Pinned by digest: a Deno upgrade is a security
# upgrade -- its permission checks are the sandbox -- so it happens on purpose, in a commit,
# with the escape probes in ScriptRunnerTests passing on the new version.
FROM denoland/deno:bin-2.5.4@sha256:d47e6b0d14e183ff4d2d7eae113df77855ff019cdfac3f2873af0f0be927aa68 AS deno

# esbuild bundles each function at deploy time (ADR 0029); it never runs tenant code. The
# platform binary from npm, pinned by version and checksum and kept in step with the repo's
# esbuild override (pnpm-workspace.yaml). Deno's own `deno bundle` fetches esbuild from npm at
# run time, which this host has no network for.
FROM registry-gitlab.highgeek.eu/cursedhawk/baas-dcms/mirror/dotnet-sdk:10.0 AS esbuild
ARG ESBUILD_VERSION=0.28.2
ARG ESBUILD_SHA256=9573bb2233aab0f9ea7647d5cca9726113cc1768de61d66b17267f4db84488f6
RUN curl -fsSL -o /tmp/esbuild.tgz "https://registry.npmjs.org/@esbuild/linux-x64/-/linux-x64-${ESBUILD_VERSION}.tgz" \
    && echo "${ESBUILD_SHA256}  /tmp/esbuild.tgz" | sha256sum -c - \
    && tar -xzf /tmp/esbuild.tgz -C /tmp && install -m 0755 /tmp/package/bin/esbuild /esbuild

# Pyroscope .NET profiler, as in the other services (TODO/PYROSCOPE-INTGERATION-PLAN.MD). Only
# the .NET host loads it; a script's Deno process starts with a cleared environment.
FROM registry-gitlab.highgeek.eu/cursedhawk/baas-dcms/mirror/dotnet-sdk:10.0 AS pyroscope
ARG PYROSCOPE_DOTNET_VERSION=1.5.1
ARG PYROSCOPE_DOTNET_SHA256=06bc3888e45d53cecefc3263db957833b1c0cb082f9129d1b1e5f63c84b88d18
RUN apt-get update && apt-get install -y --no-install-recommends binutils && apt-get clean \
    && curl -fsSL -o /tmp/pyroscope.tar.gz \
       "https://github.com/grafana/pyroscope-dotnet/releases/download/pyroscope-${PYROSCOPE_DOTNET_VERSION}/pyroscope.${PYROSCOPE_DOTNET_VERSION}-glibc-x86_64.tar.gz" \
    && echo "${PYROSCOPE_DOTNET_SHA256}  /tmp/pyroscope.tar.gz" | sha256sum -c - \
    && mkdir /pyroscope && tar -xzf /tmp/pyroscope.tar.gz -C /pyroscope \
    && strip --strip-unneeded /pyroscope/Pyroscope.Profiler.Native.so

FROM registry-gitlab.highgeek.eu/cursedhawk/baas-dcms/mirror/dotnet-aspnet:10.0 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && apt-get clean
COPY --from=deno /deno /usr/local/bin/deno
COPY --from=esbuild /esbuild /usr/local/bin/esbuild
COPY --from=pyroscope /pyroscope/Pyroscope.Profiler.Native.so /pyroscope/Pyroscope.Linux.ApiWrapper.x64.so /opt/pyroscope/
ENV CORECLR_ENABLE_PROFILING=1 \
    CORECLR_PROFILER={BD1A650D-AC5D-4896-B64F-D6FA25D6B26A} \
    CORECLR_PROFILER_PATH=/opt/pyroscope/Pyroscope.Profiler.Native.so \
    LD_PRELOAD=/opt/pyroscope/Pyroscope.Linux.ApiWrapper.x64.so \
    LD_LIBRARY_PATH=/opt/pyroscope \
    PYROSCOPE_PROFILING_ENABLED=1 \
    PYROSCOPE_APPLICATION_NAME=script-runner \
    ScriptRunner__DenoPath=/usr/local/bin/deno \
    ScriptRunner__EsbuildPath=/usr/local/bin/esbuild
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Dcms.ScriptRunner.dll"]
