#pragma warning disable CA1707

using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using NimBus.Core.Events;
using NimBus.WebApp.Controllers;

namespace NimBus.WebApp.Tests
{
    /// <summary>
    /// The Event Types page shows the example authored on the event class. The
    /// mapper carries it to the client as <c>examplePayload</c>; without it the
    /// client can only render property type names.
    /// </summary>
    [TestClass]
    public class EventTypeExamplePayloadMappingTests
    {
        public class AuthoredEvent : NimBus.Core.Events.Event
        {
            public static readonly AuthoredEvent Example = new()
            {
                CustomerId = Guid.Parse("9c1a6f2e-4c31-4f0e-9a1c-2f7d5b3e8a10"),
                Name = "Nordvest Logistik A/S",
            };

            public Guid CustomerId { get; set; }
            public string Name { get; set; } = string.Empty;
        }

        public class UnauthoredEvent : NimBus.Core.Events.Event
        {
            public Guid CustomerId { get; set; }
        }

        [TestMethod]
        public void Authored_example_is_serialized_as_indented_json()
        {
            var mapped = Mapper.EventTypeFromIEventType(new EventType(typeof(AuthoredEvent)));

            Assert.IsNotNull(mapped.ExamplePayload);
            var json = JObject.Parse(mapped.ExamplePayload);
            Assert.AreEqual("9c1a6f2e-4c31-4f0e-9a1c-2f7d5b3e8a10", (string?)json["CustomerId"]);
            Assert.AreEqual("Nordvest Logistik A/S", (string?)json["Name"]);
            Assert.IsTrue(mapped.ExamplePayload.Contains('\n'), "expected indented output");
        }

        [TestMethod]
        public void Counted_overload_carries_the_example_too()
        {
            var mapped = Mapper.EventTypeFromIEventType(new EventType(typeof(AuthoredEvent)), 1, 2);

            Assert.IsNotNull(mapped.ExamplePayload);
        }

        [TestMethod]
        public void Missing_example_maps_to_null()
        {
            var mapped = Mapper.EventTypeFromIEventType(new EventType(typeof(UnauthoredEvent)));

            Assert.IsNull(mapped.ExamplePayload);
        }
    }
}
