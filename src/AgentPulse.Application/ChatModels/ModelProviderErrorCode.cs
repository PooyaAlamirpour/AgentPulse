namespace AgentPulse.Application.ChatModels;

public enum ModelProviderErrorCode
{
    Authentication = 1,
    PermissionDenied = 2,
    RateLimited = 3,
    InvalidRequest = 4,
    Unavailable = 5,
    Timeout = 6,
    Protocol = 7,
    InvalidResponse = 8,
    Cancelled = 9,
    Unknown = 10,
    UnsupportedFeature = 11,

    // Compatibility aliases retained for Phase 6 callers.
    ServiceUnavailable = Unavailable,
    ConnectionFailed = Unavailable,
    FirstByteTimeout = Timeout,
    StreamIdleTimeout = Timeout,
}
