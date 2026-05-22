using AniSync.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AniSync.Helpers
{
    public class MemoryCacheHelper
    {
        public static string GetLastCallDateTimeKey(ApiName provider) => $"{provider}LastCallDateTime";
    }
}
