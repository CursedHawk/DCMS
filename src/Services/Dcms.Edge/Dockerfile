FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/ src/
RUN dotnet publish src/Services/Dcms.Edge/Dcms.Edge.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# curl backs the compose healthcheck
RUN apt-get update && apt-get install -y --no-install-recommends curl && apt-get clean
WORKDIR /app
COPY --from=build /app .
# Only used while TLS is off. Once Edge:Certificates:TlsEnabled is set, EdgeTlsConfigurator
# calls Listen explicitly and Kestrel ignores this -- which is why it binds BOTH ports there
# rather than only :8443, or the ACME challenge and the HTTPS redirect lose their listener.
ENV ASPNETCORE_URLS=http://+:8080
# 8080/8443, not 80/443: the runtime image drops to a non-root uid below and cannot bind a
# privileged port. Docker publishes the public ports onto these; Edge:Certificates
# :PublicHttpsPort is what redirects are built from, so the listener number never reaches a
# browser.
EXPOSE 8080 8443
USER $APP_UID
ENTRYPOINT ["dotnet", "Dcms.Edge.dll"]
