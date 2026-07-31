using System;
using NimBus.Core.Messages;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Provider-neutral rules for shaping <see cref="States.BlockedMessageEvent"/> results.
/// Every storage provider must apply the same rules so operators see identical
/// blocked-event data regardless of the configured store.
/// </summary>
public static class BlockedEventRules
{
    /// <summary>
    /// Resolves the originating message id shown to operators. Messages whose
    /// <paramref name="originatingMessageId"/> is the <see cref="Constants.Self"/>
    /// placeholder originate from their own last message; everything else passes
    /// through unchanged. Null-safe on both inputs.
    /// </summary>
    public static string ResolveOriginatingId(string? originatingMessageId, string? lastMessageId)
        => string.Equals(originatingMessageId, Constants.Self, StringComparison.OrdinalIgnoreCase)
            ? lastMessageId ?? string.Empty
            : originatingMessageId ?? string.Empty;
}
