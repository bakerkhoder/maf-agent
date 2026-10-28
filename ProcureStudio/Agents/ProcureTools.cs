using System.ComponentModel;
using System.Security.Claims;
using ProcureStudio.Domain;

namespace ProcureStudio.Agents;

/// <summary>
/// The triage agent's tools: plain C# methods that AIFunctionFactory turns into JSON-schema'd functions.
///
/// The model sees ONLY the method name, the [Description] texts and the parameter schema, so good descriptions
/// are part of the "prompt". Tools return small anonymous objects; errors are returned as { error, message }
/// so the model can explain them, instead of throwing (a thrown exception would abort the whole run).
///
/// Validation and authorization are NOT here: they live in ToolGate (middleware) so that tool bodies stay
/// pure business logic and the rules are enforced uniformly in one place.
/// </summary>
public sealed class ProcureTools(PoRepository repo, IHttpContextAccessor http)
{
    // The signed-in user's role, read from the current HTTP request. Tools are singletons, so we never store it in a field.
    private string? Role => http.HttpContext?.User.FindFirst(ClaimTypes.Role)?.Value;

    [Description("Gets a purchase order (department, vendor, total, status) by PO number, format PO-123456.")]
    public object GetPurchaseOrder([Description("PO number, for example PO-100001")] string poNumber) =>
        repo.Find(poNumber) is { } po ? po : Error("PO_NOT_FOUND", $"Purchase order {poNumber} does not exist.");

    [Description("Checks an amount against the department budget cap of the PO, and says whether the current user may route it.")]
    public object ValidateBudgetCap(string poNumber, [Description("The PO total to validate")] decimal amount)
    {
        if (repo.Find(poNumber) is not { } po) return Error("PO_NOT_FOUND", $"Purchase order {poNumber} does not exist.");
        var cap = repo.Cap(po.Department);
        return new
        {
            poNumber, amount, departmentCap = cap,
            withinCap = amount <= cap,
            // Tell the model EARLY whether routing is allowed for this user, so it can say so before asking a human to approve.
            routableByCurrentUser = Authz.CanRoute(Role, amount),
        };
    }

    [Description("Routes a PO to the right approver. Call ONLY when the user explicitly asked to approve or route it, and the budget check passed. A human must confirm.")]
    public object RouteForApproval(string poNumber, [Description("The PO total, must equal the PO's total")] decimal amount)
    {
        if (repo.Find(poNumber) is not { } po) return Error("PO_NOT_FOUND", $"Purchase order {poNumber} does not exist.");
        if (po.Status != "Draft") return Error("PO_NOT_ROUTABLE", $"{poNumber} is already {po.Status}.");
        if (amount != po.Total) return Error("AMOUNT_MISMATCH", $"Amount must equal the PO total {po.Total:N0}.");
        if (amount > repo.Cap(po.Department)) return Error("OVER_BUDGET", "The PO exceeds its department cap.");

        var approver = amount switch { <= 10_000 => "Team Lead", <= 50_000 => "Department Head", _ => "CFO" };
        repo.SetStatus(poNumber, $"Pending ({approver})");
        return new { poNumber, status = "Pending", approver };
    }

    private static object Error(string code, string message) => new { error = code, message };
}
