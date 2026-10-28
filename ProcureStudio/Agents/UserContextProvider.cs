using System.Security.Claims;
using Microsoft.Agents.AI;

namespace ProcureStudio.Agents;

/// <summary>
/// CONTEXT PROVIDER (a.k.a. profile provider): code that runs BEFORE every agent invocation and may add
/// instructions, messages or tools to that one request. Here it injects "who is asking" so the agent can
/// address the user by name and respect their role.
///
/// Lifecycle of an AIContextProvider:
///   1. ProvideAIContextAsync  -> called before a model call; returns context that is merged into the request.
///   2. (model + tools run)
///   3. StoreAIContextAsync    -> called after the run; a place to extract additional memories.
/// The provider instance is stateless. The per-conversation counter is stored in the session StateBag,
/// rather than in this singleton, so sessions cannot leak state into one another.
/// </summary>
public sealed class UserContextProvider(IHttpContextAccessor http) : AIContextProvider
{
    private const string InvocationCountKey = "ProcureStudio.UserContextProvider.InvocationCount";
    private sealed record InvocationCount(int Value);

    protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        var user = http.HttpContext?.User;
        var name = user?.Identity?.Name ?? "an unknown user";
        var role = user?.FindFirst(ClaimTypes.Role)?.Value ?? "viewer";

        // Session-specific data belongs in the session StateBag, not this singleton provider.
        // The host serializes the session after the run, so this counter survives later requests and restarts.
        var session = context.Session
            ?? throw new InvalidOperationException("A session is required to provide conversation context.");
        var state = session.StateBag;
        var invocationCount = state.TryGetValue<InvocationCount>(InvocationCountKey, out var previousCount)
            && previousCount is not null
            ? previousCount.Value + 1
            : 1;
        state.SetValue(InvocationCountKey, new InvocationCount(invocationCount));

        return new ValueTask<AIContext>(new AIContext
        {
            Instructions = $"The signed-in user is {name} with role '{role}'. Address them by name. " +
                           $"Never suggest actions their role does not allow. This is context-provider invocation " +
                           $"{invocationCount} for this conversation."
        });
    }
}
