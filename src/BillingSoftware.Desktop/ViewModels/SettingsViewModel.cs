using System.Collections.ObjectModel;
using System.Globalization;
using BillingSoftware.Application;
using BillingSoftware.Desktop.Services;
using BillingSoftware.Desktop.Views;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure;
using BillingSoftware.Infrastructure.Persistence;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BillingSoftware.Desktop.ViewModels;

public sealed class UserRow(UserDto u)
{
    public UserDto User { get; } = u;
    public string Username => u.Username;
    public string DisplayName => u.DisplayName;
    public string RoleText => u.Role == UserRole.Admin ? "Admin" : "Cashier";
    public string ActiveText => u.IsActive ? "Active" : "Disabled";
}

public sealed record HealthRow(HealthCheck Check)
{
    public string Name => Check.Name;
    public string Message => Check.Message;
    public string LevelText => Check.Level switch { CheckLevel.Ok => "OK", CheckLevel.Warning => "Check", _ => "PROBLEM" };
    public bool IsProblem => Check.Level == CheckLevel.Problem;
    public bool IsWarning => Check.Level == CheckLevel.Warning;
}

public sealed record PrinterChoice(string Name, string Label);
public sealed record PaperChoice(int Mm, string Label);
public sealed record LabelModeChoice(string Mode, string Label);

public sealed class BackupRow(BackupFileInfo b)
{
    public BackupFileInfo Info { get; } = b;
    public string Text => $"{b.CreatedLocal:dd-MMM-yyyy HH:mm}    {b.Bytes / 1024.0 / 1024.0:0.0} MB    {b.FileName}";
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IAuthService _auth;
    private readonly IBackupService _backup;
    private readonly IRestoreService _restore;
    private readonly IPeriodLockService _lock;
    private readonly IDiagnosticsService _diag;
    private readonly IAuditService _audit;
    private readonly AppPaths _paths;
    private readonly IUiHost _ui;

    public SettingsViewModel(ISettingsService settings, IAuthService auth, IBackupService backup, IRestoreService restore, IPeriodLockService periodLock, IDiagnosticsService diag, IAuditService audit, AppPaths paths, IUiHost ui)
    { _settings = settings; _auth = auth; _backup = backup; _restore = restore; _lock = periodLock; _diag = diag; _audit = audit; _paths = paths; _ui = ui; }

    public IReadOnlyList<StateItem> States => StateCodes.All;
    public ObservableCollection<UserRow> Users { get; } = new();
    public ObservableCollection<BackupRow> Backups { get; } = new();

    // business
    [ObservableProperty] private string _businessName = "";
    [ObservableProperty] private StateItem? _state;
    [ObservableProperty] private string _gstin = "";
    [ObservableProperty] private bool _isComposition;
    [ObservableProperty] private bool _gstEnabled = true;
    private bool _loadedGstEnabled = true;
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _phone = "";
    [ObservableProperty] private string _businessStatus = "";
    [ObservableProperty] private bool _businessIsError;

    // rules
    [ObservableProperty] private string _discountLimitText = "0";
    [ObservableProperty] private string _idleMinutesText = "10";
    [ObservableProperty] private string _largeInvoiceLimitText = "100000";
    [ObservableProperty] private string _rulesStatus = "";
    [ObservableProperty] private bool _rulesIsError;

    // backup
    [ObservableProperty] private string _backupFolder = "";
    [ObservableProperty] private string _backupStatus = "";
    [ObservableProperty] private bool _backupIsError;

    // health check and support file
    public ObservableCollection<HealthRow> Health { get; } = new();
    [ObservableProperty] private string _healthStatus = "";
    [ObservableProperty] private bool _healthIsError;

