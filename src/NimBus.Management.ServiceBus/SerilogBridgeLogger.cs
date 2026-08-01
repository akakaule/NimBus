using Microsoft.Extensions.Logging;
using System;

namespace NimBus.Management.ServiceBus;

/// <summary>
/// Forwards Microsoft.Extensions.Logging calls to a caller-supplied Serilog
/// logger. Only used by the obsolete Serilog bridge constructors in this
/// project (ADR-006); dies with them at the next major version.
/// </summary>
internal sealed class SerilogBridgeLogger : ILogger
{
    private readonly Serilog.ILogger _serilog;

    public SerilogBridgeLogger(Serilog.ILogger serilog) => _serilog = serilog;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => _serilog.IsEnabled(ToSerilogLevel(logLevel));

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _serilog.Write(ToSerilogLevel(logLevel), exception, "{Message}", formatter(state, exception));

    private static Serilog.Events.LogEventLevel ToSerilogLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => Serilog.Events.LogEventLevel.Verbose,
        LogLevel.Debug => Serilog.Events.LogEventLevel.Debug,
        LogLevel.Information => Serilog.Events.LogEventLevel.Information,
        LogLevel.Warning => Serilog.Events.LogEventLevel.Warning,
        LogLevel.Error => Serilog.Events.LogEventLevel.Error,
        _ => Serilog.Events.LogEventLevel.Fatal,
    };
}
