using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleApp.Models
{
    public class RiskProfile
    {
        public int Score { get; }

        public RiskProfile(int score)
        {
            if (score < 0 || score > 100)
                throw new ArgumentOutOfRangeException(nameof(score));

            Score = score;
        }
    }
}
