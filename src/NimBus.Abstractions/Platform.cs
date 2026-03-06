using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using System.Collections.Generic;
using System.Linq;

namespace NimBus.Core
{
    public interface IPlatform
    {
        /// <summary>
        /// Endpoints connected to the platform.
        /// </summary>
        IEnumerable<IEndpoint> Endpoints { get; }

        IEnumerable<IEventType> EventTypes { get; }

        IEnumerable<IEndpoint> GetConsumers(IEventType eventType);

        IEnumerable<IEndpoint> GetProducers(IEventType eventType);
    }

    public abstract class Platform : IPlatform
    {
        private Dictionary<string, IEndpoint> _endpoints;

        public Platform()
        {
            _endpoints = new Dictionary<string, IEndpoint>();
        }

        protected void AddEndpoint(IEndpoint endpoint)
        {
            _endpoints.Add(endpoint.Id, endpoint);
        }

        public IEnumerable<IEndpoint> Endpoints => _endpoints.Values;

        public IEnumerable<IEventType> EventTypes =>
            Endpoints
            .SelectMany(ep => ep.EventTypesConsumed.Union(ep.EventTypesProduced))
            .Distinct();

        public IEnumerable<IEndpoint> GetConsumers(IEventType eventType) =>
            Endpoints
            .Where(ep => ep.EventTypesConsumed.Contains(eventType));

        public IEnumerable<IEndpoint> GetProducers(IEventType eventType) =>
            Endpoints
            .Where(ep => ep.EventTypesProduced.Contains(eventType));
    }
}
