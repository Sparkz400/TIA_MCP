// Just the members NativeFailureText reads from the V21 Siemens.Engineering exception types.
namespace Siemens.Engineering
{
    public struct ExceptionMessageData
    {
        public string Text { get; set; }
        public string DetailText { get; set; }
    }

    public class EngineeringException : System.Exception
    {
        public EngineeringException(string message, System.Exception inner = null) : base(message, inner) { }
        public ExceptionMessageData MessageData { get; set; }
        public System.Collections.Generic.IList<ExceptionMessageData> DetailMessageData { get; set; }
    }
}
