using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Data;
using Relay;
using Shared.Helpers;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSeqLogging("Relay");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddDbContextFactory<ApplicationDbContext>();
builder.Services.AddPrometheusMetrics(port: 9102);
builder.Services.AddDatabaseReadyGate();
builder.Services.AddHostedService<Worker>();

builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/healthz");

app.Run();
