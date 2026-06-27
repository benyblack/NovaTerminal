using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NovaTerminal.McpServer;

var builder = Host.CreateApplicationBuilder(args);

// CRITICAL for stdio transport: all logging must go to stderr. Anything written to stdout
// corrupts the JSON-RPC stream the MCP client reads.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

// Resolves the repository root once and provides path-safe, read-only access to docs.
builder.Services.AddSingleton(RepoContext.Discover());

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
