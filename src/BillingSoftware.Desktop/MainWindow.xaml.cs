using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BillingSoftware.Application;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Desktop.ViewModels;
using BillingSoftware.Desktop.Views;
using BillingSoftware.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace BillingSoftware.Desktop;

public partial class MainWindow : Window
{
    private const double CompactBelow = 1000;

    private readonly IServiceProvider _sp;
    private sealed record PageInfo(string Key, string Label);
    private sealed record SectionInfo(string Key, string Label, string Glyph, List<PageInfo> Pages);

    private readonly List<SectionInfo> _sections = new();
    private readonly List<(SectionInfo Section, RadioButton Button, TextBlock Label)> _rail = new();
    private readonly Dictionary<string, RadioButton> _tabs = new();
    private readonly Dictionary<string, string> _lastPage = new();      // the page last used in each section
    private SectionInfo? _section;
    private string? _pendingKey;
    private string _currentKey = "";
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _backupTimer = new() { Interval = TimeSpan.FromHours(2) };
    private DateTime _lastActivity = DateTime.UtcNow;
    private TimeSpan _idleLimit = TimeSpan.FromMinutes(10);
    private BillingView? _billing;
    private bool _locked;
    private bool _signingOut;
    private string? _businessName;

    public event Action? SignOutRequested;

    public MainWindow(IServiceProvider sp)
    {
        InitializeComponent();
        _sp = sp;
        InputManager.Current.PreProcessInput += (_, e) =>
        {
            if (e.StagingItem.Input is MouseEventArgs or KeyboardEventArgs) _lastActivity = DateTime.UtcNow;
        };
        _idleTimer.Tick += async (_, _) => await CheckIdleAsync();
        _backupTimer.Tick += async (_, _) => await RunBackupAsync();
        InputBindings.Add(new KeyBinding(new RelayCommandLite(() => Lock_Click(this, new RoutedEventArgs())), Key.L, ModifierKeys.Control));
        for (var i = 1; i <= 9; i++)
        {
            var index = i - 1;
            InputBindings.Add(new KeyBinding(new RelayCommandLite(() => { var keys = _sections.SelectMany(s => s.Pages).ToList(); if (index < keys.Count) NavigateTo(keys[index].Key); }), Key.D1 + index, ModifierKeys.Control));
        }
    }

    public bool HasOpenBill => _billing?.DataContext is BillingViewModel { HasOpenBill: true };

    /// <summary>Called after a successful sign-in to build the menu for that user's role.</summary>
    public async Task StartSessionAsync(SessionUser user)
    {
        try { await PrintConfig.LoadAsync(_sp.GetRequiredService<ISettingsService>()); } catch (Exception ex) when (ex is BusinessRuleException) { /* defaults are used */ }
        _signingOut = false;
        _billing = null;
        NavItems.Children.Clear();
        TabItems.Children.Clear();
        _sections.Clear(); _rail.Clear(); _tabs.Clear(); _lastPage.Clear();
        _section = null; _pendingKey = null; _currentKey = "";

        // The program's sections (icons on the left) and, inside each, its pages (tabs along the top).
        var admin = user.Role == UserRole.Admin;
        _sections.Add(new SectionInfo("sell", "Sell", "", new() { new("billing", "Billing"), new("bills", "Bills") }));
        if (admin) _sections.Add(new SectionInfo("items", "Items", "", new() { new("products", "Items"), new("stock", "Stock"), new("import", "Import") }));
        _sections.Add(new SectionInfo("people", "People", "", admin
            ? new() { new("customers", "Customers"), new("credit", "Credit"), new("suppliers", "Suppliers") }
            : new() { new("customers", "Customers") }));
        if (admin)
        {
            _sections.Add(new SectionInfo("money", "Money", "", new() { new("purchases", "Purchases"), new("expenses", "Expenses"), new("dayend", "Day end") }));
            _sections.Add(new SectionInfo("accounts", "Accounts", "", new() { new("gst", "GST"), new("pnl", "Profit & loss"), new("bs", "Balance sheet"), new("opening", "Opening balances"), new("reports", "Reports") }));
            _sections.Add(new SectionInfo("settings", "Settings", "", new() { new("settings", "Settings") }));
        }

        foreach (var section in _sections)
        {
            var label = new TextBlock { Text = section.Label, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) };
            var content = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new TextBlock { Text = section.Glyph, FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont"), FontSize = 21, HorizontalAlignment = HorizontalAlignment.Center });
            content.Children.Add(label);
            var rb = new RadioButton { Style = (Style)FindResource("RailButton"), Content = content, GroupName = "rail", ToolTip = section.Label };
            System.Windows.Automation.AutomationProperties.SetName(rb, section.Label);
            var s = section;
            rb.Checked += (_, _) => ShowSection(s);
            NavItems.Children.Add(rb);
            _rail.Add((section, rb, label));
        }

        var company = await _sp.GetRequiredService<ISettingsService>().GetCompanyAsync();
        _businessName = company?.Name;
        BusinessText.Text = _businessName ?? "";
        Title = string.IsNullOrWhiteSpace(_businessName) ? "Billing Software" : $"{_businessName} - Billing Software";
        UserText.Text = $"{user.DisplayName} ({(user.Role == UserRole.Admin ? "Admin" : "Cashier")})";

        var mins = int.TryParse(await _sp.GetRequiredService<ISettingsService>().GetAsync(SettingKeys.IdleLockMinutes), out var m) ? m : 10;
        _idleLimit = TimeSpan.FromMinutes(Math.Clamp(mins, 1, 240));
        _lastActivity = DateTime.UtcNow;
        _idleTimer.Start();
        _backupTimer.Start();

        ApplyNavMode();
        NavigateTo("billing");
        _ = RunBackupAsync(onlyIfOverdue: true);
        await RefreshBannerAsync();
    }

