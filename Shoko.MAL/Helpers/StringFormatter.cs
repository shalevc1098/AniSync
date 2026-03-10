using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shoko.AniSync.Helpers
{
    public class StringFormatter
    {
        public static string RemoveSpecialCharacters(string str)
        {
            if (string.IsNullOrEmpty(str)) return string.Empty;
            StringBuilder stringBuilder = new StringBuilder();
            foreach (char c in str)
            {
                if (c is >= '0' and <= '9' || c is >= 'A' and <= 'Z' || c is >= 'a' and <= 'z')
                {
                    stringBuilder.Append(c);
                }
            }

            return stringBuilder.ToString();
        }

        public static string RemoveSpaces(string str)
        {
            if (string.IsNullOrEmpty(str)) return string.Empty;
            StringBuilder stringBuilder = new StringBuilder();
            foreach (char c in str)
            {
                if (c is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or ' ')
                {
                    stringBuilder.Append(c);
                }
            }

            return stringBuilder.ToString();
        }
    }
}
