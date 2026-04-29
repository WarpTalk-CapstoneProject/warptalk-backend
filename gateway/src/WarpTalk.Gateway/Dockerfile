FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY gateway/src/WarpTalk.Gateway/WarpTalk.Gateway.csproj gateway/src/WarpTalk.Gateway/
RUN dotnet restore gateway/src/WarpTalk.Gateway/WarpTalk.Gateway.csproj

COPY . .
WORKDIR /src/gateway/src/WarpTalk.Gateway
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
EXPOSE 5200 5443
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "WarpTalk.Gateway.dll"]
