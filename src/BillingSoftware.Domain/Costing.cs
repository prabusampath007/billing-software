namespace BillingSoftware.Domain;

/// <summary>Perpetual weighted-average costing (decision D6).</summary>
public static class Costing
{
    /// <summary>
    /// Applies a stock receipt to on-hand quantity and average cost.
    /// While stock is zero or negative the average is not blended: once stock turns positive it becomes the receipt cost.
    /// </summary>
    public static (long QtyMilli, long AvgCostPaise) Receive(long qtyMilli, long avgCostPaise, long addMilli, long addCostPaise)
    {
        var newQty = qtyMilli + addMilli;
        if (newQty <= 0) return (newQty, avgCostPaise);
        if (qtyMilli <= 0) return (newQty, addCostPaise);
        var newAvg = Money.RoundHalfUp(((decimal)qtyMilli * avgCostPaise + (decimal)addMilli * addCostPaise) / newQty);
        return (newQty, newAvg);
    }

    /// <summary>Takes stock out at a given cost (for example goods sent back to a supplier) and re-averages what is left.</summary>
    public static (long QtyMilli, long AvgCostPaise) Remove(long qtyMilli, long avgCostPaise, long removeMilli, long removeCostPaise)
    {
        var newQty = qtyMilli - removeMilli;
        if (newQty <= 0) return (newQty, avgCostPaise);
        var value = (decimal)qtyMilli * avgCostPaise - (decimal)removeMilli * removeCostPaise;
        return (newQty, Math.Max(0, Money.RoundHalfUp(value / newQty)));
    }

    public static long Value(long qtyMilli, long unitCostPaise) =>
        Money.RoundHalfUp((decimal)qtyMilli * unitCostPaise / Qty.Unit);

    /// <summary>
    /// When a receipt arrives for stock that was already sold (negative stock, decision D5), the covered quantity was
    /// booked at the last known cost. The difference is the Inventory Cost Variance to post.
    /// </summary>
    public static long NegativeStockVariance(long qtyBeforeMilli, long avgCostPaise, long receivedMilli, long receiptCostPaise)
    {
        if (qtyBeforeMilli >= 0) return 0;
        var covered = Math.Min(receivedMilli, -qtyBeforeMilli);
        return Value(covered, receiptCostPaise - avgCostPaise);
    }
}
