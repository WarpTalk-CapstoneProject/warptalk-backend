using Microsoft.EntityFrameworkCore;
using WarpTalk.BillingService.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// Add DbContext
builder.Services.AddDbContext<BillingDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("BillingDb"));
    options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
});

var app = builder.Build();

app.UseAuthorization();
app.MapControllers();
app.Run();
