FROM mcr.microsoft.com/dotnet/sdk:10.0.402@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/PeerOnQ.Presence.Server/PeerOnQ.Presence.Server.csproj -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
RUN apt-get update \
    && apt-get install --no-install-recommends -y libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
USER $APP_UID
COPY --from=build /app .
ENTRYPOINT ["dotnet", "PeerOnQ.Presence.Server.dll"]
