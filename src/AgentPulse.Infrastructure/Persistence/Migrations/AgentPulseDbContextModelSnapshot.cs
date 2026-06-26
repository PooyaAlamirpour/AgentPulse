using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace AgentPulse.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AgentPulseDbContext))]
partial class AgentPulseDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        InitialDomainPersistenceModel.Build(modelBuilder);
    }
}
