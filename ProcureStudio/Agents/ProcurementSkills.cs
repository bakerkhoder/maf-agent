using Microsoft.Agents.AI;

namespace ProcureStudio.Agents;

/// <summary>
/// Code-defined skills complement the editable file-based PO review skill.
/// Use inline skills for small, fixed guidance and class-based skills when a skill
/// benefits from strongly typed resources or reusable C# behavior.
/// </summary>
public static class ProcurementSkills
{
    public static AgentInlineSkill CreateInline()
    {
        var skill = new AgentInlineSkill(
            new AgentSkillFrontmatter(
                "approval-thresholds",
                "Explain the procurement approval tiers and when quotes are required."),
            """
            Use this skill to explain which approval tier applies to a purchase amount.
            Do not authorize or route an order; routing permission is enforced by application code.
            """);

        return skill.AddResource(
            "approval-tiers",
            """
            Up to 10,000: Team Lead.
            10,001 through 50,000: Department Head.
            Above 50,000: CFO.
            Above 25,000: three vendor quotes are required.
            """,
            "The procurement approval and quote thresholds.");
    }
}

public sealed class VendorQuoteChecklistSkill : AgentClassSkill<VendorQuoteChecklistSkill>
{
    public override AgentSkillFrontmatter Frontmatter { get; } =
        new("vendor-quote-checklist", "Check the quote evidence needed for a procurement review.");

    protected override string Instructions =>
        "For orders above 25,000, verify that three vendor quotes are recorded. " +
        "Never recommend splitting an order to avoid the threshold.";

    [AgentSkillResource("quote-policy")]
    public string QuotePolicy =>
        "Orders above 25,000 require three vendor quotes. Splitting an order to avoid a threshold is forbidden.";
}
