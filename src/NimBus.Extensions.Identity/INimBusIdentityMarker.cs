namespace NimBus.Extensions.Identity;

/// <summary>
/// Marker interface registered in DI to signal that NimBus Identity is configured.
/// Startup.cs checks for this to determine the authentication mode.
/// </summary>
public interface INimBusIdentityMarker { }

internal sealed class NimBusIdentityMarker : INimBusIdentityMarker { }
