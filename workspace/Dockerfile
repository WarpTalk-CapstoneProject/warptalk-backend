FROM mcr.microsoft.com/dotnet/sdk:10.0.302@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664 AS build
WORKDIR /src

# Copy shared project first
COPY shared/WarpTalk.Shared/WarpTalk.Shared.csproj shared/WarpTalk.Shared/
# Copy all workspace layer projects
COPY workspace/src/WarpTalk.WorkspaceService.Domain/WarpTalk.WorkspaceService.Domain.csproj workspace/src/WarpTalk.WorkspaceService.Domain/
COPY workspace/src/WarpTalk.WorkspaceService.Application/WarpTalk.WorkspaceService.Application.csproj workspace/src/WarpTalk.WorkspaceService.Application/
COPY workspace/src/WarpTalk.WorkspaceService.Infrastructure/WarpTalk.WorkspaceService.Infrastructure.csproj workspace/src/WarpTalk.WorkspaceService.Infrastructure/
COPY workspace/src/WarpTalk.WorkspaceService.API/WarpTalk.WorkspaceService.API.csproj workspace/src/WarpTalk.WorkspaceService.API/
COPY . .
RUN dotnet restore workspace/src/WarpTalk.WorkspaceService.API/WarpTalk.WorkspaceService.API.csproj --locked-mode
WORKDIR /src/workspace/src/WarpTalk.WorkspaceService.API
RUN dotnet publish -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10@sha256:f1126d438ccc359f51cc6d4701a8deae513856cf10f5fe645d29ea6403dcac6b AS final
WORKDIR /app
EXPOSE 5106 50056
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "WarpTalk.WorkspaceService.API.dll"]
