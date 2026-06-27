namespace AgentPulse.Application.ChatModels;

public enum ModelProviderErrorCode
{
    Authentication = 1,
    RateLimited = 2,
    ServiceUnavailable = 3,
    ConnectionFailed = 4,
    FirstByteTimeout = 5,
    StreamIdleTimeout = 6,
    InvalidResponse = 7,
    UnsupportedFeature = 8,
    Unknown = 9,
}
