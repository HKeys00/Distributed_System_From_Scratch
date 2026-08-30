using Data;
using Microsoft.EntityFrameworkCore;
using Shared.Helpers;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSeqLogging("Data");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddPrometheusMetrics(port: 9104);

var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    var pendingMigrations = dbContext.Database.GetPendingMigrations().ToList();
    if (pendingMigrations.Count > 0)
    {
        logger.LogInformation("Applying {Count} pending migrations", pendingMigrations.Count);
        dbContext.Database.Migrate();
        logger.LogInformation("Migrations applied successfully");
    }
    else
    {
        logger.LogInformation("No pending migrations found");
    }
}

app.Run();