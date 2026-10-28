// ════════════════════════════════════════════════════════════════════════════════════════
//  ProcureStudio: composition root. Read top to bottom; each numbered block is one concern.
//  Program.cs only WIRES things together; behaviour lives in the Agents/ and Domain/ folders.
// ════════════════════════════════════════════════════════════════════════════════════════
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ProcureStudio.Agents;
using ProcureStudio.Domain;
using ProcureStudio.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5300");
var config = builder.Configuration;

// ── 1. MODEL: register ONE IChatClient in DI. Agents never create their own client.
//       Switching to Azure OpenAI later means changing only this block.
var ollama = config.GetSection("Ollama");
builder.Services.AddChatClient(new OllamaApiClient(new Uri(ollama["Endpoint"]!), ollama["Model"]!))
    .UseOpenTelemetry(sourceName: Tel.Name,                                    // a span (and token counts) per model call
        configure: c => c.EnableSensitiveData = builder.Environment.IsDevelopment());   // prompts in traces: development only

// ── 2. OBSERVABILITY: listen to our sources + the framework's. Console exporter for the demo;
//       use AddOtlpExporter() (Aspire dashboard, Jaeger, Azure Monitor...) in production.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("ProcureStudio"))
    .WithTracing(t => t.AddSource($"{Tel.Name}*").AddSource("Microsoft.Extensions.AI*").AddConsoleExporter());

// ── 3. IDENTITY: demo authentication + CLAIMS-BASED SESSION ISOLATION.
//       The AG-UI threadId comes from the browser and is NOT a credential. Isolation partitions every stored
//       conversation by the user's NameIdentifier claim, so guessing someone's threadId gets you nothing.
builder.Services.AddHttpContextAccessor();   // tools/providers/gate read the current user through this
builder.Services.AddAuthentication(DemoAuth.Scheme)
    .AddScheme<AuthenticationSchemeOptions, DemoAuthHandler>(DemoAuth.Scheme, _ => { });
builder.Services.AddAuthorization();
builder.Services.UseClaimsBasedAgentIsolation();   // if not found: Ctrl+. and add the suggested using

// ── 4. AG-UI: the protocol that streams agent events (text, tool calls, interrupts) to the browser over SSE.
builder.Services.AddAGUIServer();

// ── 5. APP SERVICES: singletons, because they are stateless or hold shared demo data.
builder.Services.AddSingleton<PoRepository>();
builder.Services.AddSingleton<ProcureTools>();
builder.Services.AddSingleton<ToolGate>();
builder.Services.AddSingleton<UserContextProvider>();

// ── 6. MCP: start the policy server (child process, stdio) and import its tools + rules resource.
using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole());
await using var mcp = await McpConnector.ConnectAsync(config, loggerFactory.CreateLogger("Mcp"));

// ── 7. AGENTS: two keyed agents sharing one durable session store.
builder.AddProcureAgents(mcp, new FileAgentSessionStore(Path.Combine(builder.Environment.ContentRootPath, "sessions")));

var app = builder.Build();

// ── PIPELINE: order matters. Static files first (the UI needs no login), then who-are-you, then what-may-you-do.
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// ── ENDPOINTS
// One AG-UI endpoint per agent. MapAGUIServer reads the thread id, loads/creates the session from the store,
// runs the agent, streams events, and saves the session. RequireAuthorization = anonymous callers get 401.
app.MapAGUIServer(ProcureAgents.Triage,  "/ag-ui/triage").RequireAuthorization();
app.MapAGUIServer(ProcureAgents.Auditor, "/ag-ui/auditor").RequireAuthorization();

// Plain REST for the side panel (not an agent concern). The UI refreshes it after each run.
app.MapGet("/api/pos", (PoRepository repo) => repo.All()).RequireAuthorization();

app.Run();
