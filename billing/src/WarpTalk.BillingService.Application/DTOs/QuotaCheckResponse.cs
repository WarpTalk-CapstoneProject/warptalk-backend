using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WarpTalk.BillingService.Application.DTOs
{
    public class QuotaCheckResponse
    {
        public Guid OwnerUserId { get; set; }

        public decimal Balance { get; set; }

        public decimal ReservedCredits { get; set; }

        public decimal AvailableCredits => Balance - ReservedCredits;
    }
}
