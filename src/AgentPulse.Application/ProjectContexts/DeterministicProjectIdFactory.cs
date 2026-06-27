using System.Security.Cryptography;
using System.Text;
using AgentPulse.Domain.Projects;

namespace AgentPulse.Application.ProjectContexts;

public sealed class DeterministicProjectIdFactory : IProjectIdFactory
{
    private const string IdentityVersion = "agentpulse-project-context:v1";

    public ProjectId Create(string canonicalProjectRoot, ProjectPlatform platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalProjectRoot);

        var identity = $"{IdentityVersion}:{platform}:{canonicalProjectRoot}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));

        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        var value = Convert.ToHexString(hash.AsSpan(0, 16));
        var guid = Guid.ParseExact(value, "N");
        return new ProjectId(guid);
    }
}
