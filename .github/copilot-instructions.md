# InfoPanel.Spotify Plugin Development Guide

## Architecture Overview

This is a **plugin for InfoPanel** that integrates with Spotify's Web API to display real-time track information. The plugin follows InfoPanel's plugin architecture pattern with these key components:

- **Main Plugin Class**: `SpotifyPlugin` inherits from `BasePlugin` and orchestrates all functionality
- **Service Layer**: Three distinct services handle different concerns
- **UI Elements**: Uses InfoPanel's `PluginText` and `PluginSensor` for data display
- **Event-Driven Communication**: Services communicate via events rather than direct coupling

## Core Service Architecture

### Three-Service Pattern
The plugin separates concerns into three sealed services:

```csharp
// Authentication & token management
SpotifyAuthService(_clientID, _tokenFilePath)

// Playback data & API calls with rate limiting
SpotifyPlaybackService(_rateLimiter)

// Rate limiting (180 req/min, 10 req/sec)
RateLimiter(180, TimeSpan.FromMinutes(1), 10, TimeSpan.FromSeconds(1))
```

### Event-Driven Communication
Services communicate through events to maintain loose coupling:
- `AuthService.AuthStateChanged` → Updates UI auth state
- `AuthService.ClientInitialized` → Provides SpotifyClient to PlaybackService
- `PlaybackService.PlaybackUpdated` → Updates track info in main plugin
- `PlaybackService.PlaybackError` → Handles API errors

## Plugin Lifecycle & InfoPanel Integration

### Required InfoPanel Plugin Methods
```csharp
public override void Initialize()     // Called on plugin load/reload
public override void Load(List<IPluginContainer> containers)  // UI setup
[PluginAction("Authorize with Spotify")]  // Creates UI button
public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);  // 1Hz updates
```

### UI Element Pattern
All data displayed through InfoPanel's UI elements:
```csharp
private readonly PluginText _currentTrack = new("current-track", "Current Track", "-");
private readonly PluginSensor _trackProgress = new("track-progress", "Track Progress (%)", 0.0F);
```

## Configuration & File Management

### Config File Pattern
Plugin creates/manages `InfoPanel.Spotify.dll.ini` with:
```ini
[Spotify Plugin]
ClientID=<your-spotify-client-id>
MaxDisplayLength=20
ForceInvalidGrant=false  ; Debug builds only
```

### Token Persistence
Refresh tokens stored in `spotifyrefresh.tmp` using custom INI format for automatic re-authentication.

## Spotify API Integration

### PKCE Authentication Flow
Uses SpotifyAPI.Web library with PKCE (Proof Key for Code Exchange):
1. Generate code verifier/challenge
2. Open browser for user authorization
3. Handle callback on `http://127.0.0.1:5000/callback`
4. Exchange code for tokens
5. Background refresh before expiry

### Rate Limiting Strategy
- **Per-minute limit**: 180 requests
- **Per-second limit**: 10 requests
- **Smart caching**: Avoid API calls when possible
- **Progressive fallback**: Estimated playback → API refresh → error states

### Token Management
- **Proactive refresh**: 5 minutes before expiry
- **Background task**: 60-second intervals
- **Retry logic**: 3 attempts with 5-second delays
- **Graceful degradation**: Continue with cached data on API failures

## Error Handling Patterns

### User-Facing Error Messages
Plugin displays specific error states in track fields:
- "Spotify client not initialized"
- "Reauthorize Required"
- "Error updating Spotify info"
- "Rate limit exceeded"
- "No track playing"

### Robust State Management
- Handles token expiry gracefully
- Detects track pausing/ending through progress monitoring
- Maintains UI responsiveness during API failures

## Development Workflows

### Build Configuration
- **Debug**: Includes ForceInvalidGrant testing feature
- **Release**: Custom output path with version folder structure
- **Dependencies**: All assemblies copied to output (`CopyLocalLockFileAssemblies=true`)

### Key Dependencies
- `SpotifyAPI.Web.Auth` (v7.2.1): PKCE authentication
- `ini-parser-netstandard` (v2.5.2): Configuration management
- Local reference to `InfoPanel.Plugins.csproj`

### Version Management
Version defined in `.csproj` and used in output path:
```xml
<Version>1.1.1</Version>
<OutputPath Condition="'$(Configuration)' == 'Release'">bin\Release\net8.0-windows\InfoPanel.Spotify-v$(Version)\InfoPanel.Spotify</OutputPath>
```

## Code Style Conventions

- **Sealed classes**: All services and main plugin class are sealed
- **Null-enabled**: Project uses nullable reference types
- **File-scoped namespaces**: `namespace InfoPanel.Spotify;`
- **Record types**: `PlaybackInfo` uses record for immutable data
- **Event patterns**: Standard .NET event handling with null checks
- **Debug output**: Extensive `Debug.WriteLine` for troubleshooting

## Testing & Debugging

### Debug Features
- `ForceInvalidGrant` setting for testing token refresh
- Comprehensive debug logging with UTC timestamps
- Auth state exposed as sensor for monitoring

### Common Issues
- Token file permissions in `C:\ProgramData\InfoPanel\plugins\`
- Spotify Developer App redirect URI must be `http://127.0.0.1:5000/callback`
- Rate limiting requires careful API call management