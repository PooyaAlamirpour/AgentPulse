namespace AgentPulse.Application.Permissions;

public sealed class PermissionStoreException : Exception
{
    public PermissionStoreException(string message)
        : base(message)
    {
    }

    public PermissionStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
