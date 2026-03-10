using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;
using Shoko.Abstractions.Plugin;
using Shoko.AniSync.Configuration;
using Shoko.AniSync.Helpers;
using Shoko.AniSync.Interfaces;
using Shoko.AniSync.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Shoko.AniSync.Api
{
    public class ApiAuthentication
    {
        private ApiName _provider;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<ApiAuthentication> _logger;
        private readonly IMemoryCache _memoryCache;
        private readonly ConfigurationProvider<Config> _configProvider;
        private readonly IApplicationPaths _applicationPaths;
        private readonly ProviderApiAuth _providerApiAuth;
        private readonly string _authApiUrl;
        private readonly string _redirectUrl;

        private readonly string _clientId;
        private readonly string _clientSecret;

        public ApiAuthentication(ApiName provider, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ConfigurationProvider<Config> configProvider, IApplicationPaths applicationPaths, IMemoryCache? memoryCache = null, string? baseUrl = null)
        {
            _provider = provider;
            _httpClientFactory = httpClientFactory;
            _loggerFactory = loggerFactory;
            _logger = _loggerFactory.CreateLogger<ApiAuthentication>();
            _memoryCache = memoryCache ?? new MemoryCache(new MemoryCacheOptions());
            _configProvider = configProvider;
            _applicationPaths = applicationPaths;

            var gs = GlobalSettings.Load(applicationPaths);
            _clientId = gs?.MalClientId ?? string.Empty;
            _clientSecret = gs?.MalClientSecret ?? string.Empty;

            _providerApiAuth =  new ProviderApiAuth()
            {
                Name = provider,
                ClientId = _clientId,
                ClientSecret = _clientSecret
            };

            // TODO: Add support for other providers
            _authApiUrl = provider switch
            {
                ApiName.Mal => "https://myanimelist.net/v1/oauth2",
                _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
            };
            _redirectUrl = baseUrl?.TrimEnd('/') + "/anisync/authCallback";
        }

        public string BuildAuthorizeRequestUrl(string? state = null)
        {
            switch (_provider)
            {
                case ApiName.Mal:
                    // Generate a unique PKCE code challenge per authorization request
                    var codeChallenge = GenerateCodeChallenge();
                    var cacheKey = $"pkce_{state ?? "default"}";
                    _memoryCache.Set(cacheKey, codeChallenge, TimeSpan.FromMinutes(10));

                    var url = $"{_authApiUrl}/authorize?response_type=code&client_id={_providerApiAuth.ClientId}&code_challenge={codeChallenge}&redirect_uri={_redirectUrl}";
                    if (!string.IsNullOrEmpty(state))
                    {
                        url += $"&state={Uri.EscapeDataString(state)}";
                    }
                    return url;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public UserApiAuth GetToken(string? code = null, string? refreshToken = null, string? shokoUsername = null, string? state = null)
        {
            var client = _httpClientFactory.CreateClient();

            HttpContent formUrlEncodedContent;

            if (refreshToken != null)
            {
                formUrlEncodedContent = new FormUrlEncodedContent(new[] {
                        new KeyValuePair<string, string>("client_id", _providerApiAuth.ClientId),
                        new KeyValuePair<string, string>("client_secret", _providerApiAuth.ClientSecret),
                        new KeyValuePair<string, string>("grant_type", "refresh_token"),
                        new KeyValuePair<string, string>("refresh_token", refreshToken)
                    });
            }
            else
            {
                List<KeyValuePair<string, string>> content = new List<KeyValuePair<string, string>>() {
                        new KeyValuePair<string, string>("client_id", _providerApiAuth.ClientId),
                        new KeyValuePair<string, string>("client_secret", _providerApiAuth.ClientSecret),
                        new KeyValuePair<string, string>("code", code ?? string.Empty),
                        new KeyValuePair<string, string>("grant_type", "authorization_code"),
                        new KeyValuePair<string, string>("redirect_uri", _redirectUrl)
                    };
                if (_provider == ApiName.Mal)
                {
                    // Retrieve the per-session PKCE code verifier from cache
                    var cacheKey = $"pkce_{state ?? "default"}";
                    var codeVerifier = _memoryCache.Get<string>(cacheKey);
                    if (string.IsNullOrEmpty(codeVerifier))
                    {
                        _logger.LogError("PKCE code verifier not found in cache for state {State}. OAuth session may have expired.", state ?? "default");
                        throw new System.Security.Authentication.AuthenticationException("OAuth session expired. Please try authenticating again.");
                    }
                    content.Add(new KeyValuePair<string, string>("code_verifier", codeVerifier));
                    _memoryCache.Remove(cacheKey); // Single use
                }
                var dict = content.ToDictionary(k => k.Key, v => v.Value);
                _logger.LogInformation("Requesting token from {Url} with form fields: {@Fields}",
                                       $"{_authApiUrl}/token", dict);
                formUrlEncodedContent = new FormUrlEncodedContent(content.ToArray());
            }

            using var response = client.PostAsync(new Uri($"{_authApiUrl}/token"), formUrlEncodedContent).Result;

            if (response.IsSuccessStatusCode)
            {
                var content = response.Content.ReadAsStream();

                using var streamReader = new StreamReader(content);

                TokenResponse? tokenResponse = JsonSerializer.Deserialize<TokenResponse>(streamReader.ReadToEnd());

                var pluginConfig = _configProvider.Load();
                if (tokenResponse != null)
                {
                    UserApiAuth newUserApiAuth = new UserApiAuth
                    {
                        AccessToken = tokenResponse.access_token ?? string.Empty,
                        ExpiresAt = tokenResponse.expires_in.HasValue
                            ? DateTimeOffset.UtcNow.AddSeconds(tokenResponse.expires_in.Value).ToUnixTimeSeconds()
                            : (long?)null
                    };

                    if (_provider is ApiName.Mal)
                    {
                        newUserApiAuth.RefreshToken = tokenResponse.refresh_token ?? string.Empty;

                        // Log token expiration info
                        if (tokenResponse.expires_in.HasValue)
                        {
                            _logger.LogInformation("Token expires in {Seconds} seconds ({Days} days)",
                                tokenResponse.expires_in.Value,
                                tokenResponse.expires_in.Value / 86400);
                        }

                        if (refreshToken != null)
                        {
                            // Token refresh: preserve existing username from config
                            var existingAuth = pluginConfig.GetAuthForShokoUser(shokoUsername, _provider);
                            if (!string.IsNullOrEmpty(existingAuth?.Username))
                            {
                                newUserApiAuth.Username = existingAuth.Username;
                                _logger.LogInformation("Preserved existing MAL username on token refresh: {Username}", existingAuth.Username);
                            }
                        }
                        else
                        {
                            // Initial auth: fetch MAL username from API
                            try
                            {
                                if (!string.IsNullOrEmpty(shokoUsername))
                                {
                                    pluginConfig.SetAuthForShokoUser(shokoUsername, _provider, newUserApiAuth);
                                    _configProvider.Save(pluginConfig);
                                    var malApi = new MalApiCalls(_httpClientFactory, _loggerFactory, _memoryCache, new Delayer(), _configProvider, _applicationPaths);
                                    var userInfo = malApi.GetUserInformation(shokoUsername).Result;
                                    if (userInfo != null)
                                    {
                                        newUserApiAuth.Username = userInfo.Name;
                                        _logger.LogInformation("Retrieved MAL username: {Username}", userInfo.Name);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to retrieve MAL username");
                            }
                        }
                    }

                    // Always try to get a Shoko username - either provided or error
                    var userToLink = shokoUsername;
                    if (string.IsNullOrEmpty(userToLink))
                    {
                        // No user specified - this should not happen in production
                        // as authentication should provide the current user
                        _logger.LogError("No Shoko user specified during authentication");
                        throw new InvalidOperationException("Cannot authenticate without a Shoko user context");
                    }

                    newUserApiAuth.ShokoUsername = userToLink;
                    pluginConfig.SetAuthForShokoUser(userToLink, _provider, newUserApiAuth);
                    _configProvider.Save(pluginConfig);
                    _logger.LogInformation("Linked MAL account {MalUser} to Shoko user {ShokoUser}",
                        newUserApiAuth.Username, userToLink);
                    return newUserApiAuth;
                }
            }

            throw new AuthenticationException($"Could not retrieve {_provider} token: " + response.StatusCode + " - " + response.ReasonPhrase);
        }

        public Task RefreshAccessToken(string shokoUsername)
        {
            var config = _configProvider.Load();
            var auth = config.GetAuthForShokoUser(shokoUsername, _provider);
            if (auth?.RefreshToken == null)
            {
                throw new InvalidOperationException("No refresh token available for user " + shokoUsername);
            }

            GetToken(refreshToken: auth.RefreshToken, shokoUsername: shokoUsername);
            return Task.CompletedTask;
        }

        private static string GenerateCodeChallenge()
        {
            return new string((from s in Enumerable.Repeat("AaBbCcDdEeFfGgHhIiJjKkLlMmNnOoPpQqRrSsTtUuVvWwXxYyZz0123456789-._~", 128)
                               select s[Random.Shared.Next(s.Length)]).ToArray());
        }
    }
    public class TokenResponse
    {
        public string access_token { get; set; } = string.Empty;
        public string refresh_token { get; set; } = string.Empty;
        public int? expires_in { get; set; }
    }
}
