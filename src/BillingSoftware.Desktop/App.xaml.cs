using System.Windows;
using System.Windows.Threading;
using BillingSoftware.Application;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Desktop.ViewModels;
using BillingSoftware.Desktop.Views;
using BillingSoftware.Infrastructure;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Formatting.Compact;

namespace BillingSoftware.Desktop;

public partial class App : System.Windows.Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    private AppPaths _paths = null!;
    private MainWindow? _main;

    private const double WheelStepPixels = 36;      // one wheel notch: a gentle step (the Windows default of three lines feels fast on a long page)

    /// <summary>
    /// The wheel scrolls the nearest panel under the mouse that can scroll in that direction, and hands over to the page when that panel is at its
    /// end or has nothing to scroll. Without this, a table, list or text box under the pointer swallows the wheel and the page does not move.
    /// </summary>
    private static void OnMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.Handled || (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0 || e.Delta == 0) return;
        if (e.OriginalSource is not DependencyObject source) return;
        DependencyObject? d = source;
        while (d != null)
        {
            if (d is System.Windows.Controls.Calendar or System.Windows.Controls.Slider) return;       // these use the wheel themselves
            if (d is System.Windows.Controls.ScrollViewer sv && CanScroll(sv, e.Delta))
            {
                if (sv.CanContentScroll && sv.ExtentHeight > 0 && sv.ViewportHeight > 0 && sv.ExtentHeight <= sv.ScrollableHeight + sv.ViewportHeight + 0.5 && sv.ScrollableHeight < 5000 && IsItemScrolling(sv))
                    sv.ScrollToVerticalOffset(sv.VerticalOffset - Math.Sign(e.Delta));                      // lists and tables scroll by whole rows: one row a notch
                else
                    sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta / 120.0 * WheelStepPixels);
                e.Handled = true;
                return;
            }
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
    }

    private static bool CanScroll(System.Windows.Controls.ScrollViewer sv, int delta) =>
        sv.VerticalScrollBarVisibility != System.Windows.Controls.ScrollBarVisibility.Disabled && sv.ScrollableHeight > 0 &&
        (delta > 0 ? sv.VerticalOffset > 0 : sv.VerticalOffset < sv.ScrollableHeight);

    /// <summary>True when the offset counts items (a virtualised list) rather than pixels.</summary>
    private static bool IsItemScrolling(System.Windows.Controls.ScrollViewer sv) =>
        sv.TemplatedParent is System.Windows.Controls.Primitives.Selector or System.Windows.Controls.DataGrid &&
        System.Windows.Controls.VirtualizingPanel.GetIsVirtualizing(sv.TemplatedParent) && sv.ExtentHeight <= sv.ScrollableHeight + sv.ViewportHeight + 0.5;

    /// <summary>Starts a new copy of the program that waits for this one to close, then closes this one.</summary>
    public static void Restart()
    {
        var exe = Environment.ProcessPath;
        if (exe != null)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, $"--after-restart {Environment.ProcessId}") { UseShellExecute = true });
        Current.Shutdown();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        // Started by Restart(): wait for the previous copy to finish closing so it has let go of the data files.
        var waitIndex = Array.IndexOf(args, "--after-restart");
        if (waitIndex >= 0 && waitIndex + 1 < args.Length && int.TryParse(args[waitIndex + 1], out var oldPid))
        {
            try { System.Diagnostics.Process.GetProcessById(oldPid).WaitForExit(30_000); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { /* it has already gone */ }
        }

        EventManager.RegisterClassHandler(typeof(UIElement), UIElement.PreviewMouseWheelEvent, new System.Windows.Input.MouseWheelEventHandler(OnMouseWheel));

        // A WPF table that has a stretch column squeezes its fixed-width columns below their size instead of scrolling sideways
        // (headers wrap letter by letter, values are cut off). Make every fixed column keep its width, for all tables in the app.
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.DataGrid), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is not System.Windows.Controls.DataGrid grid) return;
            // Text that does not fit a cell ends in "..." and shows in full when the mouse rests on it, instead of being cut off silently.
            foreach (var textColumn in grid.Columns.OfType<System.Windows.Controls.DataGridTextColumn>())
            {
                var baseStyle = textColumn.ElementStyle ?? System.Windows.Controls.DataGridTextColumn.DefaultElementStyle;
                if (baseStyle.Setters.OfType<Setter>().Any(x => x.Property == System.Windows.Controls.TextBlock.TextTrimmingProperty)) continue;
                var trimmed = new Style(typeof(System.Windows.Controls.TextBlock), baseStyle);
                trimmed.Setters.Add(new Setter(System.Windows.Controls.TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
                trimmed.Setters.Add(new Setter(System.Windows.Controls.TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
                trimmed.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("Text") { RelativeSource = System.Windows.Data.RelativeSource.Self }));
                textColumn.ElementStyle = trimmed;
            }
            var changed = false;
            foreach (var col in grid.Columns)
                if (col.Width.IsAbsolute && col.MinWidth < col.Width.Value) { col.MinWidth = col.Width.Value; changed = true; }
            if (!changed) return;
            // WPF has already shared out the width once; nudge each fixed column so it is recalculated with the new minimum.
            foreach (var col in grid.Columns.Where(c => c.Width.IsAbsolute))
            {
                var w = col.Width;
                col.Width = new System.Windows.Controls.DataGridLength(w.Value + 1);
                col.Width = w;
            }
            grid.UpdateLayout();
        }));

        // --selftest <result file>: drive the billing screen logic end to end on a throw-away database
        var testIndex = Array.IndexOf(args, "--selftest");
        if (testIndex >= 0 && testIndex + 1 < args.Length)
        {
            int failures;
            try { failures = SelfTest.Run(args[testIndex + 1]); }
            catch (Exception ex) { File.WriteAllText(args[testIndex + 1], "FAIL  " + ex); failures = 99; }
            Shutdown(failures);
            return;
        }

        // --probe <folder> <kind> <width> <height>: render one window at one size (used to chase layout problems)
        var probeIndex = Array.IndexOf(args, "--probe");
        if (probeIndex >= 0 && probeIndex + 4 < args.Length)
        {
            try { ScreenshotMode.Probe(args[probeIndex + 1], args[probeIndex + 2], int.Parse(args[probeIndex + 3]), int.Parse(args[probeIndex + 4])); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(args[probeIndex + 1], "error.txt"), ex.ToString()); }
            Shutdown();
            return;
        }

        // --screenshots <folder>: render every screen at several window sizes into PNG files (used for layout checks).
        var shotIndex = Array.IndexOf(args, "--screenshots");
        if (shotIndex >= 0 && shotIndex + 1 < args.Length)
        {
            try { ScreenshotMode.Run(args[shotIndex + 1]); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(args[shotIndex + 1], "error.txt"), ex.ToString()); }
            Shutdown();
            return;
        }

        try
        {
            _paths = AppPaths.ResolveDefault();
            ConfigureLogging(_paths);
            DispatcherUnhandledException += OnUiException;
            AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Fatal(a.ExceptionObject as Exception, "Unhandled exception, process ending");
            TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error(a.Exception, "Unobserved task exception"); a.SetObserved(); };
            Log.Information("Starting Billing Software {Version}", typeof(App).Assembly.GetName().Version);

            if (!StartServices()) { Shutdown(); return; }
            if (InfrastructureBootstrap.RestoreMessage is { Length: > 0 } restoreMessage)
                MessageBox.Show(restoreMessage, "Restore", MessageBoxButton.OK, restoreMessage.StartsWith("The backup") ? MessageBoxImage.Information : MessageBoxImage.Warning);
            RunSession();
        }
        catch (Exception ex)
        {
            var id = Ui.LogUnexpected(ex);
            MessageBox.Show($"The program could not start.\n\nError ID: {id}", "Billing Software", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private bool StartServices()
    {
        string? recovery = null;
        while (true)
        {
            var sc = new ServiceCollection();
            var result = InfrastructureBootstrap.Start(_paths, sc, recovery);
            if (result.Ok)
            {
                RegisterUi(sc);
                Services = sc.BuildServiceProvider();
                return true;
            }
            if (!result.NeedsRecoveryKey)
            {
                MessageBox.Show(result.Error, "Billing Software", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            var win = new RecoveryEntryWindow(result.Error ?? "Enter the recovery key to continue.");
            if (win.ShowDialog() != true) return false;
            recovery = win.Key;
        }
    }

    public static void RegisterUi(IServiceCollection sc)
    {
        sc.AddSingleton<IUiHost, UiHost>();
        sc.AddTransient<BillingViewModel>(); sc.AddTransient<BillingView>();
        sc.AddTransient<BillsViewModel>(); sc.AddTransient<BillsView>();
        sc.AddTransient<ProductsViewModel>(); sc.AddTransient<ProductsView>();
        sc.AddTransient<StockViewModel>(); sc.AddTransient<StockView>();
        sc.AddTransient<CustomersViewModel>(); sc.AddTransient<CustomersView>();
        sc.AddTransient<ReportsViewModel>(); sc.AddTransient<ReportsView>();
        sc.AddTransient<SettingsViewModel>(); sc.AddTransient<SettingsView>();
        sc.AddTransient<SuppliersViewModel>(); sc.AddTransient<SuppliersView>();
        sc.AddTransient<GstViewModel>(); sc.AddTransient<GstView>();
        sc.AddTransient<DayEndViewModel>(); sc.AddTransient<DayEndView>();
        sc.AddTransient<ProfitLossViewModel>(); sc.AddTransient<ProfitLossView>();
        sc.AddTransient<ExpensesViewModel>(); sc.AddTransient<ExpensesView>();
        sc.AddTransient<BalanceSheetViewModel>(); sc.AddTransient<BalanceSheetView>();
        sc.AddTransient<OpeningBalancesViewModel>(); sc.AddTransient<OpeningBalancesView>();
        sc.AddTransient<CreditViewModel>(); sc.AddTransient<CreditView>();
        sc.AddTransient<ImportViewModel>(); sc.AddTransient<ImportView>();
        sc.AddTransient<PurchasesViewModel>(); sc.AddTransient<PurchasesView>();
        sc.AddTransient<PurchaseEntryViewModel>(); sc.AddTransient<PurchaseEntryWindow>();
    }

    private void RunSession()
    {
        var auth = Services.GetRequiredService<IAuthService>();
        var settings = Services.GetRequiredService<ISettingsService>();
        var current = Services.GetRequiredService<ICurrentUser>();

        if (auth.IsFirstRunAsync().GetAwaiter().GetResult())
        {
            if (new SetupWindow(auth, settings, _paths).ShowDialog() != true) { Shutdown(); return; }
            // The key exists from the first start; make the owner save it before going on.
            var key = InfrastructureBootstrap.GetRecoveryKey(_paths);
            new RecoveryKeyWindow(key).ShowDialog();
        }
        else
        {
            var company = settings.GetCompanyAsync().GetAwaiter().GetResult();
            var login = new LoginWindow(auth, company?.Name);
            if (login.ShowDialog() != true) { Shutdown(); return; }
        }

        _main = new MainWindow(Services);
        MainWindow = _main;
        _main.SignOutRequested += OnSignOutRequested;
        _main.Closed += (_, _) => { if (!_switching) Shutdown(); };
        _main.Show();
        _ = _main.StartSessionAsync(current.User!);
    }

    private bool _switching;

    private void OnSignOutRequested()
    {
        if (_main == null || _switching) return;
        _switching = true;
        try
        {
            _main.EndSession();
            var auth = Services.GetRequiredService<IAuthService>();
            var current = Services.GetRequiredService<ICurrentUser>();
            current.SignOut();
            _main.Hide();
            var company = Services.GetRequiredService<ISettingsService>().GetCompanyAsync().GetAwaiter().GetResult();
            var login = new LoginWindow(auth, company?.Name);
            if (login.ShowDialog() != true) { _main.Close(); Shutdown(); return; }
            _main.Show();
            _ = _main.StartSessionAsync(login.Result!);
        }
        finally { _switching = false; }
    }

    private static void ConfigureLogging(AppPaths paths)
    {
        // Structured JSON logs, capped in size and retention. Customer names, phone numbers and bill contents are never logged.
        var installId = GetInstallId(paths);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("InstallId", installId)
            .Enrich.WithProperty("AppVersion", typeof(App).Assembly.GetName().Version?.ToString())
            .WriteTo.File(new CompactJsonFormatter(), Path.Combine(paths.LogsDir, "app-.json"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30, fileSizeLimitBytes: 10_000_000, rollOnFileSizeLimit: true)
            .CreateLogger();
    }

    private static string GetInstallId(AppPaths paths)
    {
        var file = Path.Combine(paths.DataDir, "install.id");
        try
        {
            if (File.Exists(file)) return File.ReadAllText(file).Trim();
            var id = Guid.NewGuid().ToString("N");
            File.WriteAllText(file, id);
            return id;
        }
        catch (IOException) { return "unknown"; }
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Ui.ShowError(Windows.OfType<Window>().FirstOrDefault(w => w.IsActive), e.Exception);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Billing Software closed");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
