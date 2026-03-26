FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY notification/src/WarpTalk.NotificationService.Domain/WarpTalk.NotificationService.Domain.csproj notification/src/WarpTalk.NotificationService.Domain/
COPY notification/src/WarpTalk.NotificationService.Application/WarpTalk.NotificationService.Application.csproj notification/src/WarpTalk.NotificationService.Application/
COPY notification/src/WarpTalk.NotificationService.Infrastructure/WarpTalk.NotificationService.Infrastructure.csproj notification/src/WarpTalk.NotificationService.Infrastructure/
COPY notification/src/WarpTalk.NotificationService.API/WarpTalk.NotificationService.API.csproj notification/src/WarpTalk.NotificationService.API/
RUN dotnet restore notification/src/WarpTalk.NotificationService.API/WarpTalk.NotificationService.API.csproj

COPY . .
WORKDIR /src/notification/src/WarpTalk.NotificationService.API
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 5104 50054
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "WarpTalk.NotificationService.API.dll"]
