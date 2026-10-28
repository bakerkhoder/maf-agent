using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using ProcureStudio.Infrastructure;

namespace ProcureStudio.Agents;

/// <summary>
/// Composition of the two agents. Everything an agent is made of, in the order a request flows through it:
///
///   request ──► [agent middleware: OpenTelemetry span] ──► [function middleware: ToolGate]
///           ──► [context layer: history + UserContext + Skills; history reducer: Compaction] ──► [IChatClient: Ollama]
///
/// DECORATOR vs INHERITANCE: we never subclass ChatClientAgent to add behaviour. AsBuilder().Use(...).Build()
/// WRAPS the agent in delegating decorators (each forwards to the one inside). Cross-cutting concerns
/// (telemetry, validation, logging) stay separate, reorderable and reusable across agents.
/// </summary>
public static class ProcureAgents
{
    // Stable names double as: DI keys, AG-UI endpoint targets, and (via Id) session-store file keys.
    public const string Triage = "ProcurementTriageAgent";
    public const string Auditor = "ProcurementPolicyAuditor";

    public static void AddProcureAgents(this WebApplicationBuilder builder, McpConnection mcp, AgentSessionStore store)
    {
        // ───────── Agent 1: the policy auditor (its knowledge comes from the MCP server) ─────────
        builder.AddAIAgent(Auditor, (sp, key) =>
        {
            var agent = sp.GetRequiredService<IChatClient>().AsAIAgent(new ChatClientAgentOptions
            {
                Id = key,                 // STABLE id (see FileAgentSessionStore)
                Name = key,
                Description = "Answers procurement policy questions: approval tiers, quotes and rules.",  // used when this agent is exposed as a tool
                ChatOptions = new ChatOptions
                {
                    Instructions = $"""
                        You answer procurement policy questions briefly and cite the rule you used.
                        Use the GetApprovalPolicy tool for approvers and quote requirements; never guess numbers.
                        Full written policy (loaded from the MCP server resource):
                        {mcp.RulesText ?? "(policy server unavailable; say so if asked)"}
                        """,
                    Tools = [.. mcp.Tools],   // MCP tools become ordinary agent tools
                },
            });
            return agent.AsBuilder().UseOpenTelemetry(Tel.Name).Build();
        }).WithSessionStore(store);          // hosting pulls/saves this agent's sessions from our durable store

        // ───────── Agent 2: the triage agent (what the user mostly talks to) ─────────
        builder.AddAIAgent(Triage, (sp, key) =>
        {
            var tools = sp.GetRequiredService<ProcureTools>();
            var auditor = sp.GetRequiredKeyedService<AIAgent>(Auditor);   // resolve the other agent by key

            var agent = sp.GetRequiredService<IChatClient>().AsAIAgent(new ChatClientAgentOptions
            {
                Id = key,
                Name = key,
                ChatOptions = new ChatOptions
                {
                    Instructions = """
                        You are the procurement triage assistant for a government agency.
                        1. Use tools for ALL PO data. Never invent PO numbers, amounts or statuses.
                        2. To review a PO, load the 'po-review' skill and follow it.
                        3. Use 'approval-thresholds' for approval tiers and 'vendor-quote-checklist' for quote checks.
                        4. Remember which PO is under discussion: "approve it" or "that one" refers to it.
                        5. Call RouteForApproval only after the user explicitly asks to approve or route, and only when
                           ValidateBudgetCap returned withinCap=true and routableByCurrentUser=true.
                        6. For policy questions, call the ProcurementPolicyAuditor tool.
                        7. If a tool returns an error, explain it plainly and say how to fix it. Keep answers short.
                        """,
                    Tools =
                    [
                        AIFunctionFactory.Create(tools.GetPurchaseOrder),
                        AIFunctionFactory.Create(tools.ValidateBudgetCap),

                        // HUMAN-IN-THE-LOOP: wrapping a tool makes the run PAUSE before it executes. Over AG-UI the run
                        // ends with an "interrupt"; the browser shows an approval card and resumes with the decision.
                        new ApprovalRequiredAIFunction(AIFunctionFactory.Create(tools.RouteForApproval)),

                        // AGENT AS A TOOL: the auditor becomes a function the triage agent can call.
                        auditor.AsAIFunction(),
                    ],
                },

                // INTERRUPT CONTENT: approvals add AG-UI-specific AIContent to chat history, so both history and
                // the durable session store use the same registered serializer to round-trip it.
                ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
                {
                    JsonSerializerOptions = AgentSessionJson.Options,
                    // COMPACTION: run the bounded sliding-window strategy before history retrieval. The built-in
                    // CompactionProvider caches message groups with default JSON options; the reducer keeps this
                    // state under the configured serializer so custom AG-UI interrupt content remains supported.
                    ChatReducer = new SlidingWindowCompactionReducer(
                        new SlidingWindowCompactionStrategy(CompactionTriggers.TurnsExceed(12))),
                    ReducerTriggerEvent = InMemoryChatHistoryProviderOptions.ChatReducerTriggerEvent.BeforeMessagesRetrieval,
                }),

                // The CONTEXT LAYER. These run before every model call, in this order.
                AIContextProviders =
                [
                    sp.GetRequiredService<UserContextProvider>(),           // 1. who is asking (custom provider)

                    // 2. SKILLS: combine file-based, inline, and class-based definitions. Discovery stays small;
                    //    the full skill instructions/resources load only when the model selects a relevant skill.
                    //    This demo fails closed for file-skill scripts rather than executing arbitrary files.
                    new AgentSkillsProviderBuilder()
                        .UseFileScriptRunner((skill, script, arguments, services, cancellationToken) =>
                            throw new NotSupportedException("File-based skill scripts are disabled in this demo."))
                        .UseOptions(options =>
                        {
                            options.DisableLoadSkillApproval = true;
                            options.DisableReadSkillResourceApproval = true;
                        })
                        .UseFileSkill(Path.Combine(AppContext.BaseDirectory, "skills"))
                        .UseSkills(ProcurementSkills.CreateInline(), new VendorQuoteChecklistSkill())
                        .Build(),
                ],
                // This explicitly configured in-memory provider stores history INSIDE the session, which is exactly
                // what the session store persists. Swap in a Cosmos/SQL ChatHistoryProvider for external history.
            });

            return agent.AsBuilder()
                .Use(sp.GetRequiredService<ToolGate>().InvokeAsync)   // function middleware: gate every tool call
                .UseOpenTelemetry(Tel.Name)                           // outermost: one span per agent run
                .Build();
        }).WithSessionStore(store);
    }
}
