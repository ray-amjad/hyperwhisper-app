const { Gio, GLib } = imports.gi;
const IFACE = `<node><interface name="org.gnome.Shell.Extensions.HyperWhisper"><method name="GetActiveWindow"><arg type="s" direction="out"/></method></interface></node>`;
function b64(value) { return GLib.base64_encode(new TextEncoder().encode(value || "")); }
class Companion { GetActiveWindow() { const win = global.display.focus_window; if (!win) return "UNAVAILABLE"; const pid = win.get_pid() || 0; const app = win.get_wm_class_instance() || win.get_wm_class() || ""; return `CONTEXT|${pid}|${b64(app)}|${b64(win.get_title() || "")}`; } }
let exported = null; let owner = 0;
function init() {}
function enable() { exported = Gio.DBusExportedObject.wrapJSObject(IFACE, new Companion()); exported.export(Gio.DBus.session, "/org/gnome/Shell/Extensions/HyperWhisper"); owner = Gio.bus_own_name_on_connection(Gio.DBus.session, "org.gnome.Shell.Extensions.HyperWhisper", Gio.BusNameOwnerFlags.NONE, null, null); }
function disable() { if (owner) Gio.bus_unown_name(owner); owner = 0; if (exported) exported.unexport(); exported = null; }
