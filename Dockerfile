# -------- Build Stage --------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json* ./
COPY osu-performance-server.sln ./
COPY PerformanceServer/PerformanceServer.csproj PerformanceServer/

RUN dotnet restore osu-performance-server.sln

COPY PerformanceServer/ PerformanceServer/

RUN dotnet nuget locals all --clear

ARG PUBLISH_CONFIGURATION=Release
RUN dotnet publish PerformanceServer/PerformanceServer.csproj \
    -c $PUBLISH_CONFIGURATION \
    -o /app/publish

# -------- Runtime Stage --------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    SAVE_BEATMAP_FILES=false \
    BEATMAPS_PATH=/data/beatmaps \
    RULESETS_PATH=/data/rulesets \
    MAX_BEATMAP_FILE_SIZE=5242880

VOLUME ["/data"]

COPY --from=build /app/publish .

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD curl http://127.0.0.1:8080 || exit 1

ENTRYPOINT ["dotnet", "PerformanceServer.dll"]
