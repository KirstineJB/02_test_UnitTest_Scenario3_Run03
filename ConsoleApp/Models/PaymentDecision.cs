using ConsoleApp.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleApp.Models
{
    public  class PaymentDecision
    {
        public PaymentDecisionStatus Status { get; }
        public string? Reason { get; }

        private PaymentDecision(PaymentDecisionStatus status, string? reason = null)
        {
            Status = status;
            Reason = reason;
        }

        public static PaymentDecision Approve() => new(PaymentDecisionStatus.Approved);
        public static PaymentDecision Reject(string reason) => new(PaymentDecisionStatus.Rejected, reason);
        public static PaymentDecision Escalate(string reason) => new(PaymentDecisionStatus.Escalated, reason);
    }
}
