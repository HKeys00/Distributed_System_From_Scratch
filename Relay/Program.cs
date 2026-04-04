using Microsoft.EntityFrameworkCore;
using Data;
using Relay;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddDbContextFactory<ApplicationDbContext>();
builder.Services.AddHostedService<Worker>();



var host = builder.Build();
host.Run();
