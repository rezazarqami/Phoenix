using System.Globalization;
using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

public sealed class SignalBatchService(
    MarketCapCatalog markets, BybitDemoClient bybit, SignalCandidateFinder finder,
    ServerOrderStore orders, SignalSubmissionService submission, TelegramNotifier telegram,
    IHostApplicationLifetime lifetime, ILogger<SignalBatchService> logger)
{
    private readonly object _sync = new();
    private BatchState _state = BatchState.Idle;
    private TaskCompletionSource<bool>? _decision;
    private string? _decisionKey;

    public BatchState Status { get { lock (_sync) return _state; } }

    public bool Start(int target, decimal positionSizeUsdt, string directionFilter, out string? error)
    {
        lock (_sync)
        {
            if (_state.Running) { error = "یک صف بررسی در حال اجراست؛ ابتدا همان صف را در تلگرام کامل کنید."; return false; }
            if (!telegram.IsConfigured) { error = "ربات تلگرام Phoenix تنظیم نشده است."; return false; }
            _state = new(true, target, 0, 0, 0, null, "در حال شروع بررسی بازارها…", null, directionFilter);
            error = null;
            _ = Task.Run(() => RunAsync(target, positionSizeUsdt, directionFilter, lifetime.ApplicationStopping));
            return true;
        }
    }

    public async Task<bool> HandleCallbackAsync(string data, string? callbackId, CancellationToken token)
    {
        var parts = data.Split(':');
        if (parts.Length != 3 || parts[0] != "batch") return false;
        TaskCompletionSource<bool>? decision;
        lock (_sync)
        {
            if (_decision is null || _decisionKey != parts[2]) return false;
            decision = _decision; _decision = null; _decisionKey = null;
        }
        var accepted = parts[1] == "yes";
        decision.TrySetResult(accepted);
        if (!string.IsNullOrWhiteSpace(callbackId))
            await telegram.AnswerCallbackAsync(callbackId, accepted ? "سیگنال تأیید شد؛ در حال ثبت…" : "پیشنهاد رد شد؛ مورد بعدی بررسی می‌شود.", token);
        return true;
    }

    private async Task RunAsync(int target, decimal positionSizeUsdt, string directionFilter, CancellationToken token)
    {
        try
        {
            var assets = await markets.GetAsync(token);
            foreach (var asset in assets)
            {
                if (Status.Approved >= target) break;
                Update(state => state with { Checked = state.Checked + 1, CurrentSymbol = asset.Symbol, Message = $"در حال بررسی {asset.Symbol}…" });
                var active = (await orders.GetAllAsync(token)).Where(x => x.Symbol.Equals(asset.Symbol, StringComparison.OrdinalIgnoreCase) && x.Status is "Pending" or "Submitting" or "Submitted" or "Filled").ToArray();
                if (active.Select(x => x.Direction).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2) continue;
                SignalCandidate? selected = null; IReadOnlyList<BybitKline>? selectedCandles = null; string? selectedInterval = null; var selectedLineMode = false;
                foreach (var interval in new[] { "60", "15", "240" })
                {
                    var candles = await bybit.GetKlinesAsync(asset.Symbol, interval, 1000, token);
                    var rules = await bybit.GetInstrumentRulesAsync(asset.Symbol, token);
                    foreach (var lineMode in new[] { false, true })
                    {
                        SignalCandidate candidate;
                        try { candidate = finder.Find(asset.Symbol, interval, candles, rules, positionSizeUsdt, 5, lineMode); }
                        catch { continue; }
                        if (candidate.IsBurned ||
                            (directionFilter != "All" && !candidate.Direction.Equals(directionFilter, StringComparison.OrdinalIgnoreCase)) ||
                            active.Any(x => x.Direction.Equals(candidate.Direction, StringComparison.OrdinalIgnoreCase))) continue;
                        selected = candidate; selectedCandles = candles; selectedInterval = interval; selectedLineMode = lineMode; break;
                    }
                    if (selected is not null) break;
                }
                if (selected is null || selectedCandles is null) continue;
                var key = Guid.NewGuid().ToString("N")[..10];
                var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_sync) { _decision = decision; _decisionKey = key; }
                var caption = $"🔎 پیشنهاد جدید Phoenix\nنماد: {selected.Symbol}\nجهت: {selected.Direction}\nتایم‌فریم: {IntervalName(selectedInterval!)}\nنوع نمودار: {(selectedLineMode ? "خطی (Close)" : "کندل‌استیک")}\nسقف: {Format(selected.Ceiling)}\nکف: {Format(selected.Floor)}\nورود: {Format(selected.EntryPrice)}\nتارگت: {Format(selected.TakeProfit)}\nاستاپ: {Format(selected.StopLoss)}\nورودی: {Format(positionSizeUsdt)} USDT\n\nآیا این سیگنال ثبت شود؟";
                var image = SignalChartRenderer.Render(selectedCandles, selected, selectedLineMode);
                Update(state => state with { Proposed = state.Proposed + 1, Message = $"منتظر پاسخ تلگرام برای {asset.Symbol}" });
                if (!await telegram.SendCandidateAsync(image, caption, key, token)) throw new InvalidOperationException("ارسال پیشنهاد به تلگرام ناموفق بود.");
                var accepted = await decision.Task.WaitAsync(token);
                if (!accepted) { Update(state => state with { Rejected = state.Rejected + 1 }); continue; }
                var outcome = await submission.QueueAsync(new SignalRequest(selected.Symbol, selected.Direction, selected.Ceiling, selected.Floor, positionSizeUsdt), token);
                if (outcome.Signal is null) { Update(state => state with { Error = outcome.Error, Message = $"ثبت {asset.Symbol} ناموفق بود؛ بررسی ادامه دارد." }); continue; }
                Update(state => state with { Approved = state.Approved + 1, Message = $"{asset.Symbol} ثبت شد؛ در حال رفتن به مورد بعدی…" });
            }
            Update(state => state with { Running = false, CurrentSymbol = null, Message = state.Approved >= target ? "تعداد درخواستی سیگنال تکمیل شد." : "فهرست بازارها بررسی شد و پیشنهاد بیشتری پیدا نشد." });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Signal batch failed");
            Update(state => state with { Running = false, Error = exception.Message, Message = "صف بررسی متوقف شد." });
        }
        finally { lock (_sync) { _decision = null; _decisionKey = null; } }
    }

    private void Update(Func<BatchState, BatchState> update) { lock (_sync) _state = update(_state); }
    private static string IntervalName(string value) => value switch { "15" => "۱۵ دقیقه", "60" => "۱ ساعت", "240" => "۴ ساعت", _ => value };
    private static string Format(decimal value) => value.ToString("0.################", CultureInfo.InvariantCulture);
}

public sealed record BatchState(bool Running, int Target, int Approved, int Rejected, int Checked,
    string? CurrentSymbol, string Message, string? Error, string DirectionFilter)
{
    public int Proposed { get; init; }
    public static BatchState Idle => new(false, 0, 0, 0, 0, null, "صفی فعال نیست.", null, "All");
}

public sealed record StartSignalBatchRequest(int Count, decimal PositionSizeUsdt, string? DirectionFilter);
