using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BillingSoftware.Tests;

public class SecurityAndStartupTests
{
    [Fact]
    public async Task Database_file_is_encrypted_and_unreadable_without_the_key()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var header = new byte[16];
        await using (var fs = new FileStream(app.Paths.DbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            _ = await fs.ReadAsync(header);
        Assert.NotEqual("SQLite format 3\0", System.Text.Encoding.ASCII.GetString(header));

        SqliteConnection.ClearAllPools();
        using var plain = new SqliteConnection($"Data Source={app.Paths.DbPath};Pooling=False");
        plain.Open();
        using var cmd = plain.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master";
        Assert.Throws<SqliteException>(() => cmd.ExecuteScalar());
    }

    [Fact]
    public async Task First_run_creates_admin_once()
    {
        using var app = TestApp.Create();
        var auth = app.Get<IAuthService>();
        Assert.True(await auth.IsFirstRunAsync());
        await auth.CreateInitialAdminAsync("owner", "Owner", TestApp.AdminPassword);
        Assert.False(await auth.IsFirstRunAsync());
        await Assert.ThrowsAsync<BusinessRuleException>(() => auth.CreateInitialAdminAsync("other", "Other", TestApp.AdminPassword));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("")]
    public async Task Weak_passwords_are_rejected(string password)
    {
        using var app = TestApp.Create();
        await Assert.ThrowsAsync<ValidationException>(() => app.Get<IAuthService>().CreateInitialAdminAsync("owner", "Owner", password));
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_even_for_the_right_password()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        app.Get<ICurrentUser>().SignOut();
        var auth = app.Get<IAuthService>();
        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<ValidationException>(() => auth.LoginAsync("owner", "wrong-password"));
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => auth.LoginAsync("owner", TestApp.AdminPassword));
        Assert.Contains("locked", ex.Message);
    }

    [Fact]
    public async Task Login_is_case_insensitive_on_username_and_does_not_reveal_which_part_was_wrong()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        app.Get<ICurrentUser>().SignOut();
        var auth = app.Get<IAuthService>();
        var ok = await auth.LoginAsync("OWNER", TestApp.AdminPassword);
        Assert.Equal(UserRole.Admin, ok.Role);
        app.Get<ICurrentUser>().SignOut();
        var a = await Assert.ThrowsAsync<ValidationException>(() => auth.LoginAsync("nobody", "x"));
        var b = await Assert.ThrowsAsync<ValidationException>(() => auth.LoginAsync("owner", "x"));
        Assert.Equal(a.Message, b.Message);
    }

    [Fact]
    public async Task Services_refuse_to_work_when_nobody_is_signed_in()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        app.Get<ICurrentUser>().SignOut();
        await Assert.ThrowsAsync<PermissionDeniedException>(() => app.Get<ICatalogService>().SearchAsync("soap"));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(pid, 1000, 11800)));
    }

    [Fact]
    public async Task Cashier_cannot_see_cost_edit_items_run_reports_or_manage_users()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        await app.AddCashierAsync();
        await app.SignInAsCashierAsync();

        var p = await app.Get<ICatalogService>().GetAsync(pid);
        Assert.Equal(0, p!.AvgCostPaise);
        Assert.Equal(11800, p.SellingPricePaise);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => app.Get<ICatalogService>().SaveAsync(p with { SellingPricePaise = 1 }));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => app.Get<IReportService>().TrialBalanceAsync(DateOnly.FromDateTime(DateTime.Today)));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => app.Get<IReportService>().DailySummaryAsync(DateOnly.FromDateTime(DateTime.Today)));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => app.Get<IAuthService>().ListUsersAsync());
        await Assert.ThrowsAsync<PermissionDeniedException>(() => app.Get<IStockService>().AdjustAsync(pid, -1000, "damaged"));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => app.Get<ISettingsService>().SetAsync(SettingKeys.CashierMaxDiscountBp, "5000"));
    }

    [Fact]
    public async Task Wrong_admin_password_at_the_approval_prompt_is_refused_and_throttled()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        await app.AddCashierAsync();
        await app.SignInAsCashierAsync();
        var auth = app.Get<IAuthService>();
        for (var i = 0; i < 5; i++) await Assert.ThrowsAsync<ValidationException>(() => auth.VerifyAdminApprovalAsync("nope"));
        await Assert.ThrowsAsync<BusinessRuleException>(() => auth.VerifyAdminApprovalAsync(TestApp.AdminPassword)); // throttled
    }

    [Fact]
    public async Task Last_admin_cannot_be_deactivated_and_nobody_can_deactivate_themselves()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var users = await app.Get<IAuthService>().ListUsersAsync();
        await Assert.ThrowsAsync<BusinessRuleException>(() => app.Get<IAuthService>().SetActiveAsync(users[0].Id, false));
    }

    [Fact]
    public async Task Audit_chain_is_valid_and_detects_tampering()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(pid, 1000, 11800));
        var audit = app.Get<IAuditService>();
        Assert.True((await audit.VerifyChainAsync()).Valid);

        SqliteConnection.ClearAllPools();
        var factory = app.Get<DbFactory>();
        using (var raw = factory.OpenRaw(app.Paths.DbPath))
        using (var cmd = raw.CreateCommand())
        {
            cmd.CommandText = "UPDATE AuditEvents SET Details = 'edited' WHERE Id = 3";
            cmd.ExecuteNonQuery();
        }
        var (valid, brokenAt) = await audit.VerifyChainAsync();
        Assert.False(valid);
        Assert.Equal(3, brokenAt);
    }
}

