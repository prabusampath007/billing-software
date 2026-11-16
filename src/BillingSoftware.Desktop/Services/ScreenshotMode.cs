using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace BillingSoftware.Desktop.Services;

/// <summary>
/// Layout check: creates a throw-away encrypted database with demo data, then renders each screen at several
/// window sizes to PNG files. Never touches the real shop data.
/// </summary>
internal static class ScreenshotMode
{
    private static readonly (int W, int H)[] Sizes = { (800, 560), (1024, 768), (1366, 768), (1920, 1080), (2560, 1440) };

    public static void Run(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var dataDir = Path.Combine(Path.GetTempPath(), "billing-shots-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(dataDir);
        var sc = new ServiceCollection();
        var result = InfrastructureBootstrap.Start(paths, sc);
        if (!result.Ok) throw new InvalidOperationException(result.Error);
        App.RegisterUi(sc);
        var sp = sc.BuildServiceProvider();
        SeedAsync(sp).GetAwaiter().GetResult();

        // Public windows first
        Capture(new Views.SetupWindow(sp.GetRequiredService<IAuthService>(), sp.GetRequiredService<ISettingsService>()), outputDir, "setup", new[] { (700, 760), (420, 560) });
        Capture(new Views.LoginWindow(sp.GetRequiredService<IAuthService>(), "Test Stores"), outputDir, "login", new[] { (460, 420) });
        Capture(new Views.RecoveryKeyWindow("ABCD-EF01-2345-6789-ABCD-EF01-2345-6789-ABCD-EF01-2345-6789-ABCD-EF01-2345-6789"), outputDir, "recovery", new[] { (640, 420) });
        var labelSpecs = new[] { ("Lux Soap 100g", "₹45.00", Ean13.FromSequence(1)), ("Tata Salt 250 g pack", "₹7.00", "4006381333931"), ("Basmati Rice 5kg", "₹680.00", "5901234123457") }
            .Select(t => new LabelSpec("Sri Lakshmi Departmental Store", t.Item1, t.Item2, t.Item3)).ToList();
        Capture(new Views.ReceiptWindow("Barcode labels", () => LabelBuilder.Build(labelSpecs)), outputDir, "labels", new[] { (900, 600) });
        var stmt = new CustomerStatement("Sri Lakshmi Departmental Store", 1, "Ravi Traders", "9000000001", null, DateOnly.FromDateTime(DateTime.Today.AddDays(-90)), DateOnly.FromDateTime(DateTime.Today),
            800_000, new[]
            {
                new LedgerEntry(DateTime.Today.AddDays(-45), "Credit sale", "INV/26-27/000012", "Goods on credit", 250_000, 0, 1_050_000),
                new LedgerEntry(DateTime.Today.AddDays(-20), "Receipt", "R-4", "Received in UPI", 0, 300_000, 750_000),
                new LedgerEntry(DateTime.Today.AddDays(-3), "Credit note", "CN/26-27/000002", "Goods returned, put against the account", 0, 50_000, 700_000),
            }, 250_000, 350_000, 700_000, 500_000);
        Capture(new Views.ReceiptWindow("Statement", () => OtherDocuments.BuildStatement(stmt)), outputDir, "statement", new[] { (900, 700) });
        var remind = new CustomerReminder("Sri Lakshmi Departmental Store", "9000000000", "Ravi Traders", DateOnly.FromDateTime(DateTime.Today), 700_000,
            new[] { new UnpaidItem("Earlier balance", DateTime.Today.AddDays(-100), 450_000, 100), new UnpaidItem("INV/26-27/000012", DateTime.Today.AddDays(-45), 250_000, 45) });
        Capture(new Views.ReceiptWindow("Reminder", () => OtherDocuments.BuildReminder(remind)), outputDir, "reminder", new[] { (900, 700) });
        var demoBill = new SaleDetailDto(1, "INV/26-27/000123", DateTime.Today, "Priya", "Kumar Stores", "33AAAAA1111A1Z5", false, 10_000, 450, 450, 0, 500, 0, 11_400, SaleStatus.Posted, 1,
            new[] { new SaleLineDto("Lux Soap 100g", "3401", "pcs", 2_000, 4_500, 0, 1800, 7_627, 1_373, 9_000), new SaleLineDto("Tata Salt 1kg", "2501", "kg", 1_000, 2_800, 400, 500, 2_286, 114, 2_400) },
            new[] { new PaymentInput(PaymentMode.Upi, 11_400) });
        Capture(new Views.ReceiptWindow("Invoice", () => InvoiceBuilder.BuildA4(demoBill, new CompanyDto("Sri Lakshmi Departmental Store", "33ABCDE1234F1Z5", "33", false, "12 Main Road, Chennai", "9000000000"), false)), outputDir, "invoice-a4", new[] { (900, 800) });
        var rollSpecs = new[] { new LabelSpec("Sri Lakshmi", "Tata Salt 250 g pack", "₹7.00", "4006381333931") };
        Capture(new Views.ReceiptWindow("Label", () => LabelBuilder.BuildRoll(rollSpecs, 50, 25)), outputDir, "label-roll-50x25", new[] { (420, 380) });
        Capture(new Views.HeldBillsWindow(new[] { new HeldBillSummary(1, DateTime.Now.AddMinutes(-12), "Priya", "Ravi Traders: Lux Soap 100g +2 more", "Ravi Traders", 3), new HeldBillSummary(2, DateTime.Now.AddMinutes(-3), "Owner", "Held at 11:20", null, 1) }, _ => Task.CompletedTask), outputDir, "held-bills", new[] { (560, 460) });

        CaptureDialogs(sp, outputDir);

        foreach (var role in new[] { "admin", "cashier" })
        {
            sp.GetRequiredService<ICurrentUser>().SignOut();
            if (role == "admin") sp.GetRequiredService<IAuthService>().LoginAsync("owner", "Admin#12345").GetAwaiter().GetResult();
            else sp.GetRequiredService<IAuthService>().LoginAsync("cashier1", "Cashier#12345").GetAwaiter().GetResult();
            var screens = role == "admin"
                ? new[] { "billing", "bills", "products", "stock", "customers", "credit", "purchases", "suppliers", "expenses", "dayend", "gst", "pnl", "bs", "opening", "import", "reports", "settings" }
                : new[] { "billing", "bills", "customers" };
            foreach (var screen in screens)
                foreach (var (w, h) in Sizes)
                {
                    if (screen == "opening") SeedOpening(sp);
                    if (screen == "credit") SeedCredit(sp);
                    var main = new MainWindow(sp);
                    var hosted = w > 1100;                       // larger than a small PC screen can show: host off-screen at exact size
                    main.Width = hosted ? 1100 : w; main.Height = hosted ? 700 : h;
                    main.Show();
                    var user = sp.GetRequiredService<ICurrentUser>().User!;
                    main.StartSessionAsync(user).GetAwaiter().GetResult();
                    Pump();
                    var host = (ContentControl)main.FindName("Host");
                    main.NavigateTo(screen);
                    if (screen == "billing") FillCart(host.Content as Views.BillingView, sp);
                    if (screen == "gst") PrepareGst(host.Content as Views.GstView);
                    if (screen is "products" or "stock") { Pump(); Pump(); }
                    if (screen == "bs" && host.Content is Views.BalanceSheetView bsv && bsv.DataContext is ViewModels.BalanceSheetViewModel bvm) { bvm.PrepareAsync().GetAwaiter().GetResult(); Pump(); }
                    if (screen == "pnl" && host.Content is Views.ProfitLossView plv && plv.DataContext is ViewModels.ProfitLossViewModel pvm2) { pvm2.PrepareAsync().GetAwaiter().GetResult(); Pump(); }
                    Pump(); Pump();
                    var file = Path.Combine(outputDir, $"{role}-{screen}-{w}x{h}.png");
                    if (hosted) SaveHosted(main, w, h, file); else Save(main, file);
                    if (screen == "products" && host.Content is Views.ProductsView pv2 && pv2.DataContext is ViewModels.ProductsViewModel pvm3 && !hosted)
                    {
                        // the edit dialog, open on the first item (and scrolled to the end, where the barcodes are)
                        pv2.SuppressDialog = true;
                        if (pvm3.Items.Count > 0) pvm3.Selected = pvm3.Items[0];
                        Pump(); Pump();
                        var dlg = pv2.ShowDialogForCheck();
                        Pump(); Pump();
                        Save(dlg, Path.Combine(outputDir, $"{role}-products-dialog-{w}x{h}.png"));
                        (dlg.FindName("BodyScroller") as ScrollViewer)?.ScrollToEnd();
                        Pump(); Pump();
                        Save(dlg, Path.Combine(outputDir, $"{role}-products-dialog-end-{w}x{h}.png"));
                        pvm3.CloseFormCommand.Execute(null);
                        Pump();
                        pvm3.NewCommand.Execute(null);
                        var dlg2 = pv2.ShowDialogForCheck();
                        Pump(); Pump();
                        Save(dlg2, Path.Combine(outputDir, $"{role}-products-dialog-new-{w}x{h}.png"));
                        pvm3.CloseFormCommand.Execute(null);
                        Pump();
                    }
                    if (screen == "stock" && host.Content is Views.StockView && host.Content is FrameworkElement sv && sv.DataContext is ViewModels.StockViewModel svm)
                    {
                        if (svm.Items.Count > 0) svm.Selected = svm.Items[0];
                        Pump(); Pump();
                        var f6 = Path.Combine(outputDir, $"{role}-stock-panel-{w}x{h}.png");
                        if (hosted) SaveHosted(main, w, h, f6); else Save(main, f6);
                    }
                    if (screen == "bs")
                    {
                        if (FindTabControl(host) is { } btabs) btabs.SelectedIndex = 1;
                        Pump(); Pump();
                        var f4 = Path.Combine(outputDir, $"{role}-bs-checks-{w}x{h}.png");
                        if (hosted) SaveHosted(main, w, h, f4); else Save(main, f4);
                    }
                    if (screen == "pnl")
                    {
                        if (FindTabControl(host) is { } ptabs) ptabs.SelectedIndex = 1;
                        Pump(); Pump();
                        var f3 = Path.Combine(outputDir, $"{role}-pnl-items-{w}x{h}.png");
                        if (hosted) SaveHosted(main, w, h, f3); else Save(main, f3);
                    }
                    if (screen == "gst")
                    {
                        // also show the widest table (B2B), the HSN table and the checks list
                        foreach (var (tab, label) in new[] { (1, "b2b"), (5, "hsn"), (9, "checks") })
                        {
                            if (FindTabControl(host) is { } tabs) tabs.SelectedIndex = tab;
                            Pump(); Pump();
                            var f2 = Path.Combine(outputDir, $"{role}-gst-{label}-{w}x{h}.png");
                            if (hosted) SaveHosted(main, w, h, f2); else Save(main, f2);
                        }
                    }
                    main.EndSession();
                    main.Close();
                }
        }
    }

    public static void Probe(string outputDir, string kind, int width, int height)
    {
        Directory.CreateDirectory(outputDir);
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "billing-probe-" + Guid.NewGuid().ToString("N")));
        var sc = new ServiceCollection();
        var result = InfrastructureBootstrap.Start(paths, sc);
        if (!result.Ok) throw new InvalidOperationException(result.Error);
        App.RegisterUi(sc);
        var sp = sc.BuildServiceProvider();
        SeedAsync(sp).GetAwaiter().GetResult();
        sp.GetRequiredService<ICurrentUser>().SignOut();
        sp.GetRequiredService<IAuthService>().LoginAsync("owner", "Admin#12345").GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(outputDir, "probe-started.txt"), kind);
        if (kind == "purchase")
        {
            var win = sp.GetRequiredService<Views.PurchaseEntryWindow>();
            var pvm = (ViewModels.PurchaseEntryViewModel)win.DataContext;
            pvm.InitializeAsync().GetAwaiter().GetResult();
            pvm.PickSupplierCommand.Execute(sp.GetRequiredService<ISupplierService>().SearchAsync("Metro").GetAwaiter().GetResult()[0]);
            foreach (var p in sp.GetRequiredService<ICatalogService>().ListAsync(0, 3).GetAwaiter().GetResult()) pvm.AddProduct(p);
            pvm.InvoiceNo = "MW-2099";
            Capture(win, outputDir, "probe-purchase", new[] { (width, height) });
        }
        File.WriteAllText(Path.Combine(outputDir, "probe-done.txt"), "ok");
    }

    private static void CaptureDialogs(IServiceProvider sp, string outputDir)
    {
        var auth = sp.GetRequiredService<IAuthService>();
        sp.GetRequiredService<ICurrentUser>().SignOut();
        auth.LoginAsync("owner", "Admin#12345").GetAwaiter().GetResult();
        var ui = sp.GetRequiredService<IUiHost>();
        var sales = sp.GetRequiredService<ISalesService>();
        var returns = sp.GetRequiredService<ISalesReturnService>();
        var settings = sp.GetRequiredService<ISettingsService>();
        var purchases = sp.GetRequiredService<IPurchaseService>();
        var company = settings.GetCompanyAsync().GetAwaiter().GetResult();

        // return dialog, filled in
        var bill = sales.ListAsync(DateOnly.FromDateTime(DateTime.Today)).GetAwaiter().GetResult().Skip(1).First();
        var lines = returns.GetReturnableAsync(bill.Id).GetAwaiter().GetResult();
        var request = new ReturnDialogRequest("Return items from a bill", $"Bill {bill.InvoiceNumber} - Walk-in", lines, true, "Reduce what the customer owes",
            (inp, mode, reason) => returns.PreviewAsync(new SaleReturnCommand(Guid.NewGuid(), bill.Id, inp, mode, reason)),
            (inp, mode, reason, id, approver) => returns.ReturnAsync(new SaleReturnCommand(id, bill.Id, inp, mode, reason, approver)));
        var vm = new ViewModels.ReturnDialogViewModel(request, ui);
        vm.Lines[0].QtyText = "1"; vm.Reason = "Wrong size"; vm.RecomputeAsync().GetAwaiter().GetResult();
        Capture(new Views.ReturnWindow(vm), outputDir, "dialog-return", new[] { (820, 640), (440, 640) });
        var bad = new ViewModels.ReturnDialogViewModel(request, ui);
        bad.Lines[0].QtyText = "99"; bad.RecomputeAsync().GetAwaiter().GetResult();
        Capture(new Views.ReturnWindow(bad), outputDir, "dialog-return-error", new[] { (820, 640) });

        // credit note and purchase voucher documents
        var rr = returns.ListAsync(DateOnly.FromDateTime(DateTime.Today)).GetAwaiter().GetResult().First();
        var detail = returns.GetAsync(rr.Id).GetAwaiter().GetResult()!;
        Capture(new Views.ReceiptWindow("Credit note", () => OtherDocuments.BuildReturn(detail, company)), outputDir, "doc-creditnote", new[] { (420, 600) });
        var pl = purchases.ListAsync(DateOnly.FromDateTime(DateTime.Today.AddDays(-1)), DateOnly.FromDateTime(DateTime.Today)).GetAwaiter().GetResult().First();
        var pd = purchases.GetAsync(pl.Id).GetAwaiter().GetResult()!;
        Capture(new Views.ReceiptWindow("Purchase", () => OtherDocuments.BuildPurchase(pd, company)), outputDir, "doc-purchase", new[] { (760, 560) });

        // the day-end (Z) report
        var dcs = sp.GetRequiredService<IDayCloseService>();
        var closes = dcs.ListAsync().GetAwaiter().GetResult();
        if (closes.Count > 0)
        {
            var z = dcs.GetAsync(closes[0].Id).GetAwaiter().GetResult()!;
        }

        // purchase entry window, filled in, at several sizes
        foreach (var size in new[] { (1180, 740), (800, 600), (520, 640) })
        {
            var win = sp.GetRequiredService<Views.PurchaseEntryWindow>();
            var pvm = (ViewModels.PurchaseEntryViewModel)win.DataContext;
            pvm.InitializeAsync().GetAwaiter().GetResult();
            var supplier = sp.GetRequiredService<ISupplierService>().SearchAsync("Metro").GetAwaiter().GetResult()[0];
            pvm.PickSupplierCommand.Execute(supplier);
            var catalog = sp.GetRequiredService<ICatalogService>();
            foreach (var p in catalog.ListAsync(0, 3).GetAwaiter().GetResult()) pvm.AddProduct(p);
            pvm.Lines[0].UnitCost = 40; pvm.Lines[1].UnitCost = 20; pvm.Lines[1].Quantity = 12; pvm.Lines[2].UnitCost = 15;
            pvm.InvoiceNo = "MW-2099";
            Capture(win, outputDir, "dialog-purchase", new[] { size });
        }
    }

    private static void PrepareGst(Views.GstView? view)
    {
        if (view?.DataContext is not ViewModels.GstViewModel vm) return;
        vm.Year = DateTime.Today.Year;
        vm.Month = vm.Months[DateTime.Today.Month - 1];
        vm.PrepareCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Pump();
    }

    private static TabControl? FindTabControl(DependencyObject? parent)
    {
        if (parent == null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TabControl tc) return tc;
            if (FindTabControl(child) is { } found) return found;
        }
        return null;
    }

    private static void FillCart(Views.BillingView? view, IServiceProvider sp)
    {
        if (view?.DataContext is not ViewModels.BillingViewModel vm) return;
        var catalog = sp.GetRequiredService<ICatalogService>();
        foreach (var p in catalog.SearchAsync("", 4).GetAwaiter().GetResult()) vm.AddProduct(p);
        if (vm.Lines.Count > 1) vm.Lines[1].Quantity = 3;
        Pump();
    }

    private static async Task SeedAsync(IServiceProvider sp)
    {
        var settings = sp.GetRequiredService<ISettingsService>();
        var auth = sp.GetRequiredService<IAuthService>();
        var catalog = sp.GetRequiredService<ICatalogService>();
        var stock = sp.GetRequiredService<IStockService>();
        var sales = sp.GetRequiredService<ISalesService>();
        await settings.SaveCompanyAsync(new CompanyDto("Sri Lakshmi Departmental Store", "33ABCDE1234F1Z5", "33", false, "12, Main Road, Salem", "9000000000"));
        await auth.CreateInitialAdminAsync("owner", "Owner", "Admin#12345");
        await auth.CreateCashierAsync("cashier1", "Priya", "Cashier#12345");
        var names = new[] { ("Lux Soap 100g", 4500L, 1800), ("Tata Salt 1kg", 2800L, 500), ("Basmati Rice 5kg", 68000L, 500), ("Colgate Toothpaste 150g", 9500L, 1800),
                            ("Notebook A4 200 pages", 8500L, 1200), ("Amul Butter 500g", 29000L, 1200), ("Steel Tumbler Set of 6", 45000L, 1800), ("Cotton Towel Large", 32500L, 500) };
        var i = 1;
        foreach (var (name, price, gst) in names)
        {
            var p = await catalog.SaveAsync(new ProductDto(0, $"SKU{i:D3}", Ean13.FromSequence(i), name, "3401", gst, "pcs", price, price, true, 0, 0, true));
            await stock.ReceiveOpeningStockAsync(p.Id, 50_000, price * 6 / 10);
            if (i <= 3) await sales.CompleteSaleAsync(new CompleteSaleCommand(Guid.NewGuid(), new[] { new SaleLineInput(p.Id, 2000) },
                new[] { new PaymentInput(i == 2 ? PaymentMode.Upi : PaymentMode.Cash, Money.ToPaise(Math.Round(Money.ToRupees(price * 2)))) }));
            i++;
        }
        await sp.GetRequiredService<IPartyService>().SaveAsync(new PartyDto(0, "Ravi Traders", "9000000001", null, "33", 0));
        // business customers so the GST tables have B2B rows (intra-state and inter-state)
        var first = (await catalog.ListAsync(0, 1)).First();
        var kumar = await sp.GetRequiredService<IPartyService>().SaveAsync(new PartyDto(0, "Kumar Stores", "9000000003", "33AAAAA1111A1Z5", null, 0));
        var blr = await sp.GetRequiredService<IPartyService>().SaveAsync(new PartyDto(0, "Bangalore Mart", null, "29AAAAA2222A1Z5", null, 0));
        await sales.CompleteSaleAsync(new CompleteSaleCommand(Guid.NewGuid(), new[] { new SaleLineInput(first.Id, 1000) },
            new[] { new PaymentInput(PaymentMode.Cash, first.SellingPricePaise / 100 * 100) }, kumar.Id, kumar.Name, kumar.Gstin, kumar.StateCode));
        await sales.CompleteSaleAsync(new CompleteSaleCommand(Guid.NewGuid(), new[] { new SaleLineInput(first.Id, 2000) },
            new[] { new PaymentInput(PaymentMode.Cash, first.SellingPricePaise * 2 / 100 * 100) }, blr.Id, blr.Name, blr.Gstin, blr.StateCode));
        // a supplier with one cash+credit purchase, and a customer return, so the new screens have something to show
        var supplier = await sp.GetRequiredService<ISupplierService>().SaveAsync(new SupplierDto(0, "Metro Wholesale", "9000000002", "33AAAAA0000A1Z5", "33", 0));
        var firstTwo = (await catalog.ListAsync(0, 2)).ToList();
        var purchasesSvc = sp.GetRequiredService<IPurchaseService>();
        var plines = firstTwo.Select(p => new PurchaseLineInput(p.Id, 10_000, 4000)).ToList();
        long exact = firstTwo.Sum(p => TaxCalculator.Calculate(4000, 10_000, 0, p.GstRateBp, false, false).TotalPaise);
        var ptotal = InvoiceRounding.ToNearestRupee(exact).Total;
        await purchasesSvc.CompleteAsync(new CompletePurchaseCommand(Guid.NewGuid(), supplier.Id, "MW-2041", DateTime.Today, plines,
            new[] { new PaymentInput(PaymentMode.Cash, 30000), new PaymentInput(PaymentMode.Credit, ptotal - 30000) }));
        var latest = (await sales.ListAsync(DateOnly.FromDateTime(DateTime.Today))).First();
        var rl = await sp.GetRequiredService<ISalesReturnService>().GetReturnableAsync(latest.Id);
        await sp.GetRequiredService<ISalesReturnService>().ReturnAsync(new SaleReturnCommand(Guid.NewGuid(), latest.Id,
            new[] { new ReturnLineInput(rl[0].LineId, 1000, true) }, PaymentMode.Cash, "Customer changed mind"));
        await settings.SetAsync(SettingKeys.BackupFolder, Path.Combine(Path.GetTempPath(), "billing-shots-backup"));

        // a few business expenses (cash, UPI, and a salary that is only part paid)
        var exp = sp.GetRequiredService<IExpenseService>();
        var expCats = (await exp.ListCategoriesAsync()).ToDictionary(c => c.Name, c => c.Id);
        await exp.RecordAsync(new RecordExpenseCommand(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.Today), expCats["Rent"], "Sri Murugan (landlord)", "Shop rent for November", null, 1_800_000, PaymentMode.Upi));
        await exp.RecordAsync(new RecordExpenseCommand(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.Today), expCats["Electricity"], "TNEB", "October electricity bill", "EB-77120", 462_000, PaymentMode.Cash));
        var sal = await exp.RecordAsync(new RecordExpenseCommand(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.Today), expCats["Salaries and wages"], "Priya", "October salary", null, 1_200_000, PaymentMode.Credit));
        await exp.PayAsync(sal.Id, 500_000, PaymentMode.Cash);
        await exp.RecordAsync(new RecordExpenseCommand(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.Today).AddDays(-2), expCats["Transport and freight"], "Metro Wholesale", "Freight on purchase MW-2041", null, 85_000, PaymentMode.Cash));

        // an earlier day-end close (a little short) and a new period with pay-outs and a sale, so the Day end screen is full
        var dc = sp.GetRequiredService<IDayCloseService>();
        var pv = await dc.PreviewAsync(5_000_000);
        await dc.CloseAsync(new DayCloseCommand(Guid.NewGuid(), pv.Report.ExpectedCashPaise - 5_000, 5_000_000, "Gave wrong change in the evening rush"));
        await dc.RecordMovementAsync(CashMovementType.Expense, 12_000, "Tea and courier");
        await dc.RecordMovementAsync(CashMovementType.BankDeposit, 50_000, "Deposit to bank");
        var firstItem = (await catalog.ListAsync(0, 1)).First();
        await sales.CompleteSaleAsync(new CompleteSaleCommand(Guid.NewGuid(), new[] { new SaleLineInput(firstItem.Id, 1000) },
            new[] { new PaymentInput(PaymentMode.Cash, firstItem.SellingPricePaise / 100 * 100) }));
    }

    private static void Capture(Window w, string dir, string name, (int W, int H)[] sizes)
    {
        foreach (var (width, height) in sizes)
        {
            w.SizeToContent = SizeToContent.Manual;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = 0; w.Top = 0;
            w.Width = width; w.Height = height;
            w.Show();
            Pump();
            Save(w, Path.Combine(dir, $"{name}-{width}x{height}.png"));
            w.Hide();
        }
        if (w is Views.PurchaseEntryWindow pw) pw.SuppressCloseConfirmation = true;
        w.Close();
    }

    private static bool _creditSeeded;

    /// <summary>Old and new credit dues for three customers, one over their limit, so the Credit screen is rendered with real rows.</summary>
    private static void SeedCredit(IServiceProvider sp)
    {
        if (_creditSeeded) return;
        _creditSeeded = true;
        var parties = sp.GetRequiredService<IPartyService>();
        var op = sp.GetRequiredService<IOpeningBalanceService>();
        var all = parties.SearchAsync("", 20).GetAwaiter().GetResult();
        void Due(string name, long amount, int daysAgo, long limit)
        {
            var p = all.FirstOrDefault(x => x.Name == name);
            if (p == null) return;
            op.AddAsync(new AddOpeningCommand(Guid.NewGuid(), OpeningKind.CustomerDue, DateOnly.FromDateTime(DateTime.Today.AddDays(-daysAgo)), p.Id, null, "", amount, null, null)).GetAwaiter().GetResult();
            if (limit > 0) parties.SaveAsync(p with { CreditLimitPaise = limit }).GetAwaiter().GetResult();
        }
        Due("Ravi Traders", 800_000, 100, 500_000);
        Due("Ravi Traders", 250_000, 45, 0);
        Due("Kumar Stores", 1_250_000, 70, 2_000_000);
        Due("Bangalore Mart", 320_000, 5, 0);
        var ravi = all.FirstOrDefault(x => x.Name == "Ravi Traders");
        var first = sp.GetRequiredService<ICatalogService>().ListAsync(0, 1).GetAwaiter().GetResult().First();
        if (ravi != null)
        {
            sp.GetRequiredService<ISalesService>().CompleteSaleAsync(new CompleteSaleCommand(Guid.NewGuid(), new[] { new SaleLineInput(first.Id, 1000) },
                new[] { new PaymentInput(PaymentMode.Credit, first.SellingPricePaise / 100 * 100) }, ravi.Id, ravi.Name, null, ravi.StateCode)).GetAwaiter().GetResult();
            parties.ReceivePaymentAsync(ravi.Id, 300_000, PaymentMode.Upi).GetAwaiter().GetResult();
        }
    }

    private static bool _openingSeeded;

    /// <summary>Adds a few opening balances so the Opening balances screen is rendered with real rows.</summary>
    private static void SeedOpening(IServiceProvider sp)
    {
        if (_openingSeeded) return;
        _openingSeeded = true;
        var op = sp.GetRequiredService<IOpeningBalanceService>();
        var yesterday = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
        var cust = sp.GetRequiredService<IPartyService>().SearchAsync("", 5).GetAwaiter().GetResult().FirstOrDefault();
        op.AddAsync(new AddOpeningCommand(Guid.NewGuid(), OpeningKind.Bank, yesterday, null, null, "State Bank current account", 25_000_000, null, null)).GetAwaiter().GetResult();
        if (cust != null) op.AddAsync(new AddOpeningCommand(Guid.NewGuid(), OpeningKind.CustomerDue, yesterday, cust.Id, null, "", 1_250_000, null, "Old credit")).GetAwaiter().GetResult();
        op.AddAsync(new AddOpeningCommand(Guid.NewGuid(), OpeningKind.FixedAsset, yesterday, null, null, "Shelves and counter", 4_000_000, 5_000_000, null)).GetAwaiter().GetResult();
        op.AddAsync(new AddOpeningCommand(Guid.NewGuid(), OpeningKind.Loan, yesterday, null, null, "Cooperative bank", 10_000_000, null, null)).GetAwaiter().GetResult();
    }

    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>Renders the window content at an exact size without needing a screen that large.</summary>
    private static void SaveHosted(Window w, int width, int height, string file)
    {
        var content = (UIElement)w.Content;
        w.Content = null;
        var host = new Border { Width = width, Height = height, Background = Brushes.White, Child = content };
        for (var i = 0; i < 3; i++)
        {
            host.Measure(new Size(width, height));
            host.Arrange(new Rect(0, 0, width, height));
            host.UpdateLayout();
            Pump();   // DataGrid redistributes star columns on a deferred dispatcher pass
        }
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(host);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        enc.Save(fs);
        host.Child = null;
        w.Content = content;
    }

    private static void Save(Window w, string file)
    {
        w.UpdateLayout();
        var content = VisualTreeHelper.GetChildrenCount(w) > 0 && VisualTreeHelper.GetChild(w, 0) is FrameworkElement chrome ? chrome : (FrameworkElement)w.Content;
        var width = (int)Math.Ceiling(content.ActualWidth);
        var height = (int)Math.Ceiling(content.ActualHeight);
        if (width == 0 || height == 0) return;
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(content);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        enc.Save(fs);
    }
}
