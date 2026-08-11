using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Phoenix.Core.Entities;
using Phoenix.Engine.Exchanges;
using Phoenix.Engine.Exchanges.Bybit;
using Phoenix.Engine.Managers;
using Phoenix.Engine.Services;

namespace Phoenix.Studio;

public partial class MainWindow : Window
{
    private readonly PaperExchange _exchange = new();
    private readonly BybitDemoClient _bybitClient = new(BybitDemoOptions.FromEnvironment());
    private readonly OrderQueueStore _queueStore = new();
    private readonly ObservableCollection<QueuedOrder> _queuedOrders = [];
    private readonly DispatcherTimer _monitorTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _marketTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Signal? _signal;
    private Position? _position;
    private BybitOrderPreview? _lastPreview;
    private BybitOrderResult? _lastDemoOrder;
    private bool _monitorRunning;
    private bool _queueCheckInProgress;
    private bool _marketRefreshInProgress;

    public MainWindow()
    {
        InitializeComponent();
        OrdersGrid.ItemsSource = _exchange.Orders;
        QueueGrid.ItemsSource = _queuedOrders;
        _monitorTimer.Tick += async (_, _) => await CheckQueueAsync(allowSubmission: true);
        _marketTimer.Tick += async (_, _) => await RefreshMarketPriceAsync();
        Loaded += async (_, _) => await StartAutomaticBybitAsync();
        Closed += (_, _) =>
        {
            _marketTimer.Stop();
            _monitorTimer.Stop();
        };
        Log("پنل در حالت Paper Trading راه‌اندازی شد؛ هیچ سفارش واقعی ارسال نمی‌شود.");
        try
        {
            foreach (var order in _queueStore.Load())
                _queuedOrders.Add(order);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            ValidationText.Text = $"بازیابی صف سفارش‌ها ناموفق بود: {exception.Message}";
            Log(ValidationText.Text);
        }
        Log($"{_queuedOrders.Count} سفارش ذخیره‌شده از صف بازیابی شد. پایش به‌صورت پیش‌فرض خاموش است.");
    }

    private async Task StartAutomaticBybitAsync()
    {
        await RunBybitActionAsync(async () =>
        {
            var status = await _bybitClient.CheckConnectionAsync();
            BybitStatusText.Text = status.Authenticated
                ? "● BYBIT DEMO: احراز هویت شد"
                : "● BYBIT DEMO: اتصال عمومی";
            BybitStatusText.Foreground = status.Authenticated
                ? System.Windows.Media.Brushes.LightGreen
                : System.Windows.Media.Brushes.Khaki;
            var equity = status.TotalEquityUsd is null ? string.Empty : $" موجودی: {status.TotalEquityUsd:N2} USD.";
            Log(status.Message + equity);
        });

        await RefreshMarketPriceAsync();
        _marketTimer.Start();
    }

    private async Task RefreshMarketPriceAsync()
    {
        if (_marketRefreshInProgress)
            return;

        _marketRefreshInProgress = true;
        try
        {
            var symbol = SymbolTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(symbol))
                return;
            var ticker = await _bybitClient.GetLastPriceAsync(symbol);
            CurrentPriceTextBox.Text = ticker.LastPrice.ToString("N4", CultureInfo.InvariantCulture);
            if (!BybitStatusText.Text.Contains("احراز هویت", StringComparison.Ordinal))
            {
                BybitStatusText.Text = "● BYBIT DEMO: متصل";
                BybitStatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException
                                          or TaskCanceledException
                                          or InvalidOperationException
                                          or ArgumentException)
        {
            CurrentPriceTextBox.Text = "خطا در دریافت قیمت";
            BybitStatusText.Text = "● BYBIT DEMO: خطا";
            BybitStatusText.Foreground = System.Windows.Media.Brushes.LightCoral;
        }
        finally
        {
            _marketRefreshInProgress = false;
        }
    }

