using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Text.Json;
namespace Phoenix.Web;

public sealed class PersonalStore
{
    private readonly string connectionString;
    public string AttachmentDirectory { get; }
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool initialized;
    public PersonalStore()
    {
        var queue = Environment.GetEnvironmentVariable("PHOENIX_QUEUE_PATH") ?? Path.Combine(AppContext.BaseDirectory, "data", "server-signals.json");
        var path = Environment.GetEnvironmentVariable("PERSONAL_DATABASE_PATH") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(queue))!, "personal.db");
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        AttachmentDirectory = Path.Combine(directory, "personal-attachments");
        Directory.CreateDirectory(AttachmentDirectory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
    }
    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(token);
        if (!initialized)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS personal_entries(id TEXT PRIMARY KEY, payload TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS personal_versions(id TEXT PRIMARY KEY, revision INTEGER NOT NULL, deleted INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE IF NOT EXISTS personal_history(id TEXT NOT NULL, revision INTEGER NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(id,revision));
                CREATE TABLE IF NOT EXISTS personal_attachments(id TEXT PRIMARY KEY, payload TEXT NOT NULL);
                """;
            await command.ExecuteNonQueryAsync(token);
            initialized = true;
        }
        return connection;
    }
    private static async Task<List<PersonalEntry>> ReadAsync(SqliteConnection connection, bool trash, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT e.payload, COALESCE(v.revision,1) FROM personal_entries e LEFT JOIN personal_versions v ON v.id=e.id WHERE COALESCE(v.deleted,0)=$deleted";
        command.Parameters.AddWithValue("$deleted", trash ? 1 : 0);
        using var reader = await command.ExecuteReaderAsync(token);
        var entries = new List<PersonalEntry>();
        while (await reader.ReadAsync(token)) entries.Add(JsonSerializer.Deserialize<PersonalEntry>(reader.GetString(0), PersonalRules.Json)! with { Revision = reader.GetInt32(1) });
        return entries.OrderByDescending(e => e.Date).ThenByDescending(e => e.CreatedAt).ToList();
    }
    public async Task<List<PersonalEntry>> ListAsync(CancellationToken token, bool trash = false)
    {
        await gate.WaitAsync(token);
        try { await using var connection = await OpenAsync(token); return await ReadAsync(connection, trash, token); }
        finally { gate.Release(); }
    }
    private static async Task WriteAsync(SqliteConnection connection, SqliteTransaction transaction, PersonalEntry entry, bool deleted, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO personal_entries(id,payload) VALUES($id,$payload) ON CONFLICT(id) DO UPDATE SET payload=$payload;
            INSERT INTO personal_versions(id,revision,deleted) VALUES($id,$revision,$deleted) ON CONFLICT(id) DO UPDATE SET revision=$revision,deleted=$deleted;
            INSERT OR IGNORE INTO personal_history(id,revision,payload) VALUES($id,$revision,$payload);
            """;
        command.Parameters.AddWithValue("$id", entry.Id.ToString());
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(entry, PersonalRules.Json));
        command.Parameters.AddWithValue("$revision", entry.Revision);
        command.Parameters.AddWithValue("$deleted", deleted ? 1 : 0);
        await command.ExecuteNonQueryAsync(token);
    }
    public async Task<PersonalEntry> CreateAsync(PersonalInput input, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            await using var connection = await OpenAsync(token);
            var entries = await ReadAsync(connection, false, token);
            var trash = await ReadAsync(connection, true, token);
            var id = input.ClientId ?? Guid.NewGuid();
            var entry = PersonalRules.Build(input, id);
            var existing = entries.Concat(trash).FirstOrDefault(e => e.Id == id);
            if (existing is not null)
            {
                // Retrying the same offline operation cannot create another record or resurrect a deleted one.
                if (!trash.Any(e => e.Id == id) && JsonSerializer.Serialize(PersonalRules.Input(existing), PersonalRules.Json) == JsonSerializer.Serialize(PersonalRules.Input(entry), PersonalRules.Json)) return existing;
                throw new PersonalConflict("این شناسه قبلاً با اطلاعات دیگری ثبت شده است؛ نسخهٔ سرور را بررسی کنید.", existing);
            }
            await CheckLinksAsync(connection, entry, entries, token);
            using var transaction = connection.BeginTransaction();
            await WriteAsync(connection, transaction, entry, false, token);
            await transaction.CommitAsync(token);
            return entry;
        }
        finally { gate.Release(); }
    }
    // Retained for existing migrations and regression fixtures.
    public async Task SaveAsync(PersonalEntry entry, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            await using var connection = await OpenAsync(token);
            using var transaction = connection.BeginTransaction();
            await WriteAsync(connection, transaction, entry, false, token);
            await transaction.CommitAsync(token);
        }
        finally { gate.Release(); }
    }
    public async Task<PersonalEntry?> UpdateAsync(Guid id, PersonalInput input, int revision, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            await using var connection = await OpenAsync(token);
            var entries = await ReadAsync(connection, false, token);
            var previous = entries.FirstOrDefault(e => e.Id == id);
            if (previous is null) return null;
            var desired = PersonalRules.Build(input, id, previous.CreatedAt, previous.Revision + 1);
            if (previous.Revision != revision)
            {
                if (JsonSerializer.Serialize(PersonalRules.Input(previous), PersonalRules.Json) == JsonSerializer.Serialize(PersonalRules.Input(desired), PersonalRules.Json)) return previous;
                throw new PersonalConflict("این ثبت در جای دیگری تغییر کرده است؛ نسخهٔ جدید را بررسی کنید.", previous);
            }
            if (previous.Kind != input.Kind) throw new PersonalConflict("نوع ثبت قابل تغییر نیست.", previous);
            if (previous.Kind == "decision" && previous.Details is { } old && desired.Details is { } next &&
                (old.Hypothesis != next.Hypothesis || old.Alternatives != next.Alternatives || old.ExpectedOutcome != next.ExpectedOutcome || old.ReviewDate != next.ReviewDate || previous.Date != desired.Date))
                throw new PersonalConflict("دلیل، گزینه‌ها و انتظار اولیهٔ تصمیم حفظ می‌شوند؛ نتیجه را در قسمت مرور ثبت کنید.", previous);
            await CheckLinksAsync(connection, desired, entries.Where(e => e.Id != id).ToList(), token);
            using var transaction = connection.BeginTransaction();
            // Save legacy revision 1 as well before its first edit.
            using var history = connection.CreateCommand(); history.Transaction = transaction;
            history.CommandText = "INSERT OR IGNORE INTO personal_history(id,revision,payload) VALUES($id,$revision,$payload)";
            history.Parameters.AddWithValue("$id", id.ToString()); history.Parameters.AddWithValue("$revision", previous.Revision);
            history.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(previous, PersonalRules.Json)); await history.ExecuteNonQueryAsync(token);
            await WriteAsync(connection, transaction, desired, false, token);
            await transaction.CommitAsync(token);
            return desired;
        }
        finally { gate.Release(); }
    }
    private static async Task CheckLinksAsync(SqliteConnection connection, PersonalEntry entry, List<PersonalEntry> entries, CancellationToken token)
    {
        if (entry.Details is not { } d) return;
        foreach (var link in new[] { (d.GoalId, "goal"), (d.ProjectId, "project"), (entry.Kind == "habit-log" ? d.ParentId : null, "habit") })
            if (link.Item1 is { } id && (id == entry.Id || !entries.Any(e => e.Id == id && e.Kind == link.Item2)))
                throw new PersonalConflict("هدف، پروژه یا عادت مرتبط موجود نیست.");
        if (entry.Kind == "category")
        {
            if (d.ParentId is { } parent && (parent == entry.Id || !entries.Any(e => e.Id == parent && e.Kind == "category" && e.Category == entry.Category)))
                throw new PersonalConflict("دستهٔ بالادست معتبر نیست.");
            if (entries.Any(e => e.Kind == "category" && e.Category == entry.Category && e.Title == entry.Title))
                throw new PersonalConflict("این دسته از قبل هست.");
            var ancestor = d.ParentId; var visited = new HashSet<Guid> { entry.Id };
            while (ancestor is { } id)
            {
                if (!visited.Add(id)) throw new PersonalConflict("دسته‌ها نمی‌توانند چرخه داشته باشند.");
                ancestor = entries.FirstOrDefault(e => e.Id == id)?.Details?.ParentId;
            }
        }
        if (entry.Kind == "task" && d.Important && d.Status != "cancelled" && entries.Count(e => e.Kind == "task" && e.Date == entry.Date && e.Details?.Important == true && e.Details.Status != "cancelled") >= 3)
            throw new PersonalConflict("برای هر روز حداکثر سه کار مهم انتخاب کنید.");
        if (entry.Kind == "checkin" && entries.Any(e => e.Kind == "checkin" && e.Date == entry.Date))
            throw new PersonalConflict("وضعیت این روز قبلاً ثبت شده؛ همان ثبت را ویرایش کنید.");
        if (entry.Kind == "habit-log" && entries.Any(e => e.Kind == "habit-log" && e.Date == entry.Date && e.Details?.ParentId == d.ParentId))
            throw new PersonalConflict("انجام این عادت در این روز قبلاً ثبت شده است.");
        if (entry.Kind == "time" && d.StartedAt is not null && d.EndedAt is null && d.Status != "cancelled" && entries.Any(e => e.Kind == "time" && e.Details?.StartedAt is not null && e.Details.EndedAt is null && e.Details.Status != "cancelled"))
            throw new PersonalConflict("یک زمان‌سنج فعال دارید؛ ابتدا آن را پایان دهید.");
        foreach (var attachmentId in d.AttachmentIds)
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM personal_attachments WHERE id=$id";
            command.Parameters.AddWithValue("$id", attachmentId.ToString());
            if ((long)(await command.ExecuteScalarAsync(token))! == 0) throw new PersonalConflict("یکی از پیوست‌ها هنوز بارگذاری نشده است.");
        }
    }
    public async Task<bool> DeleteAsync(Guid id, CancellationToken token, int? revision = null) => await SetDeletedAsync(id, true, revision, token);
    public async Task<bool> RestoreAsync(Guid id, int revision, CancellationToken token) => await SetDeletedAsync(id, false, revision, token);
    private async Task<bool> SetDeletedAsync(Guid id, bool deleted, int? revision, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            await using var connection = await OpenAsync(token);
            var records = await ReadAsync(connection, !deleted, token);
            var entry = records.FirstOrDefault(e => e.Id == id);
            if (entry is null) return false;
            if (revision.HasValue && entry.Revision != revision) throw new PersonalConflict("نسخهٔ ثبت تغییر کرده؛ ابتدا همگام‌سازی کنید.", entry);
            if (deleted && records.Any(e => e.Id != id && (e.Details?.GoalId == id || e.Details?.ProjectId == id || e.Details?.ParentId == id)))
                throw new PersonalConflict("این ثبت ارتباط فعال دارد؛ آن را بایگانی کنید یا ابتدا ارتباط‌ها را بردارید.", entry);
            if (!deleted) await CheckLinksAsync(connection, entry, await ReadAsync(connection, false, token), token);
            using var transaction = connection.BeginTransaction();
            await WriteAsync(connection, transaction, entry with { Revision = entry.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow }, deleted, token);
            await transaction.CommitAsync(token);
            return true;
        }
        finally { gate.Release(); }
    }
    public async Task<List<PersonalEntry>> HistoryAsync(Guid id, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            await using var connection = await OpenAsync(token);
            using var command = connection.CreateCommand(); command.CommandText = "SELECT payload FROM personal_history WHERE id=$id ORDER BY revision DESC";
            command.Parameters.AddWithValue("$id", id.ToString()); using var reader = await command.ExecuteReaderAsync(token);
            var result = new List<PersonalEntry>();
            while (await reader.ReadAsync(token)) result.Add(JsonSerializer.Deserialize<PersonalEntry>(reader.GetString(0), PersonalRules.Json)!);
            return result;
        }
        finally { gate.Release(); }
    }
    public static bool ValidMedia(string mime, byte[] bytes) => mime.Split(';')[0] switch
    {
        "image/jpeg" => bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255,
        "image/png" => bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "image/webp" => bytes.Length >= 12 && System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP",
        "audio/webm" => bytes.Length >= 4 && bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 26, 69, 223, 163 }),
        "audio/ogg" => bytes.Length >= 4 && System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "OggS",
        "audio/mp4" => bytes.Length >= 12 && System.Text.Encoding.ASCII.GetString(bytes, 4, 4) == "ftyp",
        "audio/mpeg" => bytes.Length >= 3 && (System.Text.Encoding.ASCII.GetString(bytes, 0, 3) == "ID3" || bytes[0] == 255 && (bytes[1] & 224) == 224),
        "audio/wav" => bytes.Length >= 12 && System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(bytes, 8, 4) == "WAVE",
        _ => false
    };
    public async Task<PersonalAttachment> AddAttachmentAsync(Guid id, string name, string mime, byte[] bytes, CancellationToken token)
    {
        if (bytes.Length == 0 || bytes.Length > 16 * 1024 * 1024 || !ValidMedia(mime, bytes)) throw new ArgumentException("فقط عکس و صدای معتبر تا ۱۶ مگابایت مجاز است.");
        await gate.WaitAsync(token);
        try
        {
            await using var connection = await OpenAsync(token);
            var previous = await ReadAttachmentAsync(connection, id, token);
            if (previous is not null)
            {
                var original = await File.ReadAllBytesAsync(Path.Combine(AttachmentDirectory, id.ToString("N")), token);
                if (!original.AsSpan().SequenceEqual(bytes)) throw new PersonalConflict("شناسهٔ پیوست با فایل دیگری استفاده شده است.");
                return previous;
            }
            var metadata = new PersonalAttachment(id, Path.GetFileName(name)[..Math.Min(Path.GetFileName(name).Length, 160)], mime, bytes.LongLength, DateTimeOffset.UtcNow);
            var path = Path.Combine(AttachmentDirectory, id.ToString("N"));
            await File.WriteAllBytesAsync(path, bytes, token);
            try
            {
                using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO personal_attachments(id,payload) VALUES($id,$payload)";
                command.Parameters.AddWithValue("$id", id.ToString()); command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(metadata, PersonalRules.Json));
                await command.ExecuteNonQueryAsync(token);
            }
            catch { File.Delete(path); throw; }
            return metadata;
        }
        finally { gate.Release(); }
    }
    private static async Task<PersonalAttachment?> ReadAttachmentAsync(SqliteConnection connection, Guid id, CancellationToken token)
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT payload FROM personal_attachments WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.ToString()); var payload = await command.ExecuteScalarAsync(token) as string;
        return payload is null ? null : JsonSerializer.Deserialize<PersonalAttachment>(payload, PersonalRules.Json);
    }
    public async Task<PersonalAttachment?> AttachmentAsync(Guid id, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { await using var connection = await OpenAsync(token); return await ReadAttachmentAsync(connection, id, token); }
        finally { gate.Release(); }
    }
    public async Task<byte[]> BackupAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            await using var connection = await OpenAsync(token);
            var entries = await ReadAsync(connection, false, token);
            var ids = entries.SelectMany(e => e.Details?.AttachmentIds ?? []).Distinct().ToArray();
            var attachments = new List<PersonalAttachment>();
            foreach (var id in ids) if (await ReadAttachmentAsync(connection, id, token) is { } attachment) attachments.Add(attachment);
            if (attachments.Sum(a => a.Size) > 128 * 1024 * 1024) throw new ArgumentException("حجم پیوست‌های پشتیبان بیشتر از ۱۲۸ مگابایت است؛ از پشتیبان سرور استفاده کنید.");
            using var memory = new MemoryStream();
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                await using (var manifest = archive.CreateEntry("personal.json").Open())
                    await JsonSerializer.SerializeAsync(manifest, new PersonalBackup(2, DateTimeOffset.UtcNow, entries.ToArray(), attachments.ToArray()), PersonalRules.Json, token);
                foreach (var attachment in attachments)
                {
                    await using var destination = archive.CreateEntry("attachments/" + attachment.Id.ToString("N"), CompressionLevel.NoCompression).Open();
                    await using var source = File.OpenRead(Path.Combine(AttachmentDirectory, attachment.Id.ToString("N")));
                    await source.CopyToAsync(destination, token);
                }
            }
            return memory.ToArray();
        }
        finally { gate.Release(); }
    }
    public async Task<(int Added, int Skipped)> ImportAsync(byte[] bytes, CancellationToken token)
    {
        using var memory = new MemoryStream(bytes); using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
        if (archive.Entries.Sum(e => e.Length) > 128 * 1024 * 1024 || archive.Entries.Count > 20000) throw new ArgumentException("حجم پشتیبان بیش از حد است.");
        var manifestEntry = archive.GetEntry("personal.json") ?? throw new ArgumentException("فایل پشتیبان معتبر نیست.");
        if (manifestEntry.Length > 16 * 1024 * 1024) throw new ArgumentException("فهرست پشتیبان بیش از حد بزرگ است.");
        await using var manifest = manifestEntry.Open();
        var backup = await JsonSerializer.DeserializeAsync<PersonalBackup>(manifest, PersonalRules.Json, token) ?? throw new ArgumentException("پشتیبان معتبر نیست.");
        if (backup.Version != 2 || backup.Entries is null || backup.Attachments is null || backup.Entries.Length > 10000 || backup.Entries.Select(e => e.Id).Distinct().Count() != backup.Entries.Length || backup.Attachments.Select(a => a.Id).Distinct().Count() != backup.Attachments.Length)
            throw new ArgumentException("نسخه یا محتوای پشتیبان معتبر نیست.");
        foreach (var entry in backup.Entries)
            if (entry.Id == Guid.Empty || entry.Revision < 1 || PersonalRules.Validate(PersonalRules.Input(entry)) is not null) throw new ArgumentException("یکی از ثبت‌های پشتیبان معتبر نیست.");
        var media = new Dictionary<Guid, byte[]>();
        foreach (var attachment in backup.Attachments)
        {
            var item = archive.GetEntry("attachments/" + attachment.Id.ToString("N")) ?? throw new ArgumentException("یکی از پیوست‌ها موجود نیست.");
            if (item.Length != attachment.Size || item.Length > 16 * 1024 * 1024) throw new ArgumentException("اندازهٔ پیوست معتبر نیست.");
            using var file = new MemoryStream(); await using var source = item.Open(); await source.CopyToAsync(file, token); var payload = file.ToArray();
            if (!ValidMedia(attachment.ContentType, payload)) throw new ArgumentException("نوع پیوست معتبر نیست.");
            media.Add(attachment.Id, payload);
        }
        await gate.WaitAsync(token);
        var written = new List<string>();
        try
        {
            await using var connection = await OpenAsync(token);
            var active = await ReadAsync(connection, false, token); var trash = await ReadAsync(connection, true, token);
            var existing = active.Concat(trash).Select(e => e.Id).ToHashSet();
            var added = backup.Entries.Where(e => !existing.Contains(e.Id)).ToArray();
            var all = active.Concat(added).ToList();
            // Validate all references before writing anything. Existing records are never overwritten.
            foreach (var e in added)
            {
                var d = e.Details; if (d is null) continue;
                foreach (var link in new[] { (d.GoalId, "goal"), (d.ProjectId, "project"), (e.Kind == "habit-log" ? d.ParentId : null, "habit") })
                    if (link.Item1 is { } id && (id == e.Id || !all.Any(r => r.Id == id && r.Kind == link.Item2))) throw new ArgumentException("یکی از ارتباط‌های پشتیبان موجود نیست.");
                if (e.Kind == "category")
                {
                    var ancestor = d.ParentId; var visited = new HashSet<Guid> { e.Id };
                    while (ancestor is { } parent)
                    {
                        var category = all.FirstOrDefault(r => r.Id == parent && r.Kind == "category" && r.Category == e.Category);
                        if (category is null || !visited.Add(parent)) throw new ArgumentException("دستهٔ بالادست معتبر نیست.");
                        ancestor = category.Details?.ParentId;
                    }
                }
                foreach (var id in d.AttachmentIds)
                    if (!media.ContainsKey(id) && await ReadAttachmentAsync(connection, id, token) is null) throw new ArgumentException("یکی از پیوست‌های ثبت موجود نیست.");
            }
            if (all.Where(e => e.Kind == "task" && e.Details?.Important == true && e.Details.Status != "cancelled").GroupBy(e => e.Date).Any(g => g.Count() > 3)
                || all.Where(e => e.Kind == "checkin").GroupBy(e => e.Date).Any(g => g.Count() > 1)
                || all.Where(e => e.Kind == "habit-log").GroupBy(e => (e.Date, e.Details?.ParentId)).Any(g => g.Count() > 1)
                || all.Count(e => e.Kind == "time" && e.Details?.StartedAt is not null && e.Details.EndedAt is null && e.Details.Status != "cancelled") > 1) throw new ArgumentException("پشتیبان با ثبت‌های روزانهٔ موجود تداخل دارد.");
            var newAttachments = new List<PersonalAttachment>();
            foreach (var attachment in backup.Attachments)
            {
                var existingAttachment = await ReadAttachmentAsync(connection, attachment.Id, token);
                if (existingAttachment is null) newAttachments.Add(attachment);
                else if (existingAttachment.ContentType != attachment.ContentType ||
                    !(await File.ReadAllBytesAsync(Path.Combine(AttachmentDirectory, attachment.Id.ToString("N")), token)).AsSpan().SequenceEqual(media[attachment.Id]))
                    throw new ArgumentException("پیوست پشتیبان با شناسهٔ موجود تداخل دارد؛ اطلاعات موجود تغییر نکرد.");
            }
            using var transaction = connection.BeginTransaction();
            foreach (var attachment in newAttachments)
            {
                var path = Path.Combine(AttachmentDirectory, attachment.Id.ToString("N")); await File.WriteAllBytesAsync(path, media[attachment.Id], token); written.Add(path);
                using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "INSERT INTO personal_attachments(id,payload) VALUES($id,$payload)";
                command.Parameters.AddWithValue("$id", attachment.Id.ToString()); command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(attachment, PersonalRules.Json)); await command.ExecuteNonQueryAsync(token);
            }
            foreach (var entry in added) await WriteAsync(connection, transaction, entry, false, token);
            await transaction.CommitAsync(token); written.Clear();
            return (added.Length, backup.Entries.Length - added.Length);
        }
        finally { foreach (var path in written) File.Delete(path); gate.Release(); }
    }
}
