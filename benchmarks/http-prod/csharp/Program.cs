// Production HTTP server in C#: ASP.NET Core's Kestrel with a minimal-API route.
//
//   GET / -> 200 "Hello, World!"; other method on / -> 405; other path -> 404.
//
// Usage: server [port] [threads]   (threads = 1: the process runs on one CPU, with
//                                   one socket I/O thread; 0 = all CPUs)
using System.Diagnostics;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;

int port = args.Length > 0 ? int.Parse(args[0]) : 8100;
int threads = args.Length > 1 ? int.Parse(args[1]) : 1;
if (threads <= 0) threads = Environment.ProcessorCount;
if (threads == 1 && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
{
    // .NET has no single-threaded mode, so pin the process to one CPU
    Process.GetCurrentProcess().ProcessorAffinity = (IntPtr)1;
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseKestrel(o =>
{
    o.ListenAnyIP(port);
    o.AddServerHeader = false;
    o.Limits.MaxRequestHeadersTotalSize = 8 * 1024;
    o.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
    o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
});
builder.Services.Configure<SocketTransportOptions>(o => o.IOQueueCount = threads);

var app = builder.Build();
var hello = "Hello, World!"u8.ToArray();
app.MapGet("/", (HttpContext ctx) =>
{
    var r = ctx.Response;
    r.ContentType = "text/plain";
    r.Headers.Server = "csharp";
    r.ContentLength = hello.Length;
    return r.Body.WriteAsync(hello).AsTask();
});
app.MapFallback((HttpContext ctx) =>
{
    bool root = ctx.Request.Path == "/";                // other methods on / get 405
    ctx.Response.StatusCode = root ? 405 : 404;
    ctx.Response.ContentType = "text/plain";
    return ctx.Response.WriteAsync(root ? "Method Not Allowed" : "Not Found");
});

Console.WriteLine($"C# (Kestrel) server on port {port} ({threads} thread{(threads == 1 ? "" : "s")})");
app.Run();
