namespace NimBus.Core.Messages.PII
{
    /// <summary>
    /// How a <see cref="SensitiveAttribute"/>-annotated value is masked when it is
    /// rendered for a caller that lacks the PII-reader role.
    /// </summary>
    public enum MaskMode
    {
        /// <summary>Replace the whole value with a fixed redaction token.</summary>
        Redact = 0,

        /// <summary>
        /// Keep the last <see cref="SensitiveAttribute.Reveal"/> characters and star out
        /// the rest, so operators can still recognise a value without reading it.
        /// </summary>
        PartialReveal = 1,

        /// <summary>
        /// Replace the value with a salted SHA-256 hash, so equal inputs stay equal
        /// across records for correlation without exposing the value.
        /// </summary>
        Hash = 2,
    }
}
