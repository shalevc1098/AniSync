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
        private static readonly SemaphoreSlim _refreshLock = new(1, 1);

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

            // Buffer form content bytes so we can recreate FormUrlEncodedContent on each retry
            // (HttpClient disposes the content after sending, so reusing it throws ObjectDisposedException)
            byte[]? formContentBytes = null;
            string? formContentType = null;
            if (formUrlEncodedContent != null)
            {
                formContentBytes = await formUrlEncodedContent.ReadAsByteArrayAsync();
                formContentType = formUrlEncodedContent.Headers.ContentType?.ToString();
            }

            byte[]? stringContentBytes = null;
            string? stringContentType = null;
            if (stringContent != null)
            {
                stringContentBytes = await stringContent.ReadAsByteArrayAsync();
                stringContentType = stringContent.Headers.ContentType?.ToString();
            }

            var client = _httpClientFactory.CreateClient();
            DateTime lastCallDateTime = _memoryCache.Get<DateTime>(MemoryCacheHelper.GetLastCallDateTimeKey(provider));
            if (lastCallDateTime != default)
            {
                var elapsed = DateTime.UtcNow.Subtract(lastCallDateTime);
                var remaining = TimeSpan.FromSeconds(defaultTimeoutSeconds) - elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    _logger.LogDebug($"({provider}) Delaying API call by {remaining.TotalSeconds:F1}s to prevent 429 (too many requests)...");
                    await _delayer.Delay(remaining);
                }
            }
            if (requestHeaders != null)
            {
                foreach (KeyValuePair<string, string> requestHeader in requestHeaders)
                {
                    client.DefaultRequestHeaders.Add(requestHeader.Key, requestHeader.Value);
                }
            }

            while (attempts < 3)
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
                HttpResponseMessage? responseMessage = null;
                try
                {
                    _memoryCache.Set(MemoryCacheHelper.GetLastCallDateTimeKey(provider), DateTime.UtcNow, TimeSpan.FromSeconds(5));

                    // Recreate content for each attempt since HttpClient disposes it after sending
                    HttpContent? bodyContent = null;
                    if (formContentBytes != null)
                    {
                        var byteContent = new ByteArrayContent(formContentBytes);
                        if (formContentType != null)
                            byteContent.Headers.TryAddWithoutValidation("Content-Type", formContentType);
                        bodyContent = byteContent;
                    }
                    else if (stringContentBytes != null)
                    {
                        var byteContent = new ByteArrayContent(stringContentBytes);
                        if (stringContentType != null)
                            byteContent.Headers.TryAddWithoutValidation("Content-Type", stringContentType);
                        bodyContent = byteContent;
                    }

                    switch (callType)
                    {
                        case CallType.GET:
                            responseMessage = await client.GetAsync(url);
                            break;
                        case CallType.POST:
                            responseMessage = await client.PostAsync(url, bodyContent);
                            break;
                        case CallType.PATCH:
                            responseMessage = await client.PatchAsync(url, bodyContent);
                            break;
                        case CallType.PUT:
                            responseMessage = await client.PutAsync(url, bodyContent);
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
                    _logger.LogError(e, "API call failed for {Url}", url);
                }


                if (responseMessage == null)
                {
                    attempts++;
                    continue;
                }

                if (responseMessage.IsSuccessStatusCode)
                {
                    return responseMessage;
                }

                // Read error info before disposing
                var failedStatusCode = responseMessage.StatusCode;
                var errorContent = await responseMessage.Content.ReadAsStringAsync();
                responseMessage.Dispose();

                switch (failedStatusCode)
                {
                    case HttpStatusCode.Unauthorized:
                        // token has probably expired; try refreshing it
                        // Use a lock to prevent concurrent refreshes from racing
                        await _refreshLock.WaitAsync();
                        try
                        {
                            // Re-read auth from config - another thread may have already refreshed
                            var latestAuth = Plugin.Instance?.Config?.GetAuthForShokoUser(shokoUsername, provider);
                            if (latestAuth != null && latestAuth.AccessToken != auth.AccessToken)
                            {
                                // Token was already refreshed by another thread, use it
                                auth = latestAuth;
                                _logger.LogDebug("Token was already refreshed by another thread, retrying with new token");
                            }
                            else
                            {
                                // We need to do the refresh ourselves
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
                                auth = newAuth;
                            }
                        }
                        finally
                        {
                            _refreshLock.Release();
                        }
                        attempts++;
                        break;
                    case HttpStatusCode.TooManyRequests:
                        _logger.LogWarning($"({provider}) API rate limit exceeded, retrying the API call again in {timeoutSeconds} seconds...");
                        await _delayer.Delay(TimeSpan.FromSeconds(timeoutSeconds));
                        timeoutSeconds *= timeoutIncrementMultiplier;
                        attempts++;
                        break;
                    default:
                        _logger.LogError($"Unable to complete {provider} API call ({callType.ToString()} {url}), reason: {failedStatusCode}, content: \n{errorContent}");
                        return null;
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
