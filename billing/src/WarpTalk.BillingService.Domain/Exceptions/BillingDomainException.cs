using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WarpTalk.BillingService.Domain.Exceptions;

public class BillingDomainException : Exception
{
    public BillingDomainException(string message)
        : base(message)
    {
    }
}