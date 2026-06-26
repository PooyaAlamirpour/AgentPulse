using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentPulse.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AgentPulseDbContext))]
[Migration("20260627120000_InitialDomainPersistence")]
partial class InitialDomainPersistence
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        InitialDomainPersistenceModel.Build(modelBuilder);
    }
}
