using NimBus.Core.Messages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Extensions
{
    /// <summary>
    /// Wraps an <see cref="IMessageHandler"/> with pipeline behaviors (middleware).
    /// Behaviors execute in registration order, each wrapping the next.
    /// </summary>
    public class MessagePipeline
    {
        private readonly IReadOnlyList<IMessagePipelineBehavior> _behaviors;

        public MessagePipeline(PipelineBehaviorRegistry registry, IServiceProvider serviceProvider)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            if (serviceProvider == null) throw new ArgumentNullException(nameof(serviceProvider));

            _behaviors = registry.BehaviorTypes
                .Select(t => (IMessagePipelineBehavior)serviceProvider.GetService(t))
                .Where(b => b != null)
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Returns true if any pipeline behaviors are registered.
        /// </summary>
        public bool HasBehaviors => _behaviors.Count > 0;

        /// <summary>
        /// Executes the pipeline behaviors around the given terminal handler.
        /// </summary>
        /// <param name="context">The message context.</param>
        /// <param name="terminalHandler">The actual message handler to invoke at the end of the pipeline.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public Task Execute(IMessageContext context, MessagePipelineDelegate terminalHandler, CancellationToken cancellationToken = default)
        {
            if (!HasBehaviors)
            {
                return terminalHandler(context, cancellationToken);
            }

            // Build the chain from the inside out: terminal -> last behavior -> ... -> first behavior
            MessagePipelineDelegate chain = terminalHandler;

            for (int i = _behaviors.Count - 1; i >= 0; i--)
            {
                var behavior = _behaviors[i];
                var next = chain;
                chain = (ctx, ct) => behavior.Handle(ctx, next, ct);
            }

            return chain(context, cancellationToken);
        }
    }
}
