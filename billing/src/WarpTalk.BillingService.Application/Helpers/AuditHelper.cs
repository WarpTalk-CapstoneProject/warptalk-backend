using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;
using WarpTalk.BillingService.Application.DTOs;
using WarpTalk.BillingService.Application.Mappers;
using WarpTalk.BillingService.Domain.Constants;
using WarpTalk.BillingService.Domain.Entities;
using WarpTalk.BillingService.Domain.Interfaces;
using WarpTalk.Shared.Models;
using WarpTalk.Shared;

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

}
