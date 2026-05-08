using System.Collections.Generic;

namespace NimBus.MessageStore.States;

public class BlockedMessageEventPage
{
    public IReadOnlyList<BlockedMessageEvent> Items { get; init; } = [];

    public int Total { get; init; }
}
