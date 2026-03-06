using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NimBus.WebApp.Models
{
    public class EventTypeViewModel
    {
        public IEventType EventType { get; set; }
        public string CodeRepoLink { get; set; }
        public string ExampleEventJson { get; set; }

        public IEnumerable<IEndpoint> Producers { get; set; }
        public IEnumerable<IEndpoint> Consumers { get; set; }
    }
}
