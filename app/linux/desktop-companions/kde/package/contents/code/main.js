function publish(window) {
    if (!window) return;
    const pid = Number(window.pid || 0); const app = String(window.resourceClass || window.resourceName || ""); const title = String(window.caption || "");
    callDBus("org.kde.KWin.HyperWhisper", "/HyperWhisper", "org.kde.KWin.HyperWhisper", "UpdateActiveWindow", pid, app, title);
}
if (workspace.windowActivated) workspace.windowActivated.connect(publish); else if (workspace.clientActivated) workspace.clientActivated.connect(publish);
publish(workspace.activeWindow || workspace.activeClient);
