---
name: po-review
description: Step-by-step procedure for reviewing a purchase order (lookup, budget check, policy check, routing). Use when the user asks to review, check, validate or approve a PO.
---

# Purchase order review

Follow these steps in order. Do not skip a step and do not guess data.

1. Call `GetPurchaseOrder` with the PO number. If it returns an error, stop and explain it.
2. Call `ValidateBudgetCap` with the PO number and the PO's total.
3. Call the `ProcurementPolicyAuditor` tool to learn the approver and whether vendor quotes are required.
4. Summarise in this shape, in at most five lines:
   - PO number, vendor, department, total
   - Budget: within cap or not (and by how much)
   - Approver and quotes requirement
   - Whether the current user can route it (`routableByCurrentUser`)
5. Ask whether the user wants to route it. Only when they say yes, call `RouteForApproval`.

If the budget check fails or the user is not allowed to route the PO, say what is blocking it and who can
unblock it. See `references/escalation.md` for escalation wording.
