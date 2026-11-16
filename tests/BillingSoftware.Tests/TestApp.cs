using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace BillingSoftware.Tests;

/// <summary>A fully isolated app instance: its own temp folder, encrypted database and services.</summary>
public sealed class TestApp : IDisposable
{
    public const string AdminPassword = "Admin#12345";
    public const string CashierPassword = "Cashier#12345";

    public string Dir { get; }
    public AppPaths Paths { get; }
    public ServiceProvider Services { get; }
    public StartupResult Startup { get; }

    private TestApp(string dir, StartupResult startup, ServiceProvider services, AppPaths paths)
    { Dir = dir; Startup = startup; Services = services; Paths = paths; }

    public static TestApp Create(string? existingDir = null, string? recoveryKey = null)
    {
        var dir = existingDir ?? Path.Combine(Path.GetTempPath(), "billing-tests-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(dir);
        var sc = new ServiceCollection();
        var result = InfrastructureBootstrap.Start(paths, sc, recoveryKey);
        if (!result.Ok) throw new InvalidOperationException(result.Error);
        return new TestApp(dir, result, sc.BuildServiceProvider(), paths);
    }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>Company (Tamil Nadu, regular), Admin account (signed in), one product and 10 units of stock at cost 60.00.</summary>
    public async Task<long> SeedAsync(bool composition = false, long priceInclusivePaise = 11800)
    {
        await Get<ISettingsService>().SaveCompanyAsync(new CompanyDto("Test Stores", "33ABCDE1234F1Z5", "33", composition, "Main Road", "9000000000"));
        await Get<IAuthService>().CreateInitialAdminAsync("owner", "Owner", AdminPassword);
        var p = await Get<ICatalogService>().SaveAsync(new ProductDto(0, "SKU1", "8901234567890", "Test Soap", "3401", 1800, "pcs",
            priceInclusivePaise, priceInclusivePaise, true, 0, 0, true));
        await Get<IStockService>().ReceiveOpeningStockAsync(p.Id, 10_000, 6000);
        return p.Id;
    }

    public async Task<long> AddCashierAsync()
    {
        var u = await Get<IAuthService>().CreateCashierAsync("cashier1", "Cashier One", CashierPassword);
        return u.Id;
    }

    public async Task SignInAsAdminAsync() { Get<ICurrentUser>().SignOut(); await Get<IAuthService>().LoginAsync("owner", AdminPassword); }
    public async Task SignInAsCashierAsync() { Get<ICurrentUser>().SignOut(); await Get<IAuthService>().LoginAsync("cashier1", CashierPassword); }

    public static CompleteSaleCommand Sale(long productId, long qtyMilli, long totalPaise, PaymentMode mode = PaymentMode.Cash,
        long discount = 0, Guid? id = null, long? partyId = null, string? buyerState = null, long? approvedBy = null) =>
        new(id ?? Guid.NewGuid(), new[] { new SaleLineInput(productId, qtyMilli, discount) },
            new[] { new PaymentInput(mode, totalPaise) }, partyId, null, null, buyerState, approvedBy);

    public void Dispose()
    {
        Services.Dispose();
        try { Directory.Delete(Dir, true); } catch (IOException) { /* best effort */ }
    }
}
