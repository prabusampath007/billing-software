using System.Text;
using BillingSoftware.Application;
using BillingSoftware.Desktop.ViewModels;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace BillingSoftware.Desktop.Services;

/// <summary>
/// Drives the real billing screen logic (view model) end to end against a throw-away encrypted database,
/// with a fake UI host instead of dialogs. Run with: BillingSoftware.exe --selftest result.txt
/// </summary>
internal static class SelfTest
{
    private sealed class FakeUi(IAuthService auth) : IUiHost
    {
        public string? AdminPasswordToGive;
        public int ApprovalPrompts;
        public readonly List<string> Errors = new();
        public int Receipts;

        public async Task<long?> RequestApprovalAsync(string message)
        {
            ApprovalPrompts++;
            if (AdminPasswordToGive == null) return null;
            try { return await auth.VerifyAdminApprovalAsync(AdminPasswordToGive); }
            catch (BusinessRuleException) { return null; }
        }
        public void ShowError(Exception ex) => Errors.Add(ex.Message);
        public bool Confirm(string message, string title = "") => true;
        public void Info(string message, string title = "") { }
        public void ShowReceipt(SaleDetailDto sale, CompanyDto? company, bool duplicate) => Receipts++;
        public string? PickFolder(string? initial) => null;
        public string? PromptAnswer;
        public string? Prompt(string message, string title, bool isPassword = false) => PromptAnswer;
        public System.Windows.Documents.FlowDocument? LastLabels, LastCreditDoc;
        public void ShowDocument(string title, Func<System.Windows.Documents.FlowDocument> build)
        {
            if (title == "Barcode labels") LastLabels = build();
            else if (title.StartsWith("Statement") || title.StartsWith("Reminder")) LastCreditDoc = build();
        }
        public async Task<ReturnResult?> ShowReturnDialogAsync(ReturnDialogRequest request) => ReturnHandler == null ? null : await ReturnHandler(request);
        public bool ShowPurchaseEntry() => false;
        public string? SavePath;
        public string? PickSaveFile(string suggestedName, string filter) => SavePath;
        public string? OpenPath;
        public string? PickOpenFile(string title, string filter) => OpenPath;
        public int Restarts;
        public long? HeldPick;
        public long? HeldDiscard;
        public async Task<long?> PickHeldBillAsync(IReadOnlyList<HeldBillSummary> items, Func<long, Task> discard)
        {
            if (HeldDiscard is { } d) { await discard(d); HeldDiscard = null; return null; }
            return HeldPick;
        }
        public void RestartApplication() => Restarts++;
        public Func<ReturnDialogRequest, Task<ReturnResult?>>? ReturnHandler;
    }

