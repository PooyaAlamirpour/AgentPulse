using AgentPulse.Domain.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentPulse.Infrastructure.Persistence.Configurations;

internal sealed class TextMessagePartConfiguration : IEntityTypeConfiguration<TextMessagePart>
{
    public void Configure(EntityTypeBuilder<TextMessagePart> builder)
    {
        builder.Property(part => part.Text)
            .IsRequired(false);
    }
}
