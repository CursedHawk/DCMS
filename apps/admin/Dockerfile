FROM node:24-alpine AS build
RUN npm install -g pnpm
WORKDIR /repo
# No VITE_OIDC_AUTHORITY build arg any more, deliberately.
#
# Vite substitutes import.meta.env at BUILD time, so baking the authority in produced an
# environment-SPECIFIC image: the artifact built for dev could not serve production, which
# makes "promote the tested digest" impossible for the one thing users actually load. The
# per-environment values are now substituted into index.html at container start by
# docker-entrypoint.sh. See apps/admin/src/runtime-config.ts.
COPY package.json pnpm-workspace.yaml pnpm-lock.yaml ./
COPY apps/ apps/
COPY packages/ packages/
# Plugins' admin UIs are workspace packages beside their C# (docs/adr/0019); the lockfile names
# them, so the install needs them even where the app does not compile them in.
COPY src/Plugins/ src/Plugins/
COPY samples/ samples/
# The console compiles every built-in plugin's admin UI in; Node's default heap is not always enough.
ENV NODE_OPTIONS=--max-old-space-size=4096
RUN pnpm install --frozen-lockfile && pnpm --filter @dcms/admin build

# The dcms command line (ADR 0032), served by the console that it talks to: one self-contained
# file per platform, their SHA256SUMS and the version, which `dcms version` compares with its own.
# Installed with  curl -fsSL https://<console>/downloads/install.sh | sh  (or install.ps1).
FROM registry-gitlab.highgeek.eu/cursedhawk/baas-dcms/mirror/dotnet-sdk:10.0 AS cli
ARG DCMS_VERSION=dev
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/Tools/Dcms.Cli/ src/Tools/Dcms.Cli/
RUN set -e; mkdir -p /out/downloads; \
    for rid in linux-x64 linux-arm64 osx-x64 osx-arm64 win-x64; do \
      dotnet publish src/Tools/Dcms.Cli/Dcms.Cli.csproj -c Release -r "$rid" -p:DCMS_VERSION="$DCMS_VERSION" -o "/tmp/$rid"; \
      ext=""; [ "$rid" = win-x64 ] && ext=".exe"; \
      cp "/tmp/$rid/dcms$ext" "/out/downloads/dcms-$rid$ext"; \
    done; \
    cd /out/downloads && sha256sum dcms-* > SHA256SUMS && printf '%s\n' "$DCMS_VERSION" > version; \
    test "$(wc -l < SHA256SUMS)" -eq 5

# The IDE's C# language service (ADR 0030): Roslyn on .NET's WebAssembly runtime, served as
# static files under /csharp/ and loaded by the console's csharp.worker.ts on the first .cs file.
FROM registry-gitlab.highgeek.eu/cursedhawk/baas-dcms/mirror/dotnet-sdk:10.0 AS csharp
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/ src/
RUN dotnet publish src/Functions/Dcms.Functions.LanguageWorker/Dcms.Functions.LanguageWorker.csproj -c Release -o /out

FROM nginx:alpine AS runtime
COPY apps/admin/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /repo/apps/admin/dist /usr/share/nginx/html
COPY --from=csharp /out/wwwroot /usr/share/nginx/html/csharp
COPY --from=cli /out/downloads /usr/share/nginx/html/downloads
COPY apps/admin/downloads/ /usr/share/nginx/html/downloads/
COPY apps/admin/docker-entrypoint.sh /docker-entrypoint.d/00-dcms-runtime-config.sh
RUN chmod +x /docker-entrypoint.d/00-dcms-runtime-config.sh
EXPOSE 80
