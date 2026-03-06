using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;

namespace NimBus.Core.Events
{
    public class Heartbeat : Event
    {
        public static Heartbeat Example = new Heartbeat()
        {
            ForwardSendTime = DateTime.Now,
            ForwardReceivedTime = DateTime.Now,
            BackwardSendTime = DateTime.Now,
            BackwardReceivedTime = DateTime.Now,
            Endpoint = "Alice"
        };

        [Description("The time the heartbeat is sent out in forward propagation")]
        public DateTime ForwardSendTime { get; set; }

        [Description("The time the heartbeat is received in forward propagation")]
        public DateTime ForwardReceivedTime { get; set; }

        [Description("The time the heartbeat is sent in backward propagation")]
        public DateTime BackwardSendTime { get; set; }

        [Description("The time the heartbeat is received in backward propagation")]
        public DateTime BackwardReceivedTime { get; set; }

        [Description("Targeted endpoint")]
        public string Endpoint { get; set; }
    }
}
