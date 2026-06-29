using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentPulse.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AgentPulseDbContext))]
[Migration("20260628180000_AddToolCallingMessages")]
partial class AddToolCallingMessages
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        ToolCallingModel.Build(modelBuilder);
    }
}
