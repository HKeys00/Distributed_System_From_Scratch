using Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddDbContextFactory<ApplicationDbContext>();

builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();
app.Run();
