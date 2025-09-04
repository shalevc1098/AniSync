# Shoko Server Plugin Development Guide

This document provides comprehensive guidance for developing plugins for Shoko Server using the Shoko.Plugin.Abstractions library.

## Overview

Shoko plugins extend the functionality of Shoko Server, an anime management system. Plugins are developed using C# and the .NET ecosystem, leveraging dependency injection and modern async patterns.

## Plugin Architecture

### Core Interfaces

#### IPlugin
The main plugin interface that all plugins must implement:
```csharp
public class MyPlugin : IPlugin, IPluginSettings
{
    public string Name => "MyPlugin";
    
    public MyPlugin(IApplicationPaths applicationPaths, ILoggerFactory loggerFactory)
    {
        // Initialize plugin
    }
    
    public void Load() { }
    public void OnSettingsLoaded(IPluginSettings settings) { }
}
```

#### IPluginServiceRegistration
Register services for dependency injection:
```csharp
public class PluginServiceRegistration : IPluginServiceRegistration
{
    public void RegisterServices(IServiceCollection serviceCollection, IApplicationPaths applicationPaths)
    {
        serviceCollection.AddHostedService<MyBackgroundService>();
        serviceCollection.AddSingleton<IMyService, MyService>();
    }
}
```

### Background Services

Use `IHostedService` for background tasks:
```csharp
public class MyBackgroundService : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Start background work
        return Task.CompletedTask;
    }
    
    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Cleanup
        return Task.CompletedTask;
    }
}
```

## Available Services

### Core Services Injected via DI

#### IApplicationPaths
Provides paths for plugin storage and configuration:
- `PluginsPath`: Directory where plugins are stored
- `ApplicationPath`: Main application directory

#### ILoggerFactory
Create loggers for different components:
```csharp
private readonly ILogger<MyPlugin> _logger;

public MyPlugin(ILoggerFactory loggerFactory)
{
    _logger = loggerFactory.CreateLogger<MyPlugin>();
}
```

#### IMetadataService
Access and manipulate anime metadata:
```csharp
public interface IMetadataService
{
    // Get anime by various IDs
    IShokoSeries GetSeriesByAnidbID(int anidbId);
    IShokoEpisode GetEpisodeByID(int episodeId);
    // More methods...
}
```

#### IUserDataService
Handle user-specific data and watch events:
```csharp
public interface IUserDataService
{
    // Events
    event EventHandler<VideoUserDataSavedEventArgs> VideoUserDataSaved;
    
    // Methods for user data
    IUserData GetUserData(int userId, int fileId);
}
```

### Event System

#### VideoUserDataSaved Event
Triggered when a user watches an episode:
```csharp
private void OnEpisodeWatched(object sender, VideoUserDataSavedEventArgs e)
{
    // e.Video contains the watched video
    // e.Video.Episodes contains episode information
    // e.UserData contains watch status
    
    foreach (var episode in e.Video.Episodes)
    {
        if (episode.Type == EpisodeType.Episode)
        {
            // Process watched episode
        }
    }
}

// Subscribe in StartAsync
_userDataService.VideoUserDataSaved += OnEpisodeWatched;

// Unsubscribe in StopAsync
_userDataService.VideoUserDataSaved -= OnEpisodeWatched;
```

## Data Models

### IShokoSeries
Represents an anime series:
```csharp
public interface IShokoSeries
{
    int ID { get; }
    int AnidbAnimeID { get; }
    string PreferredTitle { get; }
    IReadOnlyList<AnimeTitle> Titles { get; }
    DateTime? AirDate { get; }
    AnimeType Type { get; }
}
```

### IShokoEpisode
Represents an episode:
```csharp
public interface IShokoEpisode
{
    int ID { get; }
    int EpisodeNumber { get; }
    EpisodeType Type { get; }
    IShokoSeries Series { get; }
    IReadOnlyList<AnimeTitle> Titles { get; }
}
```

### IShokoVideo
Represents a video file:
```csharp
public interface IShokoVideo
{
    int ID { get; }
    IMediaInfo MediaInfo { get; }
    IReadOnlyList<IShokoEpisode> Episodes { get; }
}
```

## Configuration Management

