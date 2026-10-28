using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AGUI.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;

namespace ProcureStudio.Infrastructure;

/// <summary>
/// Shared serializer settings for chat history and session snapshots, including AG-UI approval interrupts.
/// </summary>
internal static class AgentSessionJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };
        AGUIJsonUtilities.RegisterInterruptContentTypes(options);
        return options;
    }
}

/// <summary>
/// Durable session store: one JSON file per conversation, so chats survive a server restart.
///
/// WHAT IS A SESSION?  The agent object is stateless and shared by everyone. A session holds ONE
/// conversation's state (chat history, context-provider state, pending approvals). The hosting layer asks
/// this store to load the session before a run and save it after.
///
/// WHAT IS THE KEY?  AgentSessionStoreKey = SessionId (the AG-UI threadId, supplied by the client, so it is
/// NOT trustworthy) + Partitions (added by the isolation provider, here the signed-in user's id).
/// Hashing BOTH into the file name guarantees that user B can never read user A's conversation
/// even if B guesses A's threadId.
///
/// To go to production: keep these three methods, replace File.* with SQL / Cosmos DB / Redis calls.
/// </summary>
public sealed class FileAgentSessionStore(string directory) : AgentSessionStore
{
    private string PathFor(AIAgent agent, AgentSessionStoreKey key)
    {
        var partitions = string.Join(",", (key.Partitions ?? new Dictionary<string, string>())
            .OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"));
        // agent.Id must be STABLE (we set it explicitly), otherwise a restart would look in different files.
        var raw = $"{agent.Id}|{key.SessionId}|{partitions}";
        Directory.CreateDirectory(directory);
        // Hashing also removes any path-traversal risk from client-supplied ids like "../../x".
        return Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))) + ".json");
    }

    public override async ValueTask SaveSessionAsync(AIAgent agent, AgentSessionStoreKey key, AgentSession session,
        CancellationToken cancellationToken = default)
    {
        // Always serialize through the OWNING agent so provider state (history, compaction, skills) is included.
        JsonElement state = await agent.SerializeSessionAsync(session, AgentSessionJson.Options, cancellationToken);
        await File.WriteAllTextAsync(PathFor(agent, key), state.GetRawText(), cancellationToken);
    }

    public override async ValueTask<AgentSession?> GetSessionAsync(AIAgent agent, AgentSessionStoreKey key,
        CancellationToken cancellationToken = default)
    {
        var path = PathFor(agent, key);
        if (!File.Exists(path)) return null;               // null = "new conversation"; the host creates a fresh session
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
        return await agent.DeserializeSessionAsync(doc.RootElement.Clone(), AgentSessionJson.Options, cancellationToken);
    }

    public override ValueTask DeleteSessionAsync(AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken = default)
    {
        File.Delete(PathFor(agent, key));
        return ValueTask.CompletedTask;
    }
}
