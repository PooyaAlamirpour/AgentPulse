using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AgentPulse.Infrastructure.Persistence.Interceptors;

internal sealed class SqliteForeignKeysInterceptor : DbConnectionInterceptor
{
    public static SqliteForeignKeysInterceptor Instance { get; } = new();

    private SqliteForeignKeysInterceptor()
    {
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