### Plugin Configuration
Store configuration in JSON:
```csharp
public class Config
{
    private readonly string _filePath;
    
    public Config(string filePath)
    {
        _filePath = filePath;
        
        if (!File.Exists(_filePath))
        {
            Save(); // Create default config
        }
        else
        {
            var json = File.ReadAllText(_filePath);
            JsonConvert.PopulateObject(json, this);
        }
    }
    
    public void Save()
    {
        var json = JsonConvert.SerializeObject(this, Formatting.Indented);
        File.WriteAllText(_filePath, json);
    }
}
```

### Configuration Path
```csharp
var configPath = Path.Combine(applicationPaths.PluginsPath, Name, "config.json");
Config = new Config(configPath);
```

## Web API Integration

### Creating Controllers
Expose HTTP endpoints:
```csharp
[ApiController]
[ApiVersionNeutral]
[Route("[controller]")]
public class MyPluginController : ControllerBase
{
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new { status = "running" });
    }
    
    [HttpPost("callback")]
    public async Task<IActionResult> HandleCallback([FromQuery] string code)
    {
        // Handle OAuth callbacks
        return Ok();
    }
}
```

## Common Patterns

### Singleton Pattern for Plugin Instance
```csharp
public class Plugin : IPlugin
{
    public static Plugin? Instance { get; private set; }
    
    public Plugin()
    {
        Instance = this;
    }
}
```

### Async Operations with HttpClient
```csharp
private readonly IHttpClientFactory _httpClientFactory;

public async Task<T> MakeApiCall<T>(string url)
{
    var client = _httpClientFactory.CreateClient();
    var response = await client.GetAsync(url);
    var json = await response.Content.ReadAsStringAsync();
    return JsonConvert.DeserializeObject<T>(json);
}
```

### Memory Caching
```csharp
private readonly IMemoryCache _memoryCache;

public async Task<T> GetCachedData<T>(string key, Func<Task<T>> factory)
{
    return await _memoryCache.GetOrCreateAsync(key, async entry =>
    {
        entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
        return await factory();
    });
}
```

## Plugin Lifecycle

1. **Loading**: Plugin DLL placed in Shoko's plugins folder
2. **Construction**: Plugin class instantiated with DI
3. **Service Registration**: `IPluginServiceRegistration.RegisterServices` called
4. **Initialization**: `IPlugin.Load()` called
5. **Running**: `IHostedService.StartAsync()` for background services
6. **Events**: Subscribe to Shoko events for reactive behavior
7. **Shutdown**: `IHostedService.StopAsync()` for cleanup

## Deployment

### Build Configuration
```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net8.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
    </PropertyGroup>
    
    <ItemGroup>
        <FrameworkReference Include="Microsoft.AspNetCore.App" />
        <PackageReference Include="Shoko.Plugin.Abstractions" Version="4.2.0-beta4" />
    </ItemGroup>
</Project>
```

### Publishing
```bash
dotnet publish -c Release
```

Place the resulting DLL and dependencies in:
```
ShokoServer/plugins/YourPlugin/
```

## Best Practices

1. **Error Handling**: Always handle exceptions in event handlers
2. **Logging**: Use ILogger for debugging and error tracking
3. **Async/Await**: Use async patterns for I/O operations
4. **Cancellation**: Respect CancellationTokens in long-running operations
5. **Configuration**: Store sensitive data securely, never in source code
6. **Memory Management**: Dispose resources properly, use using statements
7. **Thread Safety**: Use concurrent collections for shared state

## Common Use Cases

### Syncing Watch Status
```csharp
_userDataService.VideoUserDataSaved += async (sender, e) =>
{
    foreach (var episode in e.Video.Episodes.Where(ep => ep.Type == EpisodeType.Episode))
    {
        // Sync to external service
        await SyncEpisodeStatus(episode, e.UserData.WatchedDate);
    }
};
```

### Custom Renaming
```csharp
public class CustomRenamer : IRenamer
{
    public string GetFileName(IShokoEpisode episode)
    {
        // Custom naming logic
        return $"{episode.Series.PreferredTitle} - {episode.EpisodeNumber:00}";
    }
}
```

### Metadata Enhancement
```csharp
public async Task EnhanceMetadata(IShokoSeries series)
{
    var externalData = await FetchExternalData(series.AnidbAnimeID);
    // Process and store additional metadata
}
```

## Resources

- **GitHub**: https://github.com/ShokoAnime/Shoko.Plugin.Abstractions
- **Sample Plugins**: https://github.com/ShokoAnime/SamplePlugins
- **NuGet Package**: https://www.nuget.org/packages/Shoko.Plugin.Abstractions
- **Discord**: Join Shoko Discord for development support