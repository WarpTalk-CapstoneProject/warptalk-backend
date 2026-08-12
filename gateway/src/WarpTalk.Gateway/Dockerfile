FROM mcr.microsoft.com/dotnet/sdk:10.0.302@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664 AS build
ENV DOTNET_EnableWriteXorExecute=0
WORKDIR /src

COPY shared/WarpTalk.Shared/WarpTalk.Shared.csproj shared/WarpTalk.Shared/
COPY gateway/src/WarpTalk.Gateway/WarpTalk.Gateway.csproj gateway/src/WarpTalk.Gateway/
COPY . .
RUN dotnet restore gateway/src/WarpTalk.Gateway/WarpTalk.Gateway.csproj --locked-mode
WORKDIR /src/gateway/src/WarpTalk.Gateway
RUN dotnet publish -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11@sha256:207cc51496778557731c81ff670333d8ade4a4fec22768fd1be8e78474a84ecf AS final
WORKDIR /app
EXPOSE 5200
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "WarpTalk.Gateway.dll"]
