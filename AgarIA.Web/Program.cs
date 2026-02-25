using AgarIA.Core.AI;
using AgarIA.Core.Data;
using AgarIA.Core.Data.Models;
using AgarIA.Core.Game;
using AgarIA.Core.Repositories;
using AgarIA.Web.Data;
using AgarIA.Web.Services;
using AgarIA.Web.Services.FlashMessage;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Data directory for persistent files (SQLite DB, genome files)
var dataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? Directory.GetCurrentDirectory();
if (!Directory.Exists(dataDir))
    Directory.CreateDirectory(dataDir);

builder.Services.Configure<HostOptions>(opts => opts.ShutdownTimeout = TimeSpan.FromSeconds(5));

builder.Services.AddData();
builder.Services.AddRepositories();
builder.Services.AddGame();
builder.Services.AddAI();
builder.Services.AddSignalR();

// Admin portal: EF Core + SQLite
builder.Services.AddDbContext<AdminDbContext>(options =>
    options.UseSqlite($"Data Source={Path.Combine(dataDir, "admin.db")}"));

// ASP.NET Core Identity
builder.Services.AddIdentity<IdentityUser, IdentityRole>(options => {
    options.User.RequireUniqueEmail = false;
    options.Password.RequireDigit = false;
    options.Password.RequiredLength = 4;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireLowercase = false;
})
.AddEntityFrameworkStores<AdminDbContext>()
.AddDefaultTokenProviders();

// Cookie auth
builder.Services.ConfigureApplicationCookie(options => {
    options.LoginPath = "/admin/auth/login";
    options.LogoutPath = "/admin/auth/logout";
    options.AccessDeniedPath = "/admin/auth/login";
});

// Force lowercase URLs for Url.Action / tag helpers
builder.Services.Configure<RouteOptions>(options => {
    options.LowercaseUrls = true;
});

// Flash messages
builder.Services.AddHttpContextAccessor();
builder.Services.AddFlashMessage();

// MVC + Razor
builder.Services.AddControllersWithViews()
    .AddRazorRuntimeCompilation();

// Game settings singleton
builder.Services.AddSingleton<GameSettings>();

// Game history service
builder.Services.AddSingleton<GameHistoryService>();
builder.Services.AddSingleton<IGameResetHandler>(sp => sp.GetRequiredService<GameHistoryService>());

var app = builder.Build();

// Auto-migrate and seed admin user
using (var scope = app.Services.CreateScope()) {
    var db = scope.ServiceProvider.GetRequiredService<AdminDbContext>();
    db.Database.Migrate();

    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
    if (await userManager.FindByNameAsync("admin") == null) {
        var admin = new IdentityUser { UserName = "admin" };
        await userManager.CreateAsync(admin, "admin");
    }

    // Load persisted settings from database
    var gameSettings = app.Services.GetRequiredService<GameSettings>();
    await AdminSettingsService.Load(db, gameSettings);
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<GameHub>("/gamehub");

app.Start();

// Apply persisted runtime settings to engine
{
    var gameSettings = app.Services.GetRequiredService<GameSettings>();
    var gameEngine = app.Services.GetRequiredService<GameEngine>();
    var aiController = app.Services.GetRequiredService<IAIController>();
    gameEngine.SetResetSecondsRange(gameSettings.MinResetSeconds, gameSettings.MaxResetSeconds);
    gameEngine.SetMaxSpeed(gameSettings.MaxSpeed);
    aiController.SetResetAtScore(gameSettings.ResetAtScore);
}

var urls = app.Urls;
var port = new Uri(urls.First()).Port;

Console.WriteLine();
Console.WriteLine($"  Local:   http://localhost:{port}");
Console.WriteLine($"  Admin:   http://localhost:{port}/admin/");

try
{
    var networkIp = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName())
        .AddressList
        .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a));
    if (networkIp != null)
        Console.WriteLine($"  Network: http://{networkIp}:{port}");
}
catch { }

Console.WriteLine();

app.WaitForShutdown();
