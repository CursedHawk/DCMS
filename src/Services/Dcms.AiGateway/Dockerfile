FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/ src/
RUN dotnet publish src/Services/Dcms.AiGateway/Dcms.AiGateway.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# curl backs the compose healthcheck
RUN apt-get update && apt-get install -y --no-install-recommends curl && apt-get clean
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Dcms.AiGateway.dll"]
