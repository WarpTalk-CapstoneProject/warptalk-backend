FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy shared project first
COPY shared/WarpTalk.Shared/WarpTalk.Shared.csproj shared/WarpTalk.Shared/
# Copy all assistant layer projects
COPY assistant/src/WarpTalk.AssistantService.Domain/WarpTalk.AssistantService.Domain.csproj assistant/src/WarpTalk.AssistantService.Domain/
COPY assistant/src/WarpTalk.AssistantService.Application/WarpTalk.AssistantService.Application.csproj assistant/src/WarpTalk.AssistantService.Application/
COPY assistant/src/WarpTalk.AssistantService.Infrastructure/WarpTalk.AssistantService.Infrastructure.csproj assistant/src/WarpTalk.AssistantService.Infrastructure/
COPY assistant/src/WarpTalk.AssistantService.API/WarpTalk.AssistantService.API.csproj assistant/src/WarpTalk.AssistantService.API/
RUN dotnet restore assistant/src/WarpTalk.AssistantService.API/WarpTalk.AssistantService.API.csproj

COPY . .
WORKDIR /src/assistant/src/WarpTalk.AssistantService.API
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 5108
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "WarpTalk.AssistantService.API.dll"]
