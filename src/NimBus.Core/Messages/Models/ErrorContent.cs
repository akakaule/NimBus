namespace NimBus.Core.Messages
{
    public class ErrorContent
    {
        public string ErrorText { get; set; }
        public string ErrorType { get; set; }
        public string ExceptionStackTrace { get; set; }
        public string ExceptionSource { get; internal set; }
    }
}
