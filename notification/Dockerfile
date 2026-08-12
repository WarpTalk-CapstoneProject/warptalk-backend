FROM mcr.microsoft.com/dotnet/sdk:10.0.302@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664 AS build
ENV DOTNET_EnableWriteXorExecute=0
WORKDIR /src

COPY notification/src/WarpTalk.NotificationService.Domain/WarpTalk.NotificationService.Domain.csproj notification/src/WarpTalk.NotificationService.Domain/
COPY notification/src/WarpTalk.NotificationService.Application/WarpTalk.NotificationService.Application.csproj notification/src/WarpTalk.NotificationService.Application/
COPY notification/src/WarpTalk.NotificationService.Infrastructure/WarpTalk.NotificationService.Infrastructure.csproj notification/src/WarpTalk.NotificationService.Infrastructure/
COPY notification/src/WarpTalk.NotificationService.API/WarpTalk.NotificationService.API.csproj notification/src/WarpTalk.NotificationService.API/
COPY . .
RUN dotnet restore notification/src/WarpTalk.NotificationService.API/WarpTalk.NotificationService.API.csproj --locked-mode
WORKDIR /src/notification/src/WarpTalk.NotificationService.API
RUN dotnet publish -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11@sha256:207cc51496778557731c81ff670333d8ade4a4fec22768fd1be8e78474a84ecf AS final
WORKDIR /app
EXPOSE 5104 50054
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "WarpTalk.NotificationService.API.dll"]