    public void EndSession()
    {
        _idleTimer.Stop();
        _backupTimer.Stop();
        _signingOut = true;
        Host.Content = null;
        _billing = null;
    }

    /// <summary>Opens a page by its key, choosing its section and tab (used by the menu, shortcuts and the layout check).</summary>
    public void NavigateTo(string key)
    {
        var section = _sections.FirstOrDefault(s => s.Pages.Any(p => p.Key == key));
        if (section == null) return;
        if (_section == section && _tabs.TryGetValue(key, out var tab)) { tab.IsChecked = true; return; }
        _pendingKey = key;
        var rail = _rail.First(r => r.Section == section).Button;
        if (rail.IsChecked == true) ShowSection(section); else rail.IsChecked = true;
    }

    /// <summary>The key of the page that is open now.</summary>
    public string CurrentPage => _currentKey;

    /// <summary>Shows the tabs of a section and opens the page last used there (or the one asked for).</summary>
    private void ShowSection(SectionInfo section)
    {
        _section = section;
        TabItems.Children.Clear();
        _tabs.Clear();
        foreach (var page in section.Pages)
        {
            var tab = new RadioButton { Style = (Style)FindResource("TopTab"), Content = page.Label, GroupName = "tabs" };
            System.Windows.Automation.AutomationProperties.SetName(tab, page.Label);
            var key = page.Key;
            tab.Checked += (_, _) => { _lastPage[section.Key] = key; Navigate(key); };
            TabItems.Children.Add(tab);
            _tabs[page.Key] = tab;
        }
        TabStrip.Visibility = section.Pages.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        var want = _pendingKey != null && _tabs.ContainsKey(_pendingKey) ? _pendingKey
                 : _lastPage.TryGetValue(section.Key, out var last) && _tabs.ContainsKey(last) ? last
                 : section.Pages[0].Key;
        _pendingKey = null;
        _tabs[want].IsChecked = true;
    }

    private void Navigate(string key)
    {
        try
        {
            UserControl view = key switch
            {
                "billing" => _billing ??= _sp.GetRequiredService<BillingView>(),
                "bills" => _sp.GetRequiredService<BillsView>(),
                "products" => _sp.GetRequiredService<ProductsView>(),
                "stock" => _sp.GetRequiredService<StockView>(),
                "customers" => _sp.GetRequiredService<CustomersView>(),
                "purchases" => _sp.GetRequiredService<PurchasesView>(),
                "suppliers" => _sp.GetRequiredService<SuppliersView>(),
                "reports" => _sp.GetRequiredService<ReportsView>(),
                "gst" => _sp.GetRequiredService<GstView>(),
                "dayend" => _sp.GetRequiredService<DayEndView>(),
                "expenses" => _sp.GetRequiredService<ExpensesView>(),
                "pnl" => _sp.GetRequiredService<ProfitLossView>(),
                "bs" => _sp.GetRequiredService<BalanceSheetView>(),
                "credit" => _sp.GetRequiredService<CreditView>(),
                "opening" => _sp.GetRequiredService<OpeningBalancesView>(),
                "import" => _sp.GetRequiredService<ImportView>(),
                "settings" => _sp.GetRequiredService<SettingsView>(),
                _ => throw new InvalidOperationException("Unknown screen " + key),
            };
            _currentKey = key;
            Host.Content = view;
            if (key != "settings") _ = RefreshBannerAsync();
        }
        catch (Exception ex) { Ui.ShowError(this, ex); }
    }

