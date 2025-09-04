using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Shoko.AniSync.Api;
using Shoko.AniSync.Configuration;
using Shoko.AniSync.Interfaces;
using Shoko.Plugin.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Authentication;
using System.Text;
using System.Threading.Tasks;

namespace Shoko.AniSync.Controllers
{
    [ApiController]
    [ApiVersionNeutral]
    [Route("[controller]")]
    public class AniSyncController : ControllerBase
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IApplicationPaths _applicationPaths;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<AniSyncController> _logger;
        private readonly IMemoryCache _memoryCache;
        private readonly IAsyncDelayer _delayer;

        public AniSyncController(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, IHttpContextAccessor httpContextAccessor, IApplicationPaths applicationPaths, IMemoryCache memoryCache)
        {
            _httpClientFactory = httpClientFactory;
            _httpContextAccessor = httpContextAccessor;
            _applicationPaths = applicationPaths;
            _loggerFactory = loggerFactory;
            _logger = _loggerFactory.CreateLogger<AniSyncController>();
            _memoryCache = memoryCache;
            _delayer = new Delayer();
        }

        [HttpGet]
        [Route("buildAuthorizeRequestUrl")]
        public string BuildAuthorizeRequestUrl(ApiName provider)
        {
            return new ApiAuthentication(provider, _httpClientFactory, _loggerFactory).BuildAuthorizeRequestUrl();
        }

        [HttpGet]
        [Route("authCallback")]
        public IActionResult MalCallback(string code)
        {
            ApiName provider = ApiName.Mal;
            try
            {
                new ApiAuthentication(provider, _httpClientFactory, _loggerFactory).GetToken(code);
                return new ObjectResult("Success! Received access token.") { StatusCode = 200 };

            }
            catch (AuthenticationException authEx)
            {
                // this is the exception you throw when you get a non‐200 back
                _logger.LogError(authEx, "Failed to retrieve MAL token: {Message}", authEx.Message);
                return BadRequest($"Authentication failed: {authEx.Message}");
            }
            catch (HttpRequestException httpEx)
            {
                // HTTP errors (DNS, timeout, 5xx, etc)
                _logger.LogError(httpEx, "HTTP request to MAL failed");
                return StatusCode(502, "Upstream service error");
            }
            catch (Exception ex)
            {
                // anything else
                _logger.LogError(ex, "Unexpected error in auth callback");
                return StatusCode(500, "Internal server error");
            }
        }
    }
}
