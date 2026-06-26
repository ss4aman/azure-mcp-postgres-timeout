// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Data.Common;
using Npgsql;

namespace Azure.Mcp.Tools.Postgres.Providers;

internal class DbProvider : IDbProvider
{
    public async Task<IPostgresResource> GetPostgresResource(string connectionString, string authType, CancellationToken cancellationToken)
    {
        return await PostgresResource.CreateAsync(connectionString, authType, cancellationToken);
    }

    public NpgsqlCommand GetCommand(string query, IPostgresResource postgresResource, int? commandTimeoutSeconds = null)
    {
        var command = new NpgsqlCommand(query, postgresResource.Connection);

        // Resolve the per-command timeout (in seconds) with the following precedence:
        //   1. The explicit per-call value (e.g. supplied by the agent as 'command-timeout').
        //   2. The AZURE_MCP_POSTGRES_COMMAND_TIMEOUT environment variable (server-wide default).
        //   3. A default of 300 seconds (5 minutes) when neither is set.
        // A value of 0 disables the timeout (waits indefinitely), which lets long-running
        // queries (e.g. exact count(*) over large tables) avoid "Exception while reading
        // from stream" timeouts.
        const int DefaultCommandTimeoutSeconds = 300;
        int? resolvedTimeout = commandTimeoutSeconds;
        if (resolvedTimeout is null)
        {
            var timeoutValue = Environment.GetEnvironmentVariable("AZURE_MCP_POSTGRES_COMMAND_TIMEOUT");
            resolvedTimeout = int.TryParse(timeoutValue, out var envTimeout)
                ? envTimeout
                : DefaultCommandTimeoutSeconds;
        }

        if (resolvedTimeout is int timeout && timeout >= 0)
        {
            command.CommandTimeout = timeout;
        }

        return command;
    }

    public async Task<DbDataReader> ExecuteReaderAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        return await command.ExecuteReaderAsync(cancellationToken);
    }
}
