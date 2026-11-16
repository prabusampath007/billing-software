using System.Text.Json;
using BillingSoftware.Application;
using BillingSoftware.Domain;
using BillingSoftware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BillingSoftware.Infrastructure.Services;

internal sealed class HoldService(DbFactory factory, ICurrentUser current) : ServiceBase(factory, current), IHoldService
{
    private const int MaxHeld = 30;
    private const int MaxLines = 300;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(3);

    public async Task<long> HoldAsync(HoldBillCommand cmd)
    {
        var user = RequireUser();
        if (cmd.Lines.Count == 0) throw new ValidationException("There is nothing on the bill to hold.");
        if (cmd.Lines.Count > MaxLines) throw new ValidationException($"A bill can have at most {MaxLines} lines.");
        if (cmd.Lines.Any(l => l.QtyMilli <= 0 || l.DiscountPaise < 0)) throw new ValidationException("Every line needs a quantity above zero and no negative discount.");
        var label = (cmd.Label ?? "").Trim();
        if (label.Length > 60) label = label[..60];

        await using var db = Factory.Create();
        var ids = cmd.Lines.Select(l => l.ProductId).Distinct().ToList();
        if (await db.Products.CountAsync(p => ids.Contains(p.Id)) != ids.Count) throw new ValidationException("An item on the bill no longer exists.");
        if (cmd.PartyId is { } pid && !await db.Parties.AnyAsync(p => p.Id == pid)) throw new ValidationException("That customer no longer exists.");
        await PurgeOldAsync(db);
        if (await db.HeldBills.CountAsync() >= MaxHeld)
            throw new BusinessRuleException($"There are already {MaxHeld} held bills. Complete or discard some before holding more.");

        var h = new HeldBill
        {
            CreatedAt = DateTime.Now, UserId = user.Id, Label = label, PartyId = cmd.PartyId,
            LinesJson = JsonSerializer.Serialize(cmd.Lines.Select(l => new HeldLine(l.ProductId, l.QtyMilli, l.DiscountPaise))),
        };
        db.HeldBills.Add(h);
        await db.SaveChangesAsync();
        return h.Id;
    }

    private static async Task PurgeOldAsync(AppDbContext db)
    {
        var cutoff = DateTime.Now - MaxAge;
        var old = await db.HeldBills.Where(h => h.CreatedAt < cutoff).ToListAsync();
        if (old.Count == 0) return;
        db.HeldBills.RemoveRange(old);
        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<HeldBillSummary>> ListAsync()
    {
        RequireUser();
        await using var db = Factory.Create();
        await PurgeOldAsync(db);
        var rows = await (from h in db.HeldBills.AsNoTracking()
                          join u in db.Users on h.UserId equals u.Id
                          orderby h.CreatedAt
                          select new { h, u.DisplayName }).ToListAsync();
        var partyIds = rows.Where(r => r.h.PartyId != null).Select(r => r.h.PartyId!.Value).Distinct().ToList();
        var parties = await db.Parties.AsNoTracking().Where(p => partyIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name);
        return rows.Select(r =>
        {
            var count = JsonSerializer.Deserialize<List<HeldLine>>(r.h.LinesJson)?.Count ?? 0;
            var customer = r.h.PartyId is { } id ? parties.GetValueOrDefault(id) : null;
            var label = string.IsNullOrWhiteSpace(r.h.Label) ? $"Held at {r.h.CreatedAt:HH:mm}" : r.h.Label;
            return new HeldBillSummary(r.h.Id, r.h.CreatedAt, r.DisplayName, label, customer, count);
        }).ToList();
    }

    public async Task<HeldBillDto?> RecallAsync(long id)
    {
        RequireUser();
        await using var db = Factory.Create();
        await using var tx = await db.Database.BeginTransactionAsync();
        var h = await db.HeldBills.FirstOrDefaultAsync(x => x.Id == id);
        if (h == null) return null;                                   // someone else already recalled it
        var lines = JsonSerializer.Deserialize<List<HeldLine>>(h.LinesJson) ?? new();
        var customer = h.PartyId is { } pid ? await db.Parties.AsNoTracking().Where(p => p.Id == pid).Select(p => p.Name).FirstOrDefaultAsync() : null;
        var label = string.IsNullOrWhiteSpace(h.Label) ? $"Held at {h.CreatedAt:HH:mm}" : h.Label;
        db.HeldBills.Remove(h);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return new HeldBillDto(h.Id, label, h.PartyId, customer, lines);
    }

    public async Task DiscardAsync(long id)
    {
        RequireUser();
        await using var db = Factory.Create();
        var h = await db.HeldBills.FirstOrDefaultAsync(x => x.Id == id);
        if (h == null) return;
        db.HeldBills.Remove(h);
        await db.SaveChangesAsync();
    }
}
