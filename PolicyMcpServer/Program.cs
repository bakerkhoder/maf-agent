// ════════════════════════════════════════════════════════════════════════════════════════
//  PolicyMcpServer: a tiny MCP (Model Context Protocol) server that owns the procurement policy.
//
//  WHY A SEPARATE PROCESS?  Policy is a "capability" the agent consumes, not code it owns.
//  Any MCP client (our agent, VS Code, Claude Desktop...) can use this same server unchanged.
//
//  THE THREE MCP PRIMITIVES (all three are demonstrated below):
//    • Tool     = an ACTION the MODEL decides to call     (GetApprovalPolicy)
//    • Resource = read-only DATA the APP decides to load  (policy://procurement/rules)
//    • Prompt   = a reusable TEMPLATE the USER picks      (audit_po)
//
//  TRANSPORT = stdio.  The client launches this exe as a child process and talks JSON-RPC over:
//    stdin  (client -> server messages)    stdout (server -> client messages)    stderr (free for logs)
//  ⚠ stdout IS THE PROTOCOL CHANNEL. A single Console.WriteLine to stdout corrupts the stream,
//    so every log below is redirected to stderr.
// ════════════════════════════════════════════════════════════════════════════════════════
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);

// Send ALL console logging to stderr (threshold Trace = every level), keeping stdout clean for JSON-RPC.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()      // talk over stdin/stdout
    .WithToolsFromAssembly()         // discovers every [McpServerToolType] class in this assembly
    .WithResourcesFromAssembly()     // ... [McpServerResourceType]
    .WithPromptsFromAssembly();      // ... [McpServerPromptType]

await builder.Build().RunAsync();

// ───────────────────────── Primitive 1: TOOL ─────────────────────────
[McpServerToolType]
public static class PolicyTools
{
    [McpServerTool, Description("Returns who must approve a purchase of the given total, and whether vendor quotes are required.")]
    public static ApprovalPolicy GetApprovalPolicy([Description("Purchase order total")] decimal amount) => amount switch
    {
        <= 10_000 => new("Team Lead",       false, "Up to 10,000: Team Lead approves."),
        <= 25_000 => new("Department Head", false, "10,001 to 25,000: Department Head approves."),
        <= 50_000 => new("Department Head", true,  "25,001 to 50,000: Department Head approves; three vendor quotes required."),
        _         => new("CFO",             true,  "Over 50,000: CFO approves; three vendor quotes required."),
    };
}

public sealed record ApprovalPolicy(string Approver, bool QuotesRequired, string Rule);

// ───────────────────────── Primitive 2: RESOURCE ─────────────────────────
// Resources are NOT called by the model. The host application reads them and decides how to use them.
[McpServerResourceType]
public static class PolicyResources
{
    [McpServerResource(UriTemplate = "policy://procurement/rules", Name = "Procurement rules", MimeType = "text/plain"),
     Description("The full written procurement policy.")]
    public static string Rules() => """
        1. Approval tiers: up to 10,000 Team Lead; up to 50,000 Department Head; above 50,000 CFO.
        2. Orders above 25,000 need three vendor quotes.
        3. Splitting an order to stay under a threshold is forbidden.
        4. Only buyers and managers may route a PO; routing above 50,000 requires a manager.
        """;
}

// ───────────────────────── Primitive 3: PROMPT ─────────────────────────
// Prompts are user-selected templates (think "slash commands"), parameterised and server-owned.
[McpServerPromptType]
public static class PolicyPrompts
{
    [McpServerPrompt(Name = "audit_po"), Description("Template asking for a policy audit of one purchase order.")]
    public static string AuditPo([Description("PO number, e.g. PO-100001")] string poNumber) =>
        $"Audit {poNumber}: fetch it, check approval tier, quotes and budget, and list any policy violations.";
}
