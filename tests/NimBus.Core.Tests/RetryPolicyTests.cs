#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.Core.Messages;

namespace NimBus.Core.Tests;

[TestClass]
public class RetryPolicyTests
{
    [TestMethod]
    public void GetDelay_DefaultJitter_PreservesExistingBackoffValues()
    {
        var fixedPolicy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Fixed,
            BaseDelay = TimeSpan.FromSeconds(5),
        };
        var linearPolicy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Linear,
            BaseDelay = TimeSpan.FromSeconds(5),
        };
        var exponentialPolicy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Exponential,
            BaseDelay = TimeSpan.FromSeconds(5),
        };
        var cappedPolicy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Exponential,
            BaseDelay = TimeSpan.FromSeconds(5),
            MaxDelay = TimeSpan.FromMinutes(1),
        };

        Assert.AreEqual(JitterMode.None, fixedPolicy.Jitter);
        Assert.AreEqual(0.25, fixedPolicy.BoundedJitterFactor, 0.000001);
        var random = new FixedRandom(0.5);
        Assert.AreEqual(TimeSpan.FromSeconds(5), fixedPolicy.GetDelay(3, random));
        Assert.AreEqual(0, random.NextDoubleCalls);
        Assert.AreEqual(TimeSpan.FromSeconds(15), linearPolicy.GetDelay(2));
        Assert.AreEqual(TimeSpan.FromSeconds(40), exponentialPolicy.GetDelay(3));
        Assert.AreEqual(TimeSpan.FromMinutes(1), cappedPolicy.GetDelay(6));
        Assert.AreEqual(2, new RetryPolicy { BaseDelay = TimeSpan.FromSeconds(90) }.GetDelayMinutes(0));
    }

    [TestMethod]
    public void GetDelay_FullJitter_StaysWithinExpectedRange()
    {
        var baseDelay = TimeSpan.FromSeconds(10);
        var policy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Fixed,
            BaseDelay = baseDelay,
            Jitter = JitterMode.Full,
        };
        var random = new Random(12345);

        Assert.AreEqual(TimeSpan.FromSeconds(15), policy.GetDelay(0, new FixedRandom(0.5)));
        Assert.IsTrue(policy.GetDelay(0, new FixedRandom(Math.BitDecrement(1.0))) < baseDelay * 2);

        for (var sample = 0; sample < 100; sample++)
        {
            var delay = policy.GetDelay(0, random);

            Assert.IsTrue(delay >= baseDelay, $"Sample {sample} was below the base delay: {delay}.");
            Assert.IsTrue(delay < baseDelay * 2, $"Sample {sample} reached or exceeded twice the base delay: {delay}.");
        }
    }

    [TestMethod]
    public void GetDelay_BoundedJitter_StaysWithinConfiguredRange()
    {
        var baseDelay = TimeSpan.FromSeconds(10);
        const double factor = 0.4;
        var policy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Fixed,
            BaseDelay = baseDelay,
            Jitter = JitterMode.Bounded,
            BoundedJitterFactor = factor,
        };
        var random = new Random(54321);
        var upperBound = baseDelay * (1 + factor);

        Assert.AreEqual(TimeSpan.FromSeconds(12), policy.GetDelay(0, new FixedRandom(0.5)));
        Assert.IsTrue(policy.GetDelay(0, new FixedRandom(Math.BitDecrement(1.0))) < upperBound);

        for (var sample = 0; sample < 100; sample++)
        {
            var delay = policy.GetDelay(0, random);

            Assert.IsTrue(delay >= baseDelay, $"Sample {sample} was below the base delay: {delay}.");
            Assert.IsTrue(delay < upperBound, $"Sample {sample} reached or exceeded the bounded jitter range: {delay}.");
        }
    }

    [TestMethod]
    public void GetDelay_MaxDelayCapsAfterJitter()
    {
        var baseDelay = TimeSpan.FromSeconds(10);
        var maxDelay = TimeSpan.FromSeconds(12);
        var uncappedPolicy = new RetryPolicy
        {
            BaseDelay = baseDelay,
            Jitter = JitterMode.Full,
        };
        var cappedPolicy = new RetryPolicy
        {
            BaseDelay = baseDelay,
            Jitter = JitterMode.Full,
            MaxDelay = maxDelay,
        };
        var boundedCappedPolicy = new RetryPolicy
        {
            BaseDelay = baseDelay,
            Jitter = JitterMode.Bounded,
            BoundedJitterFactor = 0.5,
            MaxDelay = maxDelay,
        };

        Assert.AreEqual(TimeSpan.FromSeconds(15), uncappedPolicy.GetDelay(0, new FixedRandom(0.5)));
        Assert.AreEqual(maxDelay, cappedPolicy.GetDelay(0, new FixedRandom(0.5)));
        Assert.AreEqual(maxDelay, boundedCappedPolicy.GetDelay(0, new FixedRandom(0.5)));
    }

    [TestMethod]
    public void NewtonsoftSerialization_PreservesLegacyDefaultsAndJitterSettings()
    {
        const string legacyJson = "{\"MaxRetries\":3,\"Strategy\":2,\"BaseDelay\":\"00:00:05\",\"MaxDelay\":\"00:01:00\"}";

        var legacyPolicy = JsonConvert.DeserializeObject<RetryPolicy>(legacyJson);

        Assert.IsNotNull(legacyPolicy);
        Assert.AreEqual(3, legacyPolicy.MaxRetries);
        Assert.AreEqual(BackoffStrategy.Exponential, legacyPolicy.Strategy);
        Assert.AreEqual(TimeSpan.FromSeconds(5), legacyPolicy.BaseDelay);
        Assert.AreEqual(TimeSpan.FromMinutes(1), legacyPolicy.MaxDelay);
        Assert.AreEqual(JitterMode.None, legacyPolicy.Jitter);
        Assert.AreEqual(0.25, legacyPolicy.BoundedJitterFactor, 0.000001);

        var configuredPolicy = new RetryPolicy
        {
            Jitter = JitterMode.Bounded,
            BoundedJitterFactor = 0.4,
        };
        var roundTrippedPolicy = JsonConvert.DeserializeObject<RetryPolicy>(
            JsonConvert.SerializeObject(configuredPolicy));

        Assert.IsNotNull(roundTrippedPolicy);
        Assert.AreEqual(JitterMode.Bounded, roundTrippedPolicy.Jitter);
        Assert.AreEqual(0.4, roundTrippedPolicy.BoundedJitterFactor, 0.000001);
    }

    private sealed class FixedRandom : Random
    {
        private readonly double _value;

        public FixedRandom(double value)
        {
            _value = value;
        }

        public int NextDoubleCalls { get; private set; }

        public override double NextDouble()
        {
            NextDoubleCalls++;
            return _value;
        }
    }
}
