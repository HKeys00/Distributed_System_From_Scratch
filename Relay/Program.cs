using Microsoft.EntityFrameworkCore;
using Data;
using Relay;
using Shared.Helpers;

var builder = Host.CreateDefaultBuilder(args)
    .UseSeqLogging("Relay")
    .ConfigureServices((context, services) =>
    {
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(context.Configuration.GetConnectionString("DefaultConnection")));
        services.AddDbContextFactory<ApplicationDbContext>();
        services.AddHostedService<Worker>();
    });

var host = builder.Build();
host.Run();
