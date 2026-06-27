namespace AgentPulse.Application.ModelRequests;

public enum ChatModelRequestErrorCode
{
    InvalidInput = 1,
    InvalidProjectContext = 2,
    InvalidCurrentUserRole = 3,
    InvalidCurrentUserStatus = 4,
    SessionMismatch = 5,
    DuplicateHistorySequence = 6,
    CurrentMessageSequenceConflict = 7,
    UnsupportedMessageRole = 8,
    UnsupportedMessageStatus = 9,
    UnsupportedMessagePart = 10,
    EmptyUserPrompt = 11,
    MessageHasNoUsableText = 12,
}
