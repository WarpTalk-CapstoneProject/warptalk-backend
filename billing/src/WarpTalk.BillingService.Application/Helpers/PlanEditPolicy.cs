using System;
using System.Collections.Generic;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Domain.Entities;

namespace WarpTalk.BillingService.Application.Helpers;

/// <summary>
/// What may still be edited on a plan somebody can already buy. WT-481.
///
/// `PUT /plans/{id}` writes all 22 columns from the request, so an administrator opening a live
/// plan to correct a typo re-sent every commercial term with it — and the admin UI's own DTO
/// carried fewer fields than the entity, so the ones it did not know about were written back as
/// whatever the form defaulted to. A workspace already paying against that plan then had its
/// price, its credit allowance and its overage economics quietly redefined underneath it.
///
/// The rule the owner chose is not "published means frozen". A typo in a plan name is worth
/// fixing, and hiding a retired plan has to stay possible or there is no way to retire one at all.
/// So the split is by CONSEQUENCE:
///
///   - What somebody is paying for — price, currency, cycle, credits, overage, invoice terms —
///     and what they are entitled to — participants, languages, voice clone, assistant, glossary,
///     dedicated GPU — is locked.
///   - What it is CALLED and where it APPEARS — name, features, sort order, active flag — stays
///     editable, because none of it changes the bargain.
///
/// Identity is locked with the money: the slug is what invoices, entitlement snapshots and the
/// seed data all point at, and the tier is what the plan ladder is ordered by.
///
/// This is a pure diff so the rule can be tested without a database, and so the caller decides
/// WHEN it applies — the answer to "is this plan live" involves a subscription query that has no
/// business being inside a comparison.
/// </summary>
public static class PlanEditPolicy
{
    /// <summary>
    /// The locked fields this request would change, by their on-screen names. Empty means the
    /// request only touches things that stay editable once a plan is live.
    /// </summary>
    public static IReadOnlyList<string> LockedFieldChanges(Plan plan, PlanRequest request)
    {
        var changed = new List<string>();

        void Check(string field, object? current, object? proposed)
        {
            if (!Equals(current, proposed)) changed.Add(field);
        }

        // Identity — what everything downstream points at.
        Check("slug", plan.Slug, request.Slug?.ToLowerInvariant().Trim());
        Check("tier", plan.Tier, request.Tier);

        // Price and the shape of the bargain.
        Check("price", plan.Price, request.Price);
        Check("currency", plan.Currency, request.Currency);
        Check("billing cycle", plan.BillingCycle, request.BillingCycle);
        Check("credits per cycle", plan.CreditsPerCycle, request.CreditsPerCycle);
        Check("overage cap", plan.OverageCapCredits, request.OverageCapCredits);
        Check("overage price per credit", plan.OveragePricePerCredit, request.OveragePricePerCredit);
        Check("low balance threshold", plan.LowBalanceThresholdCredits, request.LowBalanceThresholdCredits);
        Check("rollover cap", plan.RolloverCapCredits, request.RolloverCapCredits);
        Check("invoice terms", plan.InvoiceTermsDays, request.InvoiceTermsDays);
        Check("invoice grace hours", plan.InvoiceGraceHours, request.InvoiceGraceHours);

        // Entitlements — what the subscriber is allowed to do.
        Check("max participants", plan.MaxParticipants, request.MaxParticipants);
        Check("max languages", plan.MaxLanguages, request.MaxLanguages);
        Check("voice cloning", plan.VoiceCloneEnabled, request.VoiceCloneEnabled);
        Check("AI assistant", plan.AiAssistantEnabled, request.AiAssistantEnabled);
        Check("glossary", plan.GlossaryEnabled, request.GlossaryEnabled);
        Check("dedicated GPU", plan.DedicatedGpu, request.DedicatedGpu);

        // Deliberately absent, and each for its own reason:
        //   Name, Features  — what it is called and how it is described. No effect on the bargain.
        //   SortOrder       — where it sits in the ladder.
        //   IsActive        — hiding a plan is how one is RETIRED. Locking this would make a
        //                     published plan permanent, which is the opposite of the point.
        return changed;
    }
}