    // printers
    public ObservableCollection<PrinterChoice> Printers { get; } = new();
    public IReadOnlyList<PaperChoice> PaperChoices { get; } = new[] { new PaperChoice(80, "80 mm roll"), new PaperChoice(58, "58 mm roll") };
    public IReadOnlyList<LabelModeChoice> LabelModes { get; } = new[] { new LabelModeChoice("roll", "Label printer: one label per label"), new LabelModeChoice("sheet", "A4 sheet of labels") };
    [ObservableProperty] private PrinterChoice? _receiptPrinter;
    [ObservableProperty] private PrinterChoice? _documentPrinter;
    [ObservableProperty] private PrinterChoice? _labelPrinter;
    [ObservableProperty] private PaperChoice? _paper;
    [ObservableProperty] private LabelModeChoice? _labelMode;
    [ObservableProperty] private string _labelWidthText = "50";
    [ObservableProperty] private string _labelHeightText = "25";
    [ObservableProperty] private string _printStatus = "";
    [ObservableProperty] private bool _printIsError;

    // period lock
    [ObservableProperty] private DateTime? _lockDate;
    [ObservableProperty] private string _lockSummary = "";
    [ObservableProperty] private string _lockReason = "";
    [ObservableProperty] private string _lockStatus = "";
    [ObservableProperty] private bool _lockIsError;
    public bool HasLock => _lockedThrough != null;
    private DateOnly? _lockedThrough;

    // restore
    [ObservableProperty] private BackupRow? _selectedBackup;
    [ObservableProperty] private string _restoreText = "";
    [ObservableProperty] private bool _restoreCanRun;
    [ObservableProperty] private string _restoreStatus = "";
    [ObservableProperty] private bool _restoreIsError;
    private string? _restoreFile;

    // users
    [ObservableProperty] private UserRow? _selectedUser;
    [ObservableProperty] private string _newUsername = "";
    [ObservableProperty] private string _newDisplayName = "";
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private string _usersStatus = "";
    [ObservableProperty] private bool _usersIsError;

    // security
    [ObservableProperty] private string _securityStatus = "";
    [ObservableProperty] private bool _securityIsError;