    private async void CreateQueuedOrder_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildSignal(out var signal))
            return;

        CreateOrderButton.IsEnabled = false;
        try
        {
            var manager = new SignalManager(new CalculationService(new StrategyCalculator()));
            manager.AddSignal(signal);
            var position = new ExecutionManager().PreparePosition(signal)
                ?? throw new InvalidOperationException("ساخت سفارش برنامه‌ریزی‌شده ناموفق بود.");
            var rules = await _bybitClient.GetInstrumentRulesAsync(signal.Symbol);
            var preview = BybitOrderPreviewBuilder.Build(signal.Symbol, position, rules);
            var queuedOrder = new QueuedOrder
            {
                Symbol = preview.Symbol,
                Side = preview.Side,
                Quantity = preview.Quantity,
                EntryPrice = preview.Price,
                TakeProfit = preview.TakeProfit,
                StopLoss = preview.StopLoss,
                PositionSizeUsdt = preview.EstimatedNotional
            };

            _queuedOrders.Add(queuedOrder);
            if (!SaveQueue())
            {
                _queuedOrders.Remove(queuedOrder);
                return;
            }

            _signal = signal;
            _position = null;
            _lastPreview = null;
            ShowPlan(signal.TradePlan!);
            StatusValue.Text = "در صف سفارش‌ها";
            ValidationText.Text = string.Empty;
            QueueGrid.Items.Refresh();
            OrdersTabs.SelectedIndex = 1;
            Log($"سفارش {queuedOrder.Side} {queuedOrder.Symbol} @ {queuedOrder.EntryPrice} به صف اضافه شد.");
        }
        catch (Exception exception) when (exception is HttpRequestException
                                          or TaskCanceledException
                                          or InvalidOperationException
                                          or ArgumentException)
        {
            ValidationText.Text = exception.Message;
            Log($"ثبت سفارش ناموفق بود: {exception.Message}");
        }
        finally
        {
            CreateOrderButton.IsEnabled = true;
        }
    }

    private async void FetchBybitPrice_Click(object sender, RoutedEventArgs e)
    {
        await RunBybitActionAsync(async () =>
        {
            var ticker = await _bybitClient.GetLastPriceAsync(SymbolTextBox.Text);
            CurrentPriceTextBox.Text = ticker.LastPrice.ToString(CultureInfo.InvariantCulture);
            BybitStatusText.Text = "● BYBIT DEMO: متصل";
            BybitStatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
            Log($"قیمت {ticker.Symbol} از Bybit Demo دریافت شد: {ticker.LastPrice:N4}");
        });
    }

    private async void CheckBybitConnection_Click(object sender, RoutedEventArgs e)
    {
        await RunBybitActionAsync(async () =>
        {
            var status = await _bybitClient.CheckConnectionAsync();
            BybitStatusText.Text = status.Authenticated
                ? "● BYBIT DEMO: احراز هویت شد"
                : "● BYBIT DEMO: عمومی متصل";
            BybitStatusText.Foreground = status.Authenticated
                ? System.Windows.Media.Brushes.LightGreen
                : System.Windows.Media.Brushes.Khaki;
            var equity = status.TotalEquityUsd is null ? string.Empty : $" موجودی کل: {status.TotalEquityUsd:N2} USD.";
            Log(status.Message + equity);
        });
    }

    private void Calculate_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildSignal(out var signal))
            return;

        try
        {
            var manager = new SignalManager(new CalculationService(new StrategyCalculator()));
            manager.AddSignal(signal);
            _signal = signal;
            _position = null;
            _lastPreview = null;
            ShowPlan(signal.TradePlan!);
            StatusValue.Text = "برنامه آماده";
            ValidationText.Text = string.Empty;
            Log($"برنامه {signal.Direction} برای {signal.Symbol} محاسبه شد.");
        }
        catch (ArgumentException exception)
        {
            ValidationText.Text = exception.Message;
        }
    }

    private void Execute_Click(object sender, RoutedEventArgs e)
    {
        Calculate_Click(sender, e);
        if (_signal?.TradePlan is null || !TryDecimal(CurrentPriceTextBox.Text, out var currentPrice))
        {
            ValidationText.Text = "قیمت فعلی معتبر وارد کنید.";
            return;
        }

        if (!new EntryManager().CanOpenPosition(_signal, currentPrice))
        {
            StatusValue.Text = "منتظر ورود";
            Log($"قیمت {currentPrice:N4} هنوز به نقطه ورود نرسیده است.");
            return;
        }

        var position = new ExecutionManager().OpenPosition(_signal);
        if (position is null || !new OrderManager(_exchange).PlaceOrders(position))
        {
            StatusValue.Text = "خطای اجرا";
            ValidationText.Text = "ثبت سفارش‌های آزمایشی کامل نشد.";
            return;
        }

        OrdersGrid.Items.Refresh();
        _position = position;
        _lastPreview = null;
        StatusValue.Text = "موقعیت باز";
        Log($"موقعیت آزمایشی باز شد: {position.Quantity:N8} {position.Direction} با ارزش {position.PositionSizeUsdt:N2} USDT.");
    }

    private async void PreviewBybitOrder_Click(object sender, RoutedEventArgs e)
    {
        if (_signal?.TradePlan is null)
        {
            ValidationText.Text = "ابتدا برنامه معامله را محاسبه کنید.";
            return;
        }

        await RunBybitActionAsync(async () =>
        {
            var plannedPosition = _position ?? new ExecutionManager().PreparePosition(_signal)
                ?? throw new InvalidOperationException("ساخت موقعیت برنامه‌ریزی‌شده ناموفق بود.");
            var rules = await _bybitClient.GetInstrumentRulesAsync(SymbolTextBox.Text);
            var preview = BybitOrderPreviewBuilder.Build(SymbolTextBox.Text, plannedPosition, rules);
            _lastPreview = preview;
            Log($"پیش‌نمایش Bybit: {preview.Side} {preview.Quantity} {preview.Symbol} @ {preview.Price}، ارزش تقریبی {preview.EstimatedNotional:N2} USDT، TP={preview.TakeProfit}، SL={preview.StopLoss}. هیچ سفارشی ارسال نشد.");
        });
    }

    private async void SubmitBybitOrder_Click(object sender, RoutedEventArgs e)
    {
        if (_lastPreview is null)
        {
            ValidationText.Text = "ابتدا پیش‌نمایش سفارش Bybit را اجرا کنید.";
            return;
        }

        var preview = _lastPreview;
        var confirmation = MessageBox.Show(
            $"این سفارش فقط به Bybit Demo ارسال می‌شود.\n\n{preview.Side} {preview.Quantity} {preview.Symbol}\nLimit: {preview.Price}\nTP: {preview.TakeProfit}\nSL: {preview.StopLoss}\n\nارسال شود؟",
            "تأیید نهایی سفارش Demo",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            Log("ارسال سفارش Demo توسط کاربر لغو شد.");
            return;
        }

        await RunBybitActionAsync(async () =>
        {
            _lastDemoOrder = await _bybitClient.PlaceLimitOrderAsync(preview);
            _lastPreview = null;
            StatusValue.Text = "سفارش Demo ارسال شد";
            Log($"سفارش Bybit Demo پذیرفته شد: {_lastDemoOrder.OrderId}، {_lastDemoOrder.Side} {_lastDemoOrder.Quantity} {_lastDemoOrder.Symbol} @ {_lastDemoOrder.Price}.");
        });
    }

    private void AddToQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_lastPreview is null)
        {
            ValidationText.Text = "ابتدا پیش‌نمایش سفارش Bybit را اجرا کنید.";
            return;
        }

        var preview = _lastPreview;
        var queuedOrder = new QueuedOrder
        {
            Symbol = preview.Symbol,
            Side = preview.Side,
            Quantity = preview.Quantity,
            EntryPrice = preview.Price,
            TakeProfit = preview.TakeProfit,
            StopLoss = preview.StopLoss,
            PositionSizeUsdt = preview.EstimatedNotional
        };
        _queuedOrders.Add(queuedOrder);
        SaveQueue();
        _lastPreview = null;
        ValidationText.Text = string.Empty;
        Log($"سفارش {queuedOrder.Side} {queuedOrder.Symbol} @ {queuedOrder.EntryPrice} به صف اضافه شد.");
    }

    private async void ToggleMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (_monitorRunning)
        {
            StopMonitor();
            return;
        }

        var pendingCount = _queuedOrders.Count(order => order.Status == QueuedOrderStatus.Pending);
        if (pendingCount == 0)
        {
            ValidationText.Text = "هیچ سفارش در انتظاری برای پایش وجود ندارد.";
            return;
        }

        var confirmation = MessageBox.Show(
            $"پایش {pendingCount} سفارش فعال شود؟\n\nبا رسیدن قیمت به شرط ورود، سفارش‌ها بدون تأیید دوباره و فقط به Bybit Demo ارسال می‌شوند. برنامه باید باز بماند.",
            "فعال‌سازی پایش خودکار Demo",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
            return;

        var intervalItem = (System.Windows.Controls.ComboBoxItem)MonitorIntervalComboBox.SelectedItem;
        var intervalSeconds = int.Parse(intervalItem.Tag.ToString()!, CultureInfo.InvariantCulture);
        _monitorTimer.Interval = TimeSpan.FromSeconds(intervalSeconds);
        _monitorRunning = true;
        _monitorTimer.Start();
        ToggleMonitorButton.Content = "توقف پایش";
        ToggleMonitorButton.Background = System.Windows.Media.Brushes.Firebrick;
        QueueStatusText.Text = $"پایش فعال است؛ هر {intervalSeconds} ثانیه";
        QueueStatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
        Log("پایش خودکار صف سفارش‌های Demo فعال شد.");
        await CheckQueueAsync(allowSubmission: true);
    }

    private void StopMonitor()
    {
        _monitorRunning = false;
        _monitorTimer.Stop();
        ToggleMonitorButton.Content = "فعال‌سازی پایش";
        ToggleMonitorButton.Background = System.Windows.Media.Brushes.SeaGreen;
        QueueStatusText.Text = "پایش خاموش است";
        QueueStatusText.Foreground = System.Windows.Media.Brushes.Gold;
        Log("پایش خودکار صف متوقف شد.");
    }

    private async void CheckQueueNow_Click(object sender, RoutedEventArgs e) =>
        await CheckQueueAsync(allowSubmission: false);

    private async Task CheckQueueAsync(bool allowSubmission)
    {
        if (_queueCheckInProgress)
            return;

        _queueCheckInProgress = true;
        try
        {
            var pendingOrders = _queuedOrders
                .Where(order => order.Status == QueuedOrderStatus.Pending)
                .ToList();
            var tickers = new Dictionary<string, BybitTicker>(StringComparer.OrdinalIgnoreCase);

            foreach (var order in pendingOrders)
            {
                try
                {
                    if (!tickers.TryGetValue(order.Symbol, out var ticker))
                    {
                        ticker = await _bybitClient.GetLastPriceAsync(order.Symbol);
                        tickers[order.Symbol] = ticker;
                    }

                    order.LastPrice = ticker.LastPrice;
                    order.LastCheckedAtUtc = DateTime.UtcNow;
                    order.ErrorMessage = null;
                    if (!QueuedOrderRules.IsEntryReached(order, ticker.LastPrice))
                        continue;
                    if (!allowSubmission || !_monitorRunning)
                        continue;

                    var preview = new BybitOrderPreview(
                        order.Symbol,
                        order.Side,
                        order.Quantity,
                        order.EntryPrice,
                        order.TakeProfit,
                        order.StopLoss,
                        order.PositionSizeUsdt);
                    order.OrderLinkId ??= $"phoenix-q-{order.Id:N}"[..36];
                    if (!SaveQueue())
                    {
                        order.Status = QueuedOrderStatus.Error;
                        order.ErrorMessage = "شناسه سفارش پیش از ارسال ذخیره نشد؛ برای جلوگیری از سفارش تکراری ارسال متوقف شد.";
                        continue;
                    }
                    var result = await _bybitClient.PlaceLimitOrderAsync(preview, order.OrderLinkId);
                    order.BybitOrderId = result.OrderId;
                    order.SubmittedAtUtc = DateTime.UtcNow;
                    order.Status = QueuedOrderStatus.Submitted;
                    Log($"سفارش صف به Bybit Demo ارسال شد: {result.OrderId}، {order.Side} {order.Quantity} {order.Symbol} @ {order.EntryPrice}.");
                }
                catch (Exception exception) when (exception is HttpRequestException
                                                  or TaskCanceledException
                                                  or InvalidOperationException
                                                  or ArgumentException)
                {
                    order.Status = QueuedOrderStatus.Error;
                    order.ErrorMessage = exception.Message;
                    Log($"خطای سفارش صف {order.Symbol}: {exception.Message}");
                }
            }

            SaveQueue();
            QueueGrid.Items.Refresh();
            QueueStatusText.Text = _monitorRunning
                ? $"پایش فعال؛ آخرین بررسی {DateTime.Now:HH:mm:ss}"
                : $"بررسی دستی {DateTime.Now:HH:mm:ss}";
        }
        finally
        {
            _queueCheckInProgress = false;
        }
    }

    private void RetryQueueItem_Click(object sender, RoutedEventArgs e)
    {
        if (QueueGrid.SelectedItem is not QueuedOrder order)
            return;
        if (order.Status == QueuedOrderStatus.Submitted)
        {
            ValidationText.Text = "سفارش ارسال‌شده را نمی‌توان دوباره به صف برگرداند.";
            return;
        }

        order.Status = QueuedOrderStatus.Pending;
        order.ErrorMessage = null;
        SaveQueue();
        QueueGrid.Items.Refresh();
    }

    private void DeleteQueueItem_Click(object sender, RoutedEventArgs e)
    {
        if (QueueGrid.SelectedItem is not QueuedOrder order)
            return;
        if (order.Status == QueuedOrderStatus.Submitted)
        {
            ValidationText.Text = "این مورد قبلاً به Bybit ارسال شده است؛ حذف آن سفارش صرافی را لغو نمی‌کند.";
            return;
        }

        _queuedOrders.Remove(order);
        SaveQueue();
        Log($"سفارش {order.Symbol} از صف محلی حذف شد.");
    }

    private bool SaveQueue()
    {
        try
        {
            _queueStore.Save(_queuedOrders);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ValidationText.Text = $"ذخیره صف سفارش‌ها ناموفق بود: {exception.Message}";
            Log(ValidationText.Text);
            return false;
        }
    }

    private async void CancelBybitOrder_Click(object sender, RoutedEventArgs e)
    {
        if (_lastDemoOrder is null)
        {
            ValidationText.Text = "هنوز سفارشی از این پنل به Demo ارسال نشده است.";
            return;
        }

        var order = _lastDemoOrder;
        var confirmation = MessageBox.Show(
            $"سفارش {order.OrderId} در Bybit Demo لغو شود؟",
            "تأیید لغو سفارش Demo",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
            return;

        await RunBybitActionAsync(async () =>
        {
            var result = await _bybitClient.CancelOrderAsync(order.Symbol, order.OrderId);
            _lastDemoOrder = null;
            StatusValue.Text = "لغو سفارش درخواست شد";
            Log($"درخواست لغو سفارش Bybit Demo پذیرفته شد: {result.OrderId}.");
        });
    }

    private bool TryBuildSignal(out Signal signal)
    {
        signal = null!;
        if (string.IsNullOrWhiteSpace(SymbolTextBox.Text)
            || !TryDecimal(HighTextBox.Text, out var high)
            || !TryDecimal(LowTextBox.Text, out var low)
            || !TryDecimal(SizeTextBox.Text, out var size))
        {
            ValidationText.Text = "همه مقادیر را به‌صورت معتبر وارد کنید.";
            return false;
        }

        signal = new Signal
        {
            Id = Guid.NewGuid(), Symbol = SymbolTextBox.Text.Trim().ToUpperInvariant(),
            Direction = DirectionComboBox.SelectedIndex == 0 ? Direction.Long : Direction.Short,
            High = high, Low = low, PositionSizeUsdt = size,
            Status = SignalStatus.WaitingEntry, CreatedAt = DateTime.UtcNow
        };
        return true;
    }

    private void ShowPlan(TradePlan plan)
    {
        EntryValue.Text = plan.EntryPrice.ToString("N4");
        TakeProfitValue.Text = plan.TakeProfit.ToString("N4");
        StopLossValue.Text = plan.StopLoss1.ToString("N4");
    }

    private static bool TryDecimal(string value, out decimal result) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out result)
        || decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out result);

    private void Log(string message) => EventsList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");

    private async Task RunBybitActionAsync(Func<Task> action)
    {
        ValidationText.Text = string.Empty;
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is HttpRequestException
                                          or TaskCanceledException
                                          or InvalidOperationException
                                          or ArgumentException)
        {
            BybitStatusText.Text = "● BYBIT DEMO: خطا";
            BybitStatusText.Foreground = System.Windows.Media.Brushes.LightCoral;
            ValidationText.Text = exception.Message;
            Log($"خطای اتصال Bybit: {exception.Message}");
        }
    }
}
