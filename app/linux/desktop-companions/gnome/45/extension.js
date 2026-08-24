import Gio from "gi://Gio";
import GLib from "gi://GLib";
import { Extension } from "resource:///org/gnome/shell/extensions/extension.js";
const IFACE = `<node><interface name="org.gnome.Shell.Extensions.HyperWhisper"><method name="GetActiveWindow"><arg type="s" direction="out"/></method></interface></node>`;
function b64(value) { return GLib.base64_encode(new TextEncoder().encode(value || "")); }
class Companion { GetActiveWindow() { const win = global.display.focus_window; if (!win) return "UNAVAILABLE"; const pid = win.get_pid() || 0; const app = win.get_wm_class_instance() || win.get_wm_class() || ""; return `CONTEXT|${pid}|${b64(app)}|${b64(win.get_title() || "")}`; } }
export default class HyperWhisperCompanion extends Extension {
    enable() { this._exported = Gio.DBusExportedObject.wrapJSObject(IFACE, new Companion()); this._exported.export(Gio.DBus.session, "/org/gnome/Shell/Extensions/HyperWhisper"); this._owner = Gio.bus_own_name_on_connection(Gio.DBus.session, "org.gnome.Shell.Extensions.HyperWhisper", Gio.BusNameOwnerFlags.NONE, null, null); }
    disable() { if (this._owner) Gio.bus_unown_name(this._owner); this._owner = 0; if (this._exported) this._exported.unexport(); this._exported = null; }
}
