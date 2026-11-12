// APP-TYPE RECONCILIATION (issue #887)
//
// ApplicationContextService.GatherContext probes the foreground window, then
// hands what it saw to ReconcileClassification, which decides the app type,
// category and text format: the webmail tab-title override, the shared
// classifier, the webmail fallback when the classifier answers Other, and the
// category and text-format overwrite. These checks drive that pure half with
// no real window. The classifier itself is the shared core's, so the inputs
// below were chosen against its catalog: an email address in a browser tab is
// webmail but matches no title keyword, so the classifier answers Other.

using HyperWhisper.AppClassification;
using HyperWhisper.Services;

namespace HyperWhisper.SmokeTests;

internal static class ReconcileClassificationChecks
{
    private const string WebmailTab = "Quarterly numbers - alice@example.com";

    public static void RunAll(Action<string, Action> run)
    {
        run("app context: ReconcileClassification falls back to webmail when the classifier answers Other (#887)", () =>
        {
            var (category, textFormat, classification) = ApplicationContextService.ReconcileClassification(
                processName: "chrome",
                windowTitle: WebmailTab + " - Google Chrome",
                browserTabTitle: WebmailTab,
                browserHost: null,
                focusedElementType: "Edit",
                focusedContent: null,
                category: "Web Browser",
                textFormat: "text");

            var classifierAlone = AppTypeClassifier.Classify(new AppClassificationRequest(
                ProcessName: "chrome",
                Title: WebmailTab + " " + WebmailTab + " - Google Chrome",
                FocusedPieces: ["Edit"]));
            Assert(classifierAlone.AppType == AppType.Other && AppTypeClassifier.IsWebmail(WebmailTab),
                $"the classifier alone now answers {classifierAlone.AppType} for this tab; pick another");
            Assert(classification.AppType == AppType.Email, $"app type {classification.AppType}, want Email");
            Assert(classification.Confidence == "weak", $"confidence '{classification.Confidence}', want weak");
            Assert(classification.Source == "webmailTitleFallback", $"source '{classification.Source}', want webmailTitleFallback");
            Assert(classification.Matched == null, $"matched '{classification.Matched}', want null");
            Assert(category == "Email Client", $"category '{category}', want Email Client");
            Assert(textFormat == "email", $"text format '{textFormat}', want email");
        });

        run("app context: ReconcileClassification keeps a non-Other classifier verdict over a webmail tab (#887)", () =>
        {
            const string tab = "ChatGPT - alice@example.com";
            var (category, textFormat, classification) = ApplicationContextService.ReconcileClassification(
                processName: "chrome",
                windowTitle: tab + " - Google Chrome",
                browserTabTitle: tab,
                browserHost: "chatgpt.com",
                focusedElementType: "Edit",
                focusedContent: null,
                category: "Web Browser",
                textFormat: "text");

            Assert(AppTypeClassifier.IsWebmail(tab), "the tab title no longer reads as webmail; pick another");
            Assert(classification.AppType == AppType.Ai, $"app type {classification.AppType}, want Ai");
            Assert(classification.Source == "browserHost", $"source '{classification.Source}', want browserHost");
            Assert(classification.Confidence == "medium", $"confidence '{classification.Confidence}', want medium (a probed host)");
            Assert(category == "AI", $"category '{category}', want AI");
            // The webmail override set "email" first; the classifier's verdict overwrites it.
            Assert(textFormat == "text", $"text format '{textFormat}', want text");
        });

        run("app context: ReconcileClassification leaves a non-browser's category and text format alone (#887)", () =>
        {
            var (category, textFormat, classification) = ApplicationContextService.ReconcileClassification(
                processName: "SomeUnknownTool",
                windowTitle: WebmailTab,
                browserTabTitle: WebmailTab,
                browserHost: null,
                focusedElementType: null,
                focusedContent: null,
                category: "Code Editor",
                textFormat: "code");

            Assert(classification.AppType == AppType.Other, $"app type {classification.AppType}, want Other");
            Assert(classification.Source != "webmailTitleFallback", "the webmail fallback fired outside a browser");
            Assert(category == "Code Editor", $"category '{category}', want Code Editor");
            Assert(textFormat == "code", $"text format '{textFormat}', want the InferTextFormat value code");
        });

        run("app context: ReconcileClassification passes the focused element to the classifier (#887)", () =>
        {
            var (category, textFormat, classification) = ApplicationContextService.ReconcileClassification(
                processName: "SomeUnknownTool",
                windowTitle: "Untitled",
                browserTabTitle: null,
                browserHost: null,
                focusedElementType: "Edit",
                focusedContent: "Subject: Q3 numbers",
                category: "",
                textFormat: "text");

            Assert(classification.AppType == AppType.Email, $"app type {classification.AppType}, want Email");
            Assert(classification.Source == "focusedElement", $"source '{classification.Source}', want focusedElement");
            Assert(category == "Email Client", $"category '{category}', want Email Client");
            Assert(textFormat == "email", $"text format '{textFormat}', want email");
        });

        // Edge puts a ZERO WIDTH SPACE inside its suffix ("Microsoft\u200B Edge") and the
        // profile name before it. The suffix goes; the profile segment stays (see
        // ParseBrowserTabTitle's summary for why).
        run("app context: ParseBrowserTabTitle strips Edge's zero-width-space hyphen suffix (#3022)", () =>
        {
            var tab = ApplicationContextService.ParseBrowserTabTitle("Inbox - Profile 1 - Microsoft\u200B Edge", "Web Browser");
            Assert(tab == "Inbox - Profile 1", $"tab '{tab}', want 'Inbox - Profile 1'");

            var webmail = ApplicationContextService.ParseBrowserTabTitle(
                WebmailTab + " - Profile 1 - Microsoft\u200B Edge", "Web Browser");
            Assert(webmail == WebmailTab + " - Profile 1", $"tab '{webmail}', want '{WebmailTab} - Profile 1'");
        });

        run("app context: ParseBrowserTabTitle strips Edge's zero-width-space em-dash suffix (#3022)", () =>
        {
            var tab = ApplicationContextService.ParseBrowserTabTitle("Inbox \u2014 Microsoft\u200B Edge", "Web Browser");
            Assert(tab == "Inbox", $"tab '{tab}', want 'Inbox'");

            var otherZeroWidth = ApplicationContextService.ParseBrowserTabTitle("Inbox - Microsoft\u2060 Edge\uFEFF", "Web Browser");
            Assert(otherZeroWidth == "Inbox", $"tab '{otherZeroWidth}', want 'Inbox' (word joiner + trailing BOM)");
        });

        run("app context: ParseBrowserTabTitle still strips a plain Edge suffix and leaves Chrome as before (#3022)", () =>
        {
            var edge = ApplicationContextService.ParseBrowserTabTitle("Inbox - Microsoft Edge", "Web Browser");
            Assert(edge == "Inbox", $"edge tab '{edge}', want 'Inbox'");

            var chrome = ApplicationContextService.ParseBrowserTabTitle(WebmailTab + " - Google Chrome", "Web Browser");
            Assert(chrome == WebmailTab, $"chrome tab '{chrome}', want '{WebmailTab}'");

            var unknown = ApplicationContextService.ParseBrowserTabTitle("Some page - Unknown Browser", "Web Browser");
            Assert(unknown == "Some page - Unknown Browser", $"unknown tab '{unknown}', want the full title");
        });

        run("app context: ParseBrowserTabTitle returns null for a bare suffix or a non-browser (#3022)", () =>
        {
            var bare = ApplicationContextService.ParseBrowserTabTitle(" - Microsoft\u200B Edge", "Web Browser");
            Assert(bare == null, $"bare suffix gave '{bare}', want null");

            var notBrowser = ApplicationContextService.ParseBrowserTabTitle("Inbox - Microsoft\u200B Edge", "Code Editor");
            Assert(notBrowser == null, $"non-browser gave '{notBrowser}', want null");
        });

        run("app context: ParseBrowserTabTitle keeps a zero-width space that is part of the page title (#3022)", () =>
        {
            var tab = ApplicationContextService.ParseBrowserTabTitle("Q3\u200B report\u200B - Microsoft\u200B Edge", "Web Browser");
            Assert(tab == "Q3\u200B report\u200B", $"tab '{tab}', want 'Q3\\u200B report\\u200B'");

            var chrome = ApplicationContextService.ParseBrowserTabTitle("Q3\u200B report - Google Chrome", "Web Browser");
            Assert(chrome == "Q3\u200B report", $"chrome tab '{chrome}', want 'Q3\\u200B report'");
        });
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