    public static int Run(string resultFile)
    {
        var log = new StringBuilder();
        var failed = 0;
        void Check(bool ok, string what) { log.AppendLine((ok ? "PASS  " : "FAIL  ") + what); if (!ok) failed++; }

        var dir = Path.Combine(Path.GetTempPath(), "billing-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(dir);
            var sc = new ServiceCollection();
            var start = InfrastructureBootstrap.Start(paths, sc);
            Check(start.Ok && !string.IsNullOrEmpty(start.RecoveryKey), "encrypted database created and recovery key issued");
            var sp = sc.BuildServiceProvider();
            var auth = sp.GetRequiredService<IAuthService>();
            var settings = sp.GetRequiredService<ISettingsService>();
            var catalog = sp.GetRequiredService<ICatalogService>();
            var stock = sp.GetRequiredService<IStockService>();
            var sales = sp.GetRequiredService<ISalesService>();
            var parties = sp.GetRequiredService<IPartyService>();
            var reports = sp.GetRequiredService<IReportService>();
            var current = sp.GetRequiredService<ICurrentUser>();

            settings.SaveCompanyAsync(new CompanyDto("Self Test Stores", "33ABCDE1234F1Z5", "33", false, null, null)).GetAwaiter().GetResult();
            auth.CreateInitialAdminAsync("owner", "Owner", "Admin#12345").GetAwaiter().GetResult();
            var soap = catalog.SaveAsync(new ProductDto(0, "S1", "111", "Soap", "3401", 1800, "pcs", 11800, 11800, true, 0, 0, true)).GetAwaiter().GetResult();
            var salt = catalog.SaveAsync(new ProductDto(0, "S2", "222", "Salt", "2501", 500, "kg", 2800, 2800, true, 0, 0, true)).GetAwaiter().GetResult();
            stock.ReceiveOpeningStockAsync(soap.Id, 20_000, 6000).GetAwaiter().GetResult();
            stock.ReceiveOpeningStockAsync(salt.Id, 20_000, 1500).GetAwaiter().GetResult();
            var ravi = parties.SaveAsync(new PartyDto(0, "Ravi Traders", "9000000001", null, "33", 0)).GetAwaiter().GetResult();
            auth.CreateCashierAsync("cashier1", "Cashier", "Cashier#12345").GetAwaiter().GetResult();

            var ui = new FakeUi(auth);
            BillingViewModel NewVm() { var v = new BillingViewModel(catalog, parties, sales, settings, sp.GetRequiredService<IHoldService>(), ui); v.InitializeAsync().GetAwaiter().GetResult(); return v; }
            long CountBills() => sales.ListAsync(DateOnly.FromDateTime(DateTime.Today)).GetAwaiter().GetResult().Count(x => x.Status == SaleStatus.Posted);

            // ---------- Admin at the counter ----------
            var vm = NewVm();
            vm.ScanText = "111"; vm.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(vm.Lines.Count == 1 && vm.TotalPaise == 11800, "scanning a barcode adds the item and totals 118.00");
            vm.ScanText = "111"; vm.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(vm.Lines.Count == 1 && vm.Lines[0].Quantity == 2 && vm.TotalPaise == 23600, "scanning the same item again increases the quantity");
            vm.ScanText = "no-such-item"; vm.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(vm.StatusIsError && vm.Lines.Count == 1, "unknown barcode shows a message and changes nothing");
            vm.ScanText = "S"; vm.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(vm.Matches.Count == 2, "a partial search lists the matching items to choose from");
            vm.PickMatchCommand.Execute(vm.Matches.First(m => m.Sku == "S2"));
            Check(vm.Lines.Count == 2, "choosing a match adds it to the bill");

            vm.Lines[0].Quantity = 0;
            Check(vm.Lines[0].HasError && !vm.CompleteCommand.CanExecute(null), "zero quantity is flagged and the sale cannot be completed");
            vm.Lines[0].Quantity = 1;
            Check(vm.Lines[0].HasError == false && vm.TotalPaise == 11800 + 2800, "fixing the quantity clears the error");

            vm.CashText = "100";
            Check(!vm.PaymentOk && vm.PaymentNote.Contains("short"), "cash less than the total is refused with a clear note");
            vm.CashText = "500";
            Check(vm.PaymentOk && vm.PaymentNote.Contains("change"), "cash above the total shows the change to return");
            vm.CashText = ""; vm.CreditText = "100";
            Check(!vm.PaymentOk && vm.PaymentNote.Contains("customer"), "credit without a customer is refused");
            vm.CreditText = "";

            var before = CountBills();
            vm.CompleteCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(CountBills() == before + 1 && vm.Lines.Count == 0 && ui.Receipts == 1, "completing the sale saves one bill, shows the receipt and clears the screen");
            Check(catalog.GetAsync(soap.Id).GetAwaiter().GetResult()!.QtyOnHandMilli == 19_000, "stock is reduced by the quantity sold");

            // double click: two completions started together must still produce one bill
            vm.ScanText = "222"; vm.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            before = CountBills();
            var t1 = vm.CompleteCommand.ExecuteAsync(null);
            var t2 = vm.CompleteCommand.ExecuteAsync(null);
            Task.WhenAll(t1, t2).GetAwaiter().GetResult();
            Check(CountBills() == before + 1, "double-clicking Complete never creates a duplicate bill");

            // mixed payment
            vm = NewVm();
            vm.ScanText = "111"; vm.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            vm.UpiText = "50"; vm.CashText = "68";
            Check(vm.PaymentOk, "cash plus UPI that add up to the bill are accepted");
            vm.CompleteCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            var today = reports.DailySummaryAsync(DateOnly.FromDateTime(DateTime.Today)).GetAwaiter().GetResult();
            Check(today.ByPaymentMode.Any(x => x.Mode == PaymentMode.Upi && x.AmountPaise == 5000), "the UPI part is recorded separately in the day summary");

            // credit sale with a customer
            vm = NewVm();
            vm.PickCustomerCommand.Execute(parties.SearchAsync("Ravi").GetAwaiter().GetResult()[0]);
            vm.ScanText = "111"; vm.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            vm.CreditText = "118";
            Check(vm.PaymentOk, "a credit sale with a customer chosen is accepted");
            vm.CompleteCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(parties.SearchAsync("Ravi").GetAwaiter().GetResult()[0].OutstandingPaise == 11800, "the customer now owes the credit amount");

            // inter-state customer gets IGST
            var karnataka = parties.SaveAsync(new PartyDto(0, "Bangalore Mart", null, null, "29", 0)).GetAwaiter().GetResult();
            vm = NewVm();
            vm.PickCustomerCommand.Execute(parties.SearchAsync("Bangalore").GetAwaiter().GetResult()[0]);
            vm.ScanText = "111"; vm.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            vm.CompleteCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            var lastBill = sales.ListAsync(DateOnly.FromDateTime(DateTime.Today)).GetAwaiter().GetResult().First();
            Check(sales.GetAsync(lastBill.Id).GetAwaiter().GetResult()!.IgstPaise == 1800, "a customer from another state is charged IGST");

            // ---------- Cashier at the counter ----------
            current.SignOut();
            auth.LoginAsync("cashier1", "Cashier#12345").GetAwaiter().GetResult();
            vm = NewVm();
            vm.ScanText = "111"; vm.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            vm.Lines[0].Discount = 10;
            before = CountBills(); ui.ApprovalPrompts = 0; ui.AdminPasswordToGive = null;
            vm.CompleteCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(ui.ApprovalPrompts == 1 && CountBills() == before && vm.Lines.Count == 1, "a cashier discount asks for Admin approval and, if refused, nothing is saved");
            ui.AdminPasswordToGive = "wrong-password";
            vm.CompleteCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(CountBills() == before, "a wrong Admin password does not approve the discount");
            ui.AdminPasswordToGive = "Admin#12345";
            vm.CompleteCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(CountBills() == before + 1, "with the right Admin password the discounted bill is saved");
            Check(catalog.GetByBarcodeAsync("111").GetAwaiter().GetResult()!.AvgCostPaise == 0, "the cashier never receives the purchase cost");

            // negative stock needs approval
            vm = NewVm();
            vm.ScanText = "222"; vm.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            vm.Lines[0].Quantity = 500;
            vm.CashText = "";
            ui.AdminPasswordToGive = null; ui.ApprovalPrompts = 0; before = CountBills();
            vm.CompleteCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(ui.ApprovalPrompts == 1 && CountBills() == before, "selling far more than the stock asks for approval and saves nothing if refused");

            // books are balanced after everything
            current.SignOut();
            auth.LoginAsync("owner", "Admin#12345").GetAwaiter().GetResult();
            // ---------- returns, suppliers and purchases through the screens' logic ----------
            var returnsSvc = sp.GetRequiredService<ISalesReturnService>();
            var suppliersSvc = sp.GetRequiredService<ISupplierService>();
            var purchasesSvc = sp.GetRequiredService<IPurchaseService>();

            // (a) the Bills screen: pick a bill, return one item, a credit note appears
            vm = NewVm();
            vm.ScanText = "111"; vm.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            vm.Lines[0].Quantity = 3;
            vm.CompleteCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            var bills = new BillsViewModel(sales, returnsSvc, settings, ui);
            bills.LoadAsync().GetAwaiter().GetResult();
            bills.Selected = bills.Items.First(b => !b.IsVoided);
            var billId = bills.Selected.Item.Id;
            var stockBefore = catalog.GetAsync(soap.Id).GetAwaiter().GetResult()!.QtyOnHandMilli;
            long previewTotal = 0;
            ui.ReturnHandler = async req =>
            {
                var inputs = new[] { new ReturnLineInput(req.Lines[0].LineId, 1000, true) };
                previewTotal = (await req.Preview(inputs, PaymentMode.Cash, "test")).TotalPaise;
                return await req.Submit(inputs, PaymentMode.Cash, "Customer changed mind", Guid.NewGuid(), null);
            };
            bills.ReturnItemsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(previewTotal == 11800, "the return dialog previews the refund (118.00) before anything is saved");
            Check(bills.ReturnRows.Count == 1 && bills.Status.Contains("Credit note"), "returning from the Bills screen creates a credit note that is listed");
            Check(catalog.GetAsync(soap.Id).GetAwaiter().GetResult()!.QtyOnHandMilli == stockBefore + 1000, "the returned item goes back to stock");
            ui.ReturnHandler = null;

            // (b) the return dialog itself: bad numbers, limits, reason, confirm
            ReturnDialogRequest MakeRequest(long saleId) => new("Return", "Bill", returnsSvc.GetReturnableAsync(saleId).GetAwaiter().GetResult(), true, "Credit",
                (inp, mode, reason) => returnsSvc.PreviewAsync(new SaleReturnCommand(Guid.NewGuid(), saleId, inp, mode, reason)),
                (inp, mode, reason, id, approver) => returnsSvc.ReturnAsync(new SaleReturnCommand(id, saleId, inp, mode, reason, approver)));
            var dlg = new ReturnDialogViewModel(MakeRequest(billId), ui);
            dlg.Lines[0].QtyText = "abc"; dlg.RecomputeAsync().GetAwaiter().GetResult();
            Check(dlg.Lines[0].HasError && !dlg.CanConfirm, "a non-number quantity is flagged and cannot be confirmed");
            dlg.Lines[0].QtyText = "99"; dlg.RecomputeAsync().GetAwaiter().GetResult();
            Check(dlg.Lines[0].Error!.Contains("At most") && !dlg.CanConfirm, "returning more than is left is flagged with the limit");
            dlg.Lines[0].QtyText = "1"; dlg.RecomputeAsync().GetAwaiter().GetResult();
            Check(dlg.PreviewText.Contains("118") && !dlg.CanConfirm, "a valid quantity shows the amount but still needs a reason");
            dlg.Reason = "Wrong size"; dlg.RecomputeAsync().GetAwaiter().GetResult();
            Check(dlg.CanConfirm, "with a reason the return can be confirmed");
            dlg.ConfirmCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(dlg.Result != null && dlg.Result.ReturnNumber.StartsWith("CN/"), "confirming saves the credit note");

            // (c) a cashier doing the last return needs Admin approval, given at the prompt
            current.SignOut();
            auth.LoginAsync("cashier1", "Cashier#12345").GetAwaiter().GetResult();
            ui.AdminPasswordToGive = "Admin#12345"; ui.ApprovalPrompts = 0;
            var dlg2 = new ReturnDialogViewModel(MakeRequest(billId), ui);
            dlg2.Lines[0].QtyText = "1"; dlg2.Reason = "Faulty"; dlg2.RecomputeAsync().GetAwaiter().GetResult();
            dlg2.ConfirmCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(ui.ApprovalPrompts == 1 && dlg2.Result != null, "a cashier's return asks for Admin approval and goes through once approved");
            Check(returnsSvc.GetReturnableAsync(billId).GetAwaiter().GetResult()[0].RemainingMilli == 0, "all three units are now returned and none can be returned again");
            current.SignOut();
            auth.LoginAsync("owner", "Admin#12345").GetAwaiter().GetResult();

            // (d) purchase entry window logic
            suppliersSvc.SaveAsync(new SupplierDto(0, "Metro Wholesale", "9000000002", null, "33", 0)).GetAwaiter().GetResult();
            PurchaseEntryViewModel NewPurchase() { var v = new PurchaseEntryViewModel(purchasesSvc, suppliersSvc, catalog, settings, ui); v.InitializeAsync().GetAwaiter().GetResult(); return v; }
            var pe = NewPurchase();
            pe.SupplierQuery = "Metro"; pe.SearchSupplierCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(pe.SupplierMatches.Count == 1, "searching for a supplier finds it");
            pe.PickSupplierCommand.Execute(pe.SupplierMatches[0]);
            pe.ScanText = "222"; pe.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(pe.Lines.Count == 1 && pe.Lines[0].GstPercent == 5, "scanning an item adds it with its GST rate pre-filled");
            pe.Lines[0].Quantity = 10; pe.Lines[0].UnitCost = 20;
            Check(pe.TotalPaise == 21000, "10 items at 20.00 plus 5% GST totals 210.00");
            Check(!pe.SaveCommand.CanExecute(null), "a purchase cannot be saved without the supplier's invoice number");
            pe.InvoiceNo = "SUP-1";
            Check(pe.PaymentOk && pe.SaveCommand.CanExecute(null), "with the invoice number the purchase can be saved");
            pe.Lines[0].Quantity = 0;
            Check(pe.Lines[0].HasError && !pe.SaveCommand.CanExecute(null), "zero quantity on a purchase line is flagged");
            pe.Lines[0].Quantity = 10;
            var saltBefore = catalog.GetAsync(salt.Id).GetAwaiter().GetResult()!.QtyOnHandMilli;
            pe.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(catalog.GetAsync(salt.Id).GetAwaiter().GetResult()!.QtyOnHandMilli == saltBefore + 10_000, "saving the purchase adds the stock");
            var dup = NewPurchase();
            dup.PickSupplierCommand.Execute(suppliersSvc.SearchAsync("Metro").GetAwaiter().GetResult()[0]);
            dup.ScanText = "222"; dup.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            dup.Lines[0].UnitCost = 20; dup.Lines[0].Quantity = 10; dup.InvoiceNo = "sup-1";
            dup.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(dup.StatusIsError && dup.StatusMessage.Contains("already"), "the same supplier invoice entered twice is refused with a clear message");

            // credit purchase, paid later from the Suppliers screen
            var cp = NewPurchase();
            cp.PickSupplierCommand.Execute(suppliersSvc.SearchAsync("Metro").GetAwaiter().GetResult()[0]);
            cp.ScanText = "222"; cp.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            cp.Lines[0].UnitCost = 50; cp.Lines[0].Quantity = 1; cp.InvoiceNo = "SUP-2";      // 50.00 + 5% = 52.50 -> 53
            cp.CreditText = "53";
            Check(cp.PaymentOk, "a purchase fully on account is accepted");
            cp.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            var sv = new SuppliersViewModel(suppliersSvc, ui);
            sv.LoadAsync().GetAwaiter().GetResult();
            sv.Selected = sv.Items[0];
            Check(sv.OwedText.Contains("53"), "the Suppliers screen shows what is owed");
            sv.PayAmount = "20"; sv.PayCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(sv.OwedText.Contains("33"), "paying the supplier reduces the amount owed");

            // (e) Purchases screen: list, then send goods back
            var pv = new PurchasesViewModel(purchasesSvc, settings, ui);
            pv.LoadAsync().GetAwaiter().GetResult();
            Check(pv.Items.Count == 2, "the Purchases screen lists both purchases");
            pv.Selected = pv.Items.First(i => i.InvoiceNo == "SUP-1");
            var saltMid = catalog.GetAsync(salt.Id).GetAwaiter().GetResult()!.QtyOnHandMilli;
            ui.ReturnHandler = async req =>
            {
                var inputs = new[] { new ReturnLineInput(req.Lines[0].LineId, 2000, false) };
                return await req.Submit(inputs, PaymentMode.Cash, "Damaged in transit", Guid.NewGuid(), null);
            };
            pv.ReturnToSupplierCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(pv.Returns.Count == 1 && pv.Status.Contains("Debit note"), "sending goods back creates a debit note that is listed");
            Check(catalog.GetAsync(salt.Id).GetAwaiter().GetResult()!.QtyOnHandMilli == saltMid - 2000, "the goods sent back leave the stock");
            ui.ReturnHandler = null;

            // ---------- Expenses screen ----------
            var expVm = new ExpensesViewModel(sp.GetRequiredService<IExpenseService>(), ui);
            expVm.LoadAsync().GetAwaiter().GetResult();
            Check(expVm.Categories.Count >= 18 && expVm.Category != null && expVm.FilterCategories.Count == expVm.Categories.Count + 1, "the Expenses screen lists the starting categories");
            expVm.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(expVm.StatusIsError && expVm.Items.Count == 0, "saving without an amount is refused with a message");
            expVm.Category = expVm.Categories.First(c => c.Name == "Rent");
            expVm.PayTo = "Landlord"; expVm.Description = "Shop rent"; expVm.AmountText = "15000";
            expVm.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(!expVm.StatusIsError && expVm.Status.Contains("Saved EXP/") && expVm.Items.Count == 1 && expVm.TotalText.Contains("15,000"), "a cash expense is saved, listed and totalled");
            Check(expVm.AmountText == "" && expVm.Description == "", "the form is cleared ready for the next expense");
            expVm.Category = expVm.Categories.First(c => c.Name == "Salaries and wages");
            expVm.PayOption = expVm.PayOptions[3]; expVm.PayTo = "Priya"; expVm.Description = "October salary"; expVm.AmountText = "5000";
            expVm.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            expVm.UnpaidOnly = true; expVm.LoadAsync().GetAwaiter().GetResult();
            Check(expVm.Items.Count == 1 && expVm.Items[0].OutstandingText.Contains("5,000"), "the not-paid-yet filter shows the unpaid salary");
            expVm.Selected = expVm.Items[0];
            Check(expVm.CanSettle && expVm.PayCommand.CanExecute(null), "an unpaid expense can be paid");
            expVm.SettleAmountText = "2000";
            expVm.PayCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(expVm.Items.Count == 1 && expVm.Items[0].OutstandingText.Contains("3,000"), "paying part of it leaves the rest to pay");
            expVm.SettleAmountText = "9000";
            expVm.PayCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(expVm.StatusIsError && expVm.Status.Contains("more than"), "paying more than is owed is refused");
            expVm.UnpaidOnly = false; expVm.LoadAsync().GetAwaiter().GetResult();
            expVm.Selected = expVm.Items.First(i => i.Category == "Rent");
            ui.PromptAnswer = null;
            expVm.CancelExpenseCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(!expVm.Items.First(i => i.Category == "Rent").IsVoided, "closing the reason box without a reason cancels nothing");
            ui.PromptAnswer = "Entered by mistake";
            expVm.CancelExpenseCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(expVm.Items.First(i => i.Category == "Rent").IsVoided && expVm.Items.First(i => i.Category == "Rent").PaidByText == "Cancelled", "a mistaken expense can be cancelled with a reason");
            ui.PromptAnswer = null;
            expVm.NewCategoryName = "Festival decoration";
            expVm.AddCategoryCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(expVm.Categories.Any(c => c.Name == "Festival decoration") && expVm.Category!.Name == "Festival decoration", "a new category can be added and is selected");
            expVm.NewCategoryName = "festival DECORATION";
            expVm.AddCategoryCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(expVm.StatusIsError, "a category name that already exists is refused");
            Check(expVm.ByCategory.Any(c => c.Category == "Salaries and wages"), "the by-category tab lists the totals");

            // GST credit on an expense (paid by UPI, so the drawer is unaffected)
            expVm.Category = expVm.Categories.First(c => c.Name == "Repairs and maintenance");
            expVm.PayOption = expVm.PayOptions[1];
            expVm.PayTo = "Fix-It Works"; expVm.Description = "Counter repair"; expVm.Reference = "FW-77";
            expVm.AmountText = "1180"; expVm.GstText = "180"; expVm.ClaimCredit = true; expVm.SupplierGstin = "";
            Check(expVm.ShowGst && expVm.GstSummaryText.Contains("claimed as input credit") && expVm.GstSummaryText.Contains("1,000.00"), "the form shows the expense and the GST that would be claimed");
            expVm.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(expVm.StatusIsError && expVm.Status.Contains("GSTIN"), "claiming credit without the supplier's GSTIN is refused with a message");
            long InputCredit() => reports.TrialBalanceAsync(DateOnly.FromDateTime(DateTime.Today)).GetAwaiter().GetResult().Rows.Where(x => x.Code is AccountCodes.InputCgst or AccountCodes.InputSgst).Sum(x => x.DebitPaise - x.CreditPaise);
            var creditBefore = InputCredit();
            expVm.SupplierGstin = "33AAAAA1111A1Z5";
            expVm.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(!expVm.StatusIsError && expVm.GstText == "" && !expVm.ClaimCredit && expVm.Items.Any(i => i.Category == "Repairs and maintenance" && i.CreditText.Contains("180")), "an expense with GST credit is saved, listed with its credit, and the GST fields are cleared");
            Check(InputCredit() == creditBefore + 18_000, "the claimed GST is added to the input-credit accounts in the books");
            expVm.Category = expVm.Categories.First(c => c.Name == "Depreciation");
            Check(!expVm.ShowGst, "depreciation has no GST fields");
            expVm.Category = expVm.Categories.First(c => c.Name == "Rent");
            Check(expVm.ShowGst, "the GST fields come back for other categories");

            // ---------- Day end screen ----------
            var dayVm = new DayEndViewModel(sp.GetRequiredService<IDayCloseService>(), settings, ui);
            dayVm.RefreshAsync().GetAwaiter().GetResult();
            Check(dayVm.IsFirstClose && dayVm.CashLines.Count >= 2 && dayVm.SalesLines.Count >= 3, "the Day end screen shows the cash lines and the sales of the period");
            var expectedBefore = dayVm.Preview!.Report.ExpectedCashPaise;
            dayVm.OpeningFloatText = "100000"; dayVm.RefreshAsync().GetAwaiter().GetResult();
            Check(dayVm.Preview!.Report.ExpectedCashPaise == expectedBefore + 10_000_000, "typing the opening cash raises the expected cash by that amount");
            dayVm.MovementAmountText = "120"; dayVm.MovementReason = "Tea and courier";
            dayVm.RecordMovementCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(dayVm.Movements.Count == 1 && dayVm.Preview!.Report.ExpectedCashPaise == expectedBefore + 10_000_000 - 12_000, "recording a pay-out lowers the expected cash");
            dayVm.MovementAmountText = "abc"; dayVm.RecordMovementCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(dayVm.StatusIsError && dayVm.Movements.Count == 1, "a non-number pay-out amount is refused");
            var expectedNow = dayVm.Preview!.Report.ExpectedCashPaise;
            Check(!dayVm.CloseDayCommand.CanExecute(null), "the day cannot be closed before the cash is counted");
            dayVm.CountedText = (expectedNow / 100m).ToString("0.00", System.Globalization.CultureInfo.CurrentCulture);
            Check(dayVm.DifferenceKind == 1 && dayVm.CloseDayCommand.CanExecute(null), "counting exactly the expected cash can be closed straight away");
            dayVm.CountedText = ((expectedNow - 1000) / 100m).ToString("0.00", System.Globalization.CultureInfo.CurrentCulture);
            Check(dayVm.DifferenceKind == 2 && dayVm.DifferenceText.Contains("Short") && !dayVm.CloseDayCommand.CanExecute(null), "a shortage is shown and needs a note before closing");
            dayVm.Note = "Gave too much change";
            Check(dayVm.CloseDayCommand.CanExecute(null), "with a note the shortage can be closed");
            dayVm.CloseDayCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(dayVm.Closes.Count == 1 && dayVm.Closes[0].DifferenceText.StartsWith("-") && !dayVm.IsFirstClose, "closing the day lists it, marks the shortage and starts a new period");
            var cashAfter = reports.TrialBalanceAsync(DateOnly.FromDateTime(DateTime.Today)).GetAwaiter().GetResult().Rows.Where(x => x.Code == AccountCodes.Cash).Sum(x => x.DebitPaise - x.CreditPaise);
            Check(cashAfter == expectedNow - 1000, "after the close the cash account in the books equals the cash that was counted");
            Check(dayVm.Preview!.Report.OpeningCashPaise == expectedNow - 1000, "the counted cash becomes the opening cash of the next period");

            // ---------- Profit and loss screen ----------
            var pnlVm = new ProfitLossViewModel(sp.GetRequiredService<IProfitLossService>(), ui);
            pnlVm.PrepareAsync().GetAwaiter().GetResult();
            Check(pnlVm.Report != null && pnlVm.Rows.Count > 8 && pnlVm.Items.Count > 0, "the profit and loss screen fills the statement and the profit by item");
            var tbNow = reports.TrialBalanceAsync(DateOnly.FromDateTime(DateTime.Today)).GetAwaiter().GetResult().Rows;
            var incomeBooks = tbNow.Where(x => x.Code.StartsWith("4")).Sum(x => x.CreditPaise - x.DebitPaise);
            var expenseBooks = tbNow.Where(x => x.Code.StartsWith("5") || x.Code.StartsWith("6")).Sum(x => x.DebitPaise - x.CreditPaise);
            Check(pnlVm.Report!.Current.NetProfitPaise == incomeBooks - expenseBooks, "net profit equals income less expense in the books");
            Check(pnlVm.Items.Sum(x => x.ProfitPaise) == pnlVm.Report.Current.GrossProfitPaise, "profit by item adds up to the gross profit");
            Check(pnlVm.Rows.Any(x => x.Kind == PnlRowKind.Total && x.Label == "NET PROFIT") && pnlVm.Headline.Contains("Net profit"), "the statement ends with the net profit and a headline");
            pnlVm.Period = pnlVm.Periods[1]; pnlVm.PrepareAsync().GetAwaiter().GetResult();
            Check(pnlVm.Report!.Current.NetSalesPaise == 0 && pnlVm.Status.Contains("Nothing"), "choosing last month shows an empty period with a clear message");
            pnlVm.From = DateTime.Today.AddDays(-3);
            Check(pnlVm.Period.Label == "Choose dates", "editing a date switches the period to custom dates");
            pnlVm.Period = pnlVm.Periods[0]; pnlVm.PrepareAsync().GetAwaiter().GetResult();
            var pnlFile = Path.Combine(Path.GetTempPath(), $"selftest-pnl-{Guid.NewGuid():N}.xlsx");
            ui.SavePath = pnlFile;
            pnlVm.ExportCommand.Execute(null);
            Check(File.Exists(pnlFile) && new FileInfo(pnlFile).Length > 3000, "saving writes the Excel file");
            try { File.Delete(pnlFile); } catch (IOException) { }
            Check(OtherDocuments.BuildProfitLoss(pnlVm.Report!).Blocks.Count > 10, "the printable statement is built");
            current.SignOut();
            auth.LoginAsync("cashier1", "Cashier#12345").GetAwaiter().GetResult();
            pnlVm.PrepareAsync().GetAwaiter().GetResult();
            Check(ui.Errors.Count == 0 && pnlVm.Status.Contains("Only the Admin"), "a cashier cannot see profit and loss");
            current.SignOut();
            auth.LoginAsync("owner", "Admin#12345").GetAwaiter().GetResult();

            // ---------- Balance sheet screen ----------
            var bsVm = new BalanceSheetViewModel(sp.GetRequiredService<IBalanceSheetService>(), ui);
            bsVm.PrepareAsync().GetAwaiter().GetResult();
            Check(bsVm.Report != null && bsVm.Rows.Count > 12 && bsVm.Report.Checks.Count == 4, "the balance sheet screen fills the statement and the four checks");
            Check(bsVm.Balanced && bsVm.Headline.Contains("="), "assets equal liabilities plus owner's funds");
            var tbBs = reports.TrialBalanceAsync(DateOnly.FromDateTime(DateTime.Today)).GetAwaiter().GetResult().Rows;
            Check(bsVm.Report!.Current.TotalAssetsPaise == tbBs.Where(x => x.Code.StartsWith("1")).Sum(x => x.DebitPaise - x.CreditPaise), "total assets equal the asset accounts in the books");
            bsVm.AsAt = DateTime.Today.AddDays(-1);
            Check(bsVm.CompareAt == DateTime.Today.AddDays(-1).AddMonths(-1), "changing the date moves the comparison date one month earlier");
            bsVm.PrepareAsync().GetAwaiter().GetResult();
            Check(bsVm.Report!.Checks.Count == 0 && bsVm.Warnings.Any(w => w.Contains("only made for today")), "a past date explains why the record checks are skipped");
            bsVm.AsAt = DateTime.Today; bsVm.PrepareAsync().GetAwaiter().GetResult();
            var bsFile = Path.Combine(Path.GetTempPath(), $"selftest-bs-{Guid.NewGuid():N}.xlsx");
            ui.SavePath = bsFile;
            bsVm.ExportCommand.Execute(null);
            Check(File.Exists(bsFile) && new FileInfo(bsFile).Length > 3000, "saving writes the Excel file");
            try { File.Delete(bsFile); } catch (IOException) { }
            Check(OtherDocuments.BuildBalanceSheet(bsVm.Report!).Blocks.Count > 12, "the printable balance sheet is built");
            current.SignOut();
            auth.LoginAsync("cashier1", "Cashier#12345").GetAwaiter().GetResult();
            bsVm.PrepareAsync().GetAwaiter().GetResult();
            Check(ui.Errors.Count == 0 && bsVm.Status.Contains("Only the Admin"), "a cashier cannot see the balance sheet");
            current.SignOut();
            auth.LoginAsync("owner", "Admin#12345").GetAwaiter().GetResult();

            // ---------- Credit customers: limit, statement, ageing, reminder ----------
            var creditVm = new CreditViewModel(sp.GetRequiredService<ICreditService>(), ui);
            creditVm.PrepareAsync().GetAwaiter().GetResult();
            var owedAll = parties.SearchAsync("", 100).GetAwaiter().GetResult().Sum(p => p.OutstandingPaise);
            var recvBooks = reports.TrialBalanceAsync(DateOnly.FromDateTime(DateTime.Today)).GetAwaiter().GetResult().Rows.Where(x => x.Code == AccountCodes.Receivables).Sum(x => x.DebitPaise - x.CreditPaise);
            Check(creditVm.Rows.Any(r => r.Name == "Ravi Traders") && creditVm.SummaryText.Contains("owe"), "the Credit screen lists the customers who owe money with a summary");
            Check(creditVm.Rows.Sum(r => r.Row.OutstandingPaise) == owedAll && owedAll == recvBooks, "what the Credit screen adds up to equals the customers' balances and the receivables in the books");
            creditVm.Selected = creditVm.Rows.First(r => r.Name == "Ravi Traders");
            WaitUntil(() => creditVm.Entries.Count > 0);
            Check(creditVm.Entries.Count >= 1 && creditVm.ClosingText.Contains("118.00") && creditVm.CustomerText.Contains("no credit limit"), "choosing a customer shows their statement ending on what they owe");
            creditVm.PrintStatementCommand.Execute(null);
            Check(ui.LastCreditDoc != null && ui.LastCreditDoc.PageHeight > 1000 && ui.LastCreditDoc.Blocks.Count > 5, "the statement is built as A4 pages");
            ui.LastCreditDoc = null;
            creditVm.PrintReminderCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(ui.LastCreditDoc != null && ui.LastCreditDoc.Blocks.OfType<System.Windows.Documents.Table>().Any(), "the payment reminder lists the unpaid bills");
            var creditFile = Path.Combine(Path.GetTempPath(), $"selftest-credit-{Guid.NewGuid():N}.xlsx");
            ui.SavePath = creditFile;
            creditVm.ExportAgeingCommand.Execute(null);
            Check(File.Exists(creditFile) && new FileInfo(creditFile).Length > 3000, "the list of who owes you is saved to Excel");
            try { File.Delete(creditFile); } catch (IOException) { }
            creditVm.ExportStatementCommand.Execute(null);
            Check(File.Exists(creditFile), "the statement is saved to Excel");
            try { File.Delete(creditFile); } catch (IOException) { }
            ui.SavePath = null;

            var custVm = new CustomersViewModel(parties, current, ui);
            custVm.LoadAsync().GetAwaiter().GetResult();
            custVm.Selected = custVm.Items.First(c => c.Name == "Ravi Traders");
            Check(custVm.CanSetLimit, "the Admin sees the credit limit field");
            custVm.CreditLimitText = "abc";
            custVm.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(custVm.StatusIsError && parties.SearchAsync("Ravi").GetAwaiter().GetResult()[0].CreditLimitPaise == 0, "a credit limit that is not a number is refused");
            custVm.CreditLimitText = "100";
            custVm.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(!custVm.StatusIsError && parties.SearchAsync("Ravi").GetAwaiter().GetResult()[0].CreditLimitPaise == 10_000 && custVm.OwesText.Contains("over the limit"), "a credit limit is saved and the customer is shown as over it");

            vm = NewVm();
            vm.PickCustomerCommand.Execute(parties.SearchAsync("Ravi").GetAwaiter().GetResult()[0]);
            Check(vm.CustomerText.Contains("OVER LIMIT") && vm.CustomerText.Contains("limit"), "the billing screen warns when the customer is over their limit");
            vm.ScanText = "111"; vm.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            vm.CreditText = "118";
            before = CountBills(); ui.ApprovalPrompts = 0;
            vm.CompleteCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(ui.ApprovalPrompts == 0 && CountBills() == before + 1, "the Admin can still sell on credit over the limit");

            current.SignOut();
            auth.LoginAsync("cashier1", "Cashier#12345").GetAwaiter().GetResult();
            vm = NewVm();
            vm.PickCustomerCommand.Execute(parties.SearchAsync("Ravi").GetAwaiter().GetResult()[0]);
            vm.ScanText = "111"; vm.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            vm.CreditText = "118";
            before = CountBills(); ui.ApprovalPrompts = 0; ui.AdminPasswordToGive = null;
            vm.CompleteCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(ui.ApprovalPrompts == 1 && CountBills() == before && vm.Lines.Count == 1, "a cashier's credit sale over the limit asks for Admin approval and, if refused, saves nothing");
            ui.AdminPasswordToGive = "Admin#12345";
            vm.CompleteCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(CountBills() == before + 1, "with the Admin password the credit sale is saved");
            ui.AdminPasswordToGive = null;
            Check(!new CustomersViewModel(parties, current, ui).CanSetLimit, "a cashier does not see the credit limit field");
            creditVm.PrepareAsync().GetAwaiter().GetResult();
            Check(creditVm.Status.Contains("Only the Admin") && creditVm.Rows.Count == 0, "a cashier cannot open customer accounts");
            ui.Errors.Clear();
            current.SignOut();
            auth.LoginAsync("owner", "Admin#12345").GetAwaiter().GetResult();

            // ---------- Barcodes, labels and repacking (Items screen) ----------
            // Services that do real asynchronous work (files, hashing) continue on the screen's message loop, so wait by running that loop,
            // and run such calls off it when blocking for the result.
            void WaitUntil(Func<bool> done)
            {
                for (var i = 0; i < 150 && !done(); i++)
                {
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => frame.Continue = false));
                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                    Thread.Sleep(20);
                }
            }
            void Off(Func<Task> work) => Task.Run(work).GetAwaiter().GetResult();
            var prodVm = new ProductsViewModel(catalog, sp.GetRequiredService<IBarcodeService>(), settings, ui);
            var stockVm = new StockViewModel(catalog, stock, sp.GetRequiredService<IRepackService>(), settings, ui) { LiveSearchDelayMs = 0 };
            prodVm.LoadAsync().GetAwaiter().GetResult();
            prodVm.Selected = prodVm.Items.First(i => i.Sku == "S2");
            Check(prodVm.IsExisting && prodVm.IsFormOpen && prodVm.FormTitle == "Edit item", "choosing an item opens the edit dialog for it");
            stockVm.LoadAsync().GetAwaiter().GetResult();
            stockVm.Selected = stockVm.Items.First(i => i.Sku == "S2");
            Check(stockVm.IsPanelOpen && stockVm.RepackTargets.All(t => t.Sku != "S2"), "choosing an item on the Stock tab offers every other item as a repack target");
            prodVm.GenerateBarcodeCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(BillingSoftware.Domain.Ean13.IsValid(prodVm.Barcode) && prodVm.Barcode.StartsWith("29"), "Make a barcode fills in a valid shop-made EAN-13 code");
            prodVm.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(!prodVm.StatusIsError && catalog.GetByBarcodeAsync(prodVm.Barcode).GetAwaiter().GetResult()?.Sku == "S2", "the made barcode is saved on the item and finds it when scanned");
            prodVm.ExtraCode = "4006381333931"; prodVm.ExtraQtyText = "0.25"; prodVm.ExtraNote = "250 g pack";
            prodVm.AddExtraCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            WaitUntil(() => prodVm.Extras.Count == 1);
            Check(prodVm.Extras.Count == 1 && prodVm.Extras[0].Text.Contains("0.25") && prodVm.Extras[0].Text.Contains("250 g pack"), "an extra barcode with a pack quantity is added and listed");
            prodVm.ExtraCode = "4006381333931"; prodVm.ExtraQtyText = "1";
            prodVm.AddExtraCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(prodVm.StatusIsError && prodVm.Status.Contains("already uses"), "the same barcode cannot be added twice");
            prodVm.ExtraCode = "BADCODE"; prodVm.ExtraQtyText = "abc";
            prodVm.AddExtraCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(prodVm.StatusIsError && prodVm.Extras.Count == 1, "a non-number pack quantity is refused with a message");
            prodVm.ExtraCode = prodVm.ExtraQtyText = "";

