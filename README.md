# ProcureStudio: a complete Microsoft Agent Framework application

A procurement assistant that reviews purchase orders, asks a human to approve routing, and answers policy
questions. Model inference runs locally through Ollama. The app is designed to be taught: the code comments
explain the implementation and this document maps each part to the 13-video series.

```
Browser (wwwroot/index.html, AG-UI client)
   │  POST /ag-ui/triage   (SSE stream of events back)          GET /api/pos
   ▼
ASP.NET Core ── Demo auth (headers) ─► ClaimsPrincipal ─► session isolation by user
   │
   ├─ MapAGUIServer("ProcurementTriageAgent") ─► loads session from FileAgentSessionStore
   │      agent middleware: OpenTelemetry span
   │      function middleware: ToolGate (validate + authorize + audit)
   │      context layer: UserContextProvider → mixed AgentSkillsProvider
   │      history: InMemoryChatHistoryProvider → sliding-window compaction reducer
   │      tools: GetPurchaseOrder, ValidateBudgetCap, RouteForApproval (needs human OK), Auditor-as-tool
   │      IChatClient (Ollama, with OpenTelemetry)
   │
   └─ MapAGUIServer("ProcurementPolicyAuditor") ─► tools from the MCP server
                                                      │ stdio (stdin/stdout JSON-RPC, logs on stderr)
                                                      ▼
                                           PolicyMcpServer (tool + resource + prompt)
```

## 1. Run it

```powershell
# one-time: a tool-capable local model
ollama pull qwen3:8b            # or llama3.1:8b (faster: qwen3 "thinks" first)

# build BOTH projects first. The app launches the MCP server with --no-build, so it must already be compiled.
cd ProcureStudio
dotnet build PolicyMcpServer
dotnet build ProcureStudio

cd ProcureStudio
dotnet run                      # then open http://localhost:5300
```

The console prints `MCP connected: 1 tools [GetApprovalPolicy], 1 prompts`. If it prints "MCP server unavailable", the
app still runs, only without policy tools.

### If the compiler complains
| Symptom | Fix |
|---|---|
| `UseClaimsBasedAgentIsolation` not found | Confirm `Program.cs` imports `Microsoft.Agents.AI.Hosting` and package restore completed. |
| Error code `MAAI00x` / `MEAI00x` | Add the code to `<NoWarn>` in `ProcureStudio.csproj`. |
| TargetFramework not installed | Change `net9.0` to an installed SDK/runtime target in both project files. |
| Package restore/build fails | Restore the exact package versions pinned in the project files and keep both projects on the same target framework. |

## 2. Demo script (5 minutes)

1. **Sign in as baker (buyer)**, click **PO-100001**, send. Watch the tool chips: `load_skill` → `GetPurchaseOrder` → `ValidateBudgetCap` → auditor. *(Skills, tools, agent-as-tool, MCP.)*
2. Say **"approve it"** (no PO number). An **approval slip** appears. Click **Approve and route**. The PO panel flips to `Pending (Department Head)`. *(Sessions, human-in-the-loop.)*
3. Pick **PO-100003 (85,000)** as baker. The assistant says a manager is needed *before* asking for approval (`routableByCurrentUser=false`). *(Authorization.)*
4. Switch to **maya (manager)**, same PO: approval slip, approve, routed to the CFO.
5. Stop the server (Ctrl+C), start it again, keep the tab open, and continue the same chat: the history survived. *(Durable session store.)*
6. Switch to **Policy auditor**, ask "Who approves a 30,000 order?". *(MCP tool.)*
7. Look at the server console: one span per agent run, model call and tool. *(OpenTelemetry.)*
8. Open `requests.http` to show the raw AG-UI events, and test #4: another user with the same `threadId` gets an empty conversation. *(Isolation.)*

## 3. Topic map: where each video topic lives in the code

