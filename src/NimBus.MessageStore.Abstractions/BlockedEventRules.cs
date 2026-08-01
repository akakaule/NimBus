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
    /// True when <paramref name="originatingMessageId"/> is the
    /// <see cref="Constants.Self"/> placeholder — the message originated from
    /// its own publish rather than a forwarded/replayed parent. Case-insensitive
    /// and null-safe: the single platform-wide definition of "self", matching
    /// the OrdinalIgnoreCase comparisons in ResponseService/PublisherClient.
    /// </summary>
    public static bool IsSelfOriginating(string? originatingMessageId)
        => string.Equals(originatingMessageId, Constants.Self, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the originating message id shown to operators. Messages whose
    /// <paramref name="originatingMessageId"/> is the <see cref="Constants.Self"/>
    /// placeholder originate from their own last message; everything else passes
    /// through unchanged. Null-safe on both inputs.
    /// </summary>
    public static string ResolveOriginatingId(string? originatingMessageId, string? lastMessageId)
        => IsSelfOriginating(originatingMessageId)
            ? lastMessageId ?? string.Empty
            : originatingMessageId ?? string.Empty;
}
