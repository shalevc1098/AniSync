using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Config;
using Shoko.AniSync.Api;
using Shoko.AniSync.Configuration;
using Shoko.AniSync.Interfaces;
using Shoko.AniSync.Models.ViewModels;
using Shoko.AniSync.Models.Mal;
using Shoko.MAL.Models;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.User.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Threading.Tasks;

namespace Shoko.AniSync.Controllers
{
    [Route("anisync")]
    [ApiVersionNeutral]
    public class AniSyncController : Controller
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IApplicationPaths _applicationPaths;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<AniSyncController> _logger;
        private readonly IMemoryCache _memoryCache;
        private readonly IAsyncDelayer _delayer;
        private readonly ConfigurationProvider<Config> _configProvider;
        private readonly IUserDataService _userDataService;
        private readonly IUserService _userService;

        private string ShokoApiBaseUrl
        {
            get
            {
                var request = _httpContextAccessor?.HttpContext?.Request;
                if (request != null)
                {
                    return $"{request.Scheme}://{request.Host}";
                }
                return "http://localhost:8111";
            }
        }

        private string ShokoLocalUrl
        {
            get
            {
                var localPort = _httpContextAccessor?.HttpContext?.Connection?.LocalPort ?? 8111;
                return $"http://localhost:{localPort}";
            }
        }

        public AniSyncController(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, IHttpContextAccessor httpContextAccessor, IApplicationPaths applicationPaths, IMemoryCache memoryCache, IUserDataService userDataService, IUserService userService, ConfigurationProvider<Config> configProvider)
        {
            _httpClientFactory = httpClientFactory;
            _httpContextAccessor = httpContextAccessor;
            _applicationPaths = applicationPaths;
            _loggerFactory = loggerFactory;
            _logger = _loggerFactory.CreateLogger<AniSyncController>();
            _memoryCache = memoryCache;
            _delayer = new Delayer();
            _userDataService = userDataService;
            _userService = userService;
            _configProvider = configProvider;
        }

        [HttpGet]
        [Route("buildAuthorizeRequestUrl")]
        public IActionResult BuildAuthorizeRequestUrl(ApiName provider, string? baseUrl = null)
        {
            // Called via fetch, so the apikey header is present. The OAuth state is built
            // and signed here, bound to the resolved caller - any client-supplied user is
            // ignored, which prevents linking a MAL account to someone else on callback.
            var shokoUsername = GetCurrentShokoUser();
            if (string.IsNullOrEmpty(shokoUsername))
                return Unauthorized();

            var requestHost = _httpContextAccessor?.HttpContext?.Request?.Host.Host;
            var effectiveBaseUrl = IsAllowedBaseUrl(baseUrl, requestHost) ? baseUrl!.TrimEnd('/') : ShokoApiBaseUrl;

            var state = SignState(shokoUsername, effectiveBaseUrl);
            var url = new ApiAuthentication(provider, _httpClientFactory, _loggerFactory, _configProvider, _applicationPaths, _memoryCache, effectiveBaseUrl).BuildAuthorizeRequestUrl(state);
            return Content(url, "text/plain");
        }

        /// <summary>HMAC-signs an OAuth state binding the authorized Shoko user + baseUrl, with a 10-minute expiry.</summary>
        private string SignState(string user, string baseUrl)
        {
            var payload = JsonSerializer.Serialize(new
            {
                u = user,
                b = baseUrl,
                exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()
            });
            var payloadBytes = System.Text.Encoding.UTF8.GetBytes(payload);
            var key = GlobalSettings.GetOrCreateStateSigningKey(_applicationPaths);
            using var hmac = new System.Security.Cryptography.HMACSHA256(key);
            return Base64Url(payloadBytes) + "." + Base64Url(hmac.ComputeHash(payloadBytes));
        }

        /// <summary>Verifies a signed OAuth state (signature + expiry) and extracts the bound user/baseUrl.</summary>
        private bool TryVerifyState(string state, out string? user, out string? baseUrl)
        {
            user = null;
            baseUrl = null;
            try
            {
                var parts = state.Split('.');
                if (parts.Length != 2) return false;

                var payloadBytes = Base64UrlDecode(parts[0]);
                var providedSig = Base64UrlDecode(parts[1]);
                var key = GlobalSettings.GetOrCreateStateSigningKey(_applicationPaths);
                using var hmac = new System.Security.Cryptography.HMACSHA256(key);
                var expectedSig = hmac.ComputeHash(payloadBytes);
                if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(providedSig, expectedSig))
                    return false;

                var obj = JsonSerializer.Deserialize<JsonElement>(System.Text.Encoding.UTF8.GetString(payloadBytes));
                var exp = obj.TryGetProperty("exp", out var expEl) ? expEl.GetInt64() : 0;
                if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) return false;

