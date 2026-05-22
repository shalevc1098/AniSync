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

            var config = configProvider.Load();
            (_clientId, _clientSecret) = provider switch
            {
                ApiName.Mal => (config?.MalClientId ?? string.Empty, config?.MalClientSecret ?? string.Empty),
                ApiName.AniList => (config?.AniListClientId ?? string.Empty, config?.AniListClientSecret ?? string.Empty),
                _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
            };

            _providerApiAuth =  new ProviderApiAuth()
            {
                Name = provider,
                ClientId = _clientId,
                ClientSecret = _clientSecret
            };

            _authApiUrl = provider switch
            {
                ApiName.Mal => "https://myanimelist.net/v1/oauth2",
                ApiName.AniList => "https://anilist.co/api/v2/oauth",
                _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
            };
            _redirectUrl = baseUrl?.TrimEnd('/') + "/anisync/authCallback";
        }

        public string BuildAuthorizeRequestUrl(string? state = null)
        {
            switch (_provider)
            {
                case ApiName.Mal:
                    var codeChallenge = GenerateCodeChallenge();
                    var cacheKey = $"pkce_{state ?? "default"}";
                    _memoryCache.Set(cacheKey, codeChallenge, TimeSpan.FromMinutes(10));

                    var url = $"{_authApiUrl}/authorize?response_type=code&client_id={_providerApiAuth.ClientId}&code_challenge={codeChallenge}&redirect_uri={Uri.EscapeDataString(_redirectUrl)}";
                    if (!string.IsNullOrEmpty(state))
                    {
                        url += $"&state={Uri.EscapeDataString(state)}";
                    }
                    return url;
                case ApiName.AniList:
                    var aniUrl = $"{_authApiUrl}/authorize?client_id={_providerApiAuth.ClientId}&redirect_uri={Uri.EscapeDataString(_redirectUrl)}&response_type=code";
                    if (!string.IsNullOrEmpty(state))
                    {
                        aniUrl += $"&state={Uri.EscapeDataString(state)}";
                    }
                    return aniUrl;
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
                    var cacheKey = $"pkce_{state ?? "default"}";
                    var codeVerifier = _memoryCache.Get<string>(cacheKey);
                    if (string.IsNullOrEmpty(codeVerifier))
                    {
                        _logger.LogError("PKCE code verifier not found in cache for state {State}. OAuth session may have expired.", state ?? "default");
                        throw new System.Security.Authentication.AuthenticationException("OAuth session expired. Please try authenticating again.");
                    }
                    content.Add(new KeyValuePair<string, string>("code_verifier", codeVerifier));
                    _memoryCache.Remove(cacheKey);
                }
                _logger.LogInformation("Requesting {Provider} token from {Url}", _provider, $"{_authApiUrl}/token");
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

                    newUserApiAuth.RefreshToken = tokenResponse.refresh_token ?? string.Empty;

                    if (tokenResponse.expires_in.HasValue)
                    {
                        _logger.LogInformation("Token expires in {Seconds} seconds ({Days} days)",
                            tokenResponse.expires_in.Value, tokenResponse.expires_in.Value / 86400);
                    }

                    if (refreshToken != null)
                    {
                        var existingAuth = pluginConfig.GetAuthForShokoUser(shokoUsername, _provider);
                        if (!string.IsNullOrEmpty(existingAuth?.Username))
                        {
                            newUserApiAuth.Username = existingAuth.Username;
                        }
                    }
                    else if (!string.IsNullOrEmpty(shokoUsername))
                    {
                        newUserApiAuth.ShokoUsername = shokoUsername;
                        SaveProviderAuth(shokoUsername, newUserApiAuth);
                        try
                        {
                            string? name = _provider switch
                            {
                                ApiName.Mal => new MalApiCalls(_httpClientFactory, _loggerFactory, _memoryCache, new Delayer(), _configProvider, _applicationPaths).GetUserInformation(shokoUsername).Result?.Name,
                                ApiName.AniList => new AniListApiCalls(_httpClientFactory, _loggerFactory, _memoryCache, new Delayer(), _configProvider, _applicationPaths).GetUserInformation(shokoUsername).Result?.Name,
                                _ => null
                            };
                            if (!string.IsNullOrEmpty(name))
                            {
                                newUserApiAuth.Username = name;
                                _logger.LogInformation("Retrieved {Provider} username: {Username}", _provider, name);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to retrieve {Provider} username", _provider);
                        }
                    }

                    var userToLink = shokoUsername;
                    if (string.IsNullOrEmpty(userToLink))
                    {
                        _logger.LogError("No Shoko user specified during authentication");
                        throw new InvalidOperationException("Cannot authenticate without a Shoko user context");
                    }

                    newUserApiAuth.ShokoUsername = userToLink;
                    SaveProviderAuth(userToLink, newUserApiAuth);
                    _logger.LogInformation("Linked {Provider} account {User} to Shoko user {ShokoUser}",
                        _provider, newUserApiAuth.Username, userToLink);
                    return newUserApiAuth;
                }
            }

            throw new AuthenticationException($"Could not retrieve {_provider} token: " + response.StatusCode + " - " + response.ReasonPhrase);
        }

        private void SaveProviderAuth(string shokoUsername, UserApiAuth auth)
        {
            lock (ConfigGate.Lock)
            {
                var config = _configProvider.Load();
                config.SetAuthForShokoUser(shokoUsername, _provider, auth);
                _configProvider.Save(config);
            }
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
