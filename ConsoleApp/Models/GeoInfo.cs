using ConsoleApp.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleApp.Models
{
    public class GeoInfo
    {
        public string CountryCode { get; init; }
        public CountryRisk CountryRisk { get; init; }
        public bool IsVpn { get; init; }

        public GeoInfo(string countryCode, CountryRisk countryRisk, bool isVpn)
        {
            CountryCode = countryCode;
            CountryRisk = countryRisk;
            IsVpn = isVpn;
        }
    }
}
