FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/ src/
RUN dotnet publish src/Services/Dcms.SiteBuilder/Dcms.SiteBuilder.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# Node + pnpm power the Mode A prerender and Mode B vite builds. The build
# step itself runs sandboxed (no network, resource limits) -- see Phase 8/10.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
    && curl -fsSL https://deb.nodesource.com/setup_24.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && npm install -g pnpm@11.6.0 \
    && rm -rf /var/lib/apt/lists/*

# ---------------------------------------------------------------------------
# Mode B offline dependency store.
#
# A Mode B build runs with no network, so every package it can resolve must
# already be in the image. The toolchain package.json is the fixed palette of
# dependencies a generated site may use; `pnpm fetch` populates the store from
# the resolved lockfile without materializing node_modules.
#
# ReactAppBuilder copies this package.json + lockfile into each build's work
# directory, so a site's file map can never introduce a dependency that is not
# in the store — an added import simply fails to resolve at build time.
#
# The lockfile is resolved here rather than committed because the toolchain is
# deliberately outside the pnpm workspace and never installed for development.
# package.json pins every version exactly, so a rebuild resolves the same tree.
# ---------------------------------------------------------------------------
ENV DCMS_TOOLCHAIN_DIR=/opt/dcms/toolchain \
    DCMS_PNPM_STORE_DIR=/opt/dcms/pnpm-store
# The store location is passed as an explicit --store-dir flag. `pnpm config set
# --global` refuses to run when pnpm's global bin directory is not on PATH (it
# never is in a container), and pnpm ignores the npm_config_store_dir
# environment variable — with neither, fetch silently populates a per-HOME
# default store that the non-root runtime user cannot read. ReactAppBuilder
# passes the same flag per build, so both sides agree on one mechanism.
COPY packages/site-builder-toolchain/package.json ${DCMS_TOOLCHAIN_DIR}/package.json
RUN cd ${DCMS_TOOLCHAIN_DIR} \
    && pnpm install --lockfile-only \
    && pnpm fetch --store-dir ${DCMS_PNPM_STORE_DIR} \
    && chmod -R a+rX /opt/dcms

WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Dcms.SiteBuilder.dll"]