            vm = NewVm();
            vm.ScanText = "4006381333931"; vm.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(vm.Lines.Count == 1 && vm.Lines[0].Quantity == 0.25m && vm.TotalPaise == 700 && vm.StatusMessage.Contains("pack barcode"), "scanning a pack barcode adds a quarter kilo of salt at the salt price");
            vm.ScanText = "4006381333931"; vm.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(vm.Lines.Count == 1 && vm.Lines[0].Quantity == 0.5m, "scanning it again adds another pack to the same line");
            vm.ScanText = "S2"; vm.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(vm.Lines[0].Quantity == 1.5m, "the item's own code still adds one whole unit");
            vm.ScanText = "4006381333931"; vm.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            var saltStockBeforeSale = catalog.GetAsync(salt.Id).GetAwaiter().GetResult()!.QtyOnHandMilli;
            vm.CashText = "100";
            vm.CompleteCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(catalog.GetAsync(salt.Id).GetAwaiter().GetResult()!.QtyOnHandMilli == saltStockBeforeSale - 1_750, "the pack quantities come off the stock of the item");

            prodVm.LabelCopiesText = "x";
            prodVm.PrintLabelsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(prodVm.StatusIsError && prodVm.Status.Contains("how many"), "a non-number label count is refused");
            PrintConfig.LabelMode = "sheet";
            prodVm.LabelCopiesText = "5"; prodVm.SelectedExtra = prodVm.Extras[0];
            prodVm.PrintLabelsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            var labelTable = ui.LastLabels?.Blocks.OfType<System.Windows.Documents.Table>().FirstOrDefault();
            Check(!prodVm.StatusIsError && labelTable != null && labelTable.RowGroups[0].Rows.Count == 2 && ui.LastLabels!.PageHeight > 1000, "five labels make a two-row A4 sheet");
            PrintConfig.LabelMode = "roll"; PrintConfig.LabelWidthMm = 50; PrintConfig.LabelHeightMm = 25;
            prodVm.PrintLabelsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(!prodVm.StatusIsError && ui.LastLabels != null && ui.LastLabels.Blocks.Count == 5 && ui.LastLabels.PageHeight < 200 && (ui.LastLabels.Tag as string) == "label", "on a label printer each of the five labels is its own small page");
            var bars = LabelBuilder.BarcodeVisual("4006381333931") as System.Windows.Controls.Canvas;
            Check(bars != null && bars.Children.Count > 25 && bars.Width > 150, "the barcode is drawn as bars");
            prodVm.SelectedExtra = null;
            prodVm.PrintLabelsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(!prodVm.StatusIsError, "labels can also be printed for the item's own barcode");
            var noBarcode = catalog.SaveAsync(new ProductDto(0, "S9", null, "No barcode item", null, 0, "pcs", 1000, null, true, 0, 0, true)).GetAwaiter().GetResult();
            prodVm.LoadAsync().GetAwaiter().GetResult();
            prodVm.Selected = prodVm.Items.First(i => i.Sku == "S9");
            prodVm.PrintLabelsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(prodVm.StatusIsError && prodVm.Status.Contains("no barcode"), "an item without a barcode is told to make one before printing labels");

