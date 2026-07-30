FROM mcr.microsoft.com/dotnet/sdk:10.0.302@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664 AS build
ENV DOTNET_EnableWriteXorExecute=0
WORKDIR /src

# Copy shared project first
COPY shared/WarpTalk.Shared/WarpTalk.Shared.csproj shared/WarpTalk.Shared/
# Copy all auth layer projects
COPY auth/src/WarpTalk.AuthService.Domain/WarpTalk.AuthService.Domain.csproj auth/src/WarpTalk.AuthService.Domain/
COPY auth/src/WarpTalk.AuthService.Application/WarpTalk.AuthService.Application.csproj auth/src/WarpTalk.AuthService.Application/
COPY auth/src/WarpTalk.AuthService.Infrastructure/WarpTalk.AuthService.Infrastructure.csproj auth/src/WarpTalk.AuthService.Infrastructure/
COPY auth/src/WarpTalk.AuthService.API/WarpTalk.AuthService.API.csproj auth/src/WarpTalk.AuthService.API/
COPY . .
RUN dotnet restore auth/src/WarpTalk.AuthService.API/WarpTalk.AuthService.API.csproj --locked-mode
WORKDIR /src/auth/src/WarpTalk.AuthService.API
RUN dotnet publish -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.7@sha256:55e37c7795bfaf6b9cc5d77c155811d9569f529d86e20647704bc1d7dd9741d4 AS final
WORKDIR /app
EXPOSE 5101 50051
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "WarpTalk.AuthService.API.dll"]
