using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WarpTalk.BillingService.Domain.Enums;

namespace WarpTalk.BillingService.Application.DTOs
{
    public class CreateTransactionCommand
    {
        public Guid WorkspaceId { get; set; }
        public Guid? OwnerUserId { get; set; }
        public Guid? PlanId { get; set; }
        public long OrderCode { get; set; }

        public decimal AmountVnd { get; set; }
        public decimal PurchasedCredits { get; set; }

        public TransactionType Type { get; set; }
    }
}