            var salt500 = catalog.SaveAsync(new ProductDto(0, "S2-500", "5901234123457", "Salt 500 g pack", "2501", 500, "pcs", 1500, 1500, true, 0, 0, true)).GetAwaiter().GetResult();
            stockVm.LoadAsync().GetAwaiter().GetResult();
            stockVm.Selected = stockVm.Items.First(i => i.Sku == "S2");
            stockVm.RepackTarget = stockVm.RepackTargets.First(t => t.Sku == "S2-500");
            stockVm.RepackSourceQtyText = "5"; stockVm.RepackTargetQtyText = "10";
            WaitUntil(() => stockVm.RepackPreviewText.Length > 0);
            Check(stockVm.RepackPreviewText.Contains("Uses stock worth") && stockVm.RepackPreviewText.Contains("will cost") && !stockVm.RepackPreviewIsError, "the repack preview shows the cost of each pack before saving (" + stockVm.RepackPreviewText + ")");
            var saltBeforeRepack = catalog.GetAsync(salt.Id).GetAwaiter().GetResult()!.QtyOnHandMilli;
            stockVm.RepackNowCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(!stockVm.StatusIsError && catalog.GetAsync(salt.Id).GetAwaiter().GetResult()!.QtyOnHandMilli == saltBeforeRepack - 5_000
                  && catalog.GetAsync(salt500.Id).GetAwaiter().GetResult()!.QtyOnHandMilli == 10_000, "repacking moves 5 kg of salt into 10 packs");
            stockVm.Selected = stockVm.Items.First(i => i.Sku == "S2");
            stockVm.RepackTarget = stockVm.RepackTargets.First(t => t.Sku == "S2-500");
            stockVm.RepackSourceQtyText = "9999"; stockVm.RepackTargetQtyText = "10";
            stockVm.RepackNowCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(stockVm.StatusIsError && stockVm.Status.Contains("in stock"), "repacking more than is in stock is refused with a message");
            current.SignOut();
            auth.LoginAsync("cashier1", "Cashier#12345").GetAwaiter().GetResult();
            stockVm.RepackSourceQtyText = "1"; stockVm.RepackTargetQtyText = "2";
            stockVm.RepackNowCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            prodVm.GenerateBarcodeCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(ui.Errors.Count == 0 || ui.Errors.All(e => e.Contains("Only the Admin") || e.Contains("permission")) , "a cashier cannot repack or make barcodes");
            ui.Errors.Clear();
            current.SignOut();
            auth.LoginAsync("owner", "Admin#12345").GetAwaiter().GetResult();

