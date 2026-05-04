namespace NimBus.Core.Messages
{
    /// <summary>
    /// Failure detail carried inside <see cref="MessageContent"/> on error responses.
    /// Promoted into <c>NimBus.Transport.Abstractions</c> so transport adapters can
    /// surface error metadata without depending on <c>NimBus.Core</c>; namespace
    /// stays <c>NimBus.Core.Messages</c> with a <c>[TypeForwardedTo]</c> in
    /// <c>NimBus.Core</c> preserving existing using directives.
    /// </summary>
    public class ErrorContent
    {
        public string ErrorText { get; set; }
        public string? ErrorType { get; set; }
        public string ExceptionStackTrace { get; set; }

        /// <remarks>
        /// Setter widened from <c>internal</c> to <c>public</c> as part of the
        /// transport-abstractions promotion: the type now lives in a different
        /// assembly than <c>NimBus.Core</c>, so the original assembly-internal
        /// access boundary no longer reaches the Core call site that initializes
        /// it. The property is a publicly readable diagnostic field; widening the
        /// setter is a backwards-compatible change for consumers.
        /// </remarks>
        public string? ExceptionSource { get; set; }
    }
}
