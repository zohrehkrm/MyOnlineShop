FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY MyOnlineShop/MyOnlineShop.csproj MyOnlineShop/
COPY src/ src/
RUN dotnet restore MyOnlineShop/MyOnlineShop.csproj
COPY MyOnlineShop/ MyOnlineShop/
RUN dotnet publish MyOnlineShop/MyOnlineShop.csproj --no-restore -c Release -o /app/publish -m:1 /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
USER root
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish/ ./
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 CMD curl --fail --silent http://localhost:8080/api/v1/health || exit 1
ENTRYPOINT ["dotnet", "MyOnlineShop.dll"]
