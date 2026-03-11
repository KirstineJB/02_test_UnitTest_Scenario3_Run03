using ConsoleApp.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleApp.Services
{
    public interface IGeoService
    {
        Task<GeoInfo> ResolveAsync(string ipAddress, CancellationToken ct = default);
    }
}
