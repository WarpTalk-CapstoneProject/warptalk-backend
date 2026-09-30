using System;
using System.Collections.Generic;
using System.Linq;
using WarpTalk.AuthService.Application.DTOs.Admin;
using WarpTalk.Shared.Contracts.Admin;

namespace WarpTalk.AuthService.Application.Services;

/// <summary>
/// The auth service's source for the pending-work inbox (G12): staff invitations nobody has acted on.
/// A pending one leaves by itself when it is accepted or revoked; one that expired unaccepted in the
/// last <see cref="ExpiredLookbackDays"/> days stays until someone re-invites or revokes it, because an
/// expired-but-open invitation still blocks a new one for that address.
/// </summary>
public static class StaffInboxItems
{
    public const int ExpiredLookbackDays = 30;
    private const string Href = "/admin/staff";

    public static AdminInboxSourceResponse Build(IEnumerable<StaffInvitationDto> invitations, DateTime now)
    {
        var items = new List<AdminInboxItem>();
        foreach (var invitation in invitations)
        {
            var created = DateTime.SpecifyKind(invitation.CreatedAt, DateTimeKind.Utc);
            var expires = DateTime.SpecifyKind(invitation.ExpiresAt, DateTimeKind.Utc);
            switch (invitation.Status)
            {
                case "pending":
                    items.Add(new AdminInboxItem(
                        $"{AdminInbox.Types.StaffInvitation}:{invitation.Id}",
                        AdminInbox.Types.StaffInvitation,
                        $"Staff invitation to {invitation.Email} not accepted yet",
                        $"{invitation.RoleName} · expires {expires:yyyy-MM-dd}",
                        null,
                        invitation.Email,
                        created,
                        expires,
                        expires - now <= TimeSpan.FromDays(2) ? AdminInbox.Priorities.High : AdminInbox.Priorities.Low,
                        Href,
                        NaturalCompletion: true));
                    break;
                case "expired" when now - expires <= TimeSpan.FromDays(ExpiredLookbackDays):
                    items.Add(new AdminInboxItem(
                        $"{AdminInbox.Types.StaffInvitation}:{invitation.Id}:expired",
                        AdminInbox.Types.StaffInvitation,
                        $"Staff invitation to {invitation.Email} expired unaccepted",
                        $"{invitation.RoleName} · invite again or revoke it",
                        null,
                        invitation.Email,
                        expires,
                        expires.AddDays(7),
                        AdminInbox.Priorities.Normal,
                        Href,
                        NaturalCompletion: true));
                    break;
            }
        }

        var ordered = items.OrderBy(i => i.DueAt).ToList();
        return new AdminInboxSourceResponse(
            AdminInbox.Sources.Staff,
            now,
            ordered.Take(AdminInbox.MaxItemsPerSource).ToList(),
            ordered.Count > AdminInbox.MaxItemsPerSource);
    }
}
