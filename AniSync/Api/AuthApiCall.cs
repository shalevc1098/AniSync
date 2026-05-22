using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Plugin;
using AniSync.Configuration;
using AniSync.Interfaces;
using AniSync.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using AniSync.Helpers;

namespace AniSync.Api
{
    public class AuthApiCall
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<AuthApiCall> _logger;
        private readonly IMemoryCache _memoryCache;
        private readonly IAsyncDelayer _delayer;
        private readonly ConfigurationProvider<Config> _configProvider;
        private readonly IApplicationPaths _applicationPaths;

        public static int defaultTimeoutSeconds = 5;
        public static int timeoutIncrementMultiplier = 2;
        private static readonly SemaphoreSlim _refreshLock = new(1, 1);

        public AuthApiCall(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, IMemoryCache memoryCache, IAsyncDelayer delayer, ConfigurationProvider<Config> configProvider, IApplicationPaths applicationPaths)
        {
            _httpClientFactory = httpClientFactory;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<AuthApiCall>();
            _memoryCache = memoryCache;
            _delayer = delayer;
            _configProvider = configProvider;
            _applicationPaths = applicationPaths;
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

            if (string.IsNullOrEmpty(shokoUsername))
            {
                _logger.LogError("No Shoko username provided for authenticated API call");
                return null;
            }

            var config = _configProvider.Load();
            UserApiAuth? auth = config.GetAuthForShokoUser(shokoUsername, provider);

            if (auth == null)
            {
                _logger.LogError("Could not find authentication details for user {User}, please authenticate the plugin first", shokoUsername ?? "(default)");
                return null;
            }

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

                var failedStatusCode = responseMessage.StatusCode;
                var errorContent = await responseMessage.Content.ReadAsStringAsync();
                responseMessage.Dispose();

                switch (failedStatusCode)
                {
                    case HttpStatusCode.Unauthorized:
                        await _refreshLock.WaitAsync();
                        try
                        {
                            var latestConfig = _configProvider.Load();
                            var latestAuth = latestConfig.GetAuthForShokoUser(shokoUsername, provider);
                            if (latestAuth != null && latestAuth.AccessToken != auth.AccessToken)
                            {
                                auth = latestAuth;
                                _logger.LogDebug("Token was already refreshed by another thread, retrying with new token");
                            }
                            else
                            {
                                UserApiAuth newAuth;
                                try
                                {
                                    newAuth = await new ApiAuthentication(provider, _httpClientFactory, _loggerFactory, _configProvider, _applicationPaths, _memoryCache).GetToken(refreshToken: auth.RefreshToken, shokoUsername: auth.ShokoUsername);
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