                user = obj.TryGetProperty("u", out var uEl) ? uEl.GetString() : null;
                baseUrl = obj.TryGetProperty("b", out var bEl) ? bEl.GetString() : null;
                return !string.IsNullOrEmpty(user);
            }
            catch
            {
                return false;
            }
        }

        private static string Base64Url(byte[] data) =>
            Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] Base64UrlDecode(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
            return Convert.FromBase64String(s);
        }

        /// <summary>Only allow a client-supplied baseUrl if it matches the current request host (else fall back server-side).</summary>
        private bool IsAllowedBaseUrl(string? baseUrl, string? requestHost)
        {
            if (string.IsNullOrEmpty(baseUrl)) return false;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != "http" && uri.Scheme != "https") return false;
            return uri.Host == requestHost || uri.Host == "localhost" || uri.Host == "127.0.0.1";
        }

        [HttpGet]
        [Route("authCallback")]
        public IActionResult MalCallback(string code, string? state = null)
        {
            ApiName provider = ApiName.Mal;
            try
            {
                // The state must be a valid, unexpired, server-signed token. Its bound user
                // is trusted; a forged/tampered state (e.g. naming another user) is rejected.
                if (string.IsNullOrEmpty(state) || !TryVerifyState(state, out var shokoUsername, out var callbackBaseUrl) || string.IsNullOrEmpty(shokoUsername))
                {
                    _logger.LogWarning("OAuth callback rejected: missing or invalid signed state");
                    return Redirect("/anisync?error=invalid_state");
                }

                var effectiveBaseUrl = string.IsNullOrEmpty(callbackBaseUrl) ? ShokoApiBaseUrl : callbackBaseUrl;
                _logger.LogInformation("Authenticating MAL for Shoko user: {ShokoUsername} with baseUrl: {BaseUrl}", shokoUsername, effectiveBaseUrl);
                new ApiAuthentication(provider, _httpClientFactory, _loggerFactory, _configProvider, _applicationPaths, _memoryCache, effectiveBaseUrl).GetToken(code, shokoUsername: shokoUsername, state: state);

                return Redirect("/anisync?success=connected");
            }
            catch (AuthenticationException authEx)
            {
                _logger.LogError(authEx, "Failed to retrieve MAL token: {Message}", authEx.Message);
                return Redirect("/anisync?error=" + Uri.EscapeDataString(authEx.Message));
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogError(httpEx, "HTTP request to MAL failed");
                return Redirect("/anisync?error=Failed+to+connect+to+MyAnimeList");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in auth callback");
                return Redirect("/anisync?error=An+unexpected+error+occurred");
            }
        }
        
        // Proxy to Shoko's User/Current API endpoint
        [HttpGet]
        [Route("api/v3/User/Current")]
        public async Task<IActionResult> GetCurrentUser()
        {
            try
            {
                var apiKey = Request.Headers["apikey"].FirstOrDefault();
                
                if (string.IsNullOrEmpty(apiKey))
                {
                    return Unauthorized(new { error = "No API key provided" });
                }
                
                var httpClient = _httpClientFactory.CreateClient();
                httpClient.DefaultRequestHeaders.Add("apikey", apiKey);
                
                // Since we're running as a plugin, we need to use localhost
                using var response = await httpClient.GetAsync($"{ShokoLocalUrl}/api/v3/User/Current");

                if (!response.IsSuccessStatusCode)
                {
                    return StatusCode((int)response.StatusCode, new { error = "Authentication failed" });
                }
                
                var content = await response.Content.ReadAsStringAsync();
                return Content(content, "application/json");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get current user");
                return StatusCode(500, new { error = "Failed to get current user" });
            }
        }
        
        [HttpGet]
        [Route("")]
        public IActionResult Index()
        {
            // Static page: no per-user data is rendered server-side. The page loads the
            // current user's dashboard client-side from /anisync/api/dashboard (apikey header).
            var html = GetLayout("Dashboard", LoadEmbeddedResource("dashboard.html"));
            return Content(html, "text/html");
        }
        
        
        [HttpGet]
        [Route("Settings")]
        public IActionResult Settings()
        {
            // Static page: no per-user data is rendered server-side. The page loads the
            // current user's settings + MAL status client-side from /anisync/api/user/settings.
            var html = GetLayout("Settings", LoadEmbeddedResource("settings.html"));
            return Content(html, "text/html");
        }
        
        [HttpPost]
        [Route("Settings")]
        public IActionResult Settings([FromBody] SettingsViewModel model)
        {
            _logger.LogInformation("Settings POST received. Content-Type: {ContentType}", Request.ContentType);
            _logger.LogInformation("Has apikey header: {HasApiKey}", Request.Headers.ContainsKey("apikey"));
            
            if (model == null)
            {
                _logger.LogError("Settings model is null");
                return BadRequest("Invalid settings data");
            }
            
            var shokoUsername = GetCurrentShokoUser();
            if (string.IsNullOrEmpty(shokoUsername))
            {
                return Unauthorized(new { error = "Authentication required" });
            }

            var config = _configProvider.Load();

            _logger.LogInformation("Saving settings for user: {Username}", shokoUsername);
            _logger.LogInformation("Settings model: UpdateNsfw={UpdateNsfw}, EnableAutoSync={EnableAutoSync}", model.UpdateNsfw, model.EnableAutoSync);

            config.SetUserSettings(shokoUsername, new UserSettings
            {
                UpdateNsfw = model.UpdateNsfw,
                EnableAutoSync = model.EnableAutoSync,
                SyncOnlyCompleted = model.SyncOnlyCompleted,
                SetStartDateFromAnyEpisode = model.SetStartDateFromAnyEpisode,
                EnableRewatchDetection = model.EnableRewatchDetection,
                AllowRollback = model.AllowRollback,
                TitleMatchThreshold = model.TitleMatchThreshold,
                UseFuzzyMatching = model.UseFuzzyMatching,
                SyncDelaySeconds = model.SyncDelaySeconds,
                EnableDebugLogging = model.EnableDebugLogging
            });

            _logger.LogInformation("Settings saved successfully for user: {Username}", shokoUsername);

            return Redirect("/anisync/settings");
        }
        
        [HttpGet]
        [Route("History")]
        public IActionResult History()
        {
            // Static page: no per-user data is rendered server-side. The page loads the
            // current user's history client-side from /anisync/api/history (apikey header).
            var html = GetLayout("Sync History", LoadEmbeddedResource("history.html"));
            return Content(html, "text/html");
        }
        
        // Logout
        [HttpPost]
        [Route("Logout")]
        public IActionResult Logout()
        {
            var config = _configProvider.Load();
            var currentUser = GetCurrentShokoUser();
            
            if (!string.IsNullOrEmpty(currentUser) && config?.Users.ContainsKey(currentUser) == true)
            {
                var userConfig = config.Users[currentUser];
                if (userConfig?.Providers?.ContainsKey("Mal") == true)
                {
                    userConfig.Providers.Remove("Mal");
                    _configProvider.Save(config);
                }
            }
            
            
            return RedirectToAction("Index");
        }
        
        // Refresh token
        [HttpPost]
        [Route("refresh-token")]
        public async Task<IActionResult> RefreshToken()
        {
            try
            {
                var shokoUsername = GetCurrentShokoUser();
                if (string.IsNullOrEmpty(shokoUsername))
                {
                    _logger.LogError("Cannot refresh token without a valid Shoko user");
                    return RedirectToAction("Index");
                }
                
                var auth = new ApiAuthentication(ApiName.Mal, _httpClientFactory, _loggerFactory, _configProvider, _applicationPaths, _memoryCache);
                await auth.RefreshAccessToken(shokoUsername);
                
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh token");
            }
            
            return Redirect("/anisync/settings");
        }
        
        private object? GetUserProperty(object user, string propertyName)
        {
            try
            {
                return user?.GetType().GetProperty(propertyName)?.GetValue(user);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>True if the resolved caller is a Shoko admin. Used to gate global settings.</summary>
        private bool IsCurrentUserAdmin()
        {
            var username = GetCurrentShokoUser();
            if (string.IsNullOrEmpty(username) || _userService == null) return false;
            try
            {
                var user = _userService.GetUsers()?
                    .FirstOrDefault(u => string.Equals(GetUserProperty(u, "Username") as string, username, StringComparison.Ordinal));
                return user != null && GetUserProperty(user, "IsAdmin") as bool? == true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to determine admin status for {Username}", username);
                return false;
            }
        }
        
        // Helper method to get current Shoko user from query, header, or request.
        // Result is cached per-request in HttpContext.Items to avoid redundant HTTP calls.
        private const string ShokoUserCacheKey = "__AniSync_CurrentShokoUser";
        private string? GetCurrentShokoUser()
        {
            var httpContext = _httpContextAccessor?.HttpContext;

            // Return cached result if we already resolved the user this request
            if (httpContext?.Items.TryGetValue(ShokoUserCacheKey, out var cached) == true)
            {
                return cached as string;
            }

            string? result = null;
            try
            {
                // Try to get API key from header (passed by JavaScript)
                var apiKey = httpContext?.Request?.Headers["apikey"].FirstOrDefault();
                if (!string.IsNullOrEmpty(apiKey))
                {
                    // Call Shoko's User/Current API synchronously to get the actual username
                    var httpClient = _httpClientFactory.CreateClient();
                    httpClient.DefaultRequestHeaders.Add("apikey", apiKey);

                    try
                    {
                        using var response = httpClient.GetAsync($"{ShokoLocalUrl}/api/v3/User/Current").GetAwaiter().GetResult();
                        if (response.IsSuccessStatusCode)
                        {
                            var content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                            var userData = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(content);
                            if (userData?.Username != null)
                            {
                                result = userData.Username.ToString();
                                _logger.LogDebug("Got username from API: {Username}", result);
                            }
                        }
                    }
                    catch (Exception apiEx)
                    {
                        _logger.LogWarning(apiEx, "Failed to call User/Current API");
                    }
                }

                if (result == null)
                {
                    _logger.LogDebug("No user found, returning null");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get current Shoko user, returning null");
            }

            // Cache result (even null) for this request
            if (httpContext != null)
            {
                httpContext.Items[ShokoUserCacheKey] = result;
            }
            return result;
        }
        
        [HttpGet]
        [Route("api/global-settings")]
        public IActionResult GetGlobalSettings()
        {
            var currentUser = GetCurrentShokoUser();
            if (string.IsNullOrEmpty(currentUser))
            {
                return Unauthorized(new { error = "Authentication required" });
            }

            var gs = GlobalSettings.Load(_applicationPaths);
            // Bug 6 fix: handle short secrets safely
            string maskedSecret;
            var secret = gs?.MalClientSecret;
            if (string.IsNullOrEmpty(secret))
                maskedSecret = string.Empty;
            else if (secret.Length <= 4)
                maskedSecret = new string('*', secret.Length);
            else
                maskedSecret = new string('*', secret.Length - 4) + secret[^4..];

            return Json(new
            {
                malClientId = gs?.MalClientId ?? string.Empty,
                malClientSecret = maskedSecret
            });
        }

        [HttpPost]
        [Route("api/global-settings")]
        public IActionResult SaveGlobalSettings([FromBody] GlobalSettingsRequest request)
        {
            var currentUser = GetCurrentShokoUser();
            if (string.IsNullOrEmpty(currentUser))
            {
                return Unauthorized(new { error = "Authentication required" });
            }

            // Global MAL credentials affect every user, so only admins may change them.
            if (!IsCurrentUserAdmin())
            {
                return StatusCode(403, new { error = "Only an administrator can change MAL API credentials" });
            }

            if (request == null)
                return BadRequest(new { error = "Invalid request body" });

            var existing = GlobalSettings.Load(_applicationPaths);

            // If the incoming secret is the masked version (starts with *), keep the existing secret
            var effectiveSecret = request.MalClientSecret;
            if (!string.IsNullOrEmpty(effectiveSecret) && effectiveSecret.StartsWith('*') && existing != null)
            {
                effectiveSecret = existing.MalClientSecret;
            }

            bool credentialsChanged = existing != null &&
                (!string.IsNullOrEmpty(existing.MalClientId) || !string.IsNullOrEmpty(existing.MalClientSecret)) &&
                (existing.MalClientId != request.MalClientId || existing.MalClientSecret != effectiveSecret);

            var gs = new GlobalSettings
            {
                MalClientId = request.MalClientId,
                MalClientSecret = effectiveSecret
            };
            gs.Save(_applicationPaths);

            bool reAuthRequired = false;
            if (credentialsChanged)
            {
                // Clear all MAL provider auth since tokens are tied to the old app credentials
                var config = _configProvider.Load();
                bool anyCleared = false;
                foreach (var kvp in config.Users)
                {
                    if (kvp.Value?.Providers?.ContainsKey("Mal") == true)
                    {
                        kvp.Value.Providers.Remove("Mal");
                        anyCleared = true;
                    }
                }
                if (anyCleared)
                {
                    _configProvider.Save(config);
                    reAuthRequired = true;
                    _logger.LogInformation("Cleared MAL auth for all users due to credential change");
                }
            }

            _logger.LogInformation("Global settings saved. Credentials changed: {Changed}, Re-auth required: {ReAuth}",
                credentialsChanged, reAuthRequired);

            return Json(new { success = true, reAuthRequired });
        }

        /// <summary>
        /// API endpoint to get user history in new format
        /// </summary>
        [HttpGet]
        [Route("api/history")]
        public async Task<IActionResult> GetUserHistoryApi(int? limit = null)
        {
            string? username = null;
            try
            {
                if (ShokoAniSyncPlugin.SyncHistory == null)
                {
                    return Json(new { error = "Sync history not available" });
                }

                // Always use authenticated user - never accept username from query params
                username = GetCurrentShokoUser();
                _logger.LogInformation("Getting history for user from apikey: {Username}", username ?? "null");

                if (string.IsNullOrEmpty(username))
                {
                    _logger.LogWarning("No username found from apikey header");
                    // Fail closed: no resolved user means no data to return.
                    return Unauthorized();
                }

                var userHistory = await ShokoAniSyncPlugin.SyncHistory.GetUserStatsAsync(username);
                if (userHistory == null)
                {
                    return Json(new { 
                        history = new List<object>(),
                        total_syncs = 0,
                        failed_syncs = 0,
                        last_sync = (DateTime?)null
                    });
                }

                var historyEntries = limit.HasValue 
                    ? userHistory.History.Take(limit.Value).ToList()
                    : userHistory.History;

                // Transform history entries to include generated messages
                var transformedEntries = historyEntries.Select((entry, index) => {
                    var syncAction = (SyncAction)entry.Action;
                    // Check if this is the first occurrence of this anime (for "first time" detection)
                    var isFirstTime = index == historyEntries.Count - 1 || 
                        !historyEntries.Skip(index + 1).Any(h => h.AnimeId == entry.AnimeId);
                    
                    return new {
                        timestamp = entry.Timestamp,
                        action = SyncActionHelper.GetActionText(syncAction),
                        anime_id = entry.AnimeId,
                        anime_title = entry.AnimeTitle,
                        anime_image = entry.AnimeImage,
                        episode_number = entry.EpisodeNumber,
                        status = SyncActionHelper.GetStatusString((Status)entry.Status),
                        success = entry.Success,
                        message = entry.Success ? 
                            SyncActionHelper.GetSuccessMessage(syncAction) : 
                            "Sync failed",
                        details = SyncActionHelper.GetDetailsText(syncAction, isFirstTime),
                        provider = entry.Provider
                    };
                }).ToList();

                var response = new {
                    history = transformedEntries,
                    total_syncs = userHistory.TotalSyncs,
                    failed_syncs = userHistory.FailedSyncs,
                    last_sync = userHistory.LastSync
                };
                
                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                };
                
                var jsonString = JsonSerializer.Serialize(response, jsonOptions);
                return Content(jsonString, "application/json");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving user history for {Username}", username);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Serializes an object to camelCase JSON, matching what the frontend expects.
        /// </summary>
        private ContentResult JsonCamel(object payload)
        {
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
            return Content(json, "application/json");
        }

        /// <summary>
        /// Per-user dashboard data. Resolved from the apikey; fails closed (401) if unknown.
        /// </summary>
        [HttpGet]
        [Route("api/dashboard")]
        public IActionResult GetDashboardApi()
        {
            var shokoUsername = GetCurrentShokoUser();
            if (string.IsNullOrEmpty(shokoUsername))
                return Unauthorized();

            var config = _configProvider.Load();
            var userAuth = config.GetAuthForShokoUser(shokoUsername);
            var isAuthenticated = !string.IsNullOrEmpty(userAuth?.AccessToken);

            int syncedAnime = 0;
            DateTime? lastSync = null;
            if (isAuthenticated && ShokoAniSyncPlugin.SyncHistory != null)
            {
                try
                {
                    var userHistory = ShokoAniSyncPlugin.SyncHistory.GetUserStatsAsync(shokoUsername).GetAwaiter().GetResult();
                    if (userHistory != null)
                    {
                        syncedAnime = userHistory.History
                            .Where(h => h.AnimeId.HasValue)
                            .Select(h => h.AnimeId!.Value)
                            .Distinct()
                            .Count();
                        lastSync = userHistory.LastSync;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to get dashboard statistics for {Username}", shokoUsername);
                }
            }

            var accounts = config.GetAuthenticatedUsers()
                .Select(u => u.ShokoUsername)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .ToList();

            return JsonCamel(new
            {
                isAuthenticated,
                isAdmin = IsCurrentUserAdmin(),
                shokoUsername,
                malUsername = userAuth?.Username,
                syncedAnime,
                totalAnime = syncedAnime,
                lastSync,
                pendingUpdates = 0,
                accounts
            });
        }

        /// <summary>
        /// Per-user settings + MAL connection status. Fails closed (401) if the user is unknown.
        /// </summary>
        [HttpGet]
        [Route("api/user/settings")]
        public IActionResult GetUserSettingsApi()
        {
            var shokoUsername = GetCurrentShokoUser();
            if (string.IsNullOrEmpty(shokoUsername))
                return Unauthorized();

            var config = _configProvider.Load();
            var userAuth = config.GetAuthForShokoUser(shokoUsername);
            var isAuthenticated = !string.IsNullOrEmpty(userAuth?.AccessToken);

            return JsonCamel(new
            {
                isAuthenticated,
                isAdmin = IsCurrentUserAdmin(),
                shokoUsername,
                malUsername = userAuth?.Username,
                settings = new
                {
                    updateNsfw = config.GetUpdateNsfw(shokoUsername),
                    enableAutoSync = config.GetEnableAutoSync(shokoUsername),
                    syncOnlyCompleted = config.GetSyncOnlyCompleted(shokoUsername),
                    setStartDateFromAnyEpisode = config.GetSetStartDateFromAnyEpisode(shokoUsername),
                    enableRewatchDetection = config.GetEnableRewatchDetection(shokoUsername),
                    allowRollback = config.GetAllowRollback(shokoUsername),
                    titleMatchThreshold = config.GetTitleMatchThreshold(shokoUsername),
                    useFuzzyMatching = config.GetUseFuzzyMatching(shokoUsername),
                    syncDelaySeconds = config.GetSyncDelaySeconds(shokoUsername),
                    enableDebugLogging = config.GetEnableDebugLogging(shokoUsername)
                }
            });
        }

        /// <summary>
        /// Clears the calling user's own sync history. Operates only on the resolved
        /// caller; never accepts a username from the request.
        /// </summary>
        [HttpDelete]
        [Route("api/history")]
        public async Task<IActionResult> ClearUserHistoryApi()
        {
            try
            {
                var currentUser = GetCurrentShokoUser();
                if (string.IsNullOrEmpty(currentUser))
                {
                    return Unauthorized(new { error = "Authentication required" });
                }

                if (ShokoAniSyncPlugin.SyncHistory == null)
                {
                    return Json(new { error = "Sync history not available" });
                }

                await ShokoAniSyncPlugin.SyncHistory.ClearHistoryAsync(currentUser);
                return Json(new { success = true, message = "History cleared" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing history for current user");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }
        
        private string LoadEmbeddedResource(string resourceName)
        {
            var assembly = typeof(AniSyncController).Assembly;
            var fullResourceName = $"Shoko.AniSync.wwwroot.anisync.{resourceName}";
            
            using var stream = assembly.GetManifestResourceStream(fullResourceName);
            if (stream == null)
            {
                _logger.LogError("Could not find embedded resource: {ResourceName}", fullResourceName);
                return $"<!-- Resource not found: {resourceName} -->";
            }
            
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        
        /// <summary>
        /// Composes a page from the static layout + navbar. No per-user data is rendered
        /// server-side; the navbar and page content populate themselves via authenticated
        /// fetch calls. This is the form used by the static-page + JSON-API design.
        /// </summary>
        private string GetLayout(string title, string content)
        {
            return LoadEmbeddedResource("layout.html")
                .Replace("{TITLE}", title)
                .Replace("{NAVBAR}", LoadEmbeddedResource("navbar-auth.html"))
                .Replace("{CONTENT}", content);
        }

    }
}
