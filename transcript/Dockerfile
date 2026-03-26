FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY shared/WarpTalk.Shared/WarpTalk.Shared.csproj shared/WarpTalk.Shared/
COPY transcript/src/WarpTalk.TranscriptService.Domain/WarpTalk.TranscriptService.Domain.csproj transcript/src/WarpTalk.TranscriptService.Domain/
COPY transcript/src/WarpTalk.TranscriptService.Application/WarpTalk.TranscriptService.Application.csproj transcript/src/WarpTalk.TranscriptService.Application/
COPY transcript/src/WarpTalk.TranscriptService.Infrastructure/WarpTalk.TranscriptService.Infrastructure.csproj transcript/src/WarpTalk.TranscriptService.Infrastructure/
COPY transcript/src/WarpTalk.TranscriptService.API/WarpTalk.TranscriptService.API.csproj transcript/src/WarpTalk.TranscriptService.API/
RUN dotnet restore transcript/src/WarpTalk.TranscriptService.API/WarpTalk.TranscriptService.API.csproj

COPY . .
WORKDIR /src/transcript/src/WarpTalk.TranscriptService.API
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 5103 50053
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "WarpTalk.TranscriptService.API.dll"]
