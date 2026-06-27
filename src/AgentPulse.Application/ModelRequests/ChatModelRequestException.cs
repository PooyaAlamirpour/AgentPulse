namespace AgentPulse.Application.ModelRequests;

public sealed class ChatModelRequestException : Exception
{
    public ChatModelRequestException(ChatModelRequestErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public ChatModelRequestException(
        ChatModelRequestErrorCode errorCode,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public ChatModelRequestErrorCode ErrorCode { get; }
}
