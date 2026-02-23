using AgarIA.Core.AI;
using AgarIA.Core.Data;
using AgarIA.Core.Game;
using AgarIA.Core.Repositories;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<HostOptions>(opts => opts.ShutdownTimeout = TimeSpan.FromSeconds(5));

builder.Services.AddData();
builder.Services.AddRepositories();
builder.Services.AddGame();
builder.Services.AddAI();
builder.Services.AddSignalR();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHub<GameHub>("/gamehub");

app.Start();

var urls = app.Urls;
var port = new Uri(urls.First()).Port;

Console.WriteLine();
Console.WriteLine($"  Local:   http://localhost:{port}");

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
