using Microsoft.Data.Sqlite;
using System.Text.Json;
namespace Phoenix.Web;

public record PersonalEntry(Guid Id, string Kind, DateOnly Date, string Title, string Category,
    decimal? Amount, string? Flow, string Notes, string Vehicle, int? Odometer, DateTimeOffset CreatedAt);
public record PersonalInput(string Kind, DateOnly Date, string Title, string? Category,
    decimal? Amount, string? Flow, string? Notes, string? Vehicle, int? Odometer);

public sealed class PersonalStore
{
    private readonly string connectionString;
    private readonly SemaphoreSlim gate = new(1, 1);
    public PersonalStore()
    {
        var queue = Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "data", "server-signals.json");
        var path = Environment.GetEnvironmentVariable("PERSONAL_DATABASE_PATH")
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(queue))!, "personal.db");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
    }
    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS personal_entries(id TEXT PRIMARY KEY, payload TEXT NOT NULL)";
        await command.ExecuteNonQueryAsync(token);
        return connection;
    }
    public async Task<List<PersonalEntry>> ListAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            await using var connection = await OpenAsync(token);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM personal_entries";
            using var reader = await command.ExecuteReaderAsync(token);
            var entries = new List<PersonalEntry>();
            while (await reader.ReadAsync(token)) entries.Add(JsonSerializer.Deserialize<PersonalEntry>(reader.GetString(0))!);
            return entries.OrderByDescending(e => e.Date).ThenByDescending(e => e.CreatedAt).ToList();
        }
        finally { gate.Release(); }
    }
    public async Task SaveAsync(PersonalEntry entry, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            await using var connection = await OpenAsync(token);
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO personal_entries(id,payload) VALUES($id,$payload)";
            command.Parameters.AddWithValue("$id", entry.Id.ToString());
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(entry));
            await command.ExecuteNonQueryAsync(token);
        }
        finally { gate.Release(); }
    }
    public async Task<bool> DeleteAsync(Guid id, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            await using var connection = await OpenAsync(token);
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM personal_entries WHERE id=$id";
            command.Parameters.AddWithValue("$id", id.ToString());
            return await command.ExecuteNonQueryAsync(token) > 0;
        }
        finally { gate.Release(); }
    }
}

public static class PersonalEndpoints
{
    public static void MapPersonalEndpoints(this WebApplication app)
    {
        // Defense in depth: private data always requires the owner identity.
        var group = app.MapGroup("/api/personal").AddEndpointFilter(async (context, next) =>
        {
            if (!PhoenixSessionAuth.TryGetIdentity(context.HttpContext.Request, out var identity) || !identity.IsAdmin)
                return Results.StatusCode(403);
            return await next(context);
        });
        group.MapGet("/entries", async (PersonalStore store, CancellationToken token) => Results.Ok(await store.ListAsync(token)));
        group.MapPost("/entries", async (PersonalInput input, PersonalStore store, CancellationToken token) =>
        {
            if (input.Kind is not ("finance" or "vehicle" or "journal") || string.IsNullOrWhiteSpace(input.Title)
                || input.Title.Length > 200 || (input.Notes?.Length ?? 0) > 10000
                || (input.Category?.Length ?? 0) > 100 || (input.Vehicle?.Length ?? 0) > 100
                || input.Date == default || input.Odometer < 0 || input.Amount < 0 || input.Amount > 1000000000000000m
                || (input.Kind == "finance" && (input.Amount is null or <= 0 || input.Flow is not ("income" or "expense"))))
                return Results.BadRequest(new { error = "عنوان، تاریخ و مبلغ معتبر وارد کنید؛ مبلغ باید مثبت باشد." });
            var entry = new PersonalEntry(Guid.NewGuid(), input.Kind, input.Date, input.Title.Trim(),
                input.Category?.Trim() ?? "", input.Kind == "journal" ? null : input.Amount,
                input.Kind == "finance" ? input.Flow : null, input.Notes?.Trim() ?? "",
                input.Kind == "vehicle" ? input.Vehicle?.Trim() ?? "" : "",
                input.Kind == "vehicle" ? input.Odometer : null, DateTimeOffset.UtcNow);
            await store.SaveAsync(entry, token);
            return Results.Ok(entry);
        });
        group.MapDelete("/entries/{id:guid}", async (Guid id, PersonalStore store, CancellationToken token) =>
            await store.DeleteAsync(id, token) ? Results.NoContent() : Results.NotFound());
        group.MapGet("/export", async (PersonalStore store, CancellationToken token) =>
            Results.File(JsonSerializer.SerializeToUtf8Bytes(await store.ListAsync(token), new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
                "application/json", "personal-data.json"));
    }
}
