using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Shoko.AniSync.Configuration;
using Shoko.AniSync.Interfaces;
using Shoko.AniSync.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Shoko.AniSync.Helpers;

namespace Shoko.AniSync.Api
{
    public class AuthApiCall
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<AuthApiCall> _logger;
        private readonly IMemoryCache _memoryCache;
        private readonly IAsyncDelayer _delayer;

        public static int defaultTimeoutSeconds = 5;
        public static int timeoutIncrementMultiplier = 2;

        public AuthApiCall(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, IMemoryCache memoryCache, IAsyncDelayer delayer)
        {
            _httpClientFactory = httpClientFactory;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<AuthApiCall>();
            _memoryCache = memoryCache;
            _delayer = delayer;
        }

        /// <summary>
        /// Make an authenticated API call.
        /// </summary>
        /// <param name="callType">The type of call to make.</param>
        /// <param name="url">The URL that you want to make the request to.</param>
        /// <param name="formUrlEncodedContent">The form data to be posted.</param>
        /// <returns>API call response.</returns>
        /// <exception cref="NullReferenceException">Authentication details not found.</exception>
        /// <exception cref="Exception">Non-200 response.</exception>
        /// <exception cref="AuthenticationException">Could not authenticate with the API.</exception>
        public async Task<HttpResponseMessage?> AuthenticatedApiCall(ApiName provider, CallType callType, string url, FormUrlEncodedContent? formUrlEncodedContent = null, StringContent? stringContent = null, Dictionary<string, string>? requestHeaders = null, string? shokoUsername = null)
        {
            int attempts = 0;
            int timeoutSeconds = defaultTimeoutSeconds;
            
            // shokoUsername must be provided
            if (string.IsNullOrEmpty(shokoUsername))
            {
                _logger.LogError("No Shoko username provided for authenticated API call");
                return null;
            }
            
            UserApiAuth? auth = Plugin.Instance?.Config?.GetAuthForShokoUser(shokoUsername, provider);
            
            if (auth == null)
            {
                _logger.LogError("Could not find authentication details for user {User}, please authenticate the plugin first", shokoUsername ?? "(default)");
                return null;
            }

            var client = _httpClientFactory.CreateClient();
            DateTime lastCallDateTime = _memoryCache.Get<DateTime>(MemoryCacheHelper.GetLastCallDateTimeKey(provider));
            if (lastCallDateTime != default)
            {
                _logger.LogDebug($"({provider}) Delaying API call to prevent 429 (too many requests)...");
                await _delayer.Delay(DateTime.UtcNow.Subtract(lastCallDateTime));
            }
            while (attempts < 3)
            {
                if (requestHeaders != null)
                {
                    foreach (KeyValuePair<string, string> requestHeader in requestHeaders)
                    {
                        client.DefaultRequestHeaders.Add(requestHeader.Key, requestHeader.Value);
                    }
                }

                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
                HttpResponseMessage responseMessage = new HttpResponseMessage();
                try
                {
                    _memoryCache.Set(MemoryCacheHelper.GetLastCallDateTimeKey(provider), DateTime.UtcNow, TimeSpan.FromSeconds(5));
                    switch (callType)
                    {
                        case CallType.GET:
                            responseMessage = await client.GetAsync(url);
                            break;
                        case CallType.POST:
                            responseMessage = await client.PostAsync(url, formUrlEncodedContent != null ? formUrlEncodedContent : stringContent);
                            break;
                        case CallType.PATCH:
                            responseMessage = await client.PatchAsync(url, formUrlEncodedContent != null ? formUrlEncodedContent : stringContent);
                            break;
                        case CallType.PUT:
                            responseMessage = await client.PutAsync(url, formUrlEncodedContent);
                            break;
                        case CallType.DELETE:
                            responseMessage = await client.DeleteAsync(url);
                            break;
                        default:
                            responseMessage = await client.GetAsync(url);
                            break;
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e.Message);
                }


                if (responseMessage.IsSuccessStatusCode)
                {
                    return responseMessage;
                }
                else
                {
                    switch (responseMessage.StatusCode)
                    {
                        case HttpStatusCode.Unauthorized:
                            // token has probably expired; try refreshing it
                            UserApiAuth newAuth;
                            try
                            {
                                newAuth = new ApiAuthentication(provider, _httpClientFactory, _loggerFactory, _memoryCache).GetToken(refreshToken: auth.RefreshToken, shokoUsername: auth.ShokoUsername);
                            }
                            catch (Exception e)
                            {
                                _logger.LogError($"Could not re-authenticate: {e.Message}, please manually re-authenticate the user via the AniSync configuration page");
                                return null;
                            }

                            // and then make the call again, using the new auth details
                            auth = newAuth;
                            attempts++;
                            break;
                        case HttpStatusCode.TooManyRequests:
                            _logger.LogWarning($"({provider}) API rate limit exceeded, retrying the API call again in {timeoutSeconds} seconds...");
                            await _delayer.Delay(TimeSpan.FromSeconds(timeoutSeconds));
                            timeoutSeconds *= timeoutIncrementMultiplier;
                            attempts++;
                            break;
                        default:
                            _logger.LogError($"Unable to complete {provider} API call ({callType.ToString()} {url}), reason: {responseMessage.StatusCode}, content: \n{await responseMessage.Content.ReadAsStringAsync()}");
                            return null;
                    }
                }
            }

            _logger.LogError("Unable to authenticate the API call, re-authenticate the plugin");
            return null;
        }

        public enum CallType
        {
            GET,
            POST,
            PATCH,
            PUT,
            DELETE
        }
    }
}
