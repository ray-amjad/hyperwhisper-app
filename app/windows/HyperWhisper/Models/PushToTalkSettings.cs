namespace HyperWhisper.Models;

public class PushToTalkSettings
{
    public PushToTalkMode Mode { get; set; } = PushToTalkMode.Disabled;

    /// <summary>
    /// When Mode == Modifier, this determines which modifier to monitor ("Ctrl", "Alt", "Shift", "Win").
    /// </summary>
    public string Modifier { get; set; } = "LeftAlt";

    /// <summary>
    /// When Mode == Custom, this shortcut is used for push-to-talk.
    /// </summary>
    public KeyboardShortcut? CustomShortcut { get; set; }

    /// <summary>
    /// Enables double-press lock/unlock behavior.
    /// </summary>
    public bool DoublePressLock { get; set; }
}
