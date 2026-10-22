FROM mcr.microsoft.com/dotnet/sdk:10.0.402@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
WORKDIR /source
COPY . .
RUN dotnet publish src/PeerOnQ.Signaling.Server/PeerOnQ.Signaling.Server.csproj \
    --configuration Release \
    --output /out \
    --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac AS runtime
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out .
RUN mkdir --parents /app/data /app/logs && chown --recursive "$APP_UID:$APP_UID" /app
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=3 \
    CMD curl --fail --silent http://127.0.0.1:8080/health/live || exit 1
ENTRYPOINT ["dotnet", "PeerOnQ.Signaling.Server.dll"]
