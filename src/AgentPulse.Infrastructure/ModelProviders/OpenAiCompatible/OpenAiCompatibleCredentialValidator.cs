using AgentPulse.Application.ChatModels;

namespace AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;

internal static class OpenAiCompatibleCredentialValidator
{
    public static string ValidateAndNormalize(string? credential)
    {
        if (credential is null)
        {
            throw CreateException();
        }

        if (credential.Any(static character =>
                character is <= '\u001F' or '\u007F'))
        {
            throw CreateException();
        }

        var normalized = credential.Trim();
        if (normalized.Length == 0 || normalized.Any(char.IsWhiteSpace))
        {
            throw CreateException();
        }

        return normalized;
    }

    private static ModelProviderException CreateException()
    {
        return new ModelProviderException(
            ModelProviderErrorCode.Authentication,
            "The configured API credential is empty or contains invalid whitespace or control characters.",
            ModelFailureStage.BeforeFirstToken);
    }
}
