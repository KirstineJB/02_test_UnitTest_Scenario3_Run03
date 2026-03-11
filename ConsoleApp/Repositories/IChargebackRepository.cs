using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleApp.Repositories
{
    public interface IChargebackRepository
    {
        Task<bool> HasOpenChargebackAsync(Guid userId, CancellationToken ct = default);
    }
}
