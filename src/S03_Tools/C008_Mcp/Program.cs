using Masque.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// MCP over stdio: standard output carries the protocol, so all logging goes to standard error, and only
// warnings and above, because lower levels can echo tool arguments (an assistant's SQL may hold typed values).
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSingleton(_ => MasqueToolService.CreateDefault());
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<MasqueTools>();

await builder.Build().RunAsync();
