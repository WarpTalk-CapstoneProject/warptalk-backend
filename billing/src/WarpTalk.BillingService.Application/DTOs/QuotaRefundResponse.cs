using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WarpTalk.BillingService.Application.DTOs
{
    public class QuotaRefundResponse
    {
        public bool Success { get; set; }
        public decimal NewBalance { get; set; }
    }
}
