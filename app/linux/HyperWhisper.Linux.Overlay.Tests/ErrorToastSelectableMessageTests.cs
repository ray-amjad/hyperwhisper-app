using System.Xml.Linq;

// Issue #2632. When the browser cannot open the account page, the toast's message carries the URL so
// the user can copy it (#1188), but the message was a plain TextBlock that cannot be selected, and the
// toast closes itself after 8 s. The message is a SelectableTextBlock now; the readable-in-full test
// still pins its wrapping, trimming and 90px cap.
static class ErrorToastSelectableMessageTests
{
    public static Task MessageCanBeSelected()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "OverlaySurface",
            "LinuxErrorToastWindow.axaml"));
        var message = document.Root?.Descendants()
            .FirstOrDefault(element => element.Attribute(x + "Name")?.Value == "ToastMessage")
            ?? throw new InvalidOperationException("the error toast has no ToastMessage element");
        if (message.Name.LocalName != "SelectableTextBlock")
            throw new InvalidOperationException(
                $"the error toast message is a {message.Name.LocalName}, so the URL it carries cannot be selected and copied (#2632)");
        return Task.CompletedTask;
    }
}
