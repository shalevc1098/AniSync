# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a Shoko plugin that syncs anime watch data to MyAnimeList (MAL). The plugin listens to Shoko's watch events and updates MAL accordingly using the MAL API v2.

## Build Commands

```bash
# Build the project (automatically copies to Shoko plugins folder)
dotnet build

# Clean and rebuild
dotnet clean && dotnet build

# Build in Release mode
dotnet build -c Release

# Publish the plugin
dotnet publish -c Release

# Run tests
dotnet test

# Run tests with coverage
dotnet test --collect:"XPlat Code Coverage"
```

**Note**: The build process automatically copies the plugin DLL to `%ProgramData%\ShokoServer\plugins\` on Windows.

## Project Architecture

### Core Components

- **ShokoMalPlugin.cs**: Main plugin entry point implementing `IHostedService`. Handles episode watch events from Shoko and syncs to MAL.
- **Plugin.cs**: Plugin configuration and initialization implementing `IPlugin` interface.
- **PluginServiceRegistration.cs**: Service registration for dependency injection.

### API Integration (`Api/` folder)

- **MalApiCalls.cs**: MAL API v2 client implementation for anime operations (search, get, update status).
- **AuthApiCall.cs**: Handles OAuth2 authentication flow with MAL.
- **ApiAuthentication.cs**: Token management and refresh logic.

### Configuration (`Configuration/` folder)

- **Config.cs**: Main configuration model with settings like provider selection, sync preferences.
- **UserApiAuth.cs**, **ProviderApiAuth.cs**: Authentication credential models.

### Helpers (`Helpers/` folder)

- **AnimeOfflineDatabaseHelpers.cs**: Maps AniDB IDs to MAL IDs using offline database.
- **ApiCallHelpers.cs**: Wrapper for API calls with caching and error handling.
- **StringFormatter.cs**: String normalization for title matching.

### Models (`Models/` folder)

Contains data models for MAL API responses including `Anime`, `MyListStatus`, `RelatedAnime`, etc.

### Controllers (`Controllers/` folder)

- **AniSyncController.cs**: Web API controller for OAuth callback handling and user interaction.

## Key Implementation Details

### MAL ID Resolution Strategy

1. First attempts to get MAL ID from offline database using AniDB ID
2. Falls back to searching MAL API by series titles
3. Matches based on title similarity and air date proximity (within 30 days)

### Episode Matching

- Handles regular episodes and specials
- Supports OVAs through related anime traversal
- Uses fuzzy title matching with special character normalization

### Authentication Flow

- OAuth2 with PKCE for MAL authentication
- Token refresh handled automatically
- Credentials stored in plugin configuration

## Dependencies

- .NET 8.0
- Shoko.Plugin.Abstractions v4.2.0-beta4
- ASP.NET Core for web API and OAuth callback
- Microsoft.Extensions.Caching.Memory for response caching
- HttpClientFactory for API calls

## Important Notes

- The project name is `Shoko.AniSync` (csproj) but the namespace and folder is `Shoko.MAL`
- No test project currently exists
- Plugin configuration is stored in `config.json` managed by the Config class
- Supports multiple provider APIs (MAL, AniList, etc.) but currently only MAL is implemented

## Git Commit Guidelines

- Use past tense in commit messages (e.g., "Added", "Updated", "Fixed", "Removed")
- Keep commit messages to one line, concise and descriptive
- Group related changes into single commits
- Add files in logical batches before committing
- Do not add copyright notices or signatures to commits
- **IMPORTANT**: Never commit automatically - always wait for explicit user request
- Examples:
  - "Added caching for MAL API search results"
  - "Fixed async/await pattern in FetchIdFromProvider"
  - "Updated authentication flow with refresh token handling"