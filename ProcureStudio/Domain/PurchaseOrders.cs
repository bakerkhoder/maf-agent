using System.Collections.Concurrent;

namespace ProcureStudio.Domain;

/// <summary>A purchase order. Immutable record: updating status creates a new instance (see SetStatus).</summary>
public record PurchaseOrder(string PoNumber, string Department, string Vendor, decimal Total, string Status);

/// <summary>
/// In-memory data store (singleton). Stands in for SQL Server in this demo.
/// ConcurrentDictionary because many requests share this one instance.
/// </summary>
public sealed class PoRepository
{
    private readonly ConcurrentDictionary<string, PurchaseOrder> _orders = new()
    {
        ["PO-100001"] = new("PO-100001", "IT",  "Dell",     12_000, "Draft"),
        ["PO-100002"] = new("PO-100002", "Ops", "OfficeCo", 25_000, "Draft"),   // exceeds the Ops cap
        ["PO-100003"] = new("PO-100003", "IT",  "HPE",      85_000, "Draft"),   // needs a manager and the CFO
    };

    private readonly Dictionary<string, decimal> _departmentCaps = new() { ["IT"] = 100_000, ["Ops"] = 20_000 };

    public PurchaseOrder? Find(string poNumber) => _orders.GetValueOrDefault(poNumber);
    public decimal Cap(string department) => _departmentCaps.GetValueOrDefault(department);
    public IReadOnlyList<PurchaseOrder> All() => _orders.Values.OrderBy(o => o.PoNumber).ToList();
    public void SetStatus(string poNumber, string status) => _orders[poNumber] = _orders[poNumber] with { Status = status };
}
