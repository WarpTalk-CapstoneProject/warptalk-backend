FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/WarpTalk.PaymentService.API/WarpTalk.PaymentService.API.csproj", "WarpTalk.PaymentService.API/"]
COPY ["src/WarpTalk.PaymentService.Application/WarpTalk.PaymentService.Application.csproj", "WarpTalk.PaymentService.Application/"]
COPY ["src/WarpTalk.PaymentService.Domain/WarpTalk.PaymentService.Domain.csproj", "WarpTalk.PaymentService.Domain/"]
COPY ["src/WarpTalk.PaymentService.Infrastructure/WarpTalk.PaymentService.Infrastructure.csproj", "WarpTalk.PaymentService.Infrastructure/"]
RUN dotnet restore "WarpTalk.PaymentService.API/WarpTalk.PaymentService.API.csproj"
COPY src/ .
WORKDIR "/src/WarpTalk.PaymentService.API"
RUN dotnet build "WarpTalk.PaymentService.API.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "WarpTalk.PaymentService.API.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "WarpTalk.PaymentService.API.dll"]
