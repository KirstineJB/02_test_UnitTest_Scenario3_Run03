using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleApp.Services
{
    public interface IFeatureFlagService
    {
        Task<IReadOnlyCollection<string>> GetFlagsAsync(Guid tenantId, CancellationToken ct = default);
    }
}
