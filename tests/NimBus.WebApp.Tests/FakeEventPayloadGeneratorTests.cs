#pragma warning disable CA1707, CA2007

using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NimBus.Core.Events;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests
{
    [TestClass]
    public class FakeEventPayloadGeneratorTests
    {
        private FakeEventPayloadGenerator _gen = null!;

        [TestInitialize]
        public void Setup() => _gen = new FakeEventPayloadGenerator();

        // ---- Test event definitions --------------------------------------

        public enum SampleEnum
        {
            None,
            Alpha,
            Beta,
            Gamma,
        }

        public class SimpleEvent : Event
        {
            public static readonly SimpleEvent Example = new()
            {
                CustomerId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Email = "alex@example.com",
                FullName = "Alex Example",
            };

            public Guid CustomerId { get; set; }
            public string Email { get; set; } = string.Empty;
            public string FullName { get; set; } = string.Empty;
        }

        public class StringLengthEvent : Event
        {
            public static readonly StringLengthEvent Example = new()
            {
                Code = "abc",
            };

            // Heuristic for "Code" is digit-string. We force a short max so the
            // clamp is observable regardless of heuristic length.
            [StringLength(4)]
            public string Code { get; set; } = string.Empty;
        }

        public class NullableEnumEvent : Event
        {
            public static readonly NullableEnumEvent Example = new()
            {
                Origin = SampleEnum.Alpha,
            };

            public SampleEnum? Origin { get; set; }
        }

        public class RegexImpossibleEvent : Event
        {
            public static readonly RegexImpossibleEvent Example = new()
            {
                MagicWord = "MAGIC",
            };

            // No random heuristic will ever produce literal "MAGIC", so every
            // random attempt fails validation and we fall back to the example.
            [Required]
            [RegularExpression("^MAGIC$")]
            public string MagicWord { get; set; } = string.Empty;
        }

        // No public Example field is exposed. The generator should construct
        // a bare instance and populate every leaf type-correctly, then
        // return the latest attempt because validation never passes.
        public class NoExampleNeverValidatesEvent : Event
        {
            // No `static T Example` field.

            [Required]
            [RegularExpression("^MAGIC$")]
            public string MagicWord { get; set; } = string.Empty;
        }

        public abstract class AbstractEvent : Event
        {
            public string Anything { get; set; } = string.Empty;
        }

        // ---- Tests ---------------------------------------------------------

        [TestMethod]
        public void Generate_ReturnsNull_ForAbstractType()
        {
            var eventType = new EventType(typeof(AbstractEvent));

            var result = _gen.Generate(eventType);

            Assert.IsNull(result);
        }

        [TestMethod]
        public void Generate_ReturnsValidJson_WhenExamplePresentAndNoValidation()
        {
            var eventType = new EventType(typeof(SimpleEvent));

            var result = _gen.Generate(eventType);

            Assert.IsNotNull(result);
            // Must round-trip as the same CLR type.
            var roundTrip = JsonConvert.DeserializeObject<SimpleEvent>(result!);
            Assert.IsNotNull(roundTrip);
            Assert.AreNotEqual(Guid.Empty, roundTrip!.CustomerId);
            Assert.IsFalse(string.IsNullOrEmpty(roundTrip.Email));
        }

        [TestMethod]
        public void Generate_RespectsStringLength_ClampsLongHeuristicValues()
        {
            var eventType = new EventType(typeof(StringLengthEvent));

            for (var i = 0; i < 10; i++)
            {
                var result = _gen.Generate(eventType);
                Assert.IsNotNull(result);
                var doc = JObject.Parse(result!);
                var code = (string)doc["Code"]!;
                Assert.IsNotNull(code);
                Assert.IsTrue(code.Length <= 4, $"Iteration {i}: '{code}' is longer than 4 characters.");
            }
        }

        [TestMethod]
        public void Generate_ProducesEnumMember_ForNullableEnum_NotHeuristicString()
        {
            var eventType = new EventType(typeof(NullableEnumEvent));

            // Try a few iterations to assert across random draws.
            for (var i = 0; i < 25; i++)
            {
                var result = _gen.Generate(eventType);
                Assert.IsNotNull(result);
                var doc = JObject.Parse(result!);
                var raw = doc["Origin"];
                Assert.IsNotNull(raw);
                // Newtonsoft serializes enums as numeric by default — the
                // value must be in [0..3] (one of the declared members).
                var asInt = raw!.Value<int>();
                Assert.IsTrue(asInt >= 0 && asInt <= 3, $"Iteration {i}: Origin out of range: {asInt}");
            }
        }

        [TestMethod]
        public void Generate_FallsBackToExample_WhenRandomizationNeverValidates()
        {
            var eventType = new EventType(typeof(RegexImpossibleEvent));

            var result = _gen.Generate(eventType);

            Assert.IsNotNull(result);
            var doc = JObject.Parse(result!);
            // The fallback path returns the example serialized verbatim — so
            // the regex-constrained property must equal "MAGIC".
            Assert.AreEqual("MAGIC", (string)doc["MagicWord"]!);
        }

        [TestMethod]
        public void Generate_ReturnsLatestRandomAttempt_WhenNoExampleAndValidationNeverPasses()
        {
            var eventType = new EventType(typeof(NoExampleNeverValidatesEvent));

            var result = _gen.Generate(eventType);

            // No example is authored — the generator must still return a
            // non-null populated payload (best-effort).
            Assert.IsNotNull(result);
            var doc = JObject.Parse(result!);
            // Even though it doesn't validate, the property is populated.
            Assert.IsTrue(doc.ContainsKey("MagicWord"));
            var magic = (string?)doc["MagicWord"];
            Assert.IsFalse(string.IsNullOrEmpty(magic));
        }

        [TestMethod]
        public async Task Generate_IsConcurrentSafe_TwoParallelCallsProduceTwoPayloads()
        {
            var eventType = new EventType(typeof(SimpleEvent));

            // Fan out a handful of parallel calls; they must all complete
            // successfully (no shared mutable state collisions) and produce
            // payloads with at least one distinct leaf value across the set.
            var tasks = Enumerable.Range(0, 8)
                .Select(_ => Task.Run(() => _gen.Generate(eventType)))
                .ToArray();
            var results = await Task.WhenAll(tasks);

            foreach (var r in results) Assert.IsNotNull(r);
            // Across 8 random draws on Guid + email + name, the probability
            // of all-identical is vanishingly small; assert at least one
            // leaf-set fingerprint differs from another.
            var distinct = results.Distinct().Count();
            Assert.IsTrue(distinct >= 2, "Concurrent calls produced identical payloads — shared Random state suspected.");
        }
    }
}
