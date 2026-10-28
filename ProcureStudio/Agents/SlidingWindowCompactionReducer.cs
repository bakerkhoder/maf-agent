using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

namespace ProcureStudio.Agents;

/// <summary>
/// Adapts the framework's compaction strategy to the chat-history reducer contract.
/// Keeping compaction in the history provider lets approval interrupt content use the same
/// AG-UI-aware JSON options as the rest of the persisted conversation.
/// </summary>
internal sealed class SlidingWindowCompactionReducer(CompactionStrategy strategy) : IChatReducer
{
    public Task<IEnumerable<ChatMessage>> ReduceAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken) =>
        CompactionProvider.CompactAsync(strategy, messages, cancellationToken: cancellationToken);
}
