using ConsoleApp.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleApp.Repositories
{
    public interface IAccountRepository
    {
        Task<Account?> GetByUserIdAsync(Guid userId, CancellationToken ct = default);
    }
}
