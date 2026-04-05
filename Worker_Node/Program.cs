using Microsoft.EntityFrameworkCore;
using Data;
using Worker_Node.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddSingleton<ImageService>();
builder.Services.AddSingleton<RabbitService>();

var app = builder.Build();


app.MapGet("/", () => "Hello World!");

app.Run();