            // ---------- Hold and recall ----------
            var holdSvc = sp.GetRequiredService<IHoldService>();
            vm = NewVm();
            vm.ScanText = "111"; vm.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            vm.HoldCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(vm.Lines.Count == 0 && vm.HeldCount == 1 && vm.HeldText == "Held bills (1)", "holding a bill clears the screen and counts it");
            vm.HoldCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(vm.StatusIsError && vm.HeldCount == 1, "an empty bill cannot be held");
            vm.ScanText = "S2"; vm.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            ui.HeldPick = holdSvc.ListAsync().GetAwaiter().GetResult().First().Id;
            vm.RecallCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(vm.Lines.Count == 1 && vm.Lines[0].Product.Sku == "S1" && vm.HeldCount == 1, "recalling puts the held bill back and holds the bill that was on the screen");
            ui.HeldPick = null; ui.HeldDiscard = holdSvc.ListAsync().GetAwaiter().GetResult().First().Id;
            vm.RecallCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(vm.HeldCount == 0 && vm.Lines.Count == 1, "a held bill can be discarded");
            vm.RecallCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(vm.StatusMessage.Contains("No bills"), "with nothing held the screen says so");
            vm.ClearCartCommand.Execute(null);

            // ---------- Settings: period lock, restore, printers, health check ----------
            var appPaths = sp.GetRequiredService<AppPaths>();
            var setVm = new SettingsViewModel(settings, auth, sp.GetRequiredService<IBackupService>(), sp.GetRequiredService<IRestoreService>(), sp.GetRequiredService<IPeriodLockService>(),
                sp.GetRequiredService<IDiagnosticsService>(), sp.GetRequiredService<IAuditService>(), appPaths, ui);
            var backupFolder = Path.Combine(Path.GetTempPath(), "selftest-backups-" + Guid.NewGuid().ToString("N"));
            settings.SetAsync(SettingKeys.BackupFolder, backupFolder).GetAwaiter().GetResult();
            Off(() => sp.GetRequiredService<IBackupService>().BackupNowAsync());
            setVm.LoadAsync().GetAwaiter().GetResult();

