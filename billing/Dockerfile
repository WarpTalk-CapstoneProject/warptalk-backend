FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ENV DOTNET_EnableWriteXorExecute=0
WORKDIR /src

COPY shared/WarpTalk.Shared/WarpTalk.Shared.csproj shared/WarpTalk.Shared/
COPY billing/src/WarpTalk.BillingService.Domain/WarpTalk.BillingService.Domain.csproj billing/src/WarpTalk.BillingService.Domain/
COPY billing/src/WarpTalk.BillingService.Application/WarpTalk.BillingService.Application.csproj billing/src/WarpTalk.BillingService.Application/
COPY billing/src/WarpTalk.BillingService.Infrastructure/WarpTalk.BillingService.Infrastructure.csproj billing/src/WarpTalk.BillingService.Infrastructure/
COPY billing/src/WarpTalk.BillingService.API/WarpTalk.BillingService.API.csproj billing/src/WarpTalk.BillingService.API/
RUN dotnet restore billing/src/WarpTalk.BillingService.API/WarpTalk.BillingService.API.csproj

COPY . .
WORKDIR /src/billing/src/WarpTalk.BillingService.API
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 5107 50054
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "WarpTalk.BillingService.API.dll"]
