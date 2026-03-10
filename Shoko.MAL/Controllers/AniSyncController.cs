using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Shoko.AniSync.Api;
using Shoko.AniSync.Configuration;
using Shoko.AniSync.Interfaces;
using Shoko.AniSync.Models.ViewModels;
using Shoko.AniSync.Models.Mal;
using Shoko.MAL.Models;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Services;
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
        // Shoko Services - these might need to be optional as they may not be available to plugins
        private readonly IUserDataService _userDataService; // IUserDataService
        private readonly IUserService _userService; // IUserService

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

        public AniSyncController(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, IHttpContextAccessor httpContextAccessor, IApplicationPaths applicationPaths, IMemoryCache memoryCache, IUserDataService userDataService, IUserService userService)
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
        }

        [HttpGet]
        [Route("buildAuthorizeRequestUrl")]
        public string BuildAuthorizeRequestUrl(ApiName provider, string? state = null, string? baseUrl = null)
        {
            var effectiveBaseUrl = baseUrl ?? ShokoApiBaseUrl;
            return new ApiAuthentication(provider, _httpClientFactory, _loggerFactory, _memoryCache, effectiveBaseUrl).BuildAuthorizeRequestUrl(state);
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
                new ApiAuthentication(provider, _httpClientFactory, _loggerFactory, _memoryCache, effectiveBaseUrl).GetToken(code, shokoUsername: shokoUsername, state: state);

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
            
            // Get Shoko username from query or API
            var shokoUsername = GetCurrentShokoUser();
            
            var config = Config.GetConfig(_applicationPaths);
            
            // If we got null, try to use first user from config
            if (string.IsNullOrEmpty(shokoUsername) && config?.Any() == true)
            {
                shokoUsername = config.Keys.FirstOrDefault();
                _logger.LogInformation("Using authenticated user from config: {Username}", shokoUsername);
            }
            
            // Auto-populate user config if user exists but not in config
            if (!string.IsNullOrEmpty(shokoUsername) && config != null && !config.ContainsKey(shokoUsername))
            {
                _logger.LogInformation("Auto-creating config entry for user: {Username}", shokoUsername);
                config[shokoUsername] = new UserConfig
                {
                    Providers = new Dictionary<string, ProviderAuth>(),
                    Settings = UserSettings.CreateWithDefaults(),
                    SelectedProvider = "Mal"
                };
                config.Save();
            }
            
            if (string.IsNullOrEmpty(shokoUsername))
            {
                _logger.LogWarning("No authenticated user found");
                shokoUsername = "None";
            }
            
            // Get MAL auth for current Shoko user
            UserApiAuth? userAuth = null;
            if (!string.IsNullOrEmpty(shokoUsername) && shokoUsername != "None" && config != null)
            {
                userAuth = config.GetAuthForShokoUser(shokoUsername);
            }
            
            var isAuthenticated = !string.IsNullOrEmpty(userAuth?.AccessToken);
            var malUsername = userAuth?.Username ?? "Not Connected";
            
            var html = GetDashboardHtml(isAuthenticated, malUsername, shokoUsername);
            return Content(html, "text/html");
        }
        
        
        // Settings page
        [HttpGet]
        [Route("Settings")]
        public IActionResult Settings()
        {
            _logger.LogInformation("Settings GET request received from IP: {RemoteIpAddress}", 
                Request.HttpContext.Connection.RemoteIpAddress);
            _logger.LogInformation("User-Agent: {UserAgent}", Request.Headers["User-Agent"].ToString());
            _logger.LogInformation("Has apikey header: {HasApiKey}", Request.Headers.ContainsKey("apikey"));
            
            // Get Shoko username from query or API
            var shokoUsername = GetCurrentShokoUser();
            
            var config = Config.GetConfig(_applicationPaths);
            
            // If we got null, try to use first user from config
            if (string.IsNullOrEmpty(shokoUsername) && config?.Any() == true)
            {
                shokoUsername = config.Keys.FirstOrDefault();
                _logger.LogInformation("Using authenticated user from config: {Username}", shokoUsername);
            }
            
            // Auto-populate user config if user exists but not in config
            if (!string.IsNullOrEmpty(shokoUsername) && config != null && !config.ContainsKey(shokoUsername))
            {
                _logger.LogInformation("Auto-creating config entry for user: {Username}", shokoUsername);
                config[shokoUsername] = new UserConfig
                {
                    Providers = new Dictionary<string, ProviderAuth>(),
                    Settings = UserSettings.CreateWithDefaults(),
                    SelectedProvider = "Mal"
                };
                config.Save();
            }
            
            // If we still got null, it means there's no apikey header - show settings auth page
            if (string.IsNullOrEmpty(shokoUsername))
            {
                _logger.LogInformation("No apikey header found, returning settings authentication page");
                var settingsAuthHtml = GetSettingsAuthHtml();
                return Content(settingsAuthHtml, "text/html");
            }
            
            // Get MAL auth for current Shoko user
            UserApiAuth? userAuth = null;
            if (!string.IsNullOrEmpty(shokoUsername) && shokoUsername != "None" && config != null)
            {
                userAuth = config.GetAuthForShokoUser(shokoUsername);
            }
            
            var isAuthenticated = !string.IsNullOrEmpty(userAuth?.AccessToken);
            var malUsername = userAuth?.Username ?? "Not Connected";
            
            // For settings page, we show the form even if MAL is not connected yet
            // The user can configure their sync preferences before connecting to MAL
            
            var html = GetSettingsHtml(isAuthenticated, malUsername, config, shokoUsername ?? "");
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

            var config = Config.GetConfig(_applicationPaths);

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
            // Get Shoko username from query or API
            var shokoUsername = GetCurrentShokoUser();
            
            var config = Config.GetConfig(_applicationPaths);
            
            // If we got null, try to use first user from config
            if (string.IsNullOrEmpty(shokoUsername) && config?.Any() == true)
            {
                shokoUsername = config.Keys.FirstOrDefault();
                _logger.LogInformation("Using authenticated user from config: {Username}", shokoUsername);
            }
            
            // Auto-populate user config if user exists but not in config
            if (!string.IsNullOrEmpty(shokoUsername) && config != null && !config.ContainsKey(shokoUsername))
            {
                _logger.LogInformation("Auto-creating config entry for user: {Username}", shokoUsername);
                config[shokoUsername] = new UserConfig
                {
                    Providers = new Dictionary<string, ProviderAuth>(),
                    Settings = UserSettings.CreateWithDefaults(),
                    SelectedProvider = "Mal"
                };
                config.Save();
            }
            
            if (string.IsNullOrEmpty(shokoUsername))
            {
                _logger.LogWarning("No authenticated user found");
                shokoUsername = "None";
            }
            
            // Get MAL auth for current Shoko user
            UserApiAuth? userAuth = null;
            if (!string.IsNullOrEmpty(shokoUsername) && shokoUsername != "None" && config != null)
            {
                userAuth = config.GetAuthForShokoUser(shokoUsername);
            }
            
            var isAuthenticated = !string.IsNullOrEmpty(userAuth?.AccessToken);
            var malUsername = userAuth?.Username ?? "Not Connected";
            
            var html = GetHistoryHtml(isAuthenticated, malUsername);
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
            
            // Also try to store in session as backup
            try
            {
                HttpContext.Session?.SetString("PendingAuthUser", currentUser);
            }
            catch (InvalidOperationException)
            {
                _logger.LogDebug("Session not configured, relying on state parameter");
            }
            
            var authUrl = BuildAuthorizeRequestUrl(ApiName.Mal, state);
            return Redirect(authUrl);
        }
        
        // Logout
        [HttpPost]
        [Route("Logout")]
        public IActionResult Logout()
        {
            var config = Config.GetConfig(_applicationPaths);
            var currentUser = GetCurrentShokoUser();
            
            if (!string.IsNullOrEmpty(currentUser) && config?.ContainsKey(currentUser) == true)
            {
                var userConfig = config[currentUser];
                // Remove the MAL auth for the current user
                if (userConfig?.Providers?.ContainsKey("Mal") == true)
                {
                    userConfig.Providers.Remove("Mal");
                    config.Save();
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
                
                var auth = new ApiAuthentication(ApiName.Mal, _httpClientFactory, _loggerFactory, _memoryCache);
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
        
        private string GetShokoUserOptions(string currentUser)
        {
            try
            {
                var users = _userService?.GetUsers();
                if (users == null) return "<option>No users found</option>";
                
                var options = new StringBuilder();
                foreach (var user in users)
                {
                    var username = GetUserProperty(user, "Username")?.ToString() ?? "Unknown";
                    var encodedUsername = WebUtility.HtmlEncode(username);
                    var selected = username == currentUser ? " selected" : "";
                    options.AppendLine($"<option value=\"{encodedUsername}\"{selected}>{encodedUsername}</option>");
                }
                return options.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading Shoko users for dropdown");
                return "<option>Error loading users</option>";
            }
        }
        
        // Removed: Users should not be able to switch Shoko users
        // Authentication is determined by the logged-in HTTP context user
        
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
        
        private List<SyncHistoryEntry> GetSyncHistory()
        {
            // Get real sync history from SyncHistoryManager
            if (ShokoAniSyncPlugin.SyncHistory == null)
            {
                // Return empty list if sync history is not initialized
                return new List<SyncHistoryEntry>();
            }

            // Get the current Shoko user
            var shokoUsername = GetCurrentShokoUser();
            if (string.IsNullOrEmpty(shokoUsername))
            {
                _logger.LogDebug("No user found, returning empty history");
                return new List<SyncHistoryEntry>();
            }
            
            // Get user history in new format
            var userHistory = ShokoAniSyncPlugin.SyncHistory.GetUserStatsAsync(shokoUsername).GetAwaiter().GetResult();
            if (userHistory == null)
            {
                return new List<SyncHistoryEntry>();
            }
            
            // Convert from new format to view model
            var viewModelHistory = new List<SyncHistoryEntry>();
            foreach (var entry in userHistory.History.Take(100))
            {
                var syncAction = (SyncAction)entry.Action;
                viewModelHistory.Add(new SyncHistoryEntry
                {
                    Timestamp = entry.Timestamp,
                    AnimeName = entry.AnimeTitle ?? "Unknown",
                    AnimeId = entry.AnimeId,
                    Episode = entry.EpisodeNumber ?? 0,
                    Action = SyncActionHelper.GetActionText(syncAction).ToLower(),
                    Success = entry.Success,
                    Details = SyncActionHelper.GetDetailsText(syncAction) ?? "No details available",
                    ErrorMessage = entry.Success ? null : "Sync failed"
                });
            }
            
            return viewModelHistory;
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

            var gs = GlobalSettings.Load();
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

            var existing = GlobalSettings.Load();

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
            gs.Save();

            bool reAuthRequired = false;
            if (credentialsChanged)
            {
                // Clear all MAL provider auth since tokens are tied to the old app credentials
                var config = Config.GetConfig(_applicationPaths);
                bool anyCleared = false;
                foreach (var kvp in config)
                {
                    if (kvp.Value?.Providers?.ContainsKey("Mal") == true)
                    {
                        kvp.Value.Providers.Remove("Mal");
                        anyCleared = true;
                    }
                }
                if (anyCleared)
                {
                    config.Save();
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
                    // Return empty history instead of error
                    return Json(new { 
                        history = new List<object>(),
                        total_syncs = 0,
                        failed_syncs = 0,
                        last_sync = (DateTime?)null
                    });
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
        
        private string GetDashboardHtml(bool isAuthenticated, string username, string shokoUsername = "None")
        {
            var dashboard = LoadEmbeddedResource("dashboard.html");
            
            // Get actual statistics for the dashboard
            string totalAnime = "0";
            string syncedAnime = "0";
            string lastSync = "Never";
            string pending = "0";
            
            if (isAuthenticated && ShokoAniSyncPlugin.SyncHistory != null)
            {
                try
                {
                    // Get sync history stats for this Shoko user
                    var userHistory = ShokoAniSyncPlugin.SyncHistory.GetUserStatsAsync(shokoUsername).Result;
                    if (userHistory != null)
                    {
                        // Get unique anime count from history
                        var uniqueAnimeIds = userHistory.History
                            .Where(h => h.AnimeId.HasValue)
                            .Select(h => h.AnimeId!.Value)
                            .Distinct()
                            .Count();
                        
                        syncedAnime = uniqueAnimeIds.ToString();
                        
                        // Format last sync date
                        if (userHistory.LastSync.HasValue)
                        {
                            var timeDiff = DateTime.Now - userHistory.LastSync.Value;
                            if (timeDiff.TotalMinutes < 1)
                                lastSync = "Just now";
                            else if (timeDiff.TotalHours < 1)
                                lastSync = $"{(int)timeDiff.TotalMinutes}m ago";
                            else if (timeDiff.TotalDays < 1)
                                lastSync = $"{(int)timeDiff.TotalHours}h ago";
                            else if (timeDiff.TotalDays < 7)
                                lastSync = $"{(int)timeDiff.TotalDays}d ago";
                            else
                                lastSync = userHistory.LastSync.Value.ToString("MMM dd, yyyy");
                        }
                    }
                    
                    // TODO: Get total anime count from Shoko API if available
                    // For now, we'll show the synced count as total if we don't have the real total
                    totalAnime = syncedAnime;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to get dashboard statistics");
                }
            }
            
            var dashboardContent = isAuthenticated
                ? LoadEmbeddedResource("dashboard-auth.html")
                    .Replace("{USERNAME}", WebUtility.HtmlEncode(username))
                    .Replace("{SHOKO_USER}", WebUtility.HtmlEncode(shokoUsername))
                    .Replace("{SHOKO_USER_OPTIONS}", GetShokoUserOptions(shokoUsername))
                    .Replace("{TOTAL_ANIME}", totalAnime)
                    .Replace("{SYNCED_ANIME}", syncedAnime)
                    .Replace("{LAST_SYNC}", lastSync)
                    .Replace("{PENDING}", pending)
                : LoadEmbeddedResource("dashboard-unauth.html");
            
            var content = dashboard.Replace("{DASHBOARD_CONTENT}", dashboardContent);
            return GetLayout("Dashboard", isAuthenticated, username, content);
        }
        
        private string GetLayout(string title, bool isAuthenticated, string username, string content)
        {
            var layout = LoadEmbeddedResource("layout.html");
            
            string navbar;
            if (isAuthenticated)
            {
                var config = Config.GetConfig(_applicationPaths);
                var users = config.GetAuthenticatedUsers();
                var userOptions = new StringBuilder();
                
                // Build user options for dropdown
                foreach (var user in users)
                {
                    var encodedName = WebUtility.HtmlEncode(user.Username);
                    var selected = user.Username == username ? "selected" : "";
                    userOptions.AppendLine($"<option value='{encodedName}' {selected}>{encodedName}</option>");
                }
                
                // Add option to add new account
                userOptions.AppendLine("<option value=''>+ Add Account</option>");
                
                // Get current Shoko user for navbar display
                var shokoUsername = GetCurrentShokoUser() ?? "Unknown User";
                var shokoUsernameInitial = shokoUsername.Length > 0 ? shokoUsername.Substring(0, 1).ToUpper() : "U";
                
                navbar = LoadEmbeddedResource("navbar-auth.html")
                    .Replace("{USERNAME}", WebUtility.HtmlEncode(username))
                    .Replace("{SHOKO_USERNAME}", WebUtility.HtmlEncode(shokoUsername))
                    .Replace("{SHOKO_USERNAME_INITIAL}", WebUtility.HtmlEncode(shokoUsernameInitial))
                    .Replace("{USER_OPTIONS}", userOptions.ToString());
            }
            else
            {
                navbar = LoadEmbeddedResource("navbar-unauth.html");
            }
            
            return layout
                .Replace("{TITLE}", title)
                .Replace("{NAVBAR}", navbar)
                .Replace("{CONTENT}", content);
        }
        
        private string GetSettingsHtml(bool isAuthenticated, string username, Config? config, string shokoUsername)
        {
            var content = LoadEmbeddedResource("settings.html");
            
            _logger.LogInformation("GetSettingsHtml called with shokoUsername: {ShokoUsername}", shokoUsername);
            _logger.LogInformation("Config keys: {ConfigKeys}", config != null ? string.Join(", ", config.Keys) : "null");
            
            // Get user-specific settings if available
            bool updateNsfw = config?.GetUpdateNsfw(shokoUsername) ?? false;
            
            _logger.LogInformation("Retrieved updateNsfw: {UpdateNsfw} for user: {ShokoUsername}", updateNsfw, shokoUsername);
            bool enableAutoSync = config?.GetEnableAutoSync(shokoUsername) ?? true;
            bool syncOnlyCompleted = config?.GetSyncOnlyCompleted(shokoUsername) ?? true;
            bool setStartDateFromAnyEpisode = config?.GetSetStartDateFromAnyEpisode(shokoUsername) ?? false;
            bool enableRewatchDetection = config?.GetEnableRewatchDetection(shokoUsername) ?? true;
            bool allowRollback = config?.GetAllowRollback(shokoUsername) ?? false;
            double titleMatchThreshold = config?.GetTitleMatchThreshold(shokoUsername) ?? 0.8;
            bool useFuzzyMatching = config?.GetUseFuzzyMatching(shokoUsername) ?? true;
            int syncDelaySeconds = config?.GetSyncDelaySeconds(shokoUsername) ?? 5;
            bool enableDebugLogging = config?.GetEnableDebugLogging(shokoUsername) ?? false;
            
            // Check if user has custom settings (from the new config structure) 
            bool hasUserSettings = config != null && config.ContainsKey(shokoUsername) && config[shokoUsername]?.Settings != null;
            
            // Replace placeholders with actual values
            content = content
                .Replace("{UPDATE_NSFW_CHECKED}", updateNsfw ? "checked" : "")
                .Replace("{ENABLE_AUTO_SYNC_CHECKED}", enableAutoSync ? "checked" : "")
                .Replace("{SYNC_ONLY_COMPLETED_CHECKED}", syncOnlyCompleted ? "checked" : "")
                .Replace("{SET_START_DATE_FROM_ANY_EPISODE_CHECKED}", setStartDateFromAnyEpisode ? "checked" : "")
                .Replace("{ENABLE_REWATCH_DETECTION_CHECKED}", enableRewatchDetection ? "checked" : "")
                .Replace("{ALLOW_ROLLBACK_CHECKED}", allowRollback ? "checked" : "")
                .Replace("{TITLE_MATCH_THRESHOLD}", titleMatchThreshold.ToString("F1"))
                .Replace("{USE_FUZZY_MATCHING_CHECKED}", useFuzzyMatching ? "checked" : "")
                .Replace("{SYNC_DELAY_SECONDS}", syncDelaySeconds.ToString())
                .Replace("{ENABLE_DEBUG_LOGGING_CHECKED}", enableDebugLogging ? "checked" : "")
                .Replace("{CONNECTION_STATUS}", isAuthenticated ? "Connected" : "Not Connected")
                .Replace("{CONNECTION_STATUS_CLASS}", isAuthenticated ? "status-connected" : "status-disconnected")
                .Replace("{USERNAME}", isAuthenticated ? WebUtility.HtmlEncode(username) : "Connect to enable syncing")
                .Replace("{LAST_REFRESH}", isAuthenticated ? "Not available" : "Connect to MAL first")
                .Replace("{USER_SETTINGS_INFO}", 
                    isAuthenticated && hasUserSettings ? 
                        "<div class=\"alert alert-success\"><i class=\"fas fa-check-circle\" style=\"margin-right: 8px;\"></i>Connected to MyAnimeList and using your personalized settings</div>" :
                    hasUserSettings ?
                        "<div class=\"alert alert-info\"><i class=\"fas fa-cog\" style=\"margin-right: 8px;\"></i>Settings configured for your account. <a href=\"/anisync\">Connect to MyAnimeList</a> to enable syncing.</div>" :
                        "<div class=\"alert alert-warning\"><i class=\"fas fa-info-circle\" style=\"margin-right: 8px;\"></i>Configure your sync preferences below. <a href=\"/anisync\">Connect to MyAnimeList</a> to enable syncing.</div>");
            
            return GetLayout("Settings", isAuthenticated, username, content);
        }
        
        private string GetHistoryHtml(bool isAuthenticated, string username)
        {
            var content = LoadEmbeddedResource("history.html");
            
            var syncHistory = GetSyncHistory();
            var totalSyncs = syncHistory.Count;
            var failedSyncs = syncHistory.Count(s => !s.Success);
            var successRate = totalSyncs > 0 ? ((totalSyncs - failedSyncs) * 100 / totalSyncs) : 0;
            var lastSync = syncHistory.FirstOrDefault()?.Timestamp.ToString("MMM dd, HH:mm") ?? "Never";
            
            // Build history entries HTML
            var entriesHtml = new StringBuilder();
            foreach (var entry in syncHistory)
            {
                var statusClass = entry.Success ? "success" : "failed";
                var statusText = entry.Success ? "Success" : "Failed";
                var timeDisplay = entry.Timestamp.Date == DateTime.Today 
                    ? $"Today, {entry.Timestamp:HH:mm}" 
                    : entry.Timestamp.ToString("MMM dd, HH:mm");
                
                entriesHtml.AppendLine($@"
                    <div class='history-entry {statusClass}'>
                        <div class='entry-header'>
                            <div class='entry-title'>{WebUtility.HtmlEncode(entry.AnimeName)}</div>
                            <div class='entry-time'>{WebUtility.HtmlEncode(timeDisplay)}</div>
                        </div>
                        <div class='entry-details'>
                            <div class='entry-detail'>Episode {entry.Episode}</div>
                            <div class='entry-detail'>Action: {WebUtility.HtmlEncode(entry.Action)}</div>
                            <span class='entry-status status-{statusClass.ToLower()}'>{statusText}</span>
                        </div>
                        {(!string.IsNullOrEmpty(entry.Details) ? $"<div class='entry-message'>{WebUtility.HtmlEncode(entry.Details)}</div>" : "")}
                    </div>");
            }
            
            if (syncHistory.Count == 0)
            {
                entriesHtml.AppendLine(@"
                    <div class='history-entry'>
                        <p style='text-align: center; color: #888; padding: 20px;'>No sync history available yet.</p>
                    </div>");
            }
            
            content = content
                .Replace("{TOTAL_SYNCS}", totalSyncs.ToString())
                .Replace("{FAILED_SYNCS}", failedSyncs.ToString())
                .Replace("{SUCCESS_RATE}", successRate.ToString())
                .Replace("{LAST_SYNC}", lastSync)
                .Replace("{HISTORY_ENTRIES}", entriesHtml.ToString());
            
            return GetLayout("History", isAuthenticated, username, content);
        }
        
        private string GetSettingsAuthHtml()
        {
            var content = @"<div class='alert alert-info'>
    <i class='fas fa-info-circle' style='margin-right: 8px;'></i>Loading settings...
</div>

<script>
document.addEventListener('DOMContentLoaded', async function() {
    const apiSessionStr = localStorage.getItem('apiSession');
    
    if (!apiSessionStr) {
        document.querySelector('.alert-info').innerHTML = 
            '<i class=""fas fa-exclamation-triangle"" style=""margin-right: 8px;""></i>You must log in to Shoko Server first. <a href=""/"">Go to main page to log in</a>';
        return;
    }
    
    let apiSession;
    try {
        apiSession = JSON.parse(apiSessionStr);
        
        if (apiSession && apiSession.apikey) {
            const response = await fetch('/anisync/settings', {
                headers: { 'apikey': apiSession.apikey }
            });
            
            if (response.ok) {
                const settingsHtml = await response.text();
                document.body.innerHTML = settingsHtml;
                
                // Re-initialize the Save Settings button after content is replaced
                setTimeout(() => {
                    const saveBtn = document.getElementById('saveSettingsBtn');
                    if (saveBtn) {
                        console.log('Re-attaching Save Settings button handler...');
                        saveBtn.addEventListener('click', async function(e) {
                            e.preventDefault();
                            console.log('=== SAVING SETTINGS ===');
                            
                            const settings = {
                                UpdateNsfw: document.getElementById('UpdateNsfw').checked,
                                EnableAutoSync: document.getElementById('EnableAutoSync').checked,
                                SyncOnlyCompleted: document.getElementById('SyncOnlyCompleted').checked,
                                EnableRewatchDetection: document.getElementById('EnableRewatchDetection').checked,
                                AllowRollback: document.getElementById('AllowRollback').checked,
                                TitleMatchThreshold: parseFloat(document.getElementById('TitleMatchThreshold').value) || 0.8,
                                UseFuzzyMatching: document.getElementById('UseFuzzyMatching').checked,
                                SyncDelaySeconds: parseInt(document.getElementById('SyncDelaySeconds').value) || 5,
                                EnableDebugLogging: document.getElementById('EnableDebugLogging').checked
                            };
                            
                            console.log('Settings object:', settings);
                            
                            const headers = { 'Content-Type': 'application/json' };
                            const apiSessionStr = localStorage.getItem('apiSession');
                            if (apiSessionStr) {
                                try {
                                    const apiSession = JSON.parse(apiSessionStr);
                                    if (apiSession && apiSession.apikey) {
                                        headers['apikey'] = apiSession.apikey;
                                        console.log('Added apikey header');
                                    }
                                } catch (e) {
                                    console.error('Failed to parse apiSession:', e);
                                }
                            }
                            
                            try {
                                const response = await fetch('/anisync/settings', {
                                    method: 'POST',
                                    headers: headers,
                                    body: JSON.stringify(settings)
                                });
                                
                                console.log('Response status:', response.status);
                                if (response.ok) {
                                    console.log('SUCCESS! Redirecting...');
                                    window.location.href = '/anisync/settings';
                                } else {
                                    const responseText = await response.text();
                                    console.error('Server response:', responseText);
                                    alert('Failed to save settings: ' + response.status + ' - ' + responseText);
                                }
                            } catch (error) {
                                console.error('Error saving settings:', error);
                                alert('Error saving settings: ' + error.message);
                            }
                        });
                        
                        // Also re-attach the refresh token button handler
                        const refreshBtn = document.getElementById('refreshTokenBtn');
                        if (refreshBtn) {
                            console.log('Re-attaching Refresh Token button handler...');
                            refreshBtn.addEventListener('click', async function(e) {
                                e.preventDefault();
                                console.log('=== REFRESHING TOKEN ===');
                                
                                const headers = {};
                                const apiSessionStr = localStorage.getItem('apiSession');
                                if (apiSessionStr) {
                                    try {
                                        const apiSession = JSON.parse(apiSessionStr);
                                        if (apiSession && apiSession.apikey) {
                                            headers['apikey'] = apiSession.apikey;
                                            console.log('Added apikey header for refresh');
                                        }
                                    } catch (e) {
                                        console.error('Failed to parse apiSession:', e);
                                    }
                                }
                                
                                try {
                                    const response = await fetch('/anisync/refresh-token', {
                                        method: 'POST',
                                        headers: headers
                                    });
                                    
                                    console.log('Refresh response status:', response.status);
                                    if (response.ok) {
                                        console.log('Token refreshed successfully!');
                                        window.location.href = '/anisync/settings';
                                    } else {
                                        const responseText = await response.text();
                                        console.error('Refresh failed:', responseText);
                                        alert('Failed to refresh connection: ' + response.status);
                                    }
                                } catch (error) {
                                    console.error('Error refreshing token:', error);
                                    alert('Error refreshing connection: ' + error.message);
                                }
                            });
                        }
                        
                        // Also re-attach the logout button handler
                        const logoutBtn = document.getElementById('logoutBtn');
                        if (logoutBtn) {
                            console.log('Re-attaching Logout button handler...');
                            logoutBtn.addEventListener('click', async function(e) {
                                e.preventDefault();
                                console.log('=== LOGGING OUT ===');
                                
                                if (confirm('Are you sure you want to disconnect from MyAnimeList?')) {
                                    const headers = {};
                                    const apiSessionStr = localStorage.getItem('apiSession');
                                    if (apiSessionStr) {
                                        try {
                                            const apiSession = JSON.parse(apiSessionStr);
                                            if (apiSession && apiSession.apikey) {
                                                headers['apikey'] = apiSession.apikey;
                                            }
                                        } catch (e) {
                                            console.error('Failed to parse apiSession:', e);
                                        }
                                    }
                                    
                                    try {
                                        const response = await fetch('/anisync/Logout', {
                                            method: 'POST',
                                            headers: headers
                                        });
                                        
                                        console.log('Logout response status:', response.status);
                                        if (response.ok) {
                                            console.log('Logged out successfully!');
                                            window.location.href = '/anisync';
                                        } else {
                                            const responseText = await response.text();
                                            console.error('Logout failed:', responseText);
                                            alert('Failed to logout: ' + response.status);
                                        }
                                    } catch (error) {
                                        console.error('Error during logout:', error);
                                        alert('Error during logout: ' + error.message);
                                    }
                                }
                            });
                        }
                    } else {
                        console.error('Save Settings button not found after content replacement!');
                    }
                }, 100);
            } else {
                throw new Error('Failed to load settings');
            }
        } else {
            throw new Error('No API key in session');
        }
    } catch (e) {
        console.error('Session validation failed:', e);
        document.querySelector('.alert-info').innerHTML = 
            '<i class=""fas fa-exclamation-triangle"" style=""margin-right: 8px;""></i>Your session has expired. <a href=""/"">Please log in again</a>';
    }
});
</script>";
            
            return GetLayout("Settings", false, "Loading...", content);
        }
    }
}