            // period lock
            setVm.LockDate = DateTime.Today.AddDays(-30);
            ui.PromptAnswer = "wrong-password";
            setVm.SaveLockCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(setVm.LockIsError && setVm.LockStatus.Contains("Incorrect") && !setVm.HasLock, "locking with the wrong Admin password is refused");
            ui.PromptAnswer = "Admin#12345";
            setVm.SaveLockCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(!setVm.LockIsError && setVm.HasLock && setVm.LockSummary.Contains("locked"), "a period can be locked with the Admin password");
            setVm.LockDate = DateTime.Today.AddDays(-10);
            setVm.SaveLockCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(!setVm.LockIsError && setVm.LockSummary.Contains(DateTime.Today.AddDays(-10).ToString("dd-MMM-yyyy")), "the lock can move forward");
            setVm.LockDate = DateTime.Today.AddDays(-20);
            setVm.SaveLockCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(!setVm.LockIsError && setVm.LockSummary.Contains(DateTime.Today.AddDays(-20).ToString("dd-MMM-yyyy")), "moving the lock back asks for a reason and the password");
            setVm.RemoveLockCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(!setVm.HasLock && setVm.LockSummary.Contains("No period"), "the lock can be removed");
            setVm.LockDate = DateTime.Today;
            setVm.SaveLockCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(setVm.LockIsError, "locking today is refused because it would stop billing");

            // restore
            Check(setVm.Backups.Count == 1, "the Restore section lists the backup made in the folder");
            setVm.SelectedBackup = setVm.Backups[0];
            WaitUntil(() => setVm.RestoreCanRun);
            Check(setVm.RestoreCanRun && setVm.RestoreText.Contains("bill") && setVm.RestoreText.Contains("exactly as it was"), "choosing a backup checks it and shows what it holds");
            ui.PromptAnswer = "wrong-password";
            Off(() => setVm.RestoreNowCommand.ExecuteAsync(null));
            Check(setVm.RestoreIsError && setVm.RestoreStatus.Contains("Incorrect") && ui.Restarts == 0 && !PendingRestore.IsPending(appPaths), "restoring with the wrong Admin password changes nothing");
            ui.PromptAnswer = "Admin#12345";
            Off(() => setVm.RestoreNowCommand.ExecuteAsync(null));
            Check(ui.Restarts == 1 && PendingRestore.IsPending(appPaths), "with the right password the restore is staged and the program restarts to finish it");
            PendingRestore.Cancel(appPaths);                       // the self-test never restarts: remove the staged restore so the data stays as it is
            ui.PromptAnswer = null;
            ui.OpenPath = Path.Combine(Path.GetTempPath(), "nothing-" + Guid.NewGuid().ToString("N") + ".db");
            Off(() => setVm.BrowseBackupCommand.ExecuteAsync(null));
            WaitUntil(() => setVm.RestoreText.Contains("not found"));
            Check(!setVm.RestoreCanRun && setVm.RestoreText.Contains("not found"), "a backup file that does not exist cannot be restored");
            try { Directory.Delete(backupFolder, true); } catch (IOException) { }

            // printers
            Check(setVm.Printers.Count >= 1 && setVm.Paper!.Mm == 80, "the Printers section starts with the Windows default and an 80 mm roll");
            setVm.Paper = setVm.PaperChoices[1]; setVm.LabelWidthText = "40"; setVm.LabelHeightText = "30";
            setVm.SavePrintersCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(!setVm.PrintIsError && PrintConfig.PaperMm == 58 && PrintConfig.LabelWidthMm == 40 && PrintConfig.LabelHeightMm == 30, "printer settings are saved and used straight away");
            var narrow = ReceiptBuilder.Build(sales.GetAsync(sales.ListAsync(DateOnly.FromDateTime(DateTime.Today)).GetAwaiter().GetResult().First().Id).GetAwaiter().GetResult()!, new CompanyDto("Self Test Stores", null, "33", false, null, null), false);
            Check(Math.Abs(narrow.PageWidth - PrintConfig.PaperWidthPx) < 0.5 && narrow.PageWidth < 230, "a 58 mm roll makes a narrower receipt");
            var rollDoc = LabelBuilder.BuildRoll(new[] { new LabelSpec("Shop", "Item one", "₹10.00", "4006381333931"), new LabelSpec("Shop", "Item two", "₹20.00", "5901234123457") }, 40, 30);
            Check(rollDoc.Blocks.Count == 2 && Math.Abs(rollDoc.PageWidth - PrintConfig.Px(40)) < 0.5 && ((System.Windows.Documents.BlockUIContainer)rollDoc.Blocks.LastBlock).BreakPageBefore, "roll labels are as big as the label and one to a page");
            setVm.LabelWidthText = "5";
            setVm.SavePrintersCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(setVm.PrintIsError, "a label width that is too small is refused");
            setVm.Paper = setVm.PaperChoices[0]; setVm.LabelWidthText = "50"; setVm.LabelHeightText = "25";
            setVm.TestPrintCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(!setVm.PrintIsError && PrintConfig.PaperMm == 80, "the test receipt can be made after saving the settings");
            Check(ReceiptBuilder.BuildTest("Shop").Blocks.Count > 5, "the test receipt is built");

            // health check and support file
            Off(() => setVm.RunHealthCommand.ExecuteAsync(null));
            Check(setVm.Health.Count >= 7 && setVm.Health.All(h => !h.IsProblem) && setVm.HealthStatus.Length > 0, "the health check runs and finds no problems");
            var supportZip = Path.Combine(Path.GetTempPath(), $"selftest-support-{Guid.NewGuid():N}.zip");
            ui.SavePath = supportZip;
            Off(() => setVm.SaveSupportFileCommand.ExecuteAsync(null));
            Check(File.Exists(supportZip) && new FileInfo(supportZip).Length > 300, "a support file is saved");
            try { File.Delete(supportZip); } catch (IOException) { }
            ui.SavePath = null;

            // ---------- A4 invoice ----------
            var invoiceBill = sales.GetAsync(sales.ListAsync(DateOnly.FromDateTime(DateTime.Today)).GetAwaiter().GetResult().First(b => b.Status == SaleStatus.Posted).Id).GetAwaiter().GetResult()!;
            var invoiceDoc = InvoiceBuilder.BuildA4(invoiceBill, new CompanyDto("Self Test Stores", "33ABCDE1234F1Z5", "33", false, "Main Road", "9000000000"), false);
            var invoiceText = new System.Windows.Documents.TextRange(invoiceDoc.ContentStart, invoiceDoc.ContentEnd).Text;
            Check(invoiceDoc.PageHeight > 1000 && (invoiceDoc.Tag as string) == "a4" && invoiceText.Contains("TAX INVOICE") && invoiceText.Contains("Rupees") && invoiceText.Contains(invoiceBill.InvoiceNumber), "the A4 tax invoice has the business, the bill, the tax summary and the amount in words");

            // ---------- GST can be turned off ----------
            Check(setVm.GstEnabled, "GST is on by default");
            setVm.GstEnabled = false;
            setVm.SaveBusinessCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(!setVm.BusinessIsError && !settings.GetCompanyAsync().GetAwaiter().GetResult()!.GstEnabled, "GST can be turned off in Settings");
            vm = NewVm();
            vm.ScanText = "111"; vm.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(!vm.ShowTax && vm.TotalPaise == 11_800, "with GST off the bill shows no tax and is the item's price");
            var gstOffItems = new ProductsViewModel(catalog, sp.GetRequiredService<IBarcodeService>(), settings, ui);
            gstOffItems.LoadAsync().GetAwaiter().GetResult();
            Check(!gstOffItems.ShowGst, "with GST off the Items screen hides the GST rate and HSN code");
            var gstOffScreen = new GstViewModel(sp.GetRequiredService<IGstReportService>(), ui);
            gstOffScreen.PrepareCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(gstOffScreen.Status.Contains("turned off"), "with GST off the GST screen explains it");
            vm.ClearCartCommand.Execute(null);
            setVm.GstEnabled = true;
            setVm.SaveBusinessCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            vm = NewVm();
            vm.ScanText = "111"; vm.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(vm.ShowTax && settings.GetCompanyAsync().GetAwaiter().GetResult()!.GstEnabled, "turning GST on again brings the tax back");
            vm.ClearCartCommand.Execute(null);

