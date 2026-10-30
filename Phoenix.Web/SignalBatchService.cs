using System.Globalization;
using Phoenix.Engine.Exchanges.Bybit;

namespace Phoenix.Web;

public sealed partial class SignalBatchService(
    MarketCapCatalog markets, BybitDemoClient bybit, SignalCandidateFinder finder,
    ServerOrderStore orders, SignalSubmissionService submission, TelegramNotifier telegram,
    DedicatedTelegramNotifier dedicatedTelegram,
    ProfessionalSignalAnalysisService professional,
    ShadowSignalRuntime shadow,
    IHostApplicationLifetime lifetime, ILogger<SignalBatchService> logger, ReviewArchiveStore reviews,
    ProposalLeverageCache? leverageInfo = null)
{
    private readonly object _sync = new();
    private BatchState _state = BatchState.Idle;
    private TaskCompletionSource<BatchDecision>? _decision;
    private string? _decisionKey;
    private CancellationTokenSource? _runCancellation;
    private Task? _cancellationCompletion;
    private readonly Dictionary<string, string> _pendingReasons = new(StringComparer.Ordinal);

    public BatchState Status { get { lock (_sync) return _state with { Stopping = !_state.Running && _runCancellation is not null }; } }

    public bool Start(int target, decimal positionSizeUsdt, string directionFilter, string chartFilter,
        string timeframeFilter, decimal minimumTargetProbability, bool timedMode, int durationMinutes, string? requestedByUsername,
        out string? error)
    {
        lock (_sync)
        {
            if (_runCancellation is not null) { error = "صف قبلی هنوز در حال توقف است؛ چند لحظه دیگر دوباره تلاش کنید."; return false; }
            var dedicatedRoute = false;
            DateTimeOffset? endsAt = timedMode ? DateTimeOffset.UtcNow.AddMinutes(durationMinutes) : null;
            _state = new(true, target, 0, 0, 0, null, "در حال شروع بررسی بازارها…", null,
                directionFilter, chartFilter, timeframeFilter)
            {
                RunId = Guid.NewGuid().ToString("N"),
                MinimumTargetProbability = minimumTargetProbability,
                TimedMode = timedMode, DurationMinutes = timedMode ? durationMinutes : 0, EndsAtUtc = endsAt
            };
            error = null;
            var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            _runCancellation = runCancellation;
            _cancellationCompletion = null;
            _ = Task.Run(() => RunAsync(target, positionSizeUsdt, directionFilter, chartFilter,
                timeframeFilter, minimumTargetProbability, endsAt, requestedByUsername, dedicatedRoute, runCancellation));
            return true;
        }
    }

    public bool Stop(out string? error, string? expectedRunId = null)
    {
        CancellationTokenSource cancellation;
        lock (_sync)
        {
            if (expectedRunId is not null && expectedRunId != _state.RunId)
            {
                error = "این درخواست توقف مربوط به صف قبلی است؛ وضعیت صف را دوباره دریافت کنید.";
                return false;
            }
            if (!_state.Running || _runCancellation is null)
            {
                // Stop is idempotent, including while cancelled work is draining.
                error = null;
                return true;
            }
            cancellation = _runCancellation;
            _decision = null;
            _decisionKey = null;
            _pendingReasons.Clear();
            _state = _state with
            {
                Running = false,
                CurrentSymbol = null,
                Message = "ارسال سیگنال‌ها به درخواست شما متوقف شد.",
                Error = null
            };
            error = null;
            // CancelAsync marks the token now, but runs registered cleanup away
            // from this HTTP request. Cancel() can synchronously block on IO callbacks.
            _cancellationCompletion ??= cancellation.CancelAsync();
        }
        return true;
    }

    public async Task<bool> HandleCallbackAsync(TelegramCommand command, bool dedicatedRoute,
        CancellationToken token)
    {
        var data = command.Command;
        var parts = data.Split(':');
        if (parts.Length != 3 || parts[0] != "batch" || parts[1] is not ("yes" or "no" or "reason" or "observe"))
            return false;
        if (parts[1] == "reason")
        {
            lock (_sync)
            {
                if (_decision is null || _decisionKey != parts[2]) return false;
                _pendingReasons[ReviewerKey(command, dedicatedRoute)] = parts[2];
            }
            if (!string.IsNullOrWhiteSpace(command.CallbackId))
            {
                if (dedicatedRoute)
                    await dedicatedTelegram.AnswerCallbackAsync(command.CallbackId,
                        "دلیل رد را در پیام بعدی بنویسید.", token);
                else
                    await telegram.AnswerCallbackAsync(command.CallbackId,
                        "دلیل رد را در پیام بعدی بنویسید.", token);
            }
            await SendReviewReplyAsync(command.ChatId,
                "✍️ دلیل رد این پیشنهاد را در یک پیام بنویسید. پیام بعدی شما همراه عکس در آرشیو ذخیره می‌شود.",
                dedicatedRoute, token);
            return true;
        }
        TaskCompletionSource<BatchDecision>? decision;
        lock (_sync)
        {
            if (_decision is null || _decisionKey != parts[2]) return false;
            decision = _decision; _decision = null; _decisionKey = null; _pendingReasons.Clear();
        }
        var selected = parts[1] switch
        {
            "yes" => BatchDecision.Register,
            "observe" => BatchDecision.Observe,
            _ => BatchDecision.Reject
        };
        try
        {
            var saved = selected == BatchDecision.Observe
                ? await reviews.ObserveAsync(parts[2], token)
                : await reviews.DecideAsync(parts[2], selected == BatchDecision.Register, token);
            if (!saved)
                throw new InvalidOperationException("ثبت پاسخ در آرشیو پیشنهادها ناموفق بود.");
        }
        catch (Exception exception) { decision.TrySetException(exception); throw; }
        decision.TrySetResult(selected);
        if (!string.IsNullOrWhiteSpace(command.CallbackId))
        {
            var answer = selected switch
            {
                BatchDecision.Register => "سیگنال تأیید شد؛ در حال ثبت…",
                BatchDecision.Observe => "برای پایش بدون معامله ثبت شد 👁",
                _ => "پیشنهاد رد شد؛ مورد بعدی بررسی می‌شود."
            };
            if (dedicatedRoute) await dedicatedTelegram.AnswerCallbackAsync(command.CallbackId, answer, token);
            else await telegram.AnswerCallbackAsync(command.CallbackId, answer, token);
        }
        return true;
    }

    public async Task<bool> HandleReasonAsync(TelegramCommand command, bool dedicatedRoute,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(command.Command) || command.Command.StartsWith('/')) return false;
        TaskCompletionSource<BatchDecision>? decision;
        string key;
        lock (_sync)
        {
            var reviewer = ReviewerKey(command, dedicatedRoute);
            if (!_pendingReasons.Remove(reviewer, out key!) || _decision is null || _decisionKey != key)
                return false;
            decision = _decision;
            _decision = null;
            _decisionKey = null;
            _pendingReasons.Clear();
        }
        var reason = command.Command.Trim();
        if (reason.Length > 1500) reason = reason[..1500];
        try
        {
            if (!await reviews.DecideAsync(key, false, reason, token))
                throw new InvalidOperationException("ثبت دلیل رد در آرشیو پیشنهادها ناموفق بود.");
        }
        catch (Exception exception) { decision.TrySetException(exception); throw; }
        decision.TrySetResult(BatchDecision.Reject);
        await SendReviewReplyAsync(command.ChatId,
            "✅ پیشنهاد رد شد و دلیل آن همراه عکس در آرشیو ذخیره شد.", dedicatedRoute, token);
        return true;
    }

    private async Task SendReviewReplyAsync(string chatId, string text, bool dedicatedRoute,
        CancellationToken token)
    {
        if (dedicatedRoute) await dedicatedTelegram.SendCommandReplyAsync(chatId, text, token);
        else await telegram.SendCommandReplyAsync(chatId, text, token);
    }

    private static string ReviewerKey(TelegramCommand command, bool dedicatedRoute) =>
        $"{(dedicatedRoute ? 'D' : 'M')}|{command.ChatId}|{command.UserId}";

    private async Task RunAsync(int target, decimal positionSizeUsdt, string directionFilter,
        string chartFilter, string timeframeFilter, decimal minimumTargetProbability, DateTimeOffset? endsAt,
        string? requestedByUsername, bool dedicatedRoute,
        CancellationTokenSource runCancellation)
    {
        var token = runCancellation.Token;
        Task<PreparedProposal?>? prepared = null;
        try
        {
            var assets = await markets.GetAsync(token);
            var nextAsset = 0;
            const int priorityWindow = 8;
            var existing = await PendingAsync(requestedByUsername ?? "", token);
            var proposedKeys = existing.Select(x=>ProposalKey(x.Candidate,x.Interval,x.LineMode)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool ShouldContinue() => endsAt.HasValue
                ? DateTimeOffset.UtcNow < endsAt.Value
                : Status.Approved + Status.Observed < target;
            while (ShouldContinue())
            {
                await WaitForCapacityAsync(target, endsAt, token);
                if (!ShouldContinue()) break;
                if (nextAsset >= assets.Count)
                {
                    if (!endsAt.HasValue) break;
                    nextAsset = 0;
                    Update(state => state with { Message = "یک دور بازار کامل شد؛ تا بررسی دوباره کمی صبر می‌کنیم…" });
                    await Task.Delay(TimeSpan.FromSeconds(45), token);
                    continue;
                }
                token.ThrowIfCancellationRequested();
                var window = assets.Skip(nextAsset).Take(priorityWindow).Select(x=>x.Symbol).ToArray();
                nextAsset += window.Length;
                var scanClock=System.Diagnostics.Stopwatch.StartNew();
                var scanned=await OrderedConcurrentFetch.RunAsync<RankedCandidate?>(window,
                    symbol=>ShouldContinue()?ScanCandidateAsync(symbol,positionSizeUsdt,directionFilter,chartFilter,
                        timeframeFilter,proposedKeys,token):Task.FromResult<RankedCandidate?>(null),3,token);
                var pool=scanned.Where(x=>x is not null).Select(x=>x!).ToArray();
                logger.LogInformation("Phoenix scan window completed: {Symbols} symbols in {Elapsed}ms",window.Length,scanClock.ElapsedMilliseconds);
                Update(state=>state with{LastScanDurationMs=scanClock.ElapsedMilliseconds});
                var ranked = pool.OrderBy(x => x.EntryDistancePercent).ToArray();
                for (var optionIndex = 0; optionIndex < ranked.Length; optionIndex++)
                {
                    token.ThrowIfCancellationRequested();
                    if (!ShouldContinue()) break;
                    await WaitForCapacityAsync(target, endsAt, token);
                    if (!ShouldContinue()) break;
                    var option = ranked[optionIndex];
                    Update(state=>state with{CurrentSymbol=option.Candidate.Symbol,Message=$"در حال تحلیل حمایت و مقاومت و آماده‌سازی پیشنهاد {option.Candidate.Symbol}…"});
                    var prepareClock=System.Diagnostics.Stopwatch.StartNew();
                    prepared ??= PrepareProposalAsync(option, positionSizeUsdt, minimumTargetProbability, token);
                    PreparedProposal? proposal;
                    try { proposal = await prepared; }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Skipping {Symbol}; professional analysis failed", option.Candidate.Symbol);
                        prepared = null;
                        Update(state => state with
                        {
                            Error = null,
                            Message = $"تحلیل {option.Candidate.Symbol} موقتاً ناموفق بود؛ صف ادامه دارد…"
                        });
                        continue;
                    }
                    prepared = null;
                    logger.LogInformation("Phoenix proposal prepared for {Symbol} in {Elapsed}ms",option.Candidate.Symbol,prepareClock.ElapsedMilliseconds);
                    Update(state=>state with{LastPreparationDurationMs=prepareClock.ElapsedMilliseconds});
                    if (proposal is null)
                    {
                        Update(state => state with { Message = "پیشنهاد از فیلتر فاصله/برآورد نتیجه عبور نکرد؛ بررسی ادامه دارد. برای مشاهدهٔ پیشنهادهای بدون برآورد، حداقل را صفر بگذارید." });
                        continue;
                    }
                    var key = Guid.NewGuid().ToString("N");
                    await reviews.SaveAsync(key, option.Candidate, option.Candles, option.Interval, option.LineMode, proposal.Image, token);
                    await AddProposalAsync(new PendingProposal(key, requestedByUsername ?? "", option.Candidate,
                        option.Interval, option.LineMode, proposal.Analysis, proposal.Caption, proposal.Image,
                        positionSizeUsdt, DateTime.UtcNow), token);
                    proposedKeys.Add(option.ProposalKey);
                    Update(state => state with { Proposed = state.Proposed + 1,
                        Message = "پیشنهاد در صف تأیید داخل اپ آماده است." });
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
            Task cancellationCompletion;
            lock (_sync) cancellationCompletion = _cancellationCompletion ??= runCancellation.CancelAsync();
            await ObserveCancellationAsync(cancellationCompletion);
            if (prepared is not null)
                try { await prepared; }
                catch (OperationCanceledException) { }
                catch (Exception exception) { logger.LogDebug(exception, "Unused proposal preparation ended"); }
            lock (_sync)
            {
                if (ReferenceEquals(_runCancellation, runCancellation))
                {
                    _decision = null;
                    _decisionKey = null;
                    _pendingReasons.Clear();
                    _runCancellation = null;
                    _cancellationCompletion = null;
                }
            }
            runCancellation.Dispose();
        }
    }

    // Bound the combined scan traffic, including concurrent symbols and intervals.
    private readonly SemaphoreSlim scanNetworkSlots=new(6,6);
    private readonly SemaphoreSlim scanPace=new(1,1);
    private long nextScanRequestAt;
    private async Task<T> ScanFetchAsync<T>(Func<Task<T>> fetch,CancellationToken token)
    {
        await scanNetworkSlots.WaitAsync(token);
        try
        {
            // At most 20 new scan requests/second; preserve room for monitoring
            // and account traffic even when responses arrive unusually quickly.
            await scanPace.WaitAsync(token);
            try
            {
                var delay=nextScanRequestAt-Environment.TickCount64;
                if(delay>0)await Task.Delay(TimeSpan.FromMilliseconds(delay),token);
                nextScanRequestAt=Environment.TickCount64+50;
            }
            finally{scanPace.Release();}
            return await fetch();
        }
        finally{scanNetworkSlots.Release();}
    }
    private async Task<RankedCandidate?> ScanCandidateAsync(string symbol,decimal positionSizeUsdt,
        string directionFilter,string chartFilter,string timeframeFilter,HashSet<string> proposedKeys,CancellationToken token)
    {
        Update(state=>state with{CurrentSymbol=symbol,Message=$"در حال بررسی رمزارز {symbol}…"});
        try
        {
            var active=(await orders.GetAllAsync(token)).Where(x=>x.Symbol.Equals(symbol,StringComparison.OrdinalIgnoreCase) &&
                x.Status is "Pending" or "Submitting" or "Submitted" or "Filled").ToArray();
            if(active.Select(x=>x.Direction).Distinct(StringComparer.OrdinalIgnoreCase).Count()>=2)return null;
            var intervals=Intervals(timeframeFilter);
            var rulesTask=ScanFetchAsync(()=>bybit.GetInstrumentRulesAsync(symbol,token),token);
            var historiesTask=OrderedConcurrentFetch.RunAsync(intervals,
                interval=>ScanFetchAsync(()=>bybit.GetKlinesAsync(symbol,interval,1000,token),token),3,token);
            await Task.WhenAll(rulesTask,historiesTask);
            var rules=await rulesTask;var histories=await historiesTask;
            var shared=intervals.Select((interval,index)=>(interval,index))
                .ToDictionary(x=>x.interval,x=>histories[x.index]);
            var options=new List<RankedCandidate>();
            for(var index=0;index<intervals.Length;index++)
            {
                token.ThrowIfCancellationRequested();
                var interval=intervals[index];var candles=histories[index];
                var chartModes=chartFilter switch {"Candles"=>new[]{false},"Line"=>new[]{true},_=>new[]{false,true}};
                foreach(var lineMode in chartModes)
                {
                    SignalCandidate candidate;
                    try{candidate=finder.Find(symbol,interval,candles,rules,positionSizeUsdt,5,lineMode);}
                    catch{continue;}
                    if(candidate.IsBurned || SignalQualityAssessment.IsVeryWeakImpulse(candidate,candles) ||
                        EntryToTargetPercent(candidate)<0.5m ||
                        directionFilter!="All"&&!candidate.Direction.Equals(directionFilter,StringComparison.OrdinalIgnoreCase) ||
                        active.Any(x=>x.Direction.Equals(candidate.Direction,StringComparison.OrdinalIgnoreCase)))continue;
                    var distance=Math.Abs(candidate.LastPrice-candidate.EntryPrice)/candidate.EntryPrice*100m;
                    var key=ProposalKey(candidate,interval,lineMode);
                    if(proposedKeys.Contains(key))continue;
                    options.Add(new(candidate,candles,interval,lineMode,distance,key){ScanHistories=shared});
                }
            }
            return options.MinBy(x=>x.EntryDistancePercent);
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
        catch(Exception ex)
        {
            logger.LogWarning(ex,"Skipping {Symbol}; market scan failed",symbol);
            Update(state=>state with{Error=null,Message=$"بررسی {symbol} موقتاً ناموفق بود؛ صف ادامه دارد…"});return null;
        }
        finally{if(!token.IsCancellationRequested)Update(state=>state with{Checked=state.Checked+1});}
    }

    private Task<PreparedProposal?> PrepareProposalAsync(RankedCandidate option,
        decimal positionSizeUsdt, decimal minimumTargetProbability, CancellationToken token) =>
        Task.Run(async () =>
        {
            token.ThrowIfCancellationRequested();
            var selected = option.Candidate;
            var analysis = await professional.AnalyzeAsync(selected, option.Candles, option.Interval, token, knownHistories:option.ScanHistories);
            if (EntryToTargetPercent(selected) < 0.5m)
                return (PreparedProposal?)null;
            var chartInterval = ReviewChartInterval(option.Interval);
            IReadOnlyList<BybitKline> chartCandles;
            try
            {
                chartCandles = chartInterval == option.Interval
                    ? option.Candles
                    : await professional.GetReviewCandlesAsync(selected.Symbol,chartInterval,option.ScanHistories,token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Falling back to signal timeframe for {Symbol} chart", selected.Symbol);
                chartInterval = option.Interval;
                chartCandles = option.Candles;
            }
            // Keep both selected anchors visible without fetching extra tiers
            // or inspecting Elliott turns just to prepare the review image.
            var anchorStart = Math.Min(selected.FloorTime, selected.CeilingTime);
            var anchorEnd = Math.Max(selected.FloorTime, selected.CeilingTime);
            if (chartCandles.Count < 52 || chartCandles[0].OpenTime > anchorStart ||
                chartCandles[^1].OpenTime + (chartCandles[^1].OpenTime - chartCandles[^2].OpenTime) < anchorEnd)
            {
                chartInterval = option.Interval;
                chartCandles = option.Candles;
            }
            var similarityResult = analysis.Prediction;
            var similarityLines = SignalReviewText.Similarity(similarityResult);
            var leverageLines = leverageInfo?.Describe(selected.Symbol, selected.EntryPrice, selected.TakeProfit) ?? "";
            var caption = $"🔎 پیشنهاد جدید Phoenix\nنماد: {selected.Symbol}\nجهت: {selected.Direction}\nتایم‌فریم سیگنال: {IntervalName(option.Interval)}\nفاصله تا ورود: {FormatTwoDecimals(option.EntryDistancePercent)}٪\nفاصله ورود تا تارگت: {FormatTwoDecimals(EntryToTargetPercent(selected))}٪{leverageLines}\nسقف: {Format(selected.Ceiling)}\nکف: {Format(selected.Floor)}\nورود: {Format(selected.EntryPrice)}\nتارگت: {Format(selected.TakeProfit)}\nاستاپ: {Format(selected.StopLoss)}\nورودی: {Format(positionSizeUsdt)} USDT{similarityLines}\n\nآیا این سیگنال ثبت شود؟";
            token.ThrowIfCancellationRequested();
            // Chart style is presentation-only; analysis retains the requested mode.
            var image = SignalChartRenderer.Render(chartCandles, selected, false,
                TimeframeBadge(chartInterval), similarityResult.TargetPercent, similarityResult.StopPercent,
                targetOutcome: similarityResult.TargetOutcomePercent, stopOutcome: similarityResult.StopOutcomePercent);
            token.ThrowIfCancellationRequested();
            return new PreparedProposal(analysis, caption, image);
        }, token);

    private sealed record PreparedProposal(ProfessionalSignalAnalysis Analysis, string Caption, byte[] Image);

    private async Task ObserveCancellationAsync(Task cancellation)
    {
        try { await cancellation; }
        catch (Exception exception) { logger.LogWarning(exception, "Signal batch cancellation cleanup failed"); }
    }

    private void Update(Func<BatchState, BatchState> update)
    {
        lock (_sync)
        {
            // Late scan/preparation results must not replace the stop acknowledgement.
            if (_runCancellation?.IsCancellationRequested == true && !_state.Running) return;
            _state = update(_state);
        }
    }
    private static string[] Intervals(string filter) => filter == "All" ? ["1", "5", "15", "60", "240"] : [filter];
    public static string ReviewChartInterval(string signalInterval) => signalInterval switch
    {
        "1" => "15", "5" => "60", "15" => "240", "60" => "D", "240" => "W", _ => signalInterval
    };
    private static string IntervalName(string value) => value switch { "1" => "۱ دقیقه", "5" => "۵ دقیقه", "15" => "۱۵ دقیقه", "60" => "۱ ساعت", "240" => "۴ ساعت", _ => value };
    private static string TimeframeBadge(string value) => value switch { "1" => "1M", "5" => "5M", "15" => "15M", "60" => "1H", "240" => "4H", "D" => "1D", "W" => "1W", "M" => "1M", _ => value };
    internal static bool PassesOutcomeFilter(SignalSimilarityResult prediction, decimal minimum) =>
        true;

    public static decimal EntryToTargetPercent(SignalCandidate candidate) => candidate.EntryPrice <= 0m
        ? 0m : candidate.Direction == "Long"
            ? (candidate.TakeProfit - candidate.EntryPrice) / candidate.EntryPrice * 100m
            : (candidate.EntryPrice - candidate.TakeProfit) / candidate.EntryPrice * 100m;
    private static string FormatTwoDecimals(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Format(decimal value) => value.ToString("0.################", CultureInfo.InvariantCulture);
    private static string ProposalKey(SignalCandidate candidate, string interval, bool lineMode) =>
        $"{candidate.Symbol}|{candidate.Direction}|{interval}|{lineMode}|{candidate.CeilingTime}|{candidate.FloorTime}";
    private sealed record RankedCandidate(SignalCandidate Candidate, IReadOnlyList<BybitKline> Candles,
        string Interval, bool LineMode, decimal EntryDistancePercent, string ProposalKey)
    {
        public IReadOnlyDictionary<string,IReadOnlyList<BybitKline>>? ScanHistories {get;init;}
    }
}

public sealed record BatchState(bool Running, int Target, int Approved, int Rejected, int Checked,
    string? CurrentSymbol, string Message, string? Error, string DirectionFilter, string ChartFilter,
    string TimeframeFilter)
{
    public string? RunId { get; init; }
    public bool Stopping { get; init; }
    public long LastScanDurationMs {get;init;}
    public long LastPreparationDurationMs {get;init;}
    public int Proposed { get; init; }
    public int Observed { get; init; }
    public bool TimedMode { get; init; }
    public int DurationMinutes { get; init; }
    public DateTimeOffset? EndsAtUtc { get; init; }
    public decimal MinimumTargetProbability { get; init; }
    public static BatchState Idle => new(false, 0, 0, 0, 0, null, "صفی فعال نیست.", null, "All", "All", "All");
}

public sealed record StartSignalBatchRequest(int Count, decimal PositionSizeUsdt, string? DirectionFilter,
    string? ChartFilter, string? TimeframeFilter, decimal MinimumTargetProbability = 0m,
    bool TimedMode = false, int DurationMinutes = 30);

public enum BatchDecision { Reject, Register, Observe }
