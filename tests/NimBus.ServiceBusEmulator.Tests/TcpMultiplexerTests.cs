#pragma warning disable CA1707, CA2007

using NimBus.ServiceBusEmulator.Hosting;

namespace NimBus.ServiceBusEmulator.Tests;

[TestClass]
public sealed class TcpMultiplexerTests
{
    [TestMethod]
    [DataRow("AMQP\u0003\u0001\u0000\u0000", 2)]
    [DataRow("GET /health HTTP/1.1\r\n", 3)]
    [DataRow("PUT /topic HTTP/1.1\r\n", 3)]
    public void Classifier_accepts_only_exact_supported_prefixes(string prefix, int expected)
    {
        Assert.AreEqual((FrontendProtocol)expected, ProtocolClassifier.Classify(System.Text.Encoding.ASCII.GetBytes(prefix)));
    }

    [TestMethod]
    [DataRow("Z")]
    [DataRow("AMQX\u0003\u0001\u0000\u0000")]
    [DataRow("TRACE / HTTP/1.1\r\n")]
    public void Classifier_rejects_invalid_prefixes(string prefix)
    {
        Assert.AreEqual(FrontendProtocol.Invalid, ProtocolClassifier.Classify(System.Text.Encoding.ASCII.GetBytes(prefix)));
    }

    [TestMethod]
    [DataRow("AMQ")]
    [DataRow("GE")]
    public void Classifier_waits_for_a_complete_prefix(string prefix)
    {
        Assert.AreEqual(FrontendProtocol.Incomplete, ProtocolClassifier.Classify(System.Text.Encoding.ASCII.GetBytes(prefix)));
    }
}
