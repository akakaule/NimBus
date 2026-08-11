using System.Runtime.CompilerServices;
using Amqp;

namespace NimBus.ServiceBusEmulator.Protocol;

internal sealed class SessionLinkRegistry
{
    private readonly ConditionalWeakTable<Connection, ConnectionLinks> _connections = new();

    public void Register(Connection connection, string linkName, string owner) =>
        _connections.GetOrCreateValue(connection).Register(linkName, owner);

    public string GetOwner(Connection connection, string? linkName)
    {
        if (string.IsNullOrWhiteSpace(linkName) ||
            !_connections.TryGetValue(connection, out var links) ||
            !links.TryGetOwner(linkName, out var owner))
        {
            throw new KeyNotFoundException("The associated session receiver link is not active.");
        }

        return owner;
    }

    public void Unregister(Connection connection, string linkName, string owner)
    {
        if (_connections.TryGetValue(connection, out var links))
        {
            links.Unregister(linkName, owner);
        }
    }

    private sealed class ConnectionLinks
    {
        private readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal);

        public void Register(string linkName, string owner)
        {
            lock (_owners)
            {
                _owners[linkName] = owner;
            }
        }

        public bool TryGetOwner(string linkName, out string owner)
        {
            lock (_owners)
            {
                return _owners.TryGetValue(linkName, out owner!);
            }
        }

        public void Unregister(string linkName, string owner)
        {
            lock (_owners)
            {
                if (_owners.TryGetValue(linkName, out var current) &&
                    string.Equals(current, owner, StringComparison.Ordinal))
                {
                    _owners.Remove(linkName);
                }
            }
        }
    }
}
