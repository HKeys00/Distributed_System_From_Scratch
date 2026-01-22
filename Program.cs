using Distributed_System_From_Scratch.Middleware;
using Distributed_System_From_Scratch.Services;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

// Add services to the container.
builder.Services.AddLogging(b =>
    b.AddDebug()
    .AddConsole()
    .AddConfiguration(configuration.GetSection("Logging"))
    .SetMinimumLevel(LogLevel.Information)
);
builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddSingleton<INodeInformationService, NodeInformationService>();
builder.Services.AddSingleton<IDataStoreService, DataStoreService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.UseMiddleware<RequestResponseLoggerMiddleware>();

app.MapControllers();

app.Run();
