FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY shared/WarpTalk.Shared/WarpTalk.Shared.csproj shared/WarpTalk.Shared/
COPY meeting/src/WarpTalk.MeetingService.Domain/WarpTalk.MeetingService.Domain.csproj meeting/src/WarpTalk.MeetingService.Domain/
COPY meeting/src/WarpTalk.MeetingService.Application/WarpTalk.MeetingService.Application.csproj meeting/src/WarpTalk.MeetingService.Application/
COPY meeting/src/WarpTalk.MeetingService.Infrastructure/WarpTalk.MeetingService.Infrastructure.csproj meeting/src/WarpTalk.MeetingService.Infrastructure/
COPY meeting/src/WarpTalk.MeetingService.API/WarpTalk.MeetingService.API.csproj meeting/src/WarpTalk.MeetingService.API/
RUN dotnet restore meeting/src/WarpTalk.MeetingService.API/WarpTalk.MeetingService.API.csproj

COPY . .
WORKDIR /src/meeting/src/WarpTalk.MeetingService.API
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 5105 50055
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "WarpTalk.MeetingService.API.dll"]
