using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ProcureStudio.Infrastructure;

/// <summary>What we got from the MCP server. Disposing it stops the child process.</summary>
public sealed class McpConnection(McpClient? client, IReadOnlyList<AITool> tools, string? rulesText) : IAsyncDisposable
{
    public IReadOnlyList<AITool> Tools { get; } = tools;      // MCP primitive 1: tools (the MODEL calls them)
    public string? RulesText { get; } = rulesText;            // MCP primitive 2: a resource (the APP reads it)
    public ValueTask DisposeAsync() => client is null ? ValueTask.CompletedTask : client.DisposeAsync();
}

public static class McpConnector
{
    /// <summary>
    /// Starts the policy server over STDIO and imports its capabilities.
    /// Resilient on purpose: if the server is missing or not built, the app still starts (without policy tools)
    /// instead of crashing. Optional dependencies should degrade, not take the whole app down.
    /// </summary>
    public static async Task<McpConnection> ConnectAsync(IConfiguration config, ILogger log)
    {
        try
        {
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "PolicyMcp",
                Command = config["Mcp:Command"]!,
                Arguments = config.GetSection("Mcp:Args").Get<string[]>(),
                // REMOTE alternative: an MCP server hosted over HTTP instead of a child process:
                //   new HttpClientTransport(new() { Endpoint = new Uri("https://host/mcp") })
            });
            var client = await McpClient.CreateAsync(transport);

            // Primitive 1, TOOLS: McpClientTool derives from AIFunction, so it plugs straight into an agent's tool list.
            var tools = (await client.ListToolsAsync()).Cast<AITool>().ToList();

            // Primitive 2, RESOURCES: the APP (not the model) reads the policy text and injects it into instructions.
            string? rules = null;
            try
            {
                var res = await client.ReadResourceAsync("policy://procurement/rules");
                rules = string.Join("\n", res.Contents.OfType<TextResourceContents>().Select(c => c.Text));
            }
            catch (Exception ex) { log.LogWarning("MCP resource not available: {Message}", ex.Message); }

            // Primitive 3, PROMPTS: listed here for visibility. A UI would offer them as user-selectable templates.
            var prompts = await client.ListPromptsAsync();
            log.LogInformation("MCP connected: {Tools} tools [{Names}], {Prompts} prompts",
                tools.Count, string.Join(", ", tools.Select(t => t.Name)), prompts.Count);

            return new McpConnection(client, tools, rules);
        }
        catch (Exception ex)
        {
            log.LogWarning("MCP server unavailable ({Message}). Did you run 'dotnet build' in PolicyMcpServer? Continuing without it.", ex.Message);
            return new McpConnection(null, [], null);
        }
    }
}
