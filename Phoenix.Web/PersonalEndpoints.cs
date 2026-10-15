using System.Text.Json;
namespace Phoenix.Web;
public static class PersonalEndpoints
{
    public static void MapPersonalEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/personal").AddEndpointFilter(async (context, next) =>
        {
            if (!PhoenixSessionAuth.TryGetIdentity(context.HttpContext.Request, out var identity) || !identity.IsAdmin) return Results.StatusCode(403);
            try { return await next(context); }
            catch (PersonalConflict exception) { return Results.Json(new { error = exception.Message, current = exception.Current }, statusCode: 409); }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (InvalidDataException) { return Results.BadRequest(new { error = "فایل پشتیبان معتبر نیست." }); }
            catch (JsonException) { return Results.BadRequest(new { error = "ساختار داده معتبر نیست." }); }
        });
        group.MapGet("/entries", async (PersonalStore store, CancellationToken token) => Results.Ok(await store.ListAsync(token)));
        group.MapGet("/trash", async (PersonalStore store, CancellationToken token) => Results.Ok(await store.ListAsync(token, true)));
        group.MapPost("/entries", async (PersonalInput input, PersonalStore store, CancellationToken token) =>
        {
            var error = PersonalRules.Validate(input);
            return error is not null ? Results.BadRequest(new { error }) : Results.Ok(await store.CreateAsync(input, token));
        });
        group.MapPut("/entries/{id:guid}", async (Guid id, PersonalUpdate update, PersonalStore store, CancellationToken token) =>
        {
            if (update.Entry is null || update.Revision < 1) return Results.BadRequest(new { error = "نسخه و محتوای ثبت لازم است." });
            var error = PersonalRules.Validate(update.Entry);
            if (error is not null) return Results.BadRequest(new { error });
            var result = await store.UpdateAsync(id, update.Entry, update.Revision, token);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });
        group.MapDelete("/entries/{id:guid}", async (Guid id, int? revision, PersonalStore store, CancellationToken token) =>
            await store.DeleteAsync(id, token, revision) ? Results.NoContent() : Results.NotFound());
        group.MapPost("/entries/{id:guid}/restore", async (Guid id, PersonalRevision input, PersonalStore store, CancellationToken token) =>
            await store.RestoreAsync(id, input.Revision, token) ? Results.Ok(new { restored = true }) : Results.NotFound());
        group.MapGet("/entries/{id:guid}/history", async (Guid id, PersonalStore store, CancellationToken token) => Results.Ok(await store.HistoryAsync(id, token)));
        group.MapGet("/reports", async (DateOnly from, DateOnly to, PersonalStore store, CancellationToken token) =>
            from > to || to.DayNumber - from.DayNumber > 366 ? Results.BadRequest(new { error = "بازهٔ معتبر و حداکثر یک‌ساله انتخاب کنید." })
                : Results.Ok(PersonalReports.Build(await store.ListAsync(token), from, to)));
        group.MapGet("/experiments/{id:guid}", async (Guid id, PersonalStore store, CancellationToken token) =>
        {
            var entries = await store.ListAsync(token); var experiment = entries.FirstOrDefault(e => e.Id == id && e.Kind == "experiment");
            return experiment is null ? Results.NotFound() : Results.Ok(PersonalReports.Experiment(experiment, entries));
        });
        group.MapGet("/export", async (PersonalStore store, CancellationToken token) => Results.File(JsonSerializer.SerializeToUtf8Bytes(await store.ListAsync(token), PersonalRules.Json), "application/json", "personal-data.json"));
        group.MapGet("/backup", async (PersonalStore store, CancellationToken token) => Results.File(await store.BackupAsync(token), "application/zip", "personal-backup.zip"));
        group.MapPost("/import", async (HttpRequest request, PersonalStore store, CancellationToken token) =>
        {
            var body = await ReadBytesAsync(request, 128 * 1024 * 1024, token);
            var result = await store.ImportAsync(body, token); return Results.Ok(new { result.Added, result.Skipped });
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(128 * 1024 * 1024));
        group.MapPost("/attachments/{id:guid}", async (Guid id, HttpRequest request, PersonalStore store, CancellationToken token) =>
        {
            if (id == Guid.Empty) return Results.BadRequest(new { error = "شناسهٔ فایل معتبر نیست." });
            var bytes = await ReadBytesAsync(request, 16 * 1024 * 1024, token);
            var name = Uri.UnescapeDataString(request.Headers["X-File-Name"].ToString());
            var attachment = await store.AddAttachmentAsync(id, name, request.ContentType ?? "", bytes, token);
            return Results.Ok(attachment);
        });
        group.MapGet("/attachments/{id:guid}/meta", async (Guid id, PersonalStore store, CancellationToken token) =>
        {
            var attachment = await store.AttachmentAsync(id, token); return attachment is null ? Results.NotFound() : Results.Ok(attachment);
        });
        group.MapGet("/attachments/{id:guid}", async (Guid id, HttpResponse response, PersonalStore store, CancellationToken token) =>
        {
            var attachment = await store.AttachmentAsync(id, token); if (attachment is null) return Results.NotFound();
            response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(Path.Combine(store.AttachmentDirectory, id.ToString("N")), attachment.ContentType, enableRangeProcessing: true);
        });
    }
    private static async Task<byte[]> ReadBytesAsync(HttpRequest request, int limit, CancellationToken token)
    {
        if (request.ContentLength > limit) throw new ArgumentException("حجم فایل بیش از حد مجاز است.");
        using var memory = new MemoryStream(); var buffer = new byte[81920]; int read;
        while ((read = await request.Body.ReadAsync(buffer, token)) > 0)
        { if (memory.Length + read > limit) throw new ArgumentException("حجم فایل بیش از حد مجاز است."); await memory.WriteAsync(buffer.AsMemory(0, read), token); }
        return memory.ToArray();
    }
}
