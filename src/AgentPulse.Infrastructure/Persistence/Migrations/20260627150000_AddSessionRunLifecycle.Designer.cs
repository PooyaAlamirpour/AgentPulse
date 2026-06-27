using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentPulse.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AgentPulseDbContext))]
[Migration("20260627150000_AddSessionRunLifecycle")]
partial class AddSessionRunLifecycle
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        SessionRunLifecycleModel.Build(modelBuilder);
    }
}
