using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
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
        private readonly ProviderApiAuth _providerApiAuth;
        private readonly string _authApiUrl;
        private readonly string _redirectUrl;
        private static readonly string _codeChallenge = GenerateCodeChallenge();

        private readonly string _clientId = "cb2cb041c1452a990a065d5e7ecdf89b";
        private readonly string _clientSecret = "REDACTED";

        public ApiAuthentication(ApiName provider, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, IMemoryCache? memoryCache = null)
        {
            _provider = provider;
            _httpClientFactory = httpClientFactory;
            _loggerFactory = loggerFactory;
            _logger = _loggerFactory.CreateLogger<ApiAuthentication>();
            _memoryCache = memoryCache ?? new MemoryCache(new MemoryCacheOptions());

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
            _redirectUrl = "http://localhost:8111/AniSync/authCallback";
        }

        public string BuildAuthorizeRequestUrl(string? state = null)
        {
            switch (_provider)
            {
                case ApiName.Mal:
                    var url = $"{_authApiUrl}/authorize?response_type=code&client_id={_providerApiAuth.ClientId}&code_challenge={_codeChallenge}&redirect_uri={_redirectUrl}";
                    if (!string.IsNullOrEmpty(state))
                    {
                        url += $"&state={Uri.EscapeDataString(state)}";
                    }
                    return url;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public UserApiAuth GetToken(string? code = null, string? refreshToken = null, string? shokoUsername = null)
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
                    content.Add(new KeyValuePair<string, string>("code_verifier", _codeChallenge));
                }
                var dict = content.ToDictionary(k => k.Key, v => v.Value);
                _logger.LogInformation("Requesting token from {Url} with form fields: {@Fields}",
                                       $"{_authApiUrl}/token", dict);
                formUrlEncodedContent = new FormUrlEncodedContent(content.ToArray());
            }

            var response = client.PostAsync(new Uri($"{_authApiUrl}/token"), formUrlEncodedContent).Result;

            if (response.IsSuccessStatusCode)
            {
                var content = response.Content.ReadAsStream();

                StreamReader streamReader = new StreamReader(content);

                TokenResponse? tokenResponse = JsonSerializer.Deserialize<TokenResponse>(streamReader.ReadToEnd());

                Config? pluginConfig = Plugin.Instance?.Config;
                if (pluginConfig != null && tokenResponse != null)
                {
                    UserApiAuth newUserApiAuth = new UserApiAuth
                    {
                        AccessToken = tokenResponse.access_token ?? string.Empty
                    };

                    if (_provider is ApiName.Mal)
                    {
                        newUserApiAuth.RefreshToken = tokenResponse.refresh_token ?? string.Empty;
                        
                        // Need to temporarily save auth to make API call to get username
                        // This will be properly saved later with the username
                        
                        try
                        {
                            // Temporarily save auth to make API call
                            if (!string.IsNullOrEmpty(shokoUsername))
                            {
                                pluginConfig.SetAuthForShokoUser(shokoUsername, _provider, newUserApiAuth);
                                var malApi = new MalApiCalls(_httpClientFactory, _loggerFactory, _memoryCache, new Delayer());
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
                    _logger.LogInformation("Linked MAL account {MalUser} to Shoko user {ShokoUser}", 
                        newUserApiAuth.Username, userToLink);
                    return newUserApiAuth;
                }
            }

            throw new AuthenticationException($"Could not retrieve {_provider} token: " + response.StatusCode + " - " + response.ReasonPhrase);
        }

        public Task RefreshAccessToken(string shokoUsername)
        {
            var config = Plugin.Instance?.Config;
            var auth = config?.GetAuthForShokoUser(shokoUsername, _provider);
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
    }
}
