FROM mcr.microsoft.com/dotnet/sdk:10.0.302@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664 AS build
ENV DOTNET_EnableWriteXorExecute=0
WORKDIR /src

# Copy shared project first
COPY shared/WarpTalk.Shared/WarpTalk.Shared.csproj shared/WarpTalk.Shared/
# Copy all billing layer projects
COPY billing/src/WarpTalk.BillingService.Domain/WarpTalk.BillingService.Domain.csproj billing/src/WarpTalk.BillingService.Domain/
COPY billing/src/WarpTalk.BillingService.Application/WarpTalk.BillingService.Application.csproj billing/src/WarpTalk.BillingService.Application/
COPY billing/src/WarpTalk.BillingService.Infrastructure/WarpTalk.BillingService.Infrastructure.csproj billing/src/WarpTalk.BillingService.Infrastructure/
COPY billing/src/WarpTalk.BillingService.API/WarpTalk.BillingService.API.csproj billing/src/WarpTalk.BillingService.API/
COPY . .
RUN dotnet restore billing/src/WarpTalk.BillingService.API/WarpTalk.BillingService.API.csproj --locked-mode
WORKDIR /src/billing/src/WarpTalk.BillingService.API
RUN dotnet publish -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10@sha256:f1126d438ccc359f51cc6d4701a8deae513856cf10f5fe645d29ea6403dcac6b AS final
WORKDIR /app
EXPOSE 5107
EXPOSE 50057
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "WarpTalk.BillingService.API.dll"]
