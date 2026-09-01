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
    private CancellationTokenSource? _runCancellation;

    public BatchState Status { get { lock (_sync) return _state; } }

    public bool Start(int target, decimal positionSizeUsdt, string directionFilter, string chartFilter,
        string timeframeFilter, bool timedMode, int durationMinutes, out string? error)
    {
        lock (_sync)
        {
            if (_runCancellation is not null) { error = "صف قبلی هنوز در حال توقف است؛ چند لحظه دیگر دوباره تلاش کنید."; return false; }
            if (!telegram.IsConfigured) { error = "ربات تلگرام Phoenix تنظیم نشده است."; return false; }
            DateTimeOffset? endsAt = timedMode ? DateTimeOffset.UtcNow.AddMinutes(durationMinutes) : null;
            _state = new(true, target, 0, 0, 0, null, "در حال شروع بررسی بازارها…", null,
                directionFilter, chartFilter, timeframeFilter)
            {
                TimedMode = timedMode, DurationMinutes = timedMode ? durationMinutes : 0, EndsAtUtc = endsAt
            };
            error = null;
            var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            _runCancellation = runCancellation;
            _ = Task.Run(() => RunAsync(target, positionSizeUsdt, directionFilter, chartFilter,
                timeframeFilter, endsAt, runCancellation));
            return true;
        }
    }

    public bool Stop(out string? error)
    {
        CancellationTokenSource cancellation;
        lock (_sync)
        {
            if (!_state.Running || _runCancellation is null)
            {
                error = "در حال حاضر صف فعالی برای توقف وجود ندارد.";
                return false;
            }
            cancellation = _runCancellation;
            _decision = null;
            _decisionKey = null;
            _state = _state with
            {
                Running = false,
                CurrentSymbol = null,
                Message = "ارسال سیگنال‌ها به درخواست شما متوقف شد.",
                Error = null
            };
            error = null;
        }
        cancellation.Cancel();
        return true;
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

    private async Task RunAsync(int target, decimal positionSizeUsdt, string directionFilter,
        string chartFilter, string timeframeFilter, DateTimeOffset? endsAt,
        CancellationTokenSource runCancellation)
    {
        var token = runCancellation.Token;
        try
        {
            var assets = await markets.GetAsync(token);
            var nextAsset = 0;
            const int priorityWindow = 8;
            var proposedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool ShouldContinue() => endsAt.HasValue
                ? DateTimeOffset.UtcNow < endsAt.Value
                : Status.Approved < target;
            while (ShouldContinue())
            {
                if (nextAsset >= assets.Count)
                {
                    if (!endsAt.HasValue) break;
                    nextAsset = 0;
                    Update(state => state with { Message = "یک دور بازار کامل شد؛ تا بررسی دوباره کمی صبر می‌کنیم…" });
                    await Task.Delay(TimeSpan.FromSeconds(45), token);
                    continue;
                }
                var pool = new List<RankedCandidate>();
                while (nextAsset < assets.Count && pool.Count < priorityWindow && ShouldContinue())
                {
                    var asset = assets[nextAsset++];
                    Update(state => state with
                    {
                        Checked = state.Checked + 1, CurrentSymbol = asset.Symbol,
                        Message = $"در حال یافتن نزدیک‌ترین سیگنال‌ها؛ بررسی {asset.Symbol}…"
                    });
                    var active = (await orders.GetAllAsync(token)).Where(x =>
                        x.Symbol.Equals(asset.Symbol, StringComparison.OrdinalIgnoreCase) &&
                        x.Status is "Pending" or "Submitting" or "Submitted" or "Filled").ToArray();
                    if (active.Select(x => x.Direction).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2)
                        continue;

                    var rules = await bybit.GetInstrumentRulesAsync(asset.Symbol, token);
                    var options = new List<RankedCandidate>();
                    foreach (var interval in Intervals(timeframeFilter))
                    {
                        var candles = await bybit.GetKlinesAsync(asset.Symbol, interval, 1000, token);
                        var chartModes = chartFilter switch
                        {
                            "Candles" => new[] { false }, "Line" => new[] { true }, _ => new[] { false, true }
                        };
                        foreach (var lineMode in chartModes)
                        {
                            SignalCandidate candidate;
                            try { candidate = finder.Find(asset.Symbol, interval, candles, rules, positionSizeUsdt, 5, lineMode); }
                            catch { continue; }
                            if (candidate.IsBurned ||
                                (directionFilter != "All" && !candidate.Direction.Equals(directionFilter, StringComparison.OrdinalIgnoreCase)) ||
                                active.Any(x => x.Direction.Equals(candidate.Direction, StringComparison.OrdinalIgnoreCase)))
                                continue;
                            var distance = Math.Abs(candidate.LastPrice - candidate.EntryPrice) / candidate.EntryPrice * 100m;
                            var proposalKey = ProposalKey(candidate, interval, lineMode);
                            if (!proposedKeys.Contains(proposalKey))
                                options.Add(new(candidate, candles, interval, lineMode, distance, proposalKey));
                        }
                    }
                    var closest = options.MinBy(x => x.EntryDistancePercent);
                    if (closest is not null) pool.Add(closest);
                }
                foreach (var option in pool.OrderBy(x => x.EntryDistancePercent))
                {
                    if (!ShouldContinue()) break;
                    var selected = option.Candidate;
                    var key = Guid.NewGuid().ToString("N")[..10];
                    var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_sync) { _decision = decision; _decisionKey = key; }
                    var caption = $"🔎 پیشنهاد جدید Phoenix\nنماد: {selected.Symbol}\nجهت: {selected.Direction}\nتایم‌فریم: {IntervalName(option.Interval)}\nنوع نمودار: {(option.LineMode ? "خطی (Close)" : "کندل‌استیک")}\nفاصله تا ورود: {Format(option.EntryDistancePercent)}٪\nسقف: {Format(selected.Ceiling)}\nکف: {Format(selected.Floor)}\nورود: {Format(selected.EntryPrice)}\nتارگت: {Format(selected.TakeProfit)}\nاستاپ: {Format(selected.StopLoss)}\nورودی: {Format(positionSizeUsdt)} USDT\n\nآیا این سیگنال ثبت شود؟";
                    var image = SignalChartRenderer.Render(option.Candles, selected, option.LineMode,
                        TimeframeBadge(option.Interval));
                    Update(state => state with
                    {
                        Proposed = state.Proposed + 1, CurrentSymbol = selected.Symbol,
                        Message = $"منتظر پاسخ تلگرام برای {selected.Symbol}؛ فاصله تا ورود {Format(option.EntryDistancePercent)}٪"
                    });
                    if (!await telegram.SendCandidateAsync(image, caption, key, token))
                        throw new InvalidOperationException("ارسال پیشنهاد به تلگرام ناموفق بود.");
                    proposedKeys.Add(option.ProposalKey);
                    var accepted = await decision.Task.WaitAsync(token);
                    if (!accepted) { Update(state => state with { Rejected = state.Rejected + 1 }); continue; }
                    var outcome = await submission.QueueAsync(new SignalRequest(selected.Symbol,
                        selected.Direction, selected.Ceiling, selected.Floor, positionSizeUsdt), token,
                        new SignalEvidence(option.Interval, option.LineMode ? "Line" : "Candles", image));
                    if (outcome.Signal is null)
                    {
                        Update(state => state with { Error = outcome.Error, Message = $"ثبت {selected.Symbol} ناموفق بود؛ بررسی ادامه دارد." });
                        continue;
                    }
                    Update(state => state with { Approved = state.Approved + 1, Message = $"{selected.Symbol} ثبت شد؛ در حال رفتن به مورد بعدی…" });
                }
            }
            Update(state => state with
            {
                Running = false, CurrentSymbol = null,
                Message = endsAt.HasValue
                    ? $"جست‌وجوی زمان‌دار تمام شد؛ {state.Approved} سیگنال تأیید شد."
                    : state.Approved >= target ? "تعداد درخواستی سیگنال تکمیل شد." : "فهرست بازارها بررسی شد و پیشنهاد بیشتری پیدا نشد."
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!lifetime.ApplicationStopping.IsCancellationRequested)
                Update(state => state with { Running = false, CurrentSymbol = null, Message = "ارسال سیگنال‌ها به درخواست شما متوقف شد.", Error = null });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Signal batch failed");
            Update(state => state with { Running = false, Error = exception.Message, Message = "صف بررسی متوقف شد." });
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_runCancellation, runCancellation))
                {
                    _decision = null;
                    _decisionKey = null;
                    _runCancellation = null;
                }
            }
            runCancellation.Dispose();
        }
    }

    private void Update(Func<BatchState, BatchState> update) { lock (_sync) _state = update(_state); }
    private static string[] Intervals(string filter) => filter == "All" ? ["5", "15", "60", "240"] : [filter];
    private static string IntervalName(string value) => value switch { "5" => "۵ دقیقه", "15" => "۱۵ دقیقه", "60" => "۱ ساعت", "240" => "۴ ساعت", _ => value };
    private static string TimeframeBadge(string value) => value switch { "5" => "5M", "15" => "15M", "60" => "1H", "240" => "4H", _ => value };
    private static string Format(decimal value) => value.ToString("0.################", CultureInfo.InvariantCulture);
    private static string ProposalKey(SignalCandidate candidate, string interval, bool lineMode) =>
        $"{candidate.Symbol}|{candidate.Direction}|{interval}|{lineMode}|{candidate.CeilingTime}|{candidate.FloorTime}";
    private sealed record RankedCandidate(SignalCandidate Candidate, IReadOnlyList<BybitKline> Candles,
        string Interval, bool LineMode, decimal EntryDistancePercent, string ProposalKey);
}

public sealed record BatchState(bool Running, int Target, int Approved, int Rejected, int Checked,
    string? CurrentSymbol, string Message, string? Error, string DirectionFilter, string ChartFilter,
    string TimeframeFilter)
{
    public int Proposed { get; init; }
    public bool TimedMode { get; init; }
    public int DurationMinutes { get; init; }
    public DateTimeOffset? EndsAtUtc { get; init; }
    public static BatchState Idle => new(false, 0, 0, 0, 0, null, "صفی فعال نیست.", null, "All", "All", "All");
}

public sealed record StartSignalBatchRequest(int Count, decimal PositionSizeUsdt, string? DirectionFilter,
    string? ChartFilter, string? TimeframeFilter, bool TimedMode = false, int DurationMinutes = 30);