    // ---------- responsive header and rail ----------
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyNavMode();

    private void ApplyNavMode()
    {
        var compact = ActualWidth < CompactBelow;
        NavColumn.Width = new GridLength(compact ? 66 : 80);
        // Six sections do not fit a short window at normal spacing, so tighten them when the window is short.
        var dense = ActualHeight < 640;
        foreach (var (_, button, _) in _rail)
        {
            button.Padding = dense ? new Thickness(2, 4, 2, 4) : new Thickness(2, 8, 2, 8);
            button.Margin = dense ? new Thickness(4, 1, 4, 1) : new Thickness(6, 2, 6, 2);
        }
        var labels = compact ? Visibility.Collapsed : Visibility.Visible;
        UserText.Visibility = BrandText.Visibility = labels;
        LockLabel.Visibility = SignOutLabel.Visibility = labels;
    }

    // ---------- lock, sign out ----------
    private async Task CheckIdleAsync()
    {
        if (_locked || _signingOut) return;
        if (DateTime.UtcNow - _lastActivity < _idleLimit) return;
        await LockAsync();
    }

    private void Lock_Click(object sender, RoutedEventArgs e) => _ = LockAsync();

    private async Task LockAsync()
    {
        if (_locked || _signingOut) return;
        _locked = true;
        try
        {
            var current = _sp.GetRequiredService<ICurrentUser>().User;
            if (current == null) return;
            var auth = _sp.GetRequiredService<IAuthService>();
            var win = new LoginWindow(auth, _businessName, current.Username) { Owner = this };
            Host.Visibility = Visibility.Hidden;           // hide the bill while locked
            var ok = win.ShowDialog() == true;
            Host.Visibility = Visibility.Visible;
            if (!ok) { SignOutRequested?.Invoke(); return; }
            if (win.Result?.Id != current.Id) SignOutRequested?.Invoke();   // somebody else unlocked: start a fresh session
        }
        finally { _locked = false; _lastActivity = DateTime.UtcNow; await Task.CompletedTask; }
    }

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        if (HasOpenBill && MessageBox.Show(this, "There is a bill in progress. Sign out and discard it?", "Sign out", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        SignOutRequested?.Invoke();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_signingOut) return;
        if (HasOpenBill && MessageBox.Show(this, "There is a bill in progress. Close the program and discard it?", "Close", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            e.Cancel = true;
    }

    // ---------- backup ----------
    private async Task RunBackupAsync(bool onlyIfOverdue = false)
    {
        try
        {
            var backup = _sp.GetRequiredService<IBackupService>();
            var status = await backup.GetStatusAsync();
            if (string.IsNullOrWhiteSpace(status.Folder)) { await RefreshBannerAsync(); return; }
            if (onlyIfOverdue && !status.Overdue) return;
            var result = await backup.BackupNowAsync();
            if (!result.Success) Serilog.Log.Warning("Automatic backup failed: {Message}", result.Message);
            await RefreshBannerAsync();
        }
        catch (Exception ex) { Ui.LogUnexpected(ex); }
    }

    private async Task RefreshBannerAsync()
    {
        try
        {
            if (_sp.GetRequiredService<ICurrentUser>().IsAdmin != true) { Banner.Visibility = Visibility.Collapsed; return; }
            var s = await _sp.GetRequiredService<IBackupService>().GetStatusAsync();
            if (string.IsNullOrWhiteSpace(s.Folder)) Show("Backups are not set up yet. Choose a backup folder so your data is protected.");
            else if (s.Overdue) Show("Your last good backup is more than a day old. " + s.Summary);
            else Banner.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { Ui.LogUnexpected(ex); }

        void Show(string text) { BannerText.Text = text; Banner.Visibility = Visibility.Visible; }
    }

    /// <summary>Back to the billing screen (used by the Close button on Settings).</summary>
    public void GoToBilling()
    {
        NavigateTo("billing");
    }

    private void Banner_Click(object sender, RoutedEventArgs e)
    {
        NavigateTo("settings");
    }

    private sealed class RelayCommandLite(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
