using System;

namespace InfoPanel.Spotify.Models
{
    /// <summary>
    /// Represents the authentication states for Spotify authentication.
    /// </summary>
    public enum AuthState
    {
        NotAuthenticated = 0,
        Authenticating = 1,
        Authenticated = 2,
        Error = 3
    }
}