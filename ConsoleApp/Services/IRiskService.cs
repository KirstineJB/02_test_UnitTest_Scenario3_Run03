using ConsoleApp.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleApp.Services
{
    public interface IRiskService
    {
        Task<RiskProfile> GetRiskProfileAsync(Guid userId, CancellationToken ct = default);
    }
}