public class SalesScenarioTests
{
    [Fact]
    public async Task Cash_sale_numbers_the_invoice_moves_stock_and_posts_a_balanced_journal()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var r = await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(pid, 2000, 23600));

        var fy = FinancialYears.For(DateTime.Now).Label;
        Assert.Equal($"INV/{fy}/000001", r.InvoiceNumber);
        Assert.Equal(16, r.InvoiceNumber.Length);                     // GST invoice number length limit
        Assert.Equal(23600, r.TotalPaise);
        Assert.Equal(8000, (await app.Get<ICatalogService>().GetAsync(pid))!.QtyOnHandMilli);

        var tb = await app.Get<IReportService>().TrialBalanceAsync(DateOnly.FromDateTime(DateTime.Today));
        Assert.True(tb.IsBalanced);
        long Net(string code) => tb.Rows.Where(x => x.Code == code).Sum(x => x.DebitPaise - x.CreditPaise);
        Assert.Equal(23600, Net(AccountCodes.Cash));
        Assert.Equal(-20000, Net(AccountCodes.Sales));
        Assert.Equal(-1800, Net(AccountCodes.CgstPayable));
        Assert.Equal(-1800, Net(AccountCodes.SgstPayable));
        Assert.Equal(12000, Net(AccountCodes.Cogs));                   // 2 x 60.00
        Assert.Equal(60000 - 12000, Net(AccountCodes.Inventory));      // 10 x 60.00 less cost of the 2 sold
    }

    [Fact]
    public async Task Invoice_numbers_are_gapless_across_many_sales()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var sales = app.Get<ISalesService>();
        var numbers = new List<string>();
        for (var i = 0; i < 25; i++) numbers.Add((await sales.CompleteSaleAsync(TestApp.Sale(pid, 100, 1200))).InvoiceNumber);   // 11.80 rounds to 12.00
        for (var i = 0; i < 25; i++) Assert.EndsWith($"/{i + 1:D6}", numbers[i]);
        Assert.Equal(25, numbers.Distinct().Count());
    }

    [Fact]
    public async Task Retrying_the_same_command_never_creates_a_duplicate_bill()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var sales = app.Get<ISalesService>();
        var cmd = TestApp.Sale(pid, 1000, 11800);
        var first = await sales.CompleteSaleAsync(cmd);
        var second = await sales.CompleteSaleAsync(cmd);
        Assert.False(first.AlreadyExisted);
        Assert.True(second.AlreadyExisted);
        Assert.Equal(first.InvoiceNumber, second.InvoiceNumber);
        Assert.Equal(9000, (await app.Get<ICatalogService>().GetAsync(pid))!.QtyOnHandMilli);   // stock reduced once
        Assert.Single(await sales.ListAsync(DateOnly.FromDateTime(DateTime.Today)));
    }

    [Fact]
    public async Task Payment_must_match_the_bill_total_and_nothing_is_saved_on_failure()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var sales = app.Get<ISalesService>();
        await Assert.ThrowsAsync<ValidationException>(() => sales.CompleteSaleAsync(TestApp.Sale(pid, 1000, 11000)));
        await Assert.ThrowsAsync<ValidationException>(() => sales.CompleteSaleAsync(TestApp.Sale(pid, 1000, 12000)));
        Assert.Equal(10000, (await app.Get<ICatalogService>().GetAsync(pid))!.QtyOnHandMilli);
        Assert.Empty(await sales.ListAsync(DateOnly.FromDateTime(DateTime.Today)));
        // the failed attempts must not burn an invoice number
        var ok = await sales.CompleteSaleAsync(TestApp.Sale(pid, 1000, 11800));
        Assert.EndsWith("/000001", ok.InvoiceNumber);
    }

    [Fact]
    public async Task Mixed_payments_split_across_cash_upi_and_card()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var cmd = new CompleteSaleCommand(Guid.NewGuid(), new[] { new SaleLineInput(pid, 1000) },
            new[] { new PaymentInput(PaymentMode.Cash, 5000), new PaymentInput(PaymentMode.Upi, 4000), new PaymentInput(PaymentMode.Card, 2800) });
        var r = await app.Get<ISalesService>().CompleteSaleAsync(cmd);
        var d = await app.Get<ISalesService>().GetAsync(r.SaleId);
        Assert.Equal(3, d!.Payments.Count);
        var summary = await app.Get<IReportService>().DailySummaryAsync(DateOnly.FromDateTime(DateTime.Today));
        Assert.Equal(11800, summary.ByPaymentMode.Sum(x => x.AmountPaise));
        Assert.True((await app.Get<IReportService>().TrialBalanceAsync(DateOnly.FromDateTime(DateTime.Today))).IsBalanced);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1000L)]
    public async Task Zero_or_negative_quantity_is_rejected(long qty)
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        await Assert.ThrowsAsync<ValidationException>(() => app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(pid, qty, 100)));
    }

    [Fact]
    public async Task Empty_bill_and_unknown_or_inactive_items_are_rejected()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var sales = app.Get<ISalesService>();
        await Assert.ThrowsAsync<ValidationException>(() => sales.CompleteSaleAsync(
            new CompleteSaleCommand(Guid.NewGuid(), Array.Empty<SaleLineInput>(), Array.Empty<PaymentInput>())));
        await Assert.ThrowsAsync<ValidationException>(() => sales.CompleteSaleAsync(TestApp.Sale(9999, 1000, 100)));
        var p = (await app.Get<ICatalogService>().GetAsync(pid))!;
        await app.Get<ICatalogService>().SaveAsync(p with { IsActive = false });
        await Assert.ThrowsAsync<ValidationException>(() => sales.CompleteSaleAsync(TestApp.Sale(pid, 1000, 11800)));
    }

    [Fact]
    public async Task Inter_state_sale_uses_igst()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var r = await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(pid, 1000, 11800, buyerState: "29"));
        var d = await app.Get<ISalesService>().GetAsync(r.SaleId);
        Assert.True(d!.IsInterState);
        Assert.Equal(1800, d.IgstPaise);
        Assert.Equal(0, d.CgstPaise + d.SgstPaise);
    }

    [Fact]
    public async Task Composition_dealer_issues_bill_of_supply_without_tax()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync(composition: true);
        var r = await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(pid, 1000, 11800));
        Assert.StartsWith("BOS/", r.InvoiceNumber);
        var d = await app.Get<ISalesService>().GetAsync(r.SaleId);
        Assert.Equal(0, d!.CgstPaise + d.SgstPaise + d.IgstPaise);
        Assert.Equal(11800, d.TaxablePaise);
    }

    [Fact]
    public async Task Invoice_total_is_rounded_to_the_rupee_with_a_round_off_entry()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync(priceInclusivePaise: 10049);   // 100.49
        var r = await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(pid, 1000, 10000));
        Assert.Equal(10000, r.TotalPaise);
        Assert.Equal(-49, r.RoundOffPaise);
        Assert.True((await app.Get<IReportService>().TrialBalanceAsync(DateOnly.FromDateTime(DateTime.Today))).IsBalanced);
    }

    [Fact]
    public async Task Cashier_discount_needs_admin_approval_then_succeeds_and_is_recorded()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var adminId = (await app.Get<IAuthService>().ListUsersAsync()).First(u => u.Role == UserRole.Admin).Id;
        await app.AddCashierAsync();
        await app.SignInAsCashierAsync();
        var sales = app.Get<ISalesService>();

        var cmd = TestApp.Sale(pid, 1000, 10000, discount: 1800);
        var ex = await Assert.ThrowsAsync<ApprovalRequiredException>(() => sales.CompleteSaleAsync(cmd));
        Assert.Equal(ApprovalReason.DiscountAboveLimit, ex.Reason);

        // claiming an approval without actually entering the Admin password is refused
        await Assert.ThrowsAsync<ApprovalRequiredException>(() => sales.CompleteSaleAsync(cmd with { ApprovedByUserId = adminId }));

        var approver = await app.Get<IAuthService>().VerifyAdminApprovalAsync(TestApp.AdminPassword);
        var r = await sales.CompleteSaleAsync(cmd with { ApprovedByUserId = approver, ApprovalNote = "Festival offer" });
        Assert.Equal(10000, r.TotalPaise);
    }

    [Fact]
    public async Task Price_override_needs_admin_approval_for_a_cashier()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        await app.AddCashierAsync();
        await app.SignInAsCashierAsync();
        var cmd = new CompleteSaleCommand(Guid.NewGuid(), new[] { new SaleLineInput(pid, 1000, 0, 5000) },
            new[] { new PaymentInput(PaymentMode.Cash, 5000) });
        var ex = await Assert.ThrowsAsync<ApprovalRequiredException>(() => app.Get<ISalesService>().CompleteSaleAsync(cmd));
        Assert.Equal(ApprovalReason.PriceOverride, ex.Reason);
    }

    [Fact]
    public async Task Selling_more_than_stock_needs_approval_for_cashier_and_is_allowed_for_admin()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        await app.AddCashierAsync();
        await app.SignInAsCashierAsync();
        var sales = app.Get<ISalesService>();
        var cmd = TestApp.Sale(pid, 12_000, 141_600);   // 12 units, only 10 in stock
        var ex = await Assert.ThrowsAsync<ApprovalRequiredException>(() => sales.CompleteSaleAsync(cmd));
        Assert.Equal(ApprovalReason.NegativeStock, ex.Reason);

        await app.SignInAsAdminAsync();
        await sales.CompleteSaleAsync(cmd with { CommandId = Guid.NewGuid() });
        Assert.Equal(-2000, (await app.Get<ICatalogService>().GetAsync(pid))!.QtyOnHandMilli);
        Assert.True((await app.Get<IReportService>().TrialBalanceAsync(DateOnly.FromDateTime(DateTime.Today))).IsBalanced);
    }

    [Fact]
    public async Task Receiving_stock_after_negative_sale_posts_cost_variance_and_keeps_books_balanced()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();                       // 10 @ 60.00
        await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(pid, 12_000, 141_600));   // qty -2 at cost 60
        await app.Get<IStockService>().ReceiveOpeningStockAsync(pid, 5_000, 7000);              // 5 @ 70.00
        var p = await app.Get<ICatalogService>().GetAsync(pid);
        Assert.Equal(3000, p!.QtyOnHandMilli);
        Assert.Equal(7000, p.AvgCostPaise);
        var tb = await app.Get<IReportService>().TrialBalanceAsync(DateOnly.FromDateTime(DateTime.Today));
        Assert.True(tb.IsBalanced);
        Assert.Contains(tb.Rows, r => r.Code == AccountCodes.InventoryCostVariance && r.DebitPaise == 2000);
    }

    [Fact]
    public async Task Admin_can_void_a_bill_stock_returns_and_books_net_to_zero()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var sales = app.Get<ISalesService>();
        var r = await sales.CompleteSaleAsync(TestApp.Sale(pid, 3000, 35400));
        await sales.VoidSaleAsync(r.SaleId, "Customer cancelled", null);

        Assert.Equal(10000, (await app.Get<ICatalogService>().GetAsync(pid))!.QtyOnHandMilli);
        Assert.Equal(SaleStatus.Voided, (await sales.GetAsync(r.SaleId))!.Status);
        var tb = await app.Get<IReportService>().TrialBalanceAsync(DateOnly.FromDateTime(DateTime.Today));
        Assert.True(tb.IsBalanced);
        Assert.DoesNotContain(tb.Rows, x => x.Code == AccountCodes.Sales && (x.DebitPaise != 0 || x.CreditPaise != 0));
        Assert.DoesNotContain(tb.Rows, x => x.Code == AccountCodes.Cash && (x.DebitPaise != 0 || x.CreditPaise != 0));
        var summary = await app.Get<IReportService>().DailySummaryAsync(DateOnly.FromDateTime(DateTime.Today));
        Assert.Equal(0, summary.BillCount);
        Assert.Equal(1, summary.VoidedCount);
        await Assert.ThrowsAsync<ValidationException>(() => sales.VoidSaleAsync(r.SaleId, "again", null));
    }

    [Fact]
    public async Task Cashier_cannot_void_without_admin_approval()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        await app.AddCashierAsync();
        await app.SignInAsCashierAsync();
        var sales = app.Get<ISalesService>();
        var r = await sales.CompleteSaleAsync(TestApp.Sale(pid, 1000, 11800));
        var ex = await Assert.ThrowsAsync<ApprovalRequiredException>(() => sales.VoidSaleAsync(r.SaleId, "mistake", null));
        Assert.Equal(ApprovalReason.VoidSale, ex.Reason);
        var approver = await app.Get<IAuthService>().VerifyAdminApprovalAsync(TestApp.AdminPassword);
        await sales.VoidSaleAsync(r.SaleId, "mistake", approver);
        Assert.Equal(SaleStatus.Voided, (await sales.GetAsync(r.SaleId))!.Status);
    }

    [Fact]
    public async Task Reprint_is_counted_and_a_cashier_only_lists_their_own_bills()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(pid, 1000, 11800));           // by Admin
        await app.AddCashierAsync();
        await app.SignInAsCashierAsync();
        var mine = await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(pid, 1000, 11800));
        var today = DateOnly.FromDateTime(DateTime.Today);
        var list = await app.Get<ISalesService>().ListAsync(today);
        Assert.Single(list);
        var d = await app.Get<ISalesService>().GetForReprintAsync(mine.SaleId);
        Assert.Equal(1, d!.ReprintCount);
        await app.SignInAsAdminAsync();
        Assert.Equal(2, (await app.Get<ISalesService>().ListAsync(today)).Count);
    }

    [Fact]
    public async Task Credit_sale_needs_a_customer_tracks_outstanding_and_accepts_part_payments()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var sales = app.Get<ISalesService>();
        var parties = app.Get<IPartyService>();
        await Assert.ThrowsAsync<ValidationException>(() => sales.CompleteSaleAsync(TestApp.Sale(pid, 1000, 11800, PaymentMode.Credit)));

        var party = await parties.SaveAsync(new PartyDto(0, "Ravi Traders", "9000000001", null, "33", 0));
        await sales.CompleteSaleAsync(TestApp.Sale(pid, 1000, 11800, PaymentMode.Credit, partyId: party.Id));
        Assert.Equal(11800, (await parties.SearchAsync("Ravi"))[0].OutstandingPaise);

        await parties.ReceivePaymentAsync(party.Id, 5000, PaymentMode.Cash);
        Assert.Equal(6800, (await parties.SearchAsync("Ravi"))[0].OutstandingPaise);
        await Assert.ThrowsAsync<ValidationException>(() => parties.ReceivePaymentAsync(party.Id, 6801, PaymentMode.Cash));
        await Assert.ThrowsAsync<ValidationException>(() => parties.ReceivePaymentAsync(party.Id, 100, PaymentMode.Credit));
        await parties.ReceivePaymentAsync(party.Id, 6800, PaymentMode.Upi);
        Assert.Equal(0, (await parties.SearchAsync("Ravi"))[0].OutstandingPaise);
        Assert.True((await app.Get<IReportService>().TrialBalanceAsync(DateOnly.FromDateTime(DateTime.Today))).IsBalanced);
    }

    [Fact]
    public async Task Locked_period_blocks_posting()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        await app.Get<ISettingsService>().SetAsync(SettingKeys.LockedThrough, DateTime.Today.ToString("yyyy-MM-dd"));
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(pid, 1000, 11800)));
        Assert.Contains("locked", ex.Message);
    }

    [Fact]
    public async Task Stock_adjustment_for_loss_posts_inventory_loss_and_cannot_exceed_stock()
    {
        using var app = TestApp.Create();
        var pid = await app.SeedAsync();
        var stock = app.Get<IStockService>();
        await stock.AdjustAsync(pid, -1000, "Damaged in store");
        Assert.Equal(9000, (await app.Get<ICatalogService>().GetAsync(pid))!.QtyOnHandMilli);
        await Assert.ThrowsAsync<ValidationException>(() => stock.AdjustAsync(pid, -50_000, "too many"));
        await Assert.ThrowsAsync<ValidationException>(() => stock.AdjustAsync(pid, -1000, ""));
        var tb = await app.Get<IReportService>().TrialBalanceAsync(DateOnly.FromDateTime(DateTime.Today));
        Assert.True(tb.IsBalanced);
        Assert.Contains(tb.Rows, r => r.Code == AccountCodes.InventoryLoss && r.DebitPaise == 6000);
    }

    [Fact]
    public async Task Product_rules_duplicate_sku_and_barcode_and_invalid_values()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var cat = app.Get<ICatalogService>();
        await Assert.ThrowsAsync<ValidationException>(() => cat.SaveAsync(new ProductDto(0, "SKU1", null, "Dup sku", null, 500, "pcs", 100, null, true, 0, 0, true)));
        await Assert.ThrowsAsync<ValidationException>(() => cat.SaveAsync(new ProductDto(0, "SKU2", "8901234567890", "Dup barcode", null, 500, "pcs", 100, null, true, 0, 0, true)));
        await Assert.ThrowsAsync<ValidationException>(() => cat.SaveAsync(new ProductDto(0, "SKU3", null, "Bad rate", null, 9000, "pcs", 100, null, true, 0, 0, true)));
        await Assert.ThrowsAsync<ValidationException>(() => cat.SaveAsync(new ProductDto(0, "SKU4", null, "Neg price", null, 500, "pcs", -1, null, true, 0, 0, true)));
        await Assert.ThrowsAsync<ValidationException>(() => cat.SaveAsync(new ProductDto(0, "SKU5", null, " ", null, 500, "pcs", 100, null, true, 0, 0, true)));
        Assert.NotNull(await cat.GetByBarcodeAsync("8901234567890"));
        Assert.Single(await cat.SearchAsync("soap"));
        Assert.Empty(await cat.SearchAsync("nothing-like-this"));
    }

    [Fact]
    public async Task Search_handles_special_characters_safely()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var cat = app.Get<ICatalogService>();
        Assert.Empty(await cat.SearchAsync("'; DROP TABLE Products; --"));
        Assert.Empty(await cat.SearchAsync("%"));
        Assert.Single(await cat.SearchAsync(""));   // empty query lists items
    }

    [Fact]
    public async Task Company_setup_validates_gstin_and_state()
    {
        using var app = TestApp.Create();
        var s = app.Get<ISettingsService>();
        await Assert.ThrowsAsync<ValidationException>(() => s.SaveCompanyAsync(new CompanyDto("X", "BADGSTIN", "33", false, null, null)));
        await Assert.ThrowsAsync<ValidationException>(() => s.SaveCompanyAsync(new CompanyDto("X", "29ABCDE1234F1Z5", "33", false, null, null)));
        await Assert.ThrowsAsync<ValidationException>(() => s.SaveCompanyAsync(new CompanyDto("X", null, "3", false, null, null)));
        await Assert.ThrowsAsync<ValidationException>(() => s.SaveCompanyAsync(new CompanyDto("", null, "33", false, null, null)));
        await s.SaveCompanyAsync(new CompanyDto("X", "33ABCDE1234F1Z5", "33", false, null, null));
        Assert.NotNull(await s.GetCompanyAsync());
    }

    [Fact]
    public async Task Sale_fails_cleanly_when_company_setup_is_missing()
    {
        using var app = TestApp.Create();
        await app.Get<IAuthService>().CreateInitialAdminAsync("owner", "Owner", TestApp.AdminPassword);
        var p = await app.Get<ICatalogService>().SaveAsync(new ProductDto(0, "S", null, "Thing", null, 0, "pcs", 100, null, true, 0, 0, true));
        await Assert.ThrowsAsync<BusinessRuleException>(() => app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(p.Id, 1000, 100)));
    }
}

