using Distributed_System_From_Scratch.BackgroundWorkers;
using Distributed_System_From_Scratch.Middleware;
using Distributed_System_From_Scratch.Middleware.Options;
using Distributed_System_From_Scratch.Services;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

builder.Services.Configure<MaxConcurrentRequestsOptions>(
    builder.Configuration.GetSection(nameof(MaxConcurrentRequestsOptions)));

// Add services to the container.
builder.Services.AddLogging(b =>
    b.AddDebug()
    .AddConsole()
    .AddConfiguration(configuration.GetSection("Logging"))
    .SetMinimumLevel(LogLevel.Information)
);
builder.Services.AddControllers();
builder.Services.AddHttpClient();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddSingleton<INodeInformationService, NodeInformationService>();
builder.Services.AddSingleton<INodeCommunicationService, NodeCommunicationService>();
builder.Services.AddSingleton<IDataStoreService, DataStoreService>();
builder.Services.AddSingleton<NodeMetricsService>();

//builder.Services.AddHostedService<HeartBeatHostedService>();
builder.Services.AddHostedService<OperationsHostedService>();
builder.Services.AddHostedService<MetricsHostedService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseMiddleware<RequestTimeMiddleware>();
//app.UseMiddleware<MaxConcurrentRequestsMiddleware>();
//app.UseMiddleware<RequestResponseLoggerMiddleware>();

app.MapControllers();

app.Run();
