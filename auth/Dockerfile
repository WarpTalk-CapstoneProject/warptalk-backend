FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy shared project first
COPY shared/WarpTalk.Shared/WarpTalk.Shared.csproj shared/WarpTalk.Shared/
# Copy all auth layer projects
COPY auth/src/WarpTalk.AuthService.Domain/WarpTalk.AuthService.Domain.csproj auth/src/WarpTalk.AuthService.Domain/
COPY auth/src/WarpTalk.AuthService.Application/WarpTalk.AuthService.Application.csproj auth/src/WarpTalk.AuthService.Application/
COPY auth/src/WarpTalk.AuthService.Infrastructure/WarpTalk.AuthService.Infrastructure.csproj auth/src/WarpTalk.AuthService.Infrastructure/
COPY auth/src/WarpTalk.AuthService.API/WarpTalk.AuthService.API.csproj auth/src/WarpTalk.AuthService.API/
RUN dotnet restore auth/src/WarpTalk.AuthService.API/WarpTalk.AuthService.API.csproj

COPY . .
WORKDIR /src/auth/src/WarpTalk.AuthService.API
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 5101 50051
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "WarpTalk.AuthService.API.dll"]
