FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/ src/
# Dcms.AdminApi embeds the @dcms/api-client runtime source and the
# site-template-react scaffold as resources (see ApiClientGen), so both must be
# in the build context even though nothing here compiles TypeScript.
COPY packages/api-client/src/ packages/api-client/src/
COPY packages/site-template-react/ packages/site-template-react/
RUN dotnet publish src/Services/Dcms.AdminApi/Dcms.AdminApi.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# curl backs the compose healthcheck; git drives real two-parent merge commits
# on the conflict-resolve path (the Forgejo REST API only makes single-parent commits).
RUN apt-get update && apt-get install -y --no-install-recommends curl git && apt-get clean
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Dcms.AdminApi.dll"]
