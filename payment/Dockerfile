FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY shared/WarpTalk.Shared/WarpTalk.Shared.csproj shared/WarpTalk.Shared/
COPY payment/src/WarpTalk.PaymentService.API/WarpTalk.PaymentService.API.csproj payment/src/WarpTalk.PaymentService.API/
COPY payment/src/WarpTalk.PaymentService.Application/WarpTalk.PaymentService.Application.csproj payment/src/WarpTalk.PaymentService.Application/
COPY payment/src/WarpTalk.PaymentService.Infrastructure/WarpTalk.PaymentService.Infrastructure.csproj payment/src/WarpTalk.PaymentService.Infrastructure/
RUN dotnet restore payment/src/WarpTalk.PaymentService.API/WarpTalk.PaymentService.API.csproj
COPY . .
WORKDIR /src/payment/src/WarpTalk.PaymentService.API
RUN dotnet build WarpTalk.PaymentService.API.csproj -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "WarpTalk.PaymentService.API.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "WarpTalk.PaymentService.API.dll"]
