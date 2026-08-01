FROM mcr.microsoft.com/dotnet/sdk:10.0.302@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664 AS build
WORKDIR /src

# Copy shared project first
COPY shared/WarpTalk.Shared/WarpTalk.Shared.csproj shared/WarpTalk.Shared/
# Copy all assistant layer projects
COPY assistant/src/WarpTalk.AssistantService.Domain/WarpTalk.AssistantService.Domain.csproj assistant/src/WarpTalk.AssistantService.Domain/
COPY assistant/src/WarpTalk.AssistantService.Application/WarpTalk.AssistantService.Application.csproj assistant/src/WarpTalk.AssistantService.Application/
COPY assistant/src/WarpTalk.AssistantService.Infrastructure/WarpTalk.AssistantService.Infrastructure.csproj assistant/src/WarpTalk.AssistantService.Infrastructure/
COPY assistant/src/WarpTalk.AssistantService.API/WarpTalk.AssistantService.API.csproj assistant/src/WarpTalk.AssistantService.API/
COPY . .
RUN dotnet restore assistant/src/WarpTalk.AssistantService.API/WarpTalk.AssistantService.API.csproj --locked-mode
WORKDIR /src/assistant/src/WarpTalk.AssistantService.API
RUN dotnet publish -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10@sha256:f1126d438ccc359f51cc6d4701a8deae513856cf10f5fe645d29ea6403dcac6b AS final
WORKDIR /app
EXPOSE 5108
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "WarpTalk.AssistantService.API.dll"]
