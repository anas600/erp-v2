# ===========================================
# ERP-V2 Backend (.NET 8)
# Multi-stage build for smaller image
# ===========================================

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore (cache layer)
COPY ["ErpV2.csproj", "./"]
RUN dotnet restore "ErpV2.csproj"

# Build + publish
COPY . .
RUN dotnet publish "ErpV2.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .

# Use polling file watchers instead of inotify. Render's free-tier
# container hits the inotify instance limit (128) at startup when
# ASP.NET Core's config system tries to enable file change tokens.
# This env var is read by the .NET runtime before any application
# code runs, so it must be set at process start (Dockerfile ENV,
# docker-compose env, or Render env vars).
ENV DOTNET_USE_POLLING_FILE_WATCHER=true
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "ErpV2.dll"]

