using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Phoenix.Engine.Exchanges.Bybit;
using Phoenix.Web;

internal static class CancellationTests
{
    public static void ChannelAdmins()
    {
        foreach (var role in new[] { "creator", "administrator" })
            WithFixture(f => { f.Role = role; f.Click(); Check(f.Answer.Contains("سیگنال لغو شد")); Check(f.Current.Status == "Cancelled"); Check(f.MemberChecks == 1); });
    }

    public static void Unauthorized()
    {
        foreach (var role in new[] { "member", "restricted", "left", "kicked" })
            WithFixture(f => { f.Role = role; f.Click(); Check(f.Answer.Contains("اجازه")); Check(f.Current.Status == "Pending"); });
        WithFixture(f => {
            f.Role = "creator";
            f.Access.AddAsync(f.UserId, "Disabled", null, default).GetAwaiter().GetResult();
            f.Access.SetEnabledAsync(f.UserId, false, default).GetAwaiter().GetResult();
            f.Click(); Check(f.Current.Status == "Pending"); Check(f.MemberChecks == 0);
        });
        WithFixture(f => { f.MemberFailure = true; f.Click(); Check(f.Answer.Contains("لغو انجام نشد")); Check(f.Current.Status == "Pending"); });
    }

    public static void ConfiguredOwner()
    {
        WithFixture(f => {
            f.Access.AddAsync(999, "Other user", null, default).GetAwaiter().GetResult();
            f.PrivateChat = f.UserId.ToString();
            f.MemberFailure = true;
            f.Click(); Check(f.Current.Status == "Cancelled"); Check(f.MemberChecks == 0);
        });
        WithFixture(f => {
            f.Access.AddAsync(f.UserId, "Allowed user", null, default).GetAwaiter().GetResult();
            f.MemberFailure = true;
            f.Click(); Check(f.Current.Status == "Cancelled"); Check(f.MemberChecks == 0);
        });
    }

    public static void PendingCallback()
    {
        WithFixture(f => {
            f.Role = "creator";
            var stale = f.Current;
            f.Click();
            Check(f.Current.Status == "Cancelled" && f.Current.Outcome == "Cancelled" && f.Current.CompletedAtUtc is not null);
            Check(!f.Store.TryClaimSubmissionAsync(f.Signal.Id, 100m).GetAwaiter().GetResult());
            f.Store.UpdateAsync(stale).GetAwaiter().GetResult();
            Check(f.Current.Status == "Cancelled");
            var reloaded = new ServerOrderStore(f.QueuePath, f.HistoryPath);
            Check(reloaded.GetAllAsync().GetAwaiter().GetResult().Single().Status == "Cancelled");
            Check(f.Store.GetHistoryAsync(30, 100).GetAwaiter().GetResult().Single().Signal.Outcome == "Cancelled");
            f.Click(); Check(f.Answer.Contains("قبلاً"));
            Check(f.ExchangeCalls == 0);
        });
    }

    public static void ExchangeStates()
    {
        foreach (var status in new[] { "Filled", "Closing", "Submitting", "Completed", "Expired", "Cancelled" })
            WithFixture(f => {
                f.Signal.Status = status; f.Store.UpdateAsync(f.Signal).GetAwaiter().GetResult();
                var result = f.Service.CancelAsync(f.Signal.Id).GetAwaiter().GetResult();
                Check(!result.Cancelled && f.Current.Status == status && f.ExchangeCalls == 0);
            });
        WithFixture(f => {
            f.Submitted();
            var result = f.Service.CancelAsync(f.Signal.Id).GetAwaiter().GetResult();
            Check(result.Cancelled && f.Current.Status == "Cancelled" && f.CancelCalls == 1 && f.StatusCalls == 1);
        });
        WithFixture(f => {
            f.Submitted(); f.CancelFailure = true;
            try { f.Service.CancelAsync(f.Signal.Id).GetAwaiter().GetResult(); throw new Exception("Expected exchange failure"); }
            catch (HttpRequestException) { }
            Check(f.Current.Status == "Submitted");
        });
        foreach (var status in new[] { "Filled", "PartiallyFilled", "New" })
            WithFixture(f => {
                f.Submitted(); f.ExchangeStatus = status;
                var result = f.Service.CancelAsync(f.Signal.Id).GetAwaiter().GetResult();
                Check(!result.Cancelled && f.Current.Status == "Submitted");
            });
        WithFixture(f => {
            f.Submitted(); f.ExchangeStatus = "Cancelled"; f.ExecutedQuantity = "0.5";
            Check(!f.Service.CancelAsync(f.Signal.Id).GetAwaiter().GetResult().Cancelled);
            Check(f.Current.Status == "Submitted");
        });
        WithFixture(f => {
            f.Signal.Status = "Submitted"; f.Store.UpdateAsync(f.Signal).GetAwaiter().GetResult();
            Check(!f.Service.CancelAsync(f.Signal.Id).GetAwaiter().GetResult().Cancelled);
            Check(f.ExchangeCalls == 0);
        });
    }