            // ---------- Import ----------
            var importVm = new ImportViewModel(sp.GetRequiredService<IImportService>(), ui);
            var csvPath = Path.Combine(Path.GetTempPath(), $"selftest-import-{Guid.NewGuid():N}.csv");
            File.WriteAllText(csvPath, "Name,Price,GST %\nImported pen,12.50,12\nImported book,85,5\n,10,5\nBad price,xyz,5\n");
            ui.OpenPath = csvPath;
            Off(() => importVm.ChooseFileCommand.ExecuteAsync(null));
            Check(importVm.ReadyCount == 2 && importVm.Rows.Count == 4 && importVm.ImportButtonText == "Import 2 rows" && importVm.Summary.Contains("2 ready"), "the Import screen checks the file and shows what will happen to each row");
            Check(importVm.Rows.Count(r => r.IsError) == 2, "rows with a problem are marked and will not be imported");
            Off(() => importVm.ImportNowCommand.ExecuteAsync(null));
            Check(importVm.Summary.Contains("Imported 2") && catalog.SearchAsync("Imported pen").GetAwaiter().GetResult().Count == 1, "importing saves the good rows");
            Off(() => importVm.PreviewAsync());
            Check(importVm.ReadyCount == 0 && importVm.Rows.Count(r => r.IsSkipped) == 2, "importing the same file again skips what is already there");
            var templatePath = Path.Combine(Path.GetTempPath(), $"selftest-template-{Guid.NewGuid():N}.xlsx");
            ui.SavePath = templatePath;
            importVm.SaveTemplateCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(File.Exists(templatePath), "a blank template can be saved");
            try { File.Delete(templatePath); File.Delete(csvPath); } catch (IOException) { }
            ui.SavePath = null; ui.OpenPath = null;
            importVm.Kind = importVm.Kinds[1];
            var custCsv = Path.Combine(Path.GetTempPath(), $"selftest-cust-{Guid.NewGuid():N}.csv");
            File.WriteAllText(custCsv, "Name,Opening due\nImported customer,500\n");
            importVm.FilePath = custCsv;
            Off(() => importVm.PreviewAsync());
            Check(importVm.NeedsAsAt && importVm.ReadyCount == 1, "a file with opening dues asks for the date they are as at");
            try { File.Delete(custCsv); } catch (IOException) { }

            // ---------- Fixes from the first round of testing ----------
            vm = NewVm();
            vm.ScanText = "Sal";
            vm.SearchNowAsync().GetAwaiter().GetResult();
            Check(vm.HasMatches && vm.Matches.Any(m => m.Name == "Salt") && vm.Lines.Count == 0, "while typing, the matching items are listed and nothing is added to the bill");
            Check(vm.Matches.All(m => m.PriceText.Length > 0), "each match shows its price");
            vm.ScanText = "";
            Check(!vm.HasMatches, "clearing the box clears the list");
            vm.ScanText = "no-such-thing-xyz";
            vm.SearchNowAsync().GetAwaiter().GetResult();
            Check(!vm.HasMatches, "typing something that matches nothing lists nothing");
            vm.ScanText = "";

            prodVm.LoadAsync().GetAwaiter().GetResult();
            prodVm.NewCommand.Execute(null);
            prodVm.Name = "No code item"; prodVm.PriceText = "10"; prodVm.GstText = "0"; prodVm.TamilName = "உப்பு";
            prodVm.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(!prodVm.StatusIsError && prodVm.Sku.StartsWith("ITEM-") && prodVm.TamilName == "உப்பு", "an item can be saved without a code: one is made, and the Tamil name is kept");
            Check(prodVm.IsFormOpen, "the item panel is open while an item is being edited");
            prodVm.CloseFormCommand.Execute(null);
            Check(!prodVm.IsFormOpen && prodVm.Selected == null, "Close hides the item panel and clears the selection");
            prodVm.Selected = prodVm.Items.First(i => i.Name == "No code item");
            Check(prodVm.IsFormOpen && prodVm.IsExisting, "choosing an item opens the panel again");
            prodVm.CloseFormCommand.Execute(null);
            prodVm.NewCommand.Execute(null);
            Check(prodVm.IsFormOpen && !prodVm.IsExisting, "New item opens the panel again");

            var tamilBill = new SaleDetailDto(1, "INV/26-27/000999", DateTime.Today, "Owner", null, null, false, 1000, 0, 0, 0, 0, 0, 1000, SaleStatus.Posted, 0,
                new[] { new SaleLineDto("Salt", "2501", "pcs", 1000, 1000, 0, 0, 1000, 0, 1000, "உப்பு"), new SaleLineDto("Pen", "9608", "pcs", 1000, 500, 0, 0, 500, 0, 500) },
                new[] { new PaymentInput(PaymentMode.Cash, 1500) });
            var tamilDoc = ReceiptBuilder.Build(tamilBill, new CompanyDto("Self Test Stores", null, "33", false, null, null), false);
            var tamilText = new System.Windows.Documents.TextRange(tamilDoc.ContentStart, tamilDoc.ContentEnd).Text;
            Check(tamilText.Contains("உப்பு") && !tamilText.Contains("Salt") && tamilText.Contains("Pen"), "the receipt prints the Tamil name when there is one and the English name when there is not");
            var tamilRun = tamilDoc.Blocks.OfType<System.Windows.Documents.Table>().SelectMany(t => t.RowGroups.SelectMany(g => g.Rows)).SelectMany(r => r.Cells)
                .SelectMany(c => c.Blocks.OfType<System.Windows.Documents.Paragraph>()).SelectMany(pg => pg.Inlines.OfType<System.Windows.Documents.Run>()).First(r => r.Text == "உப்பு");
            Check(tamilRun.FontFamily.Source.Contains("Nirmala UI"), "the Tamil name is printed in a Tamil font");

            Check(!sp.GetRequiredService<IBackupService>().GetStatusAsync().GetAwaiter().GetResult().Summary.Contains("system disk"), "the backup status no longer nags about the disk");

            var tableCsv = Path.Combine(Path.GetTempPath(), $"selftest-table-{Guid.NewGuid():N}.csv");
            File.WriteAllText(tableCsv, "Name,Tamil name,Price,GST %\nTable pen,பேனா,12.50,12\nTable book,புத்தகம்,85,5\n", new UTF8Encoding(true));
            var tableVm = new ImportViewModel(sp.GetRequiredService<IImportService>(), ui);
            ui.OpenPath = tableCsv;
            Off(() => tableVm.ChooseFileCommand.ExecuteAsync(null));
            Check(tableVm.Columns.SequenceEqual(new[] { "Name", "Tamil name", "Price", "GST %" }) && tableVm.Rows.Count == 2 && tableVm.Rows[0].Cells[0] == "Table pen" && tableVm.Rows[0].Cells[2] == "12.50",
                "the import preview shows the file's columns and values like a table");
            Check(tableVm.IsPanelOpen && !tableVm.IsDone, "the right-hand panel is open while choosing the file");
            Off(() => tableVm.ImportNowCommand.ExecuteAsync(null));
            Check(tableVm.IsDone && !tableVm.IsPanelOpen && tableVm.Rows.All(r => r.StatusText == "Imported") && tableVm.Rows[1].Cells[1] == "புத்தகம்", "after importing the panel closes and the table shows what was imported");
            Check(catalog.SearchAsync("Table pen").GetAwaiter().GetResult().Single().TamilName == "பேனா", "the Tamil name came in with the item");
            tableVm.ResetCommand.Execute(null);
            Check(tableVm.IsPanelOpen && !tableVm.IsDone && tableVm.Rows.Count == 0 && tableVm.Columns.Count == 0 && tableVm.FilePath == "" && tableVm.Summary == "" && !tableVm.HasFile, "Import another file clears the screen and brings the panel back");
            ui.OpenPath = null;
            try { File.Delete(tableCsv); } catch (IOException) { }

            // ---------- Items tab and Stock tab (1.1 layout) ----------
            prodVm.LiveSearchDelayMs = 0;
            prodVm.CloseFormCommand.Execute(null);
            prodVm.Query = "";
            prodVm.LoadAsync().GetAwaiter().GetResult();
            Check(prodVm.ListSummary.EndsWith("items") && !prodVm.IsFormOpen, "the Items tab lists the items with a count and no dialog open");
            prodVm.NewCommand.Execute(null);
            Check(prodVm.IsFormOpen && prodVm.FormTitle == "New item" && !prodVm.IsExisting, "New item opens the dialog empty");
            var savedRaised = 0;
            prodVm.Saved += () => savedRaised++;
            prodVm.Name = "Dialog item"; prodVm.PriceText = "25"; prodVm.GstText = "5";
            prodVm.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(!prodVm.StatusIsError && savedRaised == 1 && prodVm.Status.StartsWith("Saved"), "saving from the dialog says Saved and tells the screen to close it");
            prodVm.CloseFormCommand.Execute(null);
            Check(!prodVm.IsFormOpen && prodVm.Items.Any(i => i.Name == "Dialog item" && i.StatusText == "Active"), "after closing, the new item is in the table");
            prodVm.NewCommand.Execute(null);
            prodVm.Name = "Bad price"; prodVm.PriceText = "abc";
            prodVm.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(prodVm.StatusIsError && savedRaised == 1 && prodVm.IsFormOpen, "a refused save keeps the dialog open with the message");
            prodVm.CloseFormCommand.Execute(null);
            Check(!prodVm.StatusIsError, "cancelling clears an old error message");

            prodVm.Query = "Dialog";
            WaitUntil(() => prodVm.Items.Count == 1);
            Check(prodVm.Items.Count == 1 && prodVm.Items[0].Name == "Dialog item", "the Items table follows what is typed in the search box");
            prodVm.Query = "";
            WaitUntil(() => prodVm.Items.Count > 1);

