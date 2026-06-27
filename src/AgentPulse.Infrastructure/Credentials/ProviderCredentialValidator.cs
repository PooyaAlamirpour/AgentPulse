namespace AgentPulse.Infrastructure.Credentials;

public static class ProviderCredentialValidator
{
    public static string ValidateAndNormalize(string? credential)
    {
        if (credential is null || credential.Length == 0)
        {
            throw new ProviderCredentialValidationException();
        }

        if (credential.Any(static character =>
                char.IsControl(character) ||
                (char.IsWhiteSpace(character) && character != ' ')))
        {
            throw new ProviderCredentialValidationException();
        }

        var normalized = credential.Trim(' ');
        if (normalized.Length == 0)
        {
            throw new ProviderCredentialValidationException();
        }

        return normalized;
    }
}
