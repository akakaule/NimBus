using NimBus.Core.Endpoints;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NimBus.WebApp.Models
{
    public class EndpointViewModel
    {
        public IEndpoint Endpoint { get; set; }
        public EndpointState EndpointState { get; set; }
        public Dictionary<string, MessageContent> OriginatingMessageContents { get; set; }
        public IEnumerable<MessageEntity> FailedEvents { get; set; }
    }
}
