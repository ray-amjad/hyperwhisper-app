namespace HyperWhisper.Models;

/// <summary>
/// THEME MODE ENUM
///
/// Defines the available theme modes for the application:
/// - System: Follows the Windows system theme (light/dark)
/// - Light: Always use light theme
/// - Dark: Always use dark theme
///
/// This allows users to choose their preferred appearance:
/// 1. Match the OS setting for a consistent experience
/// 2. Override to light for bright environments
/// 3. Override to dark for low-light environments or eye comfort
/// </summary>
public enum ThemeMode
{
    /// <summary>
    /// Follow the Windows system theme setting.
    /// The app will automatically switch between light and dark
    /// when the user changes their Windows appearance settings.
    /// </summary>
    System = 0,

    /// <summary>
    /// Always use the light theme regardless of system settings.
    /// Light background with dark text.
    /// </summary>
    Light = 1,

    /// <summary>
    /// Always use the dark theme regardless of system settings.
    /// Dark background with light text.
    /// </summary>
    Dark = 2
}
