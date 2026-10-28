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
                browserTabTitle: null,
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
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