| # | Video topic | Coverage and entry point |
|---|---|---|
| 1 | Fundamentals, workflows, run types | `Day1Agent/Program.cs` demonstrates `RunAsync` and `RunStreamingAsync`; `Day4Workflows/Program.cs` demonstrates Sequential and LoopAgent workflows; `ProcureAgents.cs` composes the web agents |
| 2 | Chat options vs run options, response/update types, content types | Appendix A shows agent defaults, per-run options, typed response content, and streaming updates |
| 3 | Sessions, StateBag, compaction pipelines and triggers, profile providers | `FileAgentSessionStore.cs`, the persisted per-session counter in `UserContextProvider.cs`, and the sliding-window history reducer in `ProcureAgents.cs` / `SlidingWindowCompactionReducer.cs` |
| 4 | Decorator vs inheritance, middleware, context providers, skills | `ProcureAgents.cs`, `ToolGate.cs`, `UserContextProvider.cs`, and the three skill styles under `skills/` and `Agents/ProcurementSkills.cs` |
| 5 | Tool calling, `AIFunctionFactory`, content types | `ProcureTools.cs` defines functions; Appendix A shows typed response contents |
| 6 | Human-in-the-loop approvals | `ApprovalRequiredAIFunction` in `ProcureAgents.cs` and the approval slip in `wwwroot/index.html` |
| 7 | File-based, inline, and class-based skills | All three are loaded together by `AgentSkillsProviderBuilder` in `ProcureAgents.cs` |
| 8 | Chat completions vs the Responses API | Appendix C compares them; the live app uses Ollama's chat-completions-style client |
| 9 | MCP agents, tools, primitives, local and remote connections | Local stdio tools/resources/prompts are implemented in `PolicyMcpServer/` and `McpConnector.cs`; remote HTTP is a documented alternative |
| 10 | MCP stdin/stdout/stderr | `PolicyMcpServer/Program.cs` documents the stdio protocol and sends logs to stderr |
| 11 | Agent loop | `Day4Workflows/Program.cs` demonstrates LoopAgent; the integrated app uses the framework's tool-calling loop |
| 12 | Harness and goal-oriented agent loop | Appendix D explains the harness; ProcureStudio composes the loop, skills, compaction, approvals, and middleware explicitly |
| 13 | Generic/web hosting, AG-UI, session stores | `Program.cs` exposes hosted agents through AG-UI; the earlier console sample is `Day1Agent/Program.cs` |

The local end-to-end project implements the main path for all 13 topics. Cloud-backed history, the Responses API,
remote MCP authentication, and alternate A2A/OpenAI-compatible endpoints remain documented options because they
require additional services or provider credentials; they are not silently presented as working integrations.

## 4. Request lifecycle (explain it in this order)

1. The browser POSTs a **run** to `/ag-ui/triage`: `threadId`, a new `runId`, and only the **new** message.
2. **Authentication** builds the user from headers. **Isolation** adds the user id as a partition to the session key.
3. The host asks the **session store** for the session (null means a new conversation).
4. The agent pipeline runs: **OpenTelemetry** span → **context providers** (user, skills) → **chat-history reducer** (sliding-window compaction) → **model**.
5. The model asks for a tool. **ToolGate** validates arguments and authorizes, then the tool runs.
6. For `RouteForApproval` the run **pauses**: the stream ends with `RUN_FINISHED` carrying an **interrupt**. If the interrupt omits function metadata, the browser correlates its `toolCallId` with the earlier tool-call events to show the action and exact arguments on the approval slip.
7. The browser shows the **approval slip**. The decision is POSTed to the same endpoint as a **`resume`** array.
8. The tool runs (or is skipped if rejected), the model writes the final answer, and the host **saves the session**. The chat-history provider and `FileAgentSessionStore` share AG-UI-aware JSON options so paused approval messages can round-trip with chat history.

The wire events, in order, for a normal run: `RUN_STARTED`, `TEXT_MESSAGE_START/CONTENT/END`, `TOOL_CALL_START/ARGS/END/RESULT`, `RUN_FINISHED`.

## 5. Design decisions worth defending

- **The model proposes, code disposes.** Authorization is code (`Authz` + `ToolGate`), never a prompt instruction.
- **One source of truth** for a rule (`Authz.CanRoute`), used by both the tool and the gate.
- **Tools return errors as data** (`{ error, message }`), so the model can explain them and the run is not aborted.
- **Agents are stateless singletons; sessions hold state.** That is why one agent safely serves every user.
- **Threads are not credentials.** The `threadId` comes from the client, so conversations are partitioned by the authenticated user.
- **Optional dependencies degrade.** MCP down means no policy tools, not a crashed app.
- **Skills use progressive disclosure.** Only name and description are always in context; the body loads on demand.
- **Approval-aware compaction.** The sliding-window strategy runs as a chat-history reducer with the same AG-UI-aware JSON options as the persisted history, so interrupt content can round-trip during resume.
- **One telemetry name everywhere.** A mismatch fails silently.
- **Configuration, not constants.** Model and MCP command come from `appsettings.json`; there are no secrets (Ollama needs none).
- **Package versions are pinned.** The learning project uses exact versions so restores are reproducible; hosting APIs are still preview/evaluation APIs.

## 6. Known limitations (say these before someone asks)

