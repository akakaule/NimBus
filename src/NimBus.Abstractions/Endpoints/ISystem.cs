namespace NimBus.Core.Endpoints
{
    /// <summary>
    /// Info about integrating systems.
    /// </summary>
    public interface ISystem
    {
        /// <summary>
        /// Company identifier for the system.
        /// </summary>
        string SystemId { get; }
    }
}
