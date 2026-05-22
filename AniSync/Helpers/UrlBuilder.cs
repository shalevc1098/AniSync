using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AniSync.Helpers
{
    public class UrlBuilder
    {
        /// <summary>
        /// Gets or sets the base URL.
        /// </summary>
        public string Base { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the query parameters.
        /// </summary>
        public List<KeyValuePair<string, string>> Parameters { get; set; }

        public UrlBuilder()
        {
            Parameters = new List<KeyValuePair<string, string>>();
        }

        /// <summary>
        /// Returns the URL string.
        /// </summary>
        /// <returns></returns>
        public string Build()
        {
            StringBuilder url = new StringBuilder(Base);
            if (Parameters.Count > 0)
            {
                url.Append('?');
                for (int i = 0; i < Parameters.Count; i++)
                {
                    var parameter = Parameters[i];
                    url.Append($"{parameter.Key}={Uri.EscapeDataString(parameter.Value)}");
                    if (i < Parameters.Count - 1)
                    {
                        url.Append('&');
                    }
                }
            }

            return url.ToString();
        }
    }
}
