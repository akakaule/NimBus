using System;

namespace NimBus.Core.Messages.PII
{
    /// <summary>
    /// Marks an event property — or an entire event class, applying to every property
    /// it declares — as carrying personally identifiable information. The management
    /// WebApp masks annotated values for callers without the PII-reader role, leaving
    /// the rest of the payload readable.
    /// </summary>
    /// <remarks>
    /// Applied to a class or a complex property, the marking is inherited by every leaf
    /// beneath it, including nested objects and collection elements.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
    public sealed class SensitiveAttribute : Attribute
    {
        /// <summary>How the value is masked. Defaults to <see cref="MaskMode.Redact"/>.</summary>
        public MaskMode Mode { get; set; } = MaskMode.Redact;

        /// <summary>
        /// Number of trailing characters left visible under <see cref="MaskMode.PartialReveal"/>.
        /// Must be greater than zero for that mode; other modes ignore it.
        /// </summary>
        public int Reveal { get; set; }
    }
}
