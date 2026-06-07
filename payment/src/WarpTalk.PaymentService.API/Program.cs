using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WarpTalk.PaymentService.Application.Services;
using WarpTalk.PaymentService.Application.Interfaces;
using Stripe;
using WarpTalk.PaymentService.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins("http://localhost:3000")
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// Stripe Setup
StripeConfiguration.ApiKey = builder.Configuration["Stripe:SecretKey"] ?? "sk_test_placeholder";

// Add Infrastructure Services
builder.Services.AddScoped<IStripePaymentService, StripePaymentService>();

// Add Application Services
builder.Services.AddScoped<IPaymentAppService, PaymentAppService>();

// Add gRPC Client for Billing Service
builder.Services.AddGrpcClient<WarpTalk.Shared.Protos.BillingService.BillingServiceClient>(o =>
{
    var url = builder.Configuration["BillingServiceGrpcUrl"] ?? "http://localhost:50054";
    o.Address = new Uri(url);
});

// Add gRPC
builder.Services.AddGrpc();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("AllowFrontend");
app.UseAuthorization();
app.MapControllers();
app.MapGrpcService<WarpTalk.PaymentService.API.GrpcServices.PaymentGrpcService>();

app.Run();
