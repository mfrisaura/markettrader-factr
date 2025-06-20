using FIXLinkTradingServer.Services;
using FIXLinkTradingServer.Hubs;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddSingleton<ITradingService, TradingService>();
builder.Services.AddSingleton<IFIXLinkService, FIXLinkService>();
builder.Services.AddSingleton<ICashBalanceService, CashBalanceService>();
builder.Services.AddLogging();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(builder =>
    {
        builder.AllowAnyOrigin()
               .AllowAnyMethod()
               .AllowAnyHeader();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// Add these lines in this order:
app.UseDefaultFiles();  // This makes / serve index.html
app.UseStaticFiles();   // This serves static files
app.UseRouting();
app.UseCors();

app.MapControllers();
app.MapHub<TradingHub>("/tradinghub");

app.Run();