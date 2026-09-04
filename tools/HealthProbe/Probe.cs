using System.Net.Sockets;

// Healthcheck-проба для distroless-образа (chiseled): без shell, вызывается
// compose как ["CMD", "dotnet", "/app/probe/Probe.dll", "<port>"].
// Публикуется в Docker-стадии publish (file-based app, -p:PublishAot=false).
int port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 5023;
using var client = new TcpClient();
try
{
    await client.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(2));
}
catch
{
    return 1;
}
return client.Connected ? 0 : 1;
