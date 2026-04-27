using Microsoft.EntityFrameworkCore;
using Data;
using Worker_Node.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddDbContextFactory<ApplicationDbContext>();

builder.Services.AddHttpClient();
builder.Services.AddSingleton<RabbitService>();
builder.Services.AddHostedService<WebCrawlerService>();

var app = builder.Build();

app.Run();