    public async Task LoadAsync()
    {
        try
        {
            var c = await _settings.GetCompanyAsync();
            if (c != null)
            {
                BusinessName = c.Name; State = StateCodes.All.FirstOrDefault(s => s.Code == c.StateCode);
                Gstin = c.Gstin ?? ""; IsComposition = c.IsComposition; GstEnabled = _loadedGstEnabled = c.GstEnabled; Address = c.Address ?? ""; Phone = c.Phone ?? "";
            }
            var limitBp = int.TryParse(await _settings.GetAsync(SettingKeys.CashierMaxDiscountBp), out var bp) ? bp : 0;
            DiscountLimitText = (limitBp / 100m).ToString("0.##", CultureInfo.CurrentCulture);
            IdleMinutesText = await _settings.GetAsync(SettingKeys.IdleLockMinutes) ?? "10";
            var limit = long.TryParse(await _settings.GetAsync(SettingKeys.B2ClThresholdPaise), out var lp) ? lp : 10_000_000;
            LargeInvoiceLimitText = (limit / 100m).ToString("0.##", CultureInfo.CurrentCulture);
            BackupFolder = await _settings.GetAsync(SettingKeys.BackupFolder) ?? "";
            await RefreshBackupStatusAsync();
            await LoadBackupsAsync();
            await LoadPrintersAsync();
            await LoadLockAsync();
            await LoadUsersAsync();
        }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    private async Task LoadUsersAsync()
    {
        Users.Clear();
        foreach (var u in await _auth.ListUsersAsync()) Users.Add(new UserRow(u));
    }

    [RelayCommand]
    private async Task SaveBusinessAsync()
    {
        if (State == null) { Set("business", "Choose the state.", true); return; }
        if (GstEnabled != _loadedGstEnabled && !_ui.Confirm(GstEnabled
                ? "Turn GST on?\n\nBills made from now on will be tax invoices with GST. Earlier bills do not change."
                : "Turn GST off?\n\nBills made from now on will be bills of supply with no GST, no input credit is claimed, and the GST worksheets are not available. Earlier bills do not change.\n\nOnly do this if the business does not charge GST. Tell your accountant.", "GST")) return;
        try
        {
            await _settings.SaveCompanyAsync(new CompanyDto(BusinessName, string.IsNullOrWhiteSpace(Gstin) ? null : Gstin, State.Code, IsComposition && GstEnabled, Address, Phone, GstEnabled));
            _loadedGstEnabled = GstEnabled;
            if (!GstEnabled) IsComposition = false;
            Set("business", "Business details saved. New bills use them straight away.", false);
        }
        catch (BusinessRuleException ex) { Set("business", ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand]
    private async Task SaveRulesAsync()
    {
        if (!decimal.TryParse(DiscountLimitText.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var pct) || pct < 0 || pct > 100)
        { Set("rules", "Discount limit must be a number between 0 and 100.", true); return; }
        if (!int.TryParse(IdleMinutesText.Trim(), out var mins) || mins < 1 || mins > 240)
        { Set("rules", "Auto-lock time must be 1 to 240 minutes.", true); return; }
        if (!decimal.TryParse(LargeInvoiceLimitText.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var limitRupees) || limitRupees < 1)
        { Set("rules", "The large-invoice limit must be a number of rupees.", true); return; }
        try
        {
            await _settings.SetAsync(SettingKeys.B2ClThresholdPaise, Money.ToPaise(limitRupees).ToString(CultureInfo.InvariantCulture));
            await _settings.SetAsync(SettingKeys.CashierMaxDiscountBp, ((int)Math.Round(pct * 100)).ToString(CultureInfo.InvariantCulture));
            await _settings.SetAsync(SettingKeys.IdleLockMinutes, mins.ToString(CultureInfo.InvariantCulture));
            Set("rules", "Saved.", false);
        }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    // ---------- backup ----------
    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        var folder = _ui.PickFolder(BackupFolder);
        if (folder == null) return;
        BackupFolder = folder;
        try
        {
            await _settings.SetAsync(SettingKeys.BackupFolder, folder);
            await RefreshBackupStatusAsync();
        }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    /// <summary>Opens the backup folder in Windows Explorer, so the files can be copied to a USB drive or another computer.</summary>
    [RelayCommand]
    private void OpenBackupFolder()
    {
        if (string.IsNullOrWhiteSpace(BackupFolder) || !Directory.Exists(BackupFolder)) { Set("backup", "The backup folder does not exist yet. Press Back up now to create it.", true); return; }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{BackupFolder}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Set("backup", "The folder could not be opened.", true); }
    }

    [RelayCommand]
    private async Task BackupNowAsync()
    {
        try
        {
            var r = await _backup.BackupNowAsync(string.IsNullOrWhiteSpace(BackupFolder) ? null : BackupFolder);
            await RefreshBackupStatusAsync();
            if (!r.Success) Set("backup", r.Message, true);
            else Set("backup", r.Message + " " + BackupStatus, false);
        }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    private async Task RefreshBackupStatusAsync()
    {
        var s = await _backup.GetStatusAsync();
        Set("backup", s.Summary, s.Overdue);
    }

    // ---------- health check and support file ----------
    [RelayCommand]
    private async Task RunHealthAsync()
    {
        HealthStatus = "Checking..."; HealthIsError = false;
        try
        {
            Health.Clear();
            foreach (var c in await _diag.RunAsync()) Health.Add(new HealthRow(c));
            var problems = Health.Count(h => h.IsProblem); var warnings = Health.Count(h => h.IsWarning);
            HealthStatus = problems > 0 ? $"{problems} problem{(problems == 1 ? "" : "s")} found. Send a support file if you need help."
                         : warnings > 0 ? $"No problems, {warnings} thing{(warnings == 1 ? "" : "s")} to look at." : "Everything is fine.";
            HealthIsError = problems > 0;
        }
        catch (Exception ex) { _ui.ShowError(ex); HealthStatus = ""; }
    }

    [RelayCommand]
    private async Task SaveSupportFileAsync()
    {
        var path = _ui.PickSaveFile($"support-{DateTime.Now:yyyy-MM-dd-HHmm}.zip", "Zip file (*.zip)|*.zip");
        if (path == null) return;
        try
        {
            await _diag.CreateSupportFileAsync(path);
            HealthStatus = "Saved. Send this file to support. It has no bills, customer details, passwords or keys."; HealthIsError = false;
        }
        catch (BusinessRuleException ex) { HealthStatus = ex.Message; HealthIsError = true; }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    // ---------- printers ----------
    private async Task LoadPrintersAsync()
    {
        await PrintConfig.LoadAsync(_settings);
        Printers.Clear();
        Printers.Add(new PrinterChoice("", "(Windows default printer)"));
        foreach (var n in PrintConfig.InstalledPrinters()) Printers.Add(new PrinterChoice(n, n));
        PrinterChoice Pick(string? name) => Printers.FirstOrDefault(p => p.Name == (name ?? "")) ?? (string.IsNullOrWhiteSpace(name) ? Printers[0] : AddMissing(name!));
        PrinterChoice AddMissing(string name) { var c = new PrinterChoice(name, name + " (not found on this computer)"); Printers.Add(c); return c; }
        ReceiptPrinter = Pick(PrintConfig.ReceiptPrinter);
        DocumentPrinter = Pick(PrintConfig.DocumentPrinter);
        LabelPrinter = Pick(PrintConfig.LabelPrinter);
        Paper = PaperChoices.First(p => p.Mm == PrintConfig.PaperMm);
        LabelMode = LabelModes.First(m => m.Mode == PrintConfig.LabelMode);
        LabelWidthText = PrintConfig.LabelWidthMm.ToString(CultureInfo.InvariantCulture);
        LabelHeightText = PrintConfig.LabelHeightMm.ToString(CultureInfo.InvariantCulture);
    }

    [RelayCommand]
    private async Task SavePrintersAsync()
    {
        if (!int.TryParse(LabelWidthText.Trim(), out var w) || w < 20 || w > 120) { PrintStatus = "The label width must be 20 to 120 mm."; PrintIsError = true; return; }
        if (!int.TryParse(LabelHeightText.Trim(), out var h) || h < 15 || h > 120) { PrintStatus = "The label height must be 15 to 120 mm."; PrintIsError = true; return; }
        try
        {
            await _settings.SetAsync(SettingKeys.PrintReceiptPrinter, ReceiptPrinter?.Name ?? "");
            await _settings.SetAsync(SettingKeys.PrintDocumentPrinter, DocumentPrinter?.Name ?? "");
            await _settings.SetAsync(SettingKeys.PrintLabelPrinter, LabelPrinter?.Name ?? "");
            await _settings.SetAsync(SettingKeys.PrintPaperMm, (Paper?.Mm ?? 80).ToString(CultureInfo.InvariantCulture));
            await _settings.SetAsync(SettingKeys.PrintLabelMode, LabelMode?.Mode ?? "roll");
            await _settings.SetAsync(SettingKeys.PrintLabelWidthMm, w.ToString(CultureInfo.InvariantCulture));
            await _settings.SetAsync(SettingKeys.PrintLabelHeightMm, h.ToString(CultureInfo.InvariantCulture));
            await PrintConfig.LoadAsync(_settings);
            PrintStatus = "Saved. Use \"Print a test receipt\" to check the receipt printer."; PrintIsError = false;
        }
        catch (BusinessRuleException ex) { PrintStatus = ex.Message; PrintIsError = true; }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    /// <summary>A short receipt that shows the paper width, text sizes and a barcode, to check a printer without making a sale.</summary>
    [RelayCommand]
    private async Task TestPrintAsync()
    {
        await SavePrintersAsync();
        if (PrintIsError) return;
        var company = await _settings.GetCompanyAsync();
        _ui.ShowDocument("Test receipt", () => ReceiptBuilder.BuildTest(company?.Name));
    }

    // ---------- period lock ----------
    private async Task LoadLockAsync()
    {
        var s = await _lock.GetAsync();
        _lockedThrough = s.LockedThrough;
        LockSummary = s.Summary;
        LockDate = s.LockedThrough?.ToDateTime(TimeOnly.MinValue);
        OnPropertyChanged(nameof(HasLock));
        RemoveLockCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task SaveLockAsync()
    {
        if (LockDate is not { } d) { LockStatus = "Choose the date to lock up to."; LockIsError = true; return; }
        var date = DateOnly.FromDateTime(d);
        string? reason = null;
        if (_lockedThrough is { } old && date < old)
        {
            reason = string.IsNullOrWhiteSpace(LockReason) ? _ui.Prompt("Why is the lock being moved back?", "Move the lock back") : LockReason;
            if (string.IsNullOrWhiteSpace(reason)) return;
        }
        var pw = _ui.Prompt($"Enter the Admin password to lock everything up to {date:dd-MMM-yyyy}:", "Lock the period", isPassword: true);
        if (string.IsNullOrEmpty(pw)) return;
        try
        {
            await _lock.SetAsync(date, pw, reason);
            LockReason = "";
            await LoadLockAsync();
            LockStatus = "Saved."; LockIsError = false;
        }
        catch (BusinessRuleException ex) { LockStatus = ex.Message; LockIsError = true; }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand(CanExecute = nameof(HasLock))]
    private async Task RemoveLockAsync()
    {
        var reason = string.IsNullOrWhiteSpace(LockReason) ? _ui.Prompt("Why is the lock being removed?", "Remove the lock") : LockReason;
        if (string.IsNullOrWhiteSpace(reason)) return;
        var pw = _ui.Prompt("Enter the Admin password to remove the lock:", "Remove the lock", isPassword: true);
        if (string.IsNullOrEmpty(pw)) return;
        try
        {
            await _lock.SetAsync(null, pw, reason);
            LockReason = "";
            await LoadLockAsync();
            LockStatus = "The lock was removed."; LockIsError = false;
        }
        catch (BusinessRuleException ex) { LockStatus = ex.Message; LockIsError = true; }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    // ---------- restore ----------
    private async Task LoadBackupsAsync()
    {
        try
        {
            Backups.Clear();
            foreach (var b in await _restore.ListBackupsAsync()) Backups.Add(new BackupRow(b));
        }
        catch (PermissionDeniedException) { /* only the Admin sees this section */ }
    }

    partial void OnSelectedBackupChanged(BackupRow? value)
    {
        if (value != null) _ = PreviewAsync(value.Info.Path);
    }

    private async Task PreviewAsync(string file)
    {
        _restoreFile = file;
        RestoreCanRun = false; RestoreText = "Checking the backup...";
        try
        {
            var p = await _restore.PreviewAsync(file);
            if (_restoreFile != file) return;
            var lines = new List<string>();
            lines.Add(Path.GetFileName(file));
            if (!p.CanRestore) { RestoreText = lines[0] + "\n" + p.Message; RestoreIsError = true; RestoreStatus = ""; return; }
            lines.Add(p.BackupTakenLocal.HasValue ? $"Made on {p.BackupTakenLocal:dd-MMM-yyyy} at {p.BackupTakenLocal:HH:mm}" : "Made at an unknown time");
            lines.Add($"The backup holds {p.BillCount} bill{(p.BillCount == 1 ? "" : "s")}" + (p.LastBillNumber != null ? $", the last one {p.LastBillNumber} on {p.LastBillDate:dd-MMM-yyyy}." : "."));
            lines.Add($"Your data now holds {p.CurrentBillCount} bill{(p.CurrentBillCount == 1 ? "" : "s")}" + (p.CurrentLastBillNumber != null ? $", the last one {p.CurrentLastBillNumber} on {p.CurrentLastBillDate:dd-MMM-yyyy}." : "."));
            lines.Add(p.ChecksumVerified ? "The file was checked and is exactly as it was when the backup was made." : "The file was checked for damage.");
            foreach (var w in p.Warnings) lines.Add("Note: " + w);
            RestoreText = string.Join("\n", lines);
            RestoreIsError = false; RestoreStatus = "";
            RestoreCanRun = true;
        }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand]
    private async Task RefreshBackupsAsync() => await LoadBackupsAsync();

    [RelayCommand]
    private async Task BrowseBackupAsync()
    {
        var file = _ui.PickOpenFile("Choose a backup file", "Backup files (*.db)|*.db|All files (*.*)|*.*");
        if (file == null) return;
        SelectedBackup = null;
        await PreviewAsync(file);
    }

    [RelayCommand(CanExecute = nameof(RestoreCanRun))]
    private async Task RestoreNowAsync()
    {
        if (_restoreFile == null) return;
        if (!_ui.Confirm("Restoring replaces today's data with this backup.\n\nToday's data is first saved as a safety copy, and the program restarts to finish.\n\nContinue?", "Restore from a backup")) return;
        var pw = _ui.Prompt("Enter the Admin password to restore this backup:", "Restore", isPassword: true);
        if (string.IsNullOrEmpty(pw)) return;
        try
        {
            await _restore.ScheduleAsync(_restoreFile, pw);
            _ui.Info("The backup is ready. The program will now restart to finish the restore.", "Restore");
            _ui.RestartApplication();
        }
        catch (BusinessRuleException ex) { RestoreStatus = ex.Message; RestoreIsError = true; }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    partial void OnRestoreCanRunChanged(bool value) => RestoreNowCommand.NotifyCanExecuteChanged();

    // ---------- users ----------
    [RelayCommand]
    private async Task AddCashierAsync()
    {
        try
        {
            await _auth.CreateCashierAsync(NewUsername, NewDisplayName, NewPassword);
            NewUsername = NewDisplayName = NewPassword = "";
            await LoadUsersAsync();
            Set("users", "Cashier added. Each person who bills should have their own login.", false);
        }
        catch (BusinessRuleException ex) { Set("users", ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand]
    private async Task ResetPasswordAsync()
    {
        if (SelectedUser == null) { Set("users", "Select a user first.", true); return; }
        var pw = _ui.Prompt($"New password for {SelectedUser.Username} (at least 8 characters):", "Reset password", isPassword: true);
        if (string.IsNullOrEmpty(pw)) return;
        try
        {
            await _auth.ResetPasswordAsync(SelectedUser.User.Id, pw);
            Set("users", "Password changed.", false);
        }
        catch (BusinessRuleException ex) { Set("users", ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand]
    private async Task ToggleActiveAsync()
    {
        if (SelectedUser == null) { Set("users", "Select a user first.", true); return; }
        try
        {
            await _auth.SetActiveAsync(SelectedUser.User.Id, !SelectedUser.User.IsActive);
            await LoadUsersAsync();
            Set("users", "Updated.", false);
        }
        catch (BusinessRuleException ex) { Set("users", ex.Message, true); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    // ---------- security ----------
    [RelayCommand]
    private async Task VerifyAuditAsync()
    {
        try
        {
            var (valid, broken) = await _audit.VerifyChainAsync();
            Set("security",
                valid ?"Activity log is intact: no record has been changed." : $"WARNING: the activity log was altered at record {broken}. Contact support.", !valid);
        }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    [RelayCommand]
    private async Task ShowRecoveryKeyAsync()
    {
        // Re-check the Admin password before showing the key again.
        var approver = await _ui.RequestApprovalAsync("Enter the Admin password to view the recovery key.");
        if (approver == null) return;
        try { new RecoveryKeyWindow(InfrastructureBootstrap.GetRecoveryKey(_paths)) { Owner = System.Windows.Application.Current.MainWindow }.ShowDialog(); }
        catch (Exception ex) { _ui.ShowError(ex); }
    }

    private void Set(string section, string message, bool error)
    {
        switch (section)
        {
            case "business": BusinessStatus = message; BusinessIsError = error; break;
            case "rules": RulesStatus = message; RulesIsError = error; break;
            case "backup": BackupStatus = message; BackupIsError = error; break;
            case "users": UsersStatus = message; UsersIsError = error; break;
            default: SecurityStatus = message; SecurityIsError = error; break;
        }
    }
}
