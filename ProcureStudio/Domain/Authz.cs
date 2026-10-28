namespace ProcureStudio.Domain;

/// <summary>
/// ONE source of truth for "who may route what". Both the tool (to tell the model early)
/// and the ToolGate (to enforce) call this, so the rule can never drift between them.
/// </summary>
public static class Authz
{
    public const decimal ManagerThreshold = 50_000m;

    public static bool CanRoute(string? role, decimal amount) =>
        (role is "buyer" or "manager") && (amount <= ManagerThreshold || role == "manager");
}
