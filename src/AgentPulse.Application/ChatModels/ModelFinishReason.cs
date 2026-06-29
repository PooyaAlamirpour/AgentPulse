namespace AgentPulse.Application.ChatModels;

public enum ModelFinishReason
{
    Stop = 0,
    Length = 1,
    Cancelled = 2,
    Error = 3,
    Unknown = 4,
    ToolCalls = 5,
}
