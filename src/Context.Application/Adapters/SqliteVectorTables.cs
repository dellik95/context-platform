using Microsoft.Data.Sqlite;

namespace Context.Application.Adapters;

/// <summary>
/// Discovers the <c>{prefix}{provider}</c> sqlite-vec tables that physically exist in a database,
/// rather than only the ones the current process has already lazily opened -- a collection created
/// by an earlier run (or by a provider this process never touched) still has to be reachable for
/// project-wide and per-entity deletes.
/// </summary>
internal static class SqliteVectorTables
{
    public static async Task<IReadOnlyList<string>> ListAsync(string connectionString, string prefix, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE $pattern ESCAPE '\\';";
        command.Parameters.AddWithValue("$pattern", $"{prefix.Replace("_", "\\_", StringComparison.Ordinal)}%");

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
