using System.Globalization;
using System.Net.Http;
using System.Windows;
using Phoenix.Core.Entities;
using Phoenix.Engine.Exchanges;
using Phoenix.Engine.Exchanges.Bybit;
using Phoenix.Engine.Managers;
using Phoenix.Engine.Services;

namespace Phoenix.Studio;

public partial class MainWindow : Window
{
    private readonly PaperExchange _exchange = new();
    private readonly BybitTestnetClient _bybitClient = new(BybitTestnetOptions.FromEnvironment());
    private Signal? _signal;

    public MainWindow()
    {
        InitializeComponent();
        OrdersGrid.ItemsSource = _exchange.Orders;
        Log("پنل در حالت Paper Trading راه‌اندازی شد؛ هیچ سفارش واقعی ارسال نمی‌شود.");
    }

    private async void FetchBybitPrice_Click(object sender, RoutedEventArgs e)
    {
        await RunBybitActionAsync(async () =>
        {
            var ticker = await _bybitClient.GetLastPriceAsync(SymbolTextBox.Text);
            CurrentPriceTextBox.Text = ticker.LastPrice.ToString(CultureInfo.InvariantCulture);
            BybitStatusText.Text = "● BYBIT TESTNET: متصل";
            BybitStatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
            Log($"قیمت {ticker.Symbol} از Bybit Testnet دریافت شد: {ticker.LastPrice:N4}");
        });
    }

    private async void CheckBybitConnection_Click(object sender, RoutedEventArgs e)
    {
        await RunBybitActionAsync(async () =>
        {
            var status = await _bybitClient.CheckConnectionAsync();
            BybitStatusText.Text = status.Authenticated
                ? "● BYBIT TESTNET: احراز هویت شد"
                : "● BYBIT TESTNET: عمومی متصل";
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
        StatusValue.Text = "موقعیت باز";
        Log($"موقعیت آزمایشی باز شد: {position.Quantity:N8} {position.Direction} با ارزش {position.PositionSizeUsdt:N2} USDT.");
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
            BybitStatusText.Text = "● BYBIT TESTNET: خطا";
            BybitStatusText.Foreground = System.Windows.Media.Brushes.LightCoral;
            ValidationText.Text = exception.Message;
            Log($"خطای اتصال Bybit: {exception.Message}");
        }
    }
}