    public static void Bulk()
    {
        foreach (var direction in new[] { "All", "Long", "Short" })
            WithFixture(f => {
                var shortSignal = new ServerSignal { Id = Guid.NewGuid(), Symbol = "ETHUSDT", Direction = "Short", CreatedAtUtc = DateTime.UtcNow };
                f.Store.AddAsync(shortSignal).GetAwaiter().GetResult();
                foreach (var status in new[] { "Filled", "Submitted", "Submitting", "Closing" })
                    f.Store.AddAsync(new ServerSignal { Id = Guid.NewGuid(), Symbol = "ADAUSDT", Direction = "Long", Status = status, CreatedAtUtc = DateTime.UtcNow }).GetAwaiter().GetResult();
                var stale = f.Current;
                Check(f.Store.CancelPendingAsync(direction).GetAwaiter().GetResult() == (direction == "All" ? 2 : 1));
                var all = f.Store.GetAllAsync().GetAwaiter().GetResult();
                Check(all.Count(x => x.Status == "Cancelled") == (direction == "All" ? 2 : 1));
                foreach (var cancelled in all.Where(x => x.Status == "Cancelled"))
                    Check(!f.Store.TryClaimSubmissionAsync(cancelled.Id, 100m).GetAwaiter().GetResult());
                f.Store.UpdateAsync(stale).GetAwaiter().GetResult();
                Check(f.Current.Status == (direction == "Short" ? "Pending" : "Cancelled"));
                Check(all.Count(x => x.Status is "Filled" or "Submitted" or "Submitting" or "Closing") == 4);
                Check(f.Store.CancelPendingAsync(direction).GetAwaiter().GetResult() == 0);
            });
    }

    private static void WithFixture(Action<Fixture> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "phoenix-cancellation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = Environment.GetEnvironmentVariable("PHOENIX_TELEGRAM_ACCESS_FILE");
        Environment.SetEnvironmentVariable("PHOENIX_TELEGRAM_ACCESS_FILE", Path.Combine(root, "access.json"));
        try { test(new Fixture(root)); }
        finally { Environment.SetEnvironmentVariable("PHOENIX_TELEGRAM_ACCESS_FILE", previous); SignalHistoryStore.ClearConnectionPools(); Directory.Delete(root, true); }
    }
    private static void Check(bool value) { if (!value) throw new Exception("Cancellation regression failed."); }

    private sealed class Fixture
    {
        public readonly long UserId = 123456789;
        public readonly ServerSignal Signal = new() { Id = Guid.NewGuid(), Symbol = "BTCUSDT", Direction = "Long", CreatedAtUtc = DateTime.UtcNow };
        public readonly TelegramAccessStore Access = new();
        public readonly ServerOrderStore Store;
        public readonly SignalCancellationService Service;
        public readonly PublicSignalNotifier Notifier;
        public string QueuePath, HistoryPath;
        public string Role = "member", Answer = "", PrivateChat = "", ExchangeStatus = "Cancelled", ExecutedQuantity = "0";
        public bool MemberFailure, CancelFailure;
        public int MemberChecks, ExchangeCalls, CancelCalls, StatusCalls;
        public ServerSignal Current => Store.GetAllAsync().GetAwaiter().GetResult().Single(x => x.Id == Signal.Id);

        public Fixture(string root)
        {
            QueuePath = Path.Combine(root, "queue.json"); HistoryPath = Path.Combine(root, "history.db");
            Store = new ServerOrderStore(QueuePath, HistoryPath);
            Store.AddAsync(Signal).GetAwaiter().GetResult();
            Notifier = new PublicSignalNotifier(new("fake-public-token", "@phoenix-channel"), NullLogger<PublicSignalNotifier>.Instance,
                new HttpClient(new Handler(request => {
                    var method = request.RequestUri!.Segments.Last();
                    if (method == "getUpdates") return Json(new { ok = true, result = new[] { new {
                        update_id = 55, callback_query = new { id = "callback-55", data = $"public:cancel:{Signal.Id:N}",
                            from = new { id = UserId }, message = new { chat = new { id = -100123456789L } } }
                    } } });
                    if (method == "getChatMember") {
                        MemberChecks++;
                        using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                        Check(body.RootElement.GetProperty("chat_id").GetString() == "@phoenix-channel");
                        Check(body.RootElement.GetProperty("user_id").GetInt64() == UserId);
                        return MemberFailure ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Json(new { ok = true, result = new { status = Role } });
                    }
                    Check(method == "answerCallbackQuery");
                    using var answer = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                    Check(answer.RootElement.GetProperty("callback_query_id").GetString() == "callback-55");
                    Answer = answer.RootElement.GetProperty("text").GetString()!;
                    return Json(new { ok = true, result = true });
                })));
            var bybit = new BybitDemoClient(new("fake-key", "fake-secret"), new HttpClient(new Handler(request => {
                ExchangeCalls++;
                if (request.RequestUri!.AbsolutePath == "/v5/order/cancel") {
                    CancelCalls++;
                    return CancelFailure ? new HttpResponseMessage(HttpStatusCode.BadGateway) : Json(new { retCode = 0, result = new { orderId = "order-1", orderLinkId = "link-1" } });
                }
                StatusCalls++;
                Check(request.RequestUri.AbsolutePath == "/v5/order/realtime");
                return Json(new { retCode = 0, result = new { list = new[] { new { orderId = "order-1", symbol = "BTCUSDT", orderStatus = ExchangeStatus, cumExecQty = ExecutedQuantity } } } });
            })));
            Service = new SignalCancellationService(Store, bybit);
        }
        public void Click()
        {
            var command = Notifier.GetCommandsAsync(0, default).GetAwaiter().GetResult().Single();
            Check(command.UserId == UserId && command.ChatId == "-100123456789");
            var worker = new PublicSignalCommandWorker(Notifier, Access, Service, new(null, PrivateChat), NullLogger<PublicSignalCommandWorker>.Instance);
            worker.HandleCommandAsync(command, Guid.ParseExact(command.Command["public:cancel:".Length..], "N"), default).GetAwaiter().GetResult();
        }
        public void Submitted() { Signal.Status = "Submitted"; Signal.BybitOrderId = "order-1"; Store.UpdateAsync(Signal).GetAwaiter().GetResult(); }
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(handle(request));
    }
}
