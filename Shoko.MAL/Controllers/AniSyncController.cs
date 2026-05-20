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
        public string BuildAuthorizeRequestUrl(ApiName provider, string? state = null, string? baseUrl = null)
        {
            var effectiveBaseUrl = baseUrl ?? ShokoApiBaseUrl;
            return new ApiAuthentication(provider, _httpClientFactory, _loggerFactory, _configProvider, _applicationPaths, _memoryCache, effectiveBaseUrl).BuildAuthorizeRequestUrl(state);
        }

        [HttpGet]
        [Route("authCallback")]
        public IActionResult MalCallback(string code, string? state = null)
        {
            ApiName provider = ApiName.Mal;
            try
            {
                string? shokoUsername = null;
                string? callbackBaseUrl = null;

                // Decode JSON state: { "user": "...", "baseUrl": "..." }
                if (!string.IsNullOrEmpty(state))
                {
                    try
                    {
                        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(state));
                        var stateObj = JsonSerializer.Deserialize<JsonElement>(decoded);
                        if (stateObj.TryGetProperty("user", out var userProp))
                            shokoUsername = userProp.GetString();
                        if (stateObj.TryGetProperty("baseUrl", out var baseUrlProp))
                            callbackBaseUrl = baseUrlProp.GetString();
                        _logger.LogDebug("Decoded OAuth state - user: {Username}, baseUrl: {BaseUrl}", shokoUsername, callbackBaseUrl);
                    }
                    catch (JsonException)
                    {
                        // Legacy plain-text state (just username)
                        try
                        {
                            shokoUsername = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(state));
                            _logger.LogDebug("Decoded legacy OAuth state: {Username}", shokoUsername);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to decode state parameter");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to decode state parameter");
                    }
                }

                // Last resort - try current context
                if (string.IsNullOrEmpty(shokoUsername))
                {
                    shokoUsername = GetCurrentShokoUser();
                }

                if (string.IsNullOrEmpty(shokoUsername))
                {
                    _logger.LogError("Cannot authenticate MAL without a valid Shoko user. State: {State}", state ?? "null");
                    return Redirect("/anisync?error=No+authenticated+user+found");
                }

                var effectiveBaseUrl = callbackBaseUrl ?? ShokoApiBaseUrl;
                _logger.LogInformation("Authenticating MAL for Shoko user: {ShokoUsername} with baseUrl: {BaseUrl}", shokoUsername, effectiveBaseUrl);
                new ApiAuthentication(provider, _httpClientFactory, _loggerFactory, _configProvider, _applicationPaths, _memoryCache, effectiveBaseUrl).GetToken(code, shokoUsername: shokoUsername, state: state);

                return Redirect("/anisync?success=connected");
            }
            catch (AuthenticationException authEx)
            {
                // this is the exception you throw when you get a non‐200 back
                _logger.LogError(authEx, "Failed to retrieve MAL token: {Message}", authEx.Message);
                return Redirect("/anisync?error=" + Uri.EscapeDataString(authEx.Message));
            }
            catch (HttpRequestException httpEx)
            {
                // HTTP errors (DNS, timeout, 5xx, etc)
                _logger.LogError(httpEx, "HTTP request to MAL failed");
                return Redirect("/anisync?error=Failed+to+connect+to+MyAnimeList");
            }
            catch (Exception ex)
            {
                // anything else
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
                
                // Create HTTP client to call Shoko's actual API
                var httpClient = _httpClientFactory.CreateClient();
                httpClient.DefaultRequestHeaders.Add("apikey", apiKey);
                
                // Call Shoko's internal User/Current endpoint
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
        
        // Dashboard
        [HttpGet]
        [Route("")]
        public IActionResult Index()
        {
            // Static page: no per-user data is rendered server-side. The page loads the
            // current user's dashboard client-side from /anisync/api/dashboard (apikey header).
            var html = GetLayout("Dashboard", LoadEmbeddedResource("dashboard.html"));
            return Content(html, "text/html");
        }
        
        
        // Settings page
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
            
            // Get current Shoko user
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
        
        // History page
        [HttpGet]
        [Route("History")]
        public IActionResult History()
        {
            // Static page: no per-user data is rendered server-side. The page loads the
            // current user's history client-side from /anisync/api/history (apikey header).
            var html = GetLayout("Sync History", LoadEmbeddedResource("history.html"));
            return Content(html, "text/html");
        }
        
        // OAuth flow initiation
        [HttpGet]
        [Route("Auth")]
        public IActionResult Auth()
        {
            // Always get current user from Shoko context
            var currentUser = GetCurrentShokoUser();
            
            // If no user found, show error
            if (string.IsNullOrEmpty(currentUser) || currentUser == "None")
            {
                _logger.LogError("No Shoko user found for OAuth flow.");
                return Redirect("/anisync/login?error=Not+logged+into+Shoko");
            }
            
            // Encode username and base URL in the state parameter for OAuth flow
            var stateJson = JsonSerializer.Serialize(new { user = currentUser, baseUrl = ShokoApiBaseUrl });
            var state = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(stateJson));
            _logger.LogInformation("Starting OAuth flow for user: {Username}", currentUser);
            
            var authUrl = BuildAuthorizeRequestUrl(ApiName.Mal, state);
            return Redirect(authUrl);
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
                // Remove the MAL auth for the current user
                if (userConfig?.Providers?.ContainsKey("Mal") == true)
                {
                    userConfig.Providers.Remove("Mal");
                    _configProvider.Save(config);
                }
            }
            
            // Session data is not used - authentication is handled via apikey headers
            
            // TempData["SuccessMessage"] = "Logged out successfully";
            return RedirectToAction("Index");
        }
        
        // Refresh token
        [HttpPost]
        [Route("refresh-token")]
        public async Task<IActionResult> RefreshToken()
        {
            try
            {
                // Get current Shoko user
                var shokoUsername = GetCurrentShokoUser();
                if (string.IsNullOrEmpty(shokoUsername))
                {
                    _logger.LogError("Cannot refresh token without a valid Shoko user");
                    return RedirectToAction("Index");
                }
                
                var auth = new ApiAuthentication(ApiName.Mal, _httpClientFactory, _loggerFactory, _configProvider, _applicationPaths, _memoryCache);
                await auth.RefreshAccessToken(shokoUsername);
                
                // TempData["SuccessMessage"] = "Connection refreshed successfully!";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh token");
                // TempData["ErrorMessage"] = "Failed to refresh connection";
            }
            
            return Redirect("/anisync/settings");
        }
        
        private object? TryGetAllUsers()
        {
            try
            {
                if (_userService != null)
                {
                    var users = _userService.GetUsers();
                    return users?.Take(5).Select(u => new { 
                        ID = GetUserProperty(u, "JMMUserID"), 
                        Username = GetUserProperty(u, "Username") 
                    }).ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get users from UserService");
                return $"Error: {ex.Message}";
            }
            return null;
        }
        
        private object TryGetUserWatchDataTest(object user)
        {
            try
            {
                var userID = GetUserProperty(user, "JMMUserID");
                if (userID is int id)
                {
                    // Test with a dummy video ID - just to see if the method works
                    var watchData = TryGetUserWatchData(id, 1);
                    return watchData != null ? "Found data" : "No data for video ID 1";
                }
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
            return "Could not get user ID";
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
                            // Parse the JSON to get username
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
        
        private object? TryGetUserWatchData(int userID, int videoID)
        {
            // GetVideoUserData now requires IVideo and IUser objects instead of IDs
            // This debug method cannot easily resolve those from int IDs
            _logger.LogDebug("TryGetUserWatchData called with userID={UserID}, videoID={VideoID} - not supported in new API", userID, videoID);
            return null;
        }
        
        private List<SyncActivity> GetRecentActivity()
        {
            // This would be populated from actual sync logs
            return new List<SyncActivity>
            {
                new SyncActivity 
                { 
                    Time = DateTime.Now.AddMinutes(-5).ToString("HH:mm"),
                    AnimeName = "Kusuriya no Hitorigoto",
                    Episode = 12,
                    Success = true
                }
            };
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
        /// API endpoint to get all users' sync statistics
        /// </summary>
        [HttpGet]
        [Route("api/stats")]
        public async Task<IActionResult> GetSyncStatsApi()
        {
            try
            {
                if (ShokoAniSyncPlugin.SyncHistory == null)
                {
                    return Json(new { error = "Sync history not available" });
                }

                var stats = await ShokoAniSyncPlugin.SyncHistory.GetStatsAsync();
                return Json(new {
                    total_syncs = stats.TotalSyncs,
                    successful_syncs = stats.SuccessfulSyncs,
                    failed_syncs = stats.FailedSyncs,
                    success_rate = stats.SuccessRate,
                    last_sync_time = stats.LastSyncTime,
                    syncs_by_user = stats.SyncsByUser,
                    syncs_by_action = stats.SyncsByAction
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving sync statistics");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// API endpoint to clear history for a specific user
        /// </summary>
        [HttpDelete]
        [Route("api/history/{username}")]
        public async Task<IActionResult> ClearUserHistoryApi(string username)
        {
            try
            {
                var currentUser = GetCurrentShokoUser();
                if (string.IsNullOrEmpty(currentUser) || !currentUser.Equals(username, StringComparison.OrdinalIgnoreCase))
                {
                    return Unauthorized(new { error = "You can only clear your own history" });
                }

                if (ShokoAniSyncPlugin.SyncHistory == null)
                {
                    return Json(new { error = "Sync history not available" });
                }

                if (string.IsNullOrEmpty(username))
                {
                    return BadRequest(new { error = "Username is required" });
                }

                await ShokoAniSyncPlugin.SyncHistory.ClearHistoryAsync(username);
                return Json(new { success = true, message = $"History cleared for user {username}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing history for user {Username}", username);
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
