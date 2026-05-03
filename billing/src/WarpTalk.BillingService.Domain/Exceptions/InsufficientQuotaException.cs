using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WarpTalk.BillingService.Domain.Exceptions;

public class InsufficientQuotaException : BillingDomainException
{
    public InsufficientQuotaException()
        : base("Insufficient quota or credits.")
    {
    }
}