FROM mcr.microsoft.com/dotnet/sdk:10.0.302@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664 AS build
ENV DOTNET_EnableWriteXorExecute=0
WORKDIR /src

COPY shared/WarpTalk.Shared/WarpTalk.Shared.csproj shared/WarpTalk.Shared/
COPY meeting/src/WarpTalk.MeetingService.Domain/WarpTalk.MeetingService.Domain.csproj meeting/src/WarpTalk.MeetingService.Domain/
COPY meeting/src/WarpTalk.MeetingService.Application/WarpTalk.MeetingService.Application.csproj meeting/src/WarpTalk.MeetingService.Application/
COPY meeting/src/WarpTalk.MeetingService.Infrastructure/WarpTalk.MeetingService.Infrastructure.csproj meeting/src/WarpTalk.MeetingService.Infrastructure/
COPY meeting/src/WarpTalk.MeetingService.API/WarpTalk.MeetingService.API.csproj meeting/src/WarpTalk.MeetingService.API/
COPY . .
RUN dotnet restore meeting/src/WarpTalk.MeetingService.API/WarpTalk.MeetingService.API.csproj --locked-mode
WORKDIR /src/meeting/src/WarpTalk.MeetingService.API
RUN dotnet publish -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10@sha256:f1126d438ccc359f51cc6d4701a8deae513856cf10f5fe645d29ea6403dcac6b AS final
WORKDIR /app
EXPOSE 5105 50055
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "WarpTalk.MeetingService.API.dll"]
