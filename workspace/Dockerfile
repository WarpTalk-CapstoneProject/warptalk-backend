FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy shared project first
COPY shared/WarpTalk.Shared/WarpTalk.Shared.csproj shared/WarpTalk.Shared/
# Copy all workspace layer projects
COPY workspace/src/WarpTalk.WorkspaceService.Domain/WarpTalk.WorkspaceService.Domain.csproj workspace/src/WarpTalk.WorkspaceService.Domain/
COPY workspace/src/WarpTalk.WorkspaceService.Application/WarpTalk.WorkspaceService.Application.csproj workspace/src/WarpTalk.WorkspaceService.Application/
COPY workspace/src/WarpTalk.WorkspaceService.Infrastructure/WarpTalk.WorkspaceService.Infrastructure.csproj workspace/src/WarpTalk.WorkspaceService.Infrastructure/
COPY workspace/src/WarpTalk.WorkspaceService.API/WarpTalk.WorkspaceService.API.csproj workspace/src/WarpTalk.WorkspaceService.API/
RUN dotnet restore workspace/src/WarpTalk.WorkspaceService.API/WarpTalk.WorkspaceService.API.csproj

COPY . .
WORKDIR /src/workspace/src/WarpTalk.WorkspaceService.API
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 5105 50055
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "WarpTalk.WorkspaceService.API.dll"]
