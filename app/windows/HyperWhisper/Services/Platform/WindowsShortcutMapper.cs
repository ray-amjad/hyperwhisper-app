using System.Windows.Input;
using HyperWhisper.Models;
using PlatformContracts = HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Services.Platform;

/// <summary>
/// Lossless translation between the existing WPF settings model and the
/// framework-neutral shortcut contracts. Native key values never cross the seam.
/// </summary>
internal static class WindowsShortcutMapper
{
    internal static PlatformContracts.GlobalShortcut ToPlatform(KeyboardShortcut shortcut)
    {
        ArgumentNullException.ThrowIfNull(shortcut);

        var modifiers = PlatformContracts.ShortcutModifiers.None;
        if (shortcut.Control) modifiers |= PlatformContracts.ShortcutModifiers.Control;
        if (shortcut.Alt) modifiers |= PlatformContracts.ShortcutModifiers.Alt;
        if (shortcut.Shift) modifiers |= PlatformContracts.ShortcutModifiers.Shift;
        if (shortcut.Win) modifiers |= PlatformContracts.ShortcutModifiers.Meta;

        var key = shortcut.Key.HasValue
            ? new PlatformContracts.ShortcutKeyCode(shortcut.Key.Value.ToString())
            : PlatformContracts.ShortcutKeyCode.None;

        return new PlatformContracts.GlobalShortcut(modifiers, key);
    }

    internal static PlatformContracts.PlatformResult<KeyboardShortcut> FromPlatform(
        PlatformContracts.GlobalShortcut shortcut)
    {
        ArgumentNullException.ThrowIfNull(shortcut);

        Key? key = null;
        if (!shortcut.Key.IsNone)
        {
            if (!Enum.TryParse(shortcut.Key.Value, ignoreCase: false, out Key parsedKey)
                || parsedKey == Key.None)
            {
                return PlatformContracts.PlatformResult<KeyboardShortcut>.Failure(
                    "shortcut.unsupported_key",
                    $"Windows does not support shortcut key code '{shortcut.Key.Value}'.");
            }

            key = parsedKey;
        }

        const PlatformContracts.ShortcutModifiers knownModifiers =
            PlatformContracts.ShortcutModifiers.Control
            | PlatformContracts.ShortcutModifiers.Alt
            | PlatformContracts.ShortcutModifiers.Shift
            | PlatformContracts.ShortcutModifiers.Meta;
        if ((shortcut.Modifiers & ~knownModifiers) != 0)
        {
            return PlatformContracts.PlatformResult<KeyboardShortcut>.Failure(
                "shortcut.unsupported_modifiers",
                $"Windows does not support modifier mask '{shortcut.Modifiers}'.");
        }

        return PlatformContracts.PlatformResult<KeyboardShortcut>.Success(new KeyboardShortcut
        {
            Control = shortcut.Modifiers.HasFlag(PlatformContracts.ShortcutModifiers.Control),
            Alt = shortcut.Modifiers.HasFlag(PlatformContracts.ShortcutModifiers.Alt),
            Shift = shortcut.Modifiers.HasFlag(PlatformContracts.ShortcutModifiers.Shift),
            Win = shortcut.Modifiers.HasFlag(PlatformContracts.ShortcutModifiers.Meta),
            Key = key
        });
    }

    internal static PlatformContracts.PushToTalkConfiguration ToPlatform(PushToTalkSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new PlatformContracts.PushToTalkConfiguration(
            Mode: settings.Mode switch
            {
                PushToTalkMode.Disabled => PlatformContracts.PushToTalkMode.Disabled,
                PushToTalkMode.Modifier => PlatformContracts.PushToTalkMode.Modifier,
                PushToTalkMode.Custom => PlatformContracts.PushToTalkMode.CustomShortcut,
                _ => throw new ArgumentException("Unsupported push-to-talk mode.", nameof(settings))
            },
            Modifier: ToPlatformModifier(settings.Modifier),
            CustomShortcut: settings.CustomShortcut == null
                ? null
                : ToPlatform(settings.CustomShortcut),
            DoublePressLock: settings.DoublePressLock);
    }

    internal static PlatformContracts.PlatformResult<PushToTalkSettings> FromPlatform(
        PlatformContracts.PushToTalkConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        KeyboardShortcut? customShortcut = null;
        if (configuration.CustomShortcut != null)
        {
            var shortcutResult = FromPlatform(configuration.CustomShortcut);
            if (shortcutResult.IsFailure)
            {
                return PlatformContracts.PlatformResult<PushToTalkSettings>.Failure(
                    shortcutResult.Error!.Code,
                    shortcutResult.Error.Message);
            }

            customShortcut = shortcutResult.Value;
        }

        return PlatformContracts.PlatformResult<PushToTalkSettings>.Success(new PushToTalkSettings
        {
            Mode = configuration.Mode switch
            {
                PlatformContracts.PushToTalkMode.Disabled => PushToTalkMode.Disabled,
                PlatformContracts.PushToTalkMode.Modifier => PushToTalkMode.Modifier,
                PlatformContracts.PushToTalkMode.CustomShortcut => PushToTalkMode.Custom,
                _ => throw new ArgumentException(
                    "Unsupported push-to-talk mode.",
                    nameof(configuration))
            },
            Modifier = FromPlatformModifier(configuration.Modifier),
            CustomShortcut = customShortcut,
            DoublePressLock = configuration.DoublePressLock
        });
    }

    private static PlatformContracts.ModifierSide ToPlatformModifier(string modifier)
        => (modifier ?? string.Empty).ToLowerInvariant() switch
        {
            "leftcontrol" or "leftctrl" => PlatformContracts.ModifierSide.LeftControl,
            "rightcontrol" or "rightctrl" => PlatformContracts.ModifierSide.RightControl,
            "control" or "ctrl" => PlatformContracts.ModifierSide.Control,
            "leftalt" => PlatformContracts.ModifierSide.LeftAlt,
            "rightalt" => PlatformContracts.ModifierSide.RightAlt,
            "alt" => PlatformContracts.ModifierSide.Alt,
            "leftshift" => PlatformContracts.ModifierSide.LeftShift,
            "rightshift" => PlatformContracts.ModifierSide.RightShift,
            "shift" => PlatformContracts.ModifierSide.Shift,
            "leftmeta" or "leftwin" => PlatformContracts.ModifierSide.LeftMeta,
            "rightmeta" or "rightwin" => PlatformContracts.ModifierSide.RightMeta,
            "meta" or "win" => PlatformContracts.ModifierSide.Meta,
            _ => PlatformContracts.ModifierSide.Control
        };

    private static string FromPlatformModifier(PlatformContracts.ModifierSide modifier)
        => modifier switch
        {
            PlatformContracts.ModifierSide.Control => "Ctrl",
            PlatformContracts.ModifierSide.Alt => "Alt",
            PlatformContracts.ModifierSide.Shift => "Shift",
            PlatformContracts.ModifierSide.Meta => "Win",
            PlatformContracts.ModifierSide.LeftControl => "LeftCtrl",
            PlatformContracts.ModifierSide.RightControl => "RightCtrl",
            PlatformContracts.ModifierSide.LeftAlt => "LeftAlt",
            PlatformContracts.ModifierSide.RightAlt => "RightAlt",
            PlatformContracts.ModifierSide.LeftShift => "LeftShift",
            PlatformContracts.ModifierSide.RightShift => "RightShift",
            PlatformContracts.ModifierSide.LeftMeta => "LeftWin",
            PlatformContracts.ModifierSide.RightMeta => "RightWin",
            _ => throw new ArgumentException("Unsupported modifier.", nameof(modifier))
        };
}
