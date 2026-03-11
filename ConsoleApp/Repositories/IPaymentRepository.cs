using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleApp.Repositories
{
    public interface IPaymentRepository
    {
        Task<int> CountTodayAsync(Guid userId, CancellationToken ct = default);
        Task RecordAttemptAsync(Guid userId, decimal amount, string currency, CancellationToken ct = default);
    }
}
