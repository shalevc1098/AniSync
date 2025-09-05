using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Versioning;
using Shoko.AniSync.Configuration;
using Shoko.Plugin.Abstractions;

namespace Shoko.AniSync.Controllers
{
    [Route("razor")]
    [ApiVersionNeutral]
    public class RazorController : Controller
    {
        private readonly IApplicationPaths _applicationPaths;

        public RazorController(IApplicationPaths applicationPaths)
        {
            _applicationPaths = applicationPaths;
        }

        [HttpGet]
        [Route("")]
        public IActionResult Index()
        {
            ViewData["Title"] = "Razor Test Page";
            ViewData["Message"] = "Hello from Razor!";
            return View();
        }

        [HttpGet]
        [Route("config")]
        public IActionResult Config()
        {
            var config = Configuration.Config.GetConfig(_applicationPaths);
            return View(config);
        }

        [HttpGet]
        [Route("test")]
        public IActionResult Test()
        {
            var model = new TestViewModel
            {
                Title = "Razor View Engine Test",
                Message = "If you can see this, Razor is working!",
                Items = new List<string> { "Item 1", "Item 2", "Item 3" }
            };
            return View(model);
        }
    }

    public class TestViewModel
    {
        public string Title { get; set; }
        public string Message { get; set; }
        public List<string> Items { get; set; }
    }
}