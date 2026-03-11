using ConsoleApp.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleApp.Models
{
    public class Account
    {
        public Guid Id { get; init; }
        public Guid UserId { get; init; }
        public AccountStatus Status { get; init; }
        public string BaseCurrency { get; init; }
        public decimal DailyLimit { get; init; }

        public Account(Guid id, Guid userId, AccountStatus status, string baseCurrency, decimal dailyLimit)
        {
            Id = id;
            UserId = userId;
            Status = status;
            BaseCurrency = baseCurrency;
            DailyLimit = dailyLimit;
        }
    }
}
