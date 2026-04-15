FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY shared/WarpTalk.Shared/WarpTalk.Shared.csproj shared/WarpTalk.Shared/
COPY translation-room/src/WarpTalk.TranslationRoomService.Domain/WarpTalk.TranslationRoomService.Domain.csproj translation-room/src/WarpTalk.TranslationRoomService.Domain/
COPY translation-room/src/WarpTalk.TranslationRoomService.Application/WarpTalk.TranslationRoomService.Application.csproj translation-room/src/WarpTalk.TranslationRoomService.Application/
COPY translation-room/src/WarpTalk.TranslationRoomService.Infrastructure/WarpTalk.TranslationRoomService.Infrastructure.csproj translation-room/src/WarpTalk.TranslationRoomService.Infrastructure/
COPY translation-room/src/WarpTalk.TranslationRoomService.API/WarpTalk.TranslationRoomService.API.csproj translation-room/src/WarpTalk.TranslationRoomService.API/
RUN dotnet restore translation-room/src/WarpTalk.TranslationRoomService.API/WarpTalk.TranslationRoomService.API.csproj

COPY . .
WORKDIR /src/translation-room/src/WarpTalk.TranslationRoomService.API
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 5102 50052
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "WarpTalk.TranslationRoomService.API.dll"]
