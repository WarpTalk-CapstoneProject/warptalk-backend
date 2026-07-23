using System.Collections.Generic;

namespace WarpTalk.BillingService.Application.Helpers;

public static class AuditHelper
{
    // Track changes between oldVal and newVal and add the change to the changes list
    public static void Track<T>(List<string> changes, T oldVal, T newVal, string format)
    {
        if (!EqualityComparer<T>.Default.Equals(oldVal, newVal))
        {
            changes.Add(string.Format(format, oldVal, newVal));
        }
    }
    // Track boolean changes between oldVal and newVal and add the change to the changes list
    public static void TrackBool(List<string> changes, bool oldVal, bool newVal, string format)
    {
        if (oldVal != newVal)
        {
            changes.Add(string.Format(format, newVal ? "enabled" : "disabled"));
        }
    }
}
