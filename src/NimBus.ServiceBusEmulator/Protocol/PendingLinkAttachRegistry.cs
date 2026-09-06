using System.Runtime.CompilerServices;
using Amqp;
using Amqp.Framing;
using Amqp.Listener;

namespace NimBus.ServiceBusEmulator.Protocol;

internal sealed class PendingLinkAttachRegistry
{
    private readonly ConditionalWeakTable<Link, PendingAttach> _attaches = new();

    public PendingAttach Register(AttachContext context)
    {
        var pending = new PendingAttach(context);
        _attaches.Add(context.Link, pending);
        return pending;
    }

    public void OnRemoteClose(Link link)
    {
        if (_attaches.TryGetValue(link, out var pending))
        {
            // AMQPNetLite leaves an asynchronous attach in Start. Its OnDetach
            // rejects that state and tears down the shared session. Complete the
            // attach/detach handshake before the library processes the peer's detach.
            pending.Complete(new Error(ErrorCode.DetachForced) { Description = "The pending receiver was closed by its peer." });
        }
    }

    internal sealed class PendingAttach(AttachContext context)
    {
        private readonly object _gate = new();
        private bool _completed;

        public AttachContext Context { get; } = context;

        public bool IsPending
        {
            get
            {
                lock (_gate)
                {
                    return !_completed && !Context.Link.IsClosed;
                }
            }
        }

        public bool Complete(Error error) => Complete(() => Context.Complete(error));

        public bool Complete(LinkEndpoint endpoint) => Complete(() => Context.Complete(endpoint, 0));

        private bool Complete(Action complete)
        {
            lock (_gate)
            {
                if (_completed || Context.Link.IsClosed)
                {
                    return false;
                }

                _completed = true;
                complete();
                return true;
            }
        }
    }
}