public class BackupAndRestoreTests
{
    [Fact]
    public async Task Backup_is_verified_and_the_database_can_be_restored_with_the_recovery_key()
    {
        string backupFolder = Path.Combine(Path.GetTempPath(), "billing-backup-" + Guid.NewGuid().ToString("N"));
        string restoreDir = Path.Combine(Path.GetTempPath(), "billing-restore-" + Guid.NewGuid().ToString("N"));
        try
        {
            string recoveryKey;
            string backupFile;
            using (var app = TestApp.Create())
            {
                recoveryKey = app.Startup.RecoveryKey!;
                Assert.False(string.IsNullOrWhiteSpace(recoveryKey));
                var pid = await app.SeedAsync();
                await app.Get<ISalesService>().CompleteSaleAsync(TestApp.Sale(pid, 1000, 11800));

                var status = await app.Get<IBackupService>().GetStatusAsync();
                Assert.True(status.Overdue);                       // never backed up

                var result = await app.Get<IBackupService>().BackupNowAsync(backupFolder);
                Assert.True(result.Success, result.Message);
                backupFile = result.Path!;
                Assert.True(File.Exists(backupFile + ".manifest.json"));
                Assert.Contains(result.Checksum!, await File.ReadAllTextAsync(backupFile + ".manifest.json"));

                await app.Get<ISettingsService>().SetAsync(SettingKeys.BackupFolder, backupFolder);
                var after = await app.Get<IBackupService>().BackupNowAsync();
                Assert.True(after.Success);
                Assert.False((await app.Get<IBackupService>().GetStatusAsync()).Overdue);
            }

            // Restore drill on a "new PC": no key file, only the backup file and the recovery key.
            Directory.CreateDirectory(restoreDir);
            File.Copy(backupFile, Path.Combine(restoreDir, "billing.db"));
            using var restored = TestApp.Create(restoreDir, recoveryKey);
            var auth = restored.Get<IAuthService>();
            Assert.False(await auth.IsFirstRunAsync());
            await auth.LoginAsync("owner", TestApp.AdminPassword);
            var sales = await restored.Get<ISalesService>().ListAsync(DateOnly.FromDateTime(DateTime.Today));
            Assert.Single(sales);
            Assert.True((await restored.Get<IReportService>().TrialBalanceAsync(DateOnly.FromDateTime(DateTime.Today))).IsBalanced);
            Assert.True((await restored.Get<IAuditService>().VerifyChainAsync()).Valid);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var d in new[] { backupFolder, restoreDir }) try { Directory.Delete(d, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Missing_key_file_with_an_existing_database_asks_for_the_recovery_key()
    {
        using var app = TestApp.Create();
        var dir = app.Dir;
        var paths = new AppPaths(dir);
        File.Delete(paths.KeyPath);
        var sc = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var result = BillingSoftware.Infrastructure.InfrastructureBootstrap.Start(paths, sc);
        Assert.False(result.Ok);
        Assert.True(result.NeedsRecoveryKey);

        var bad = BillingSoftware.Infrastructure.InfrastructureBootstrap.Start(paths, new Microsoft.Extensions.DependencyInjection.ServiceCollection(), "not-a-valid-key");
        Assert.False(bad.Ok);
        Assert.True(bad.NeedsRecoveryKey);
    }

    [Fact]
    public async Task Backup_without_a_folder_reports_a_clear_message()
    {
        using var app = TestApp.Create();
        await app.SeedAsync();
        var r = await app.Get<IBackupService>().BackupNowAsync();
        Assert.False(r.Success);
        Assert.Contains("folder", r.Message, StringComparison.OrdinalIgnoreCase);
    }
}
