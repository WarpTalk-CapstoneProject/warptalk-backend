FROM mcr.microsoft.com/dotnet/sdk:10.0.302@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664 AS build
WORKDIR /src
ENV DOTNET_SYSTEM_NET_HTTP_SOCKETSHTTPHANDLER_HTTP2SUPPORT=false \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true \
    NUGET_XMLDOC_MODE=skip

COPY Directory.Build.props ./
# Copy shared project first
COPY shared/WarpTalk.Shared/WarpTalk.Shared.csproj shared/WarpTalk.Shared/
COPY shared/WarpTalk.Shared/packages.lock.json shared/WarpTalk.Shared/
# Copy all assistant layer projects
COPY assistant/src/WarpTalk.AssistantService.Domain/WarpTalk.AssistantService.Domain.csproj assistant/src/WarpTalk.AssistantService.Domain/
COPY assistant/src/WarpTalk.AssistantService.Domain/packages.lock.json assistant/src/WarpTalk.AssistantService.Domain/
COPY assistant/src/WarpTalk.AssistantService.Application/WarpTalk.AssistantService.Application.csproj assistant/src/WarpTalk.AssistantService.Application/
COPY assistant/src/WarpTalk.AssistantService.Application/packages.lock.json assistant/src/WarpTalk.AssistantService.Application/
COPY assistant/src/WarpTalk.AssistantService.Infrastructure/WarpTalk.AssistantService.Infrastructure.csproj assistant/src/WarpTalk.AssistantService.Infrastructure/
COPY assistant/src/WarpTalk.AssistantService.Infrastructure/packages.lock.json assistant/src/WarpTalk.AssistantService.Infrastructure/
COPY assistant/src/WarpTalk.AssistantService.API/WarpTalk.AssistantService.API.csproj assistant/src/WarpTalk.AssistantService.API/
COPY assistant/src/WarpTalk.AssistantService.API/packages.lock.json assistant/src/WarpTalk.AssistantService.API/
RUN --mount=type=cache,target=/root/.nuget/packages \
    --mount=type=cache,target=/root/.local/share/NuGet/v3-cache \
    dotnet restore assistant/src/WarpTalk.AssistantService.API/WarpTalk.AssistantService.API.csproj --locked-mode --disable-parallel --ignore-failed-sources /p:NuGetAudit=false
COPY . .
WORKDIR /src/assistant/src/WarpTalk.AssistantService.API
RUN dotnet publish -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11@sha256:207cc51496778557731c81ff670333d8ade4a4fec22768fd1be8e78474a84ecf AS final
WORKDIR /app
EXPOSE 5108
RUN mkdir -p /var/lib/warptalk/keys \
    && chown -R $APP_UID:$APP_UID /var/lib/warptalk
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "WarpTalk.AssistantService.API.dll"]