            stockVm.Query = "";
            stockVm.LoadAsync().GetAwaiter().GetResult();
            Check(stockVm.Summary.Contains("Stock worth") && stockVm.Items.Count > 1, "the Stock tab shows the item count, what the stock is worth at cost and how many are out of stock");
            var saltRow = stockVm.Items.First(i => i.Sku == "S2");
            Check(saltRow.ValueText.Length > 0 && saltRow.AvgCostText.Length > 0 && saltRow.QtyText.Length > 0, "each stock row shows what is on hand, the average cost and the value");
            stockVm.OnlyOut = true;
            WaitUntil(() => stockVm.Items.All(i => i.IsLow));
            Check(stockVm.Items.All(i => i.IsLow), "Out of stock only lists only items with nothing on hand");
            stockVm.OnlyOut = false;
            WaitUntil(() => stockVm.Items.Count > 1);
            stockVm.Selected = stockVm.Items.First(i => i.Name == "Dialog item");
            var beforeAdd = catalog.GetAsync(stockVm.Selected!.Product.Id).GetAwaiter().GetResult()!.QtyOnHandMilli;
            stockVm.OpeningQty = "12"; stockVm.OpeningCost = "10";
            stockVm.ReceiveStockCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(!stockVm.StatusIsError && catalog.GetAsync(stockVm.Selected!.Product.Id).GetAwaiter().GetResult()!.QtyOnHandMilli == beforeAdd + 12_000, "adding stock on the Stock tab raises what is on hand");
            stockVm.AdjustQty = "-2"; stockVm.AdjustReason = "Damaged";
            stockVm.AdjustCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(!stockVm.StatusIsError && catalog.GetAsync(stockVm.Selected!.Product.Id).GetAwaiter().GetResult()!.QtyOnHandMilli == beforeAdd + 10_000, "adjusting for damage lowers it");
            stockVm.ClosePanelCommand.Execute(null);
            Check(!stockVm.IsPanelOpen && stockVm.Selected == null, "Close hides the stock panel and clears the choice");

            // ---------- Opening balances screen ----------
            var openVm = new OpeningBalancesViewModel(sp.GetRequiredService<IOpeningBalanceService>(), parties, sp.GetRequiredService<ISupplierService>(), ui);
            openVm.LoadAsync().GetAwaiter().GetResult();
            Check(openVm.Kinds.Count == 6 && openVm.Customers.Count > 0 && openVm.Headline.Contains("Nothing"), "the Opening balances screen lists the six kinds and the customers");
            openVm.AmountText = "";
            openVm.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(openVm.StatusIsError && openVm.Items.Count == 0, "saving without an amount is refused with a message");
            openVm.Kind = openVm.Kinds.First(k => k.Kind == OpeningKind.Bank);
            Check(openVm.ShowName && !openVm.ShowCustomer && !openVm.ShowCost, "the bank form asks for a name only");
            openVm.Name = "State Bank current account"; openVm.AmountText = "250000";
            openVm.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(!openVm.StatusIsError && openVm.Items.Count == 1 && openVm.Name == "" && openVm.Summary.Any(r => r.Label.Contains("Bank") && r.Value.Contains("2,50,000")), "a bank balance is saved, listed and the form is cleared");
            openVm.Kind = openVm.Kinds.First(k => k.Kind == OpeningKind.CustomerDue);
            Check(openVm.ShowCustomer && !openVm.ShowName, "the customer form shows the customer picker");
            openVm.AmountText = "5000";
            openVm.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(openVm.StatusIsError && openVm.Status.Contains("customer") && openVm.Items.Count == 1, "a customer balance without choosing the customer is refused");
            openVm.Customer = openVm.Customers.First();
            var custId = openVm.Customer.Id;
            var custBefore = parties.SearchAsync("", 500).GetAwaiter().GetResult().Single(p => p.Id == custId).OutstandingPaise;
            openVm.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(parties.SearchAsync("", 500).GetAwaiter().GetResult().Single(p => p.Id == custId).OutstandingPaise == custBefore + 500_000, "the customer's outstanding includes the opening balance");
            openVm.Kind = openVm.Kinds.First(k => k.Kind == OpeningKind.FixedAsset);
            Check(openVm.ShowCost && openVm.AmountLabel.Contains("Value today"), "the equipment form asks for value today and original cost");
            openVm.Name = "Shelves and counter"; openVm.AmountText = "40000"; openVm.CostText = "50000";
            openVm.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(!openVm.StatusIsError && openVm.Items.Any(i => i.KindText.Contains("Furniture") && i.Detail.Contains("cost")), "equipment is saved with its original cost shown");
            openVm.Kind = openVm.Kinds.First(k => k.Kind == OpeningKind.Loan);
            openVm.Name = "Cooperative bank"; openVm.AmountText = "100000"; openVm.CostText = "";
            openVm.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(openVm.Items.Count == 4 && openVm.Summary.Any(r => r.Label == "Owner's funds at the start"), "a loan is saved and the starting position shows owner's funds");
            openVm.Kind = openVm.Kinds.First(k => k.Kind == OpeningKind.Cash);
            Check(openVm.CashBlocked && !openVm.SaveCommand.CanExecute(null), "cash cannot be added once the drawer has been counted at a day-end close");
            // depreciation from the Expenses screen
            expVm.LoadAsync().GetAwaiter().GetResult();
            expVm.Category = expVm.Categories.First(c => c.Name == "Depreciation");
            Check(expVm.PayOption.Mode == PaymentMode.NonCash, "choosing the Depreciation category selects \"No money involved\"");
            expVm.Description = "Yearly depreciation"; expVm.AmountText = "4000";
            expVm.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(!expVm.StatusIsError && expVm.Items.Any(i => i.Category == "Depreciation" && i.PaidByText == "Depreciation"), "depreciation is saved as an expense with no money involved");
            expVm.Category = expVm.Categories.First(c => c.Name == "Rent");
            Check(expVm.PayOption.Mode != PaymentMode.NonCash, "choosing another category leaves \"No money involved\"");
            openVm.LoadAsync().GetAwaiter().GetResult();
            var shelves = openVm.Items.First(i => i.KindText.Contains("Furniture"));
            openVm.Selected = shelves;
            ui.PromptAnswer = "Entered by mistake";
            openVm.CancelEntryCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            ui.PromptAnswer = null;
            Check(openVm.StatusIsError && openVm.Status.Contains("Depreciation"), "equipment that has been depreciated cannot be cancelled");
            var bsOpen = sp.GetRequiredService<IBalanceSheetService>().BuildAsync(DateOnly.FromDateTime(DateTime.Today)).GetAwaiter().GetResult();
            Check(bsOpen.Current.DifferencePaise == 0 && bsOpen.Checks.All(c => c.Ok), "the balance sheet still balances and its record checks agree after opening balances");
            current.SignOut();
            auth.LoginAsync("cashier1", "Cashier#12345").GetAwaiter().GetResult();
            openVm.LoadAsync().GetAwaiter().GetResult();
            Check(ui.Errors.Count == 0 && openVm.Status.Contains("Only the Admin"), "a cashier cannot enter opening balances");
            current.SignOut();
            auth.LoginAsync("owner", "Admin#12345").GetAwaiter().GetResult();

            // ---------- GST screen ----------
            var gstVm = new GstViewModel(sp.GetRequiredService<IGstReportService>(), ui);
            gstVm.Year = DateTime.Today.Year; gstVm.Month = gstVm.Months[DateTime.Today.Month - 1];
            gstVm.PrepareCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(gstVm.Report != null && gstVm.Summary.Count > 8 && gstVm.Warnings.Count > 0, "preparing the GST worksheets fills the summary and the checks list");
            var salesTb = reports.TrialBalanceAsync(DateOnly.FromDateTime(DateTime.Today)).GetAwaiter().GetResult().Rows.Where(x => x.Code == AccountCodes.Sales).Sum(x => x.CreditPaise - x.DebitPaise);
            Check(gstVm.Report!.Gstr3b.OutTaxablePaise + gstVm.Report.Gstr3b.NilRatedTaxablePaise == salesTb, "the GST outward taxable value equals the Sales account in the books");
            Check(gstVm.Report.B2B.Count >= 0 && gstVm.Report.Hsn.Count > 0 && gstVm.Report.Documents.Count > 0, "the GSTR-1 tables are filled");
            var xlsx = Path.Combine(Path.GetTempPath(), $"selftest-gst-{Guid.NewGuid():N}.xlsx");
            ui.SavePath = null;
            gstVm.ExportCommand.Execute(null);
            Check(!File.Exists(xlsx), "cancelling the save dialog saves nothing");
            ui.SavePath = xlsx;
            gstVm.ExportCommand.Execute(null);
            Check(File.Exists(xlsx) && new FileInfo(xlsx).Length > 3000 && gstVm.Status.Contains("Saved"), "saving writes the Excel workbook");
            try { File.Delete(xlsx); } catch (IOException) { }
            Check(OtherDocuments.BuildGstSummary(gstVm.Report).Blocks.Count > 5, "the printable GST summary is built");
            current.SignOut();
            auth.LoginAsync("cashier1", "Cashier#12345").GetAwaiter().GetResult();
            gstVm.PrepareCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(ui.Errors.Count == 0 && gstVm.Status.Contains("Only the Admin"), "a cashier cannot prepare GST worksheets");
            current.SignOut();
            auth.LoginAsync("owner", "Admin#12345").GetAwaiter().GetResult();

            var tb = reports.TrialBalanceAsync(DateOnly.FromDateTime(DateTime.Today)).GetAwaiter().GetResult();
            Check(tb.IsBalanced, "trial balance is balanced after all of the above");
            Check(sp.GetRequiredService<IAuditService>().VerifyChainAsync().GetAwaiter().GetResult().Valid, "activity log chain is intact");
            Check(ui.Errors.Count == 0, "no unexpected errors were raised (" + string.Join("; ", ui.Errors) + ")");
        }
        catch (Exception ex)
        {
            log.AppendLine("FAIL  unexpected exception: " + ex);
            failed++;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }

        log.AppendLine(failed == 0 ? "ALL SELF-TESTS PASSED" : $"{failed} SELF-TEST(S) FAILED");
        File.WriteAllText(resultFile, log.ToString());
        return failed;
    }
}