- **Demo auth trusts headers.** Anyone can claim to be a manager. Replace `DemoAuthHandler` with JWT/Entra for real use.
- **Approval is asked before the gate runs**, so an unauthorized user could be shown a slip and then denied. The tool already warns the model early (`routableByCurrentUser`); conditional approval policies would close the gap completely.
- **In-memory PO data** stands in for SQL Server. `FileAgentSessionStore` stands in for Cosmos/SQL/Redis.
- **Small local models** can skip tool calls or loop. If `qwen3:8b` misbehaves, try `llama3.1:8b` or a larger model.
- **Preview hosting APIs** can change between releases. Package versions are pinned for reproducible restores, but upgrade them deliberately.
- The AG-UI approval payload is sent with both `accepted` and `approved` keys because the documentation shows both spellings.

## Appendix A: options, responses and content types (copy into a console app)

```csharp
IChatClient chat = new OllamaApiClient(new Uri("http://localhost:11434"), "qwen3:8b");

// Agent-level defaults.
AIAgent agent = chat.AsAIAgent(new ChatClientAgentOptions
{
    ChatOptions = new ChatOptions { Instructions = "Be brief.", Temperature = 0.2f },
});

// Per-call options are MERGED with the agent-level ones before reaching the IChatClient.
var perCall = new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0.9f, MaxOutputTokens = 150 });
var response = await agent.RunAsync("Name three purchase order risks.", options: perCall);

Console.WriteLine(response.Text);                          // convenience: all text joined
foreach (var message in response.Messages)                 // the real structure: messages made of typed contents
    foreach (var content in message.Contents)
        switch (content)
        {
            case TextContent t:           Console.WriteLine($"text: {t.Text}"); break;
            case FunctionCallContent f:   Console.WriteLine($"model asked for {f.Name}"); break;
            case FunctionResultContent r: Console.WriteLine($"tool answered {r.Result}"); break;
            case UsageContent u:          Console.WriteLine($"tokens: {u.Details.TotalTokenCount}"); break;
        }

// Streaming gives the same content types, in small pieces (AgentResponseUpdate).
await foreach (var update in agent.RunStreamingAsync("Explain DI in one line."))
    foreach (var c in update.Contents.OfType<TextContent>()) Console.Write(c.Text);
```

## Appendix B: the three kinds of skill

- **File-based**: a folder with `SKILL.md` (required; frontmatter `name` and `description`), plus optional `references/`, `assets/`, `scripts/`. See `skills/po-review/`.
- **Inline (code-defined)**: `ProcurementSkills.CreateInline()` defines short guidance and a resource in C#.
- **Class-based**: `VendorQuoteChecklistSkill` derives from `AgentClassSkill<T>` and exposes a typed resource.
`ProcureAgents.cs` combines all three with `AgentSkillsProviderBuilder`. The model can discover skill names first,
then load the full instructions or resources when they are relevant. The demo's file-skill script runner fails closed;
the read-only load/resource tools are auto-approved, while script execution remains approval-gated and disabled.
Configure a reviewed, trusted runner before enabling executable skill scripts, and trust only reviewed skill content.

## Appendix C: chat completions vs the Responses API

With **chat completions** (Ollama, and `ChatClient` for OpenAI) the service is stateless: the framework resends the history on every call, so *you* own history and compaction (our `CompactionProvider`). The **Responses API** can keep the conversation on the service side (a stored response/conversation id), plus richer built-in tools. Agent Framework supports both; with Responses you can still keep history local by disabling stored output (`AsIChatClientWithStoredOutputDisabled`). Ollama speaks chat completions, so this project uses that model.

## Appendix D: the harness

A **harness agent** (`HarnessAgent`) is a pre-assembled agent for long, goal-driven work: it bundles the agent loop, a skills provider, compaction sized to the model's context window, tool-approval rules, and background agents. Our project assembles the same ingredients by hand, which is better for learning. Use the harness when you want those defaults without wiring them yourself.

## Appendix E: optional integrations and concepts not enabled in the local demo

- **Cosmos DB chat history**: no Cosmos-backed `ChatHistoryProvider` is configured. It requires a Cosmos account, database/container configuration, and a compatible provider.
- **Summarization compaction**: the live agent uses bounded sliding-window compaction. `SummarizationCompactionStrategy` is an alternative when a summary of older turns is preferable.
- **Responses API**: this app uses Ollama's chat-completions-style `IChatClient`; service-managed Responses conversations require a compatible provider and its credentials.
- **Remote MCP**: the live example uses stdio. Configure an HTTP MCP transport only when the remote endpoint and authentication are available.
- **A2A / OpenAI-compatible hosting**: the app exposes AG-UI as requested. Other protocol adapters are separate hosting surfaces and are not registered in the local demo.
