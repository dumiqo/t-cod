using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace WebApi.Tests.Infrastructure;

public sealed class QueryPlanRecorder : DbCommandInterceptor
{
    private readonly List<string> _plans = [];

    public IReadOnlyList<string> Plans => _plans;

    public static IReadOnlySet<string> IndexNames(string plan)
    {
        var names = new HashSet<string>();
        Collect(JsonDocument.Parse(plan).RootElement, names);

        return names;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        var connection = (NpgsqlConnection)command.Connection!;

        await using (var off = new NpgsqlCommand("SET enable_seqscan = off", connection))
        {
            await off.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var explain = new NpgsqlCommand("EXPLAIN (FORMAT JSON) " + command.CommandText, connection))
        {
            foreach (NpgsqlParameter parameter in command.Parameters)
            {
                explain.Parameters.Add(parameter.Clone());
            }

            _plans.Add((string)(await explain.ExecuteScalarAsync(cancellationToken))!);
        }

        await using (var reset = new NpgsqlCommand("RESET enable_seqscan", connection))
        {
            await reset.ExecuteNonQueryAsync(cancellationToken);
        }

        return result;
    }

    private static void Collect(JsonElement element, HashSet<string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name == "Index Name")
                {
                    names.Add(property.Value.GetString()!);
                }

                Collect(property.Value, names);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                Collect(item, names);
            }
        }
    }
}
