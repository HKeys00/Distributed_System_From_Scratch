using Controllers.Middleware;
using Data;
using Microsoft.EntityFrameworkCore;
using Shared.Helpers;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSeqLogging("Controllers");

builder.Services.AddHealthChecks();

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddDbContextFactory<ApplicationDbContext>();

builder.Services.AddPrometheusMetrics(port: 9101);

builder.Services.AddControllers();

var app = builder.Build();

app.MapHealthChecks("/healthz");
app.UseMiddleware<CorrelationIdMiddleware>();

app.MapControllers();
app.Run();
