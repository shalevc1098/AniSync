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
            var shokoUsername = GetCurrentShokoUser();
            if (string.IsNullOrEmpty(shokoUsername))
                return Unauthorized();

            var requestHost = _httpContextAccessor?.HttpContext?.Request?.Host.Host;
            var effectiveBaseUrl = IsAllowedBaseUrl(baseUrl, requestHost) ? baseUrl!.TrimEnd('/') : ShokoApiBaseUrl;

            var state = SignState(shokoUsername, effectiveBaseUrl, provider);
            var url = new ApiAuthentication(provider, _httpClientFactory, _loggerFactory, _configProvider, _applicationPaths, _memoryCache, effectiveBaseUrl).BuildAuthorizeRequestUrl(state);
            return Content(url, "text/plain");
        }

        /// <summary>HMAC-signs an OAuth state binding the authorized Shoko user + baseUrl + provider, with a 10-minute expiry.</summary>
        private string SignState(string user, string baseUrl, ApiName provider)
        {
            var payload = JsonSerializer.Serialize(new
            {
                u = user,
                b = baseUrl,
                p = provider.ToString(),
                exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()
            });
            var payloadBytes = System.Text.Encoding.UTF8.GetBytes(payload);
            var key = GetStateSigningKey();
            using var hmac = new System.Security.Cryptography.HMACSHA256(key);
            return Base64Url(payloadBytes) + "." + Base64Url(hmac.ComputeHash(payloadBytes));
        }

        /// <summary>Verifies a signed OAuth state (signature + expiry) and extracts the bound user/baseUrl/provider.</summary>
        private bool TryVerifyState(string state, out string? user, out string? baseUrl, out ApiName provider)
        {
            user = null;
            baseUrl = null;
            provider = ApiName.Mal;
            try
            {
                var parts = state.Split('.');
                if (parts.Length != 2) return false;

                var payloadBytes = Base64UrlDecode(parts[0]);
                var providedSig = Base64UrlDecode(parts[1]);
                var key = GetStateSigningKey();
                using var hmac = new System.Security.Cryptography.HMACSHA256(key);
                var expectedSig = hmac.ComputeHash(payloadBytes);
                if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(providedSig, expectedSig))
                    return false;

                var obj = JsonSerializer.Deserialize<JsonElement>(System.Text.Encoding.UTF8.GetString(payloadBytes));
                var exp = obj.TryGetProperty("exp", out var expEl) ? expEl.GetInt64() : 0;
                if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) return false;

                user = obj.TryGetProperty("u", out var uEl) ? uEl.GetString() : null;
                baseUrl = obj.TryGetProperty("b", out var bEl) ? bEl.GetString() : null;
                if (obj.TryGetProperty("p", out var pEl) && Enum.TryParse<ApiName>(pEl.GetString(), out var parsed))
                    provider = parsed;
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
            return uri.Host == requestHost;
        }

        [HttpGet]
        [Route("authCallback")]
        public IActionResult MalCallback(string code, string? state = null)
        {
            try
            {
                if (string.IsNullOrEmpty(state) || !TryVerifyState(state, out var shokoUsername, out var callbackBaseUrl, out var provider) || string.IsNullOrEmpty(shokoUsername))
                {
                    _logger.LogWarning("OAuth callback rejected: missing or invalid signed state");
                    return Redirect("/anisync?error=invalid_state");
                }

                var effectiveBaseUrl = string.IsNullOrEmpty(callbackBaseUrl) ? ShokoApiBaseUrl : callbackBaseUrl;
                _logger.LogInformation("Authenticating {Provider} for Shoko user: {ShokoUsername} with baseUrl: {BaseUrl}", provider, shokoUsername, effectiveBaseUrl);
                new ApiAuthentication(provider, _httpClientFactory, _loggerFactory, _configProvider, _applicationPaths, _memoryCache, effectiveBaseUrl).GetToken(code, shokoUsername: shokoUsername, state: state);

                return Redirect("/anisync?success=connected");
            }
            catch (AuthenticationException authEx)
            {
                _logger.LogError(authEx, "Failed to retrieve OAuth token: {Message}", authEx.Message);
                return Redirect("/anisync?error=" + Uri.EscapeDataString(authEx.Message));
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogError(httpEx, "OAuth token request to provider failed");
                return Redirect("/anisync?error=Failed+to+connect+to+provider");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in auth callback");
                return Redirect("/anisync?error=An+unexpected+error+occurred");
            }
        }
        
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

            _logger.LogInformation("Saving settings for user: {Username}", shokoUsername);

            lock (ConfigGate.Lock)
            {
                var config = _configProvider.Load();
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
                _configProvider.Save(config);
            }

            _logger.LogInformation("Settings saved successfully for user: {Username}", shokoUsername);

            return Json(new { success = true });
        }

        [HttpPost]
        [Route("Logout")]
        public IActionResult Logout(string provider = "Mal")
        {
            var currentUser = GetCurrentShokoUser();
            if (string.IsNullOrEmpty(currentUser))
                return Unauthorized(new { error = "Authentication required" });
            if (!Helpers.ProviderApiFactory.TryParseProvider(provider, out var providerEnum))
                return BadRequest(new { error = "Invalid provider" });

            lock (ConfigGate.Lock)
            {
                var config = _configProvider.Load();
                if (config?.Users.TryGetValue(currentUser, out var userConfig) == true
                    && userConfig?.Providers?.Remove(providerEnum.ToString()) == true)
                {
                    _configProvider.Save(config);
                    _logger.LogInformation("Disconnected {Provider} for Shoko user {User}", providerEnum, currentUser);
                }
            }
            return Json(new { success = true });
        }

        [HttpPost]
        [Route("refresh-token")]
        public async Task<IActionResult> RefreshToken(string provider = "Mal")
        {
            var shokoUsername = GetCurrentShokoUser();
            if (string.IsNullOrEmpty(shokoUsername))
                return Unauthorized(new { error = "Authentication required" });
            if (!Helpers.ProviderApiFactory.TryParseProvider(provider, out var providerEnum))
                return BadRequest(new { error = "Invalid provider" });

            try
            {
                var auth = new ApiAuthentication(providerEnum, _httpClientFactory, _loggerFactory, _configProvider, _applicationPaths, _memoryCache);
                await auth.RefreshAccessToken(shokoUsername);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh {Provider} token", providerEnum);
                return Json(new { success = false });
            }
        }
        
        private byte[] GetStateSigningKey()
        {
            lock (ConfigGate.Lock)
            {
                var config = _configProvider.Load();
                if (string.IsNullOrEmpty(config.StateSigningKey))
                {
                    config.StateSigningKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                    _configProvider.Save(config);
                }
                return Convert.FromBase64String(config.StateSigningKey);
            }
        }

        /// <summary>True if the resolved caller is a Shoko admin. Used to gate global settings.</summary>
        private bool IsCurrentUserAdmin()
        {
            var username = GetCurrentShokoUser();
            if (string.IsNullOrEmpty(username) || _userService == null) return false;
            try
            {
                return _userService.GetUserByUsername(username)?.IsAdmin == true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to determine admin status for {Username}", username);
                return false;
            }
        }
        
        private const string ShokoUserCacheKey = "__AniSync_CurrentShokoUser";
        private string? GetCurrentShokoUser()
        {
            var httpContext = _httpContextAccessor?.HttpContext;

            if (httpContext?.Items.TryGetValue(ShokoUserCacheKey, out var cached) == true)
            {
                return cached as string;
            }

            string? result = null;
            try
            {
                if (httpContext != null)
                    result = _userService?.GetUserFromHttpContext(httpContext)?.Username;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve current Shoko user");
            }

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

            if (!IsCurrentUserAdmin())
            {
                return StatusCode(403, new { error = "Only an administrator can view API credentials" });
            }

            var config = _configProvider.Load();
            return Json(new
            {
                malClientId = config?.MalClientId ?? string.Empty,
                malClientSecret = config?.MalClientSecret ?? string.Empty,
                aniListClientId = config?.AniListClientId ?? string.Empty,
                aniListClientSecret = config?.AniListClientSecret ?? string.Empty
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

            if (!IsCurrentUserAdmin())
            {
                return StatusCode(403, new { error = "Only an administrator can change API credentials" });
            }

            if (request == null)
                return BadRequest(new { error = "Invalid request body" });

            var malSecret = request.MalClientSecret;
            var aniSecret = request.AniListClientSecret;
            bool reAuthRequired = false;

            lock (ConfigGate.Lock)
            {
                var config = _configProvider.Load();

                bool malChanged = (!string.IsNullOrEmpty(config.MalClientId) || !string.IsNullOrEmpty(config.MalClientSecret)) &&
                    (config.MalClientId != request.MalClientId || config.MalClientSecret != malSecret);
                bool aniChanged = (!string.IsNullOrEmpty(config.AniListClientId) || !string.IsNullOrEmpty(config.AniListClientSecret)) &&
                    (config.AniListClientId != request.AniListClientId || config.AniListClientSecret != aniSecret);

                config.MalClientId = request.MalClientId;
                config.MalClientSecret = malSecret;
                config.AniListClientId = request.AniListClientId;
                config.AniListClientSecret = aniSecret;

                var toClear = new List<string>();
                if (malChanged) toClear.Add("Mal");
                if (aniChanged) toClear.Add("AniList");
                foreach (var kvp in config.Users)
                {
                    foreach (var name in toClear)
                    {
                        if (kvp.Value?.Providers?.Remove(name) == true) reAuthRequired = true;
                    }
                }

                _configProvider.Save(config);
                if (reAuthRequired)
                    _logger.LogInformation("Cleared auth for providers [{Providers}] due to credential change", string.Join(", ", toClear));
            }

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

                username = GetCurrentShokoUser();
                _logger.LogInformation("Getting history for user from apikey: {Username}", username ?? "null");

                if (string.IsNullOrEmpty(username))
                {
                    _logger.LogWarning("No username found from apikey header");
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

                var transformedEntries = historyEntries.Select((entry, index) => {
                    var syncAction = (SyncAction)entry.Action;
                    var isFirstTime = index == historyEntries.Count - 1 ||
                        !historyEntries.Skip(index + 1).Any(h => h.AnimeId == entry.AnimeId);
                    
                    return new {
                        event_id = entry.EventId,
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
        public async Task<IActionResult> GetDashboardApi()
        {
            var shokoUsername = GetCurrentShokoUser();
            if (string.IsNullOrEmpty(shokoUsername))
                return Unauthorized();

            var config = _configProvider.Load();
            var isAuthenticated = config.GetConnectedProviders(shokoUsername).Count > 0;

            int syncedAnime = 0;
            DateTime? lastSync = null;
            if (isAuthenticated && ShokoAniSyncPlugin.SyncHistory != null)
            {
                try
                {
                    var userHistory = await ShokoAniSyncPlugin.SyncHistory.GetUserStatsAsync(shokoUsername);
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

            return JsonCamel(new
            {
                isAuthenticated,
                isAdmin = IsCurrentUserAdmin(),
                shokoUsername,
                providers = BuildProviderStatus(config, shokoUsername),
                syncedAnime,
                totalAnime = syncedAnime,
                lastSync,
                pendingUpdates = 0
            });
        }

        /// <summary>Per-provider connection status for the given user (used by the dashboard/settings UI).</summary>
        private object BuildProviderStatus(Config config, string shokoUsername)
        {
            var mal = config.GetAuthForShokoUser(shokoUsername, ApiName.Mal);
            var ani = config.GetAuthForShokoUser(shokoUsername, ApiName.AniList);
            return new
            {
                mal = new { connected = !string.IsNullOrEmpty(mal?.AccessToken), username = mal?.Username, configured = !string.IsNullOrEmpty(config.MalClientId) },
                aniList = new { connected = !string.IsNullOrEmpty(ani?.AccessToken), username = ani?.Username, configured = !string.IsNullOrEmpty(config.AniListClientId) }
            };
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
            var isAuthenticated = config.GetConnectedProviders(shokoUsername).Count > 0;

            return JsonCamel(new
            {
                isAuthenticated,
                isAdmin = IsCurrentUserAdmin(),
                shokoUsername,
                providers = BuildProviderStatus(config, shokoUsername),
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
        
        [HttpGet]
        [Route("{**path}")]
        public IActionResult Spa(string? path = null)
        {
            if (!string.IsNullOrEmpty(path))
            {
                var asset = GetEmbeddedAsset(path);
                if (asset != null)
                    return File(asset, ContentTypeFor(path));
            }

            var index = GetEmbeddedAsset("index.html");
            if (index == null) return NotFound();
            return File(index, "text/html");
        }

        private static byte[]? GetEmbeddedAsset(string relativePath)
        {
            var assembly = typeof(AniSyncController).Assembly;
            using var stream = assembly.GetManifestResourceStream($"app/{relativePath}");
            if (stream == null) return null;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        private static string ContentTypeFor(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".html" => "text/html",
                ".js" => "text/javascript",
                ".css" => "text/css",
                ".svg" => "image/svg+xml",
                ".woff2" => "font/woff2",
                ".woff" => "font/woff",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".ico" => "image/x-icon",
                ".json" => "application/json",
                ".webmanifest" => "application/manifest+json",
                _ => "application/octet-stream"
            };
        }
    }
}
