# Media Control Setting Launches Apple Music

## Issue

When "Media Control While Recording" is set to "Pause Media" (the default), HyperWhisper sends a system-wide media pause command when recording finishes. If no media is currently playing, macOS interprets this as a request to open the default media player (Apple Music), stealing focus from the user's active application.

## Symptoms

1. User finishes transcription
2. Apple Music launches unexpectedly
3. Focus shifts away from the target application
4. Transcription appears to be "saved" but paste fails because focus changed
5. If Apple Music is already open, the issue is less noticeable (just focuses the existing window)

## Root Cause

macOS media key handling behavior:
1. HyperWhisper sends a media pause event via `MPNowPlayingInfoCenter` or similar API
2. If no media session is active, macOS routes the event to the default media player
3. Apple Music is the system default media player on macOS
4. Apple Music launches to "handle" the pause request

This is a known macOS behavior that affects many apps that interact with media controls.

## Solution

Set "Media Control While Recording" to "Off" in HyperWhisper settings.

**Path:** HyperWhisper Settings > Sound > Media Control While Recording > Off

## Additional Notes

- The "Pause Media" feature works correctly when media is actively playing (e.g., Spotify app, Apple Music)
- Browser-based media (YouTube, etc.) may not respond to the pause command reliably
- Users who don't use media playback during transcription should keep this setting "Off"

## Alternative Workaround

For users who want media control but don't want Apple Music launching, the [noTunes](https://github.com/tombonez/noTunes) utility can block Apple Music from opening:

```bash
brew install --cask notunes
```

## Related Files

- Settings UI: Look for "Media Control While Recording" setting
- Audio/recording manager: Where media control commands are sent

## Reported

January 2026 - User reported Apple Music launching after every transcription completion.
