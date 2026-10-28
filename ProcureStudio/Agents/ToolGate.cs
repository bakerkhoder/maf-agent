using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ProcureStudio.Domain;
using ProcureStudio.Infrastructure;

namespace ProcureStudio.Agents;

/// <summary>
/// FUNCTION-CALLING MIDDLEWARE: a decorator that wraps EVERY tool call the model asks for.
///
/// Principle: the model PROPOSES, the code DISPOSES. A prompt can be talked around; this code cannot.
/// Order of checks:  1) validate arguments  2) authorize  3) audit, then call the real tool via next().
/// Returning without calling next() means the tool never executes; the returned object is what the model "sees".
///
/// Note on human approval: FunctionInvokingChatClient asks the human FIRST and runs this gate when the approved
/// call executes. So the gate is defense in depth (the tool also tells the model early via routableByCurrentUser).
/// </summary>
public sealed class ToolGate(IHttpContextAccessor http, ILogger<ToolGate> log)
{
    private static readonly Regex PoFormat = new(@"^PO-\d{6}$", RegexOptions.Compiled);

    // Signature required by AIAgentBuilder.Use(...) for function-calling middleware.
    public async ValueTask<object?> InvokeAsync(AIAgent agent, FunctionInvocationContext ctx,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next, CancellationToken ct)
    {
        var tool = ctx.Function.Name;
        var user = http.HttpContext?.User;
        var role = user?.FindFirst(ClaimTypes.Role)?.Value;

        // 1. ARGUMENT VALIDATION: models hallucinate and mis-format. Parameter names match the C# method parameters.
        if (ctx.Arguments.TryGetValue("poNumber", out var po) && !PoFormat.IsMatch(po?.ToString() ?? ""))
            return Deny("INVALID_PO_NUMBER", "PO number must look like PO-123456.");

        decimal amount = 0;
        if (ctx.Arguments.TryGetValue("amount", out var amt) &&
            (!decimal.TryParse(amt?.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out amount)   // also coerces "12,000"
             || amount <= 0 || amount > 10_000_000))
            return Deny("INVALID_AMOUNT", "Amount must be a positive number up to 10,000,000.");

        // 2. AUTHORIZATION for the one tool with a side effect.
        if (tool == nameof(ProcureTools.RouteForApproval) && !Authz.CanRoute(role, amount))
            return Deny("UNAUTHORIZED_ACTION",
                $"Role '{role}' cannot route a PO of {amount:N0}. Buyers may route up to {Authz.ManagerThreshold:N0}; above that needs a manager.");

        // 3. AUDIT: one span + one log line per tool call. Arguments are NOT logged (they may be sensitive).
        using var activity = Tel.Source.StartActivity($"tool.{tool}");
        activity?.SetTag("user.id", user?.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        log.LogInformation("Tool {Tool} allowed for {User} ({Role})", tool, user?.Identity?.Name, role);

        return await next(ctx, ct);       // run the real tool
    }

    private object Deny(string code, string message)
    {
        log.LogWarning("Tool call blocked: {Code}", code);
        return new { error = code, message };
    }
}
