using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QualityEnforcerBot
{
    public class ForkedRepository
    {
        public string Name { get; set; }
        public DateTime Expiry { get; set; }
        public int PullRequest { get; set; }
    }
}
