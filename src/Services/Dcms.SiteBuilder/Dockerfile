FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/ src/
RUN dotnet publish src/Services/Dcms.SiteBuilder/Dcms.SiteBuilder.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# Node powers the Mode A prerender and Mode B builds. A Mode B site is a complete
# front-end project that brings its own package.json + lockfile, so the build runs
# the site's own package manager (npm / pnpm / yarn) with network access to fetch
# its dependencies. The build runs non-root, in a per-build ephemeral work dir,
# with hard time/resource limits (see ReactAppBuilder + compose). npm ships with
# Node; corepack provides pnpm/yarn without a global install.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates git \
    && curl -fsSL https://deb.nodesource.com/setup_24.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && corepack enable \
    && corepack prepare pnpm@11.6.0 --activate \
    && corepack prepare yarn@1.22.22 --activate \
    && rm -rf /var/lib/apt/lists/*

# Shared, warm package cache to speed repeat installs. Only holds downloaded
# tarballs (never a site's node_modules), and is mounted writable in compose.
ENV DCMS_BUILD_CACHE_DIR=/var/cache/dcms-build
RUN mkdir -p ${DCMS_BUILD_CACHE_DIR} && chmod -R a+rwX ${DCMS_BUILD_CACHE_DIR}

WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Dcms.SiteBuilder.dll"]
