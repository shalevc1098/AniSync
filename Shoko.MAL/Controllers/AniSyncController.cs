using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Shoko.AniSync.Api;
using Shoko.AniSync.Configuration;
using Shoko.AniSync.Interfaces;
using Shoko.AniSync.Models.ViewModels;
using Shoko.Plugin.Abstractions;
using Shoko.Plugin.Abstractions.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Authentication;
using System.Text;
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
        public string BuildAuthorizeRequestUrl(ApiName provider, string state = null)
        {
            return new ApiAuthentication(provider, _httpClientFactory, _loggerFactory, _memoryCache).BuildAuthorizeRequestUrl(state);
        }

        [HttpGet]
        [Route("authCallback")]
        public IActionResult MalCallback(string code, string state = null)
        {
            ApiName provider = ApiName.Mal;
            try
            {
                string shokoUsername = null;
                
                // First try to decode from state parameter
                if (!string.IsNullOrEmpty(state))
                {
                    try
                    {
                        shokoUsername = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(state));
                        _logger.LogDebug("Decoded user from OAuth state: {Username}", shokoUsername);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to decode state parameter");
                    }
                }
                
                // Try to get from session as fallback
                if (string.IsNullOrEmpty(shokoUsername))
                {
                    try
                    {
                        shokoUsername = HttpContext.Session?.GetString("PendingAuthUser");
                        if (!string.IsNullOrEmpty(shokoUsername))
                        {
                            _logger.LogDebug("Retrieved pending auth user from session: {Username}", shokoUsername);
                            HttpContext.Session?.Remove("PendingAuthUser");
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        _logger.LogDebug("Session not configured");
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
                    return Redirect("/anisync/login?error=No+authenticated+user+found");
                }
                
                _logger.LogInformation("Authenticating MAL for Shoko user: {ShokoUsername}", shokoUsername);
                new ApiAuthentication(provider, _httpClientFactory, _loggerFactory, _memoryCache).GetToken(code, shokoUsername: shokoUsername);
                
                // Store the authenticated user in session so we remember who authenticated
                try
                {
                    HttpContext.Session?.SetString("AuthenticatedShokoUser", shokoUsername);
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogWarning(ex, "Session is not configured, cannot persist authenticated user");
                }
                
                return Redirect("/anisync?success=connected");
            }
            catch (AuthenticationException authEx)
            {
                // this is the exception you throw when you get a non‐200 back
                _logger.LogError(authEx, "Failed to retrieve MAL token: {Message}", authEx.Message);
                return Redirect("/anisync/login?error=" + Uri.EscapeDataString(authEx.Message));
            }
            catch (HttpRequestException httpEx)
            {
                // HTTP errors (DNS, timeout, 5xx, etc)
                _logger.LogError(httpEx, "HTTP request to MAL failed");
                return Redirect("/anisync/login?error=Failed+to+connect+to+MyAnimeList");
            }
            catch (Exception ex)
            {
                // anything else
                _logger.LogError(ex, "Unexpected error in auth callback");
                return Redirect("/anisync/login?error=An+unexpected+error+occurred");
            }
        }
        
        // Dashboard
        [HttpGet]
        [Route("")]
        public IActionResult Index()
        {
            var config = Config.GetConfig(_applicationPaths);
            
            // Get current Shoko user
            var shokoUsername = GetCurrentShokoUser();
            
            // If no current user found, check if we have any authenticated users in config
            if (string.IsNullOrEmpty(shokoUsername) && config?.Auths?.Any() == true)
            {
                // Use the first authenticated user as fallback
                shokoUsername = config.Auths.Keys.FirstOrDefault();
                _logger.LogInformation("Using authenticated user from config: {Username}", shokoUsername);
            }
            
            if (string.IsNullOrEmpty(shokoUsername))
            {
                _logger.LogWarning("No authenticated user found");
                shokoUsername = "None";
            }
            
            // Get MAL auth for current Shoko user
            UserApiAuth userAuth = null;
            if (!string.IsNullOrEmpty(shokoUsername) && shokoUsername != "None" && config != null)
            {
                userAuth = config.GetAuthForShokoUser(shokoUsername);
            }
            
            var isAuthenticated = !string.IsNullOrEmpty(userAuth?.AccessToken);
            var malUsername = userAuth?.Username ?? "Not Connected";
            
            var html = GetDashboardHtml(isAuthenticated, malUsername, shokoUsername);
            return Content(html, "text/html");
        }
        
        // Login page
        [HttpGet]
        [Route("login")]
        public IActionResult Login()
        {
            // Get current user to pass to the form
            var currentUser = GetCurrentShokoUser();
            
            // If no current user, try to get from config
            if (string.IsNullOrEmpty(currentUser))
            {
                var config = Config.GetConfig(_applicationPaths);
                if (config?.Auths?.Any() == true)
                {
                    currentUser = config.Auths.Keys.FirstOrDefault();
                }
            }
            
            var html = GetLoginHtml(currentUser);
            return Content(html, "text/html");
        }
        
        // Settings page
        [HttpGet]
        [Route("Settings")]
        public IActionResult Settings()
        {
            var config = Config.GetConfig(_applicationPaths);
            
            // Get current Shoko user
            var shokoUsername = GetCurrentShokoUser();
            if (string.IsNullOrEmpty(shokoUsername))
            {
                _logger.LogWarning("No authenticated user found in HTTP context");
                shokoUsername = "None";
            }
            
            // Get MAL auth for current Shoko user
            UserApiAuth userAuth = null;
            if (!string.IsNullOrEmpty(shokoUsername) && config != null)
            {
                userAuth = config.GetAuthForShokoUser(shokoUsername);
            }
            
            var isAuthenticated = !string.IsNullOrEmpty(userAuth?.AccessToken);
            var malUsername = userAuth?.Username ?? "Not Connected";
            
            var html = GetSettingsHtml(isAuthenticated, malUsername, config);
            return Content(html, "text/html");
        }
        
        [HttpPost]
        [Route("Settings")]
        [Consumes("application/x-www-form-urlencoded")]
        public IActionResult Settings([FromForm] SettingsViewModel model)
        {
            var config = Config.GetConfig(_applicationPaths);
            
            // Update config with new settings
            config.EnableAutoSync = model.EnableAutoSync;
            config.SyncOnlyCompleted = model.SyncOnlyCompleted;
            config.EnableRewatchDetection = model.EnableRewatchDetection;
            config.AllowRollback = model.AllowRollback;
            config.TitleMatchThreshold = model.TitleMatchThreshold;
            config.UseFuzzyMatching = model.UseFuzzyMatching;
            config.SyncDelaySeconds = model.SyncDelaySeconds;
            config.EnableDebugLogging = model.EnableDebugLogging;
            
            config.Save();
            
            // TempData["SuccessMessage"] = "Settings saved successfully!";
            return Redirect("/anisync/settings");
        }
        
        // History page
        [HttpGet]
        [Route("History")]
        public IActionResult History()
        {
            var config = Config.GetConfig(_applicationPaths);
            var currentUser = GetCurrentShokoUser();
            var auth = currentUser != null ? config?.GetAuthForShokoUser(currentUser, ApiName.Mal) : null;
            var isAuthenticated = !string.IsNullOrEmpty(auth?.AccessToken);
            var username = auth?.Username ?? "Guest";
            
            var html = GetHistoryHtml(isAuthenticated, username);
            return Content(html, "text/html");
        }
        
        // OAuth flow initiation
        [HttpGet]
        [Route("Auth")]
        public IActionResult Auth(string username = null)
        {
            // Try to get username from query parameter first (passed from form)
            // This is needed because browser GET requests don't include API key headers
            var currentUser = username;
            
            // If not provided, try to get from API
            if (string.IsNullOrEmpty(currentUser))
            {
                currentUser = GetCurrentShokoUser();
            }
            
            // If still not found, check config for any authenticated users
            if (string.IsNullOrEmpty(currentUser))
            {
                var config = Config.GetConfig(_applicationPaths);
                if (config?.Auths?.Any() == true)
                {
                    // Use the first authenticated Shoko user as fallback
                    currentUser = config.Auths.Keys.FirstOrDefault();
                    if (!string.IsNullOrEmpty(currentUser))
                    {
                        _logger.LogInformation("Using existing Shoko user from config: {Username}", currentUser);
                    }
                }
            }
            
            string state = null;
            if (!string.IsNullOrEmpty(currentUser))
            {
                // Encode the username in the state parameter for OAuth flow
                state = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(currentUser));
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
            }
            else
            {
                _logger.LogError("No user found for OAuth flow. Please specify username parameter.");
                return Redirect("/anisync/login?error=No+user+specified");
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
            
            if (!string.IsNullOrEmpty(currentUser) && config?.Auths != null && config.Auths.ContainsKey(currentUser))
            {
                // Remove the MAL auth for the current user
                if (config.Auths[currentUser].ContainsKey(ApiName.Mal))
                {
                    config.Auths[currentUser].Remove(ApiName.Mal);
                    if (config.Auths[currentUser].Count == 0)
                    {
                        config.Auths.Remove(currentUser);
                    }
                    config.Save();
                }
            }
            
            // Clear session data
            HttpContext.Session.Remove("AuthenticatedShokoUser");
            
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
        
        // Test endpoint to check if we can access Shoko user services
        [HttpGet]
        [Route("test-shoko-user")]
        public IActionResult TestShokoUser()
        {
            var httpContext = _httpContextAccessor?.HttpContext;
            var currentUser = GetCurrentShokoUser();
            
            var result = new
            {
                HasUserDataService = _userDataService != null,
                HasUserService = _userService != null,
                CurrentUser = currentUser?.ToString() ?? "None",
                UserDataServiceType = _userDataService?.GetType().Name ?? "None",
                UserServiceType = _userService?.GetType().Name ?? "None",
                Users = TryGetAllUsers(),
                TestUserData = currentUser != null ? TryGetUserWatchDataTest(currentUser) : "No current user",
                HttpContextInfo = new
                {
                    IsAuthenticated = httpContext?.User?.Identity?.IsAuthenticated ?? false,
                    IdentityName = httpContext?.User?.Identity?.Name ?? "null",
                    AuthenticationType = httpContext?.User?.Identity?.AuthenticationType ?? "null",
                    Claims = httpContext?.User?.Claims?.Select(c => new { c.Type, c.Value }).ToList(),
                    Headers = httpContext?.Request?.Headers?.Select(h => new { h.Key, Value = h.Value.ToString() }).ToList()
                }
            };
            
            return Json(result);
        }
        
        private object TryGetAllUsers()
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
        
        private object GetUserProperty(object user, string propertyName)
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
                    var selected = username == currentUser ? " selected" : "";
                    options.AppendLine($"<option value=\"{username}\"{selected}>{username}</option>");
                }
                return options.ToString();
            }
            catch
            {
                return "<option>Error loading users</option>";
            }
        }
        
        // Removed: Users should not be able to switch Shoko users
        // Authentication is determined by the logged-in HTTP context user
        
        // Helper methods to access Shoko user services
        private string GetCurrentShokoUser()
        {
            try
            {
                var httpContext = _httpContextAccessor?.HttpContext;
                
                // First check if we have a user cached in session
                try
                {
                    var sessionUser = httpContext?.Session?.GetString("AuthenticatedShokoUser");
                    if (!string.IsNullOrEmpty(sessionUser))
                    {
                        _logger.LogDebug("Found cached user from session: {Username}", sessionUser);
                        return sessionUser;
                    }
                }
                catch (InvalidOperationException)
                {
                    // Session is not configured, continue to API call
                    _logger.LogDebug("Session is not configured, fetching from API");
                }
                
                // Get the current user from Shoko API
                var currentUser = GetCurrentUserFromApi();
                if (currentUser != null)
                {
                    _logger.LogDebug("Found authenticated user from Shoko API: {Username}", currentUser);
                    // Cache in session for performance if available
                    try
                    {
                        httpContext?.Session?.SetString("AuthenticatedShokoUser", currentUser);
                    }
                    catch (InvalidOperationException)
                    {
                        // Session not configured, continue without caching
                    }
                    return currentUser;
                }
                
                // No authenticated user found
                _logger.LogDebug("No authenticated user found from Shoko API");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get current Shoko user");
            }
            return null;
        }
        
        private string GetCurrentUserFromApi()
        {
            try
            {
                var httpContext = _httpContextAccessor?.HttpContext;
                if (httpContext == null)
                {
                    _logger.LogDebug("No HTTP context available");
                    return null;
                }
                
                // Get the API key from the request headers
                string apiKey = null;
                if (httpContext.Request.Headers.TryGetValue("apikey", out var apiKeyValues))
                {
                    apiKey = apiKeyValues.FirstOrDefault();
                }
                
                if (string.IsNullOrEmpty(apiKey))
                {
                    _logger.LogDebug("No API key found in request headers");
                    return null;
                }
                
                // Call the Shoko API to get the current user
                using var httpClient = _httpClientFactory.CreateClient();
                httpClient.DefaultRequestHeaders.Add("apikey", apiKey);
                
                // Use the same base URL as the current request
                var baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
                var response = httpClient.GetAsync($"{baseUrl}/api/v3/User/Current").GetAwaiter().GetResult();
                
                if (response.IsSuccessStatusCode)
                {
                    var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    var user = System.Text.Json.JsonSerializer.Deserialize<ShokoUser>(json, new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    return user?.Username;
                }
                else
                {
                    _logger.LogDebug("Failed to get current user from API: {StatusCode}", response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling Shoko API for current user");
            }
            return null;
        }
        
        private object TryGetUserWatchData(int userID, int videoID)
        {
            try
            {
                if (_userDataService != null)
                {
                    return _userDataService.GetVideoUserData(userID, videoID);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get user watch data for user {UserID}, video {VideoID}", userID, videoID);
            }
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
            if (ShokoMalPlugin.SyncHistory == null)
            {
                // Return empty list if sync history is not initialized
                return new List<SyncHistoryEntry>();
            }

            // Get the current Shoko user from session
            string shokoUsername = null;
            try
            {
                shokoUsername = HttpContext?.Session?.GetString("Username");
            }
            catch (InvalidOperationException)
            {
                // Session is not configured, username will be null
                _logger.LogDebug("Session is not configured, getting history for all users");
            }
            
            // Get history asynchronously - for now we'll use GetResult() but this should be refactored to async
            var historyTask = ShokoMalPlugin.SyncHistory.GetHistoryAsync(limit: 100, shokoUsername: shokoUsername);
            var realHistory = historyTask.GetAwaiter().GetResult();
            
            // Convert from our model to the view model
            var viewModelHistory = new List<SyncHistoryEntry>();
            foreach (var entry in realHistory)
            {
                viewModelHistory.Add(new SyncHistoryEntry
                {
                    Timestamp = entry.Timestamp,
                    AnimeName = entry.AnimeName ?? "Unknown",
                    Episode = entry.EpisodeNumber,
                    Action = entry.Action?.ToLower() ?? "unknown",
                    Success = entry.Success,
                    Details = entry.Details ?? entry.ErrorMessage ?? "No details available",
                    ErrorMessage = entry.ErrorMessage
                });
            }
            
            return viewModelHistory;
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
            var dashboardContent = isAuthenticated 
                ? LoadEmbeddedResource("dashboard-auth.html")
                    .Replace("{USERNAME}", username)
                    .Replace("{SHOKO_USER}", shokoUsername)
                    .Replace("{SHOKO_USER_OPTIONS}", GetShokoUserOptions(shokoUsername))
                    .Replace("{TOTAL_ANIME}", "0")
                    .Replace("{SYNCED_ANIME}", "0")
                    .Replace("{LAST_SYNC}", "Never")
                    .Replace("{PENDING}", "0")
                : LoadEmbeddedResource("dashboard-unauth.html");
            
            var content = dashboard.Replace("{DASHBOARD_CONTENT}", dashboardContent);
            return GetLayout("Dashboard", isAuthenticated, username, content);
        }
        
        private string GetLoginHtml(string currentUser = null)
        {
            var content = LoadEmbeddedResource("login.html");
            
            // Add hidden username field if we have a current user
            if (!string.IsNullOrEmpty(currentUser))
            {
                content = content.Replace("<form action='/anisync/auth' method='get'>",
                    $"<form action='/anisync/auth' method='get'>\n        <input type='hidden' name='username' value='{currentUser}' />");
            }
            
            return GetLayout("Login", false, "Guest", content);
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
                    var selected = user.Username == username ? "selected" : "";
                    userOptions.AppendLine($"<option value='{user.Username}' {selected}>👤 {user.Username}</option>");
                }
                
                // Add option to add new account
                userOptions.AppendLine("<option value=''>+ Add Account</option>");
                
                navbar = LoadEmbeddedResource("navbar-auth.html")
                    .Replace("{USERNAME}", username)
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
        
        private string GetSettingsHtml(bool isAuthenticated, string username, Config config)
        {
            var content = LoadEmbeddedResource("settings.html");
            
            // Replace placeholders with actual values
            content = content
                .Replace("{ENABLE_AUTO_SYNC_CHECKED}", (config?.EnableAutoSync ?? true) ? "checked" : "")
                .Replace("{SYNC_ONLY_COMPLETED_CHECKED}", (config?.SyncOnlyCompleted ?? true) ? "checked" : "")
                .Replace("{ENABLE_REWATCH_DETECTION_CHECKED}", (config?.EnableRewatchDetection ?? true) ? "checked" : "")
                .Replace("{ALLOW_ROLLBACK_CHECKED}", (config?.AllowRollback ?? false) ? "checked" : "")
                .Replace("{TITLE_MATCH_THRESHOLD}", (config?.TitleMatchThreshold ?? 0.8).ToString("F1"))
                .Replace("{USE_FUZZY_MATCHING_CHECKED}", (config?.UseFuzzyMatching ?? true) ? "checked" : "")
                .Replace("{SYNC_DELAY_SECONDS}", (config?.SyncDelaySeconds ?? 5).ToString())
                .Replace("{ENABLE_DEBUG_LOGGING_CHECKED}", (config?.EnableDebugLogging ?? false) ? "checked" : "")
                .Replace("{CONNECTION_STATUS}", isAuthenticated ? "Connected" : "Disconnected")
                .Replace("{CONNECTION_STATUS_CLASS}", isAuthenticated ? "status-connected" : "status-disconnected")
                .Replace("{USERNAME}", username)
                .Replace("{LAST_REFRESH}", "Not available");
            
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
                            <div class='entry-title'>{entry.AnimeName}</div>
                            <div class='entry-time'>{timeDisplay}</div>
                        </div>
                        <div class='entry-details'>
                            <div class='entry-detail'>Episode {entry.Episode}</div>
                            <div class='entry-detail'>Action: {entry.Action}</div>
                            <span class='entry-status status-{statusClass.ToLower()}'>{statusText}</span>
                        </div>
                        {(!string.IsNullOrEmpty(entry.Details) ? $"<div class='entry-message'>{entry.Details}</div>" : "")}
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
    }
    
    // Simple model for deserializing the Shoko user response
    internal class ShokoUser
    {
        public int ID { get; set; }
        public string Username { get; set; }
        public bool IsAdmin { get; set; }
    }
}
