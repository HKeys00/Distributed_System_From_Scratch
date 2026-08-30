using Microsoft.EntityFrameworkCore;
using Data;
using Shared.Helpers;
using Worker_Node.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSeqLogging("Worker_Node");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddDbContextFactory<ApplicationDbContext>();

builder.Services.AddHttpClient();
builder.Services.AddSingleton<RabbitService>();
builder.Services.AddPrometheusMetrics(port: 9103);
builder.Services.AddDatabaseReadyGate();
builder.Services.AddHostedService<WebCrawlerService>();

var app = builder.Build();

app.Run();
