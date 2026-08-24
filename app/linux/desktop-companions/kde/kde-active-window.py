#!/usr/bin/python3
import base64, os, gi
gi.require_version("Gio", "2.0")
from gi.repository import Gio, GLib
XML = """<node><interface name='org.kde.KWin.HyperWhisper'><method name='GetActiveWindow'><arg type='s' direction='out'/></method><method name='UpdateActiveWindow'><arg type='i' direction='in'/><arg type='s' direction='in'/><arg type='s' direction='in'/></method></interface></node>"""
context = None
def encoded(value): return base64.b64encode((value or "").encode("utf-8")).decode("ascii")
def called(_conn, _sender, _path, _iface, method, params, invocation):
    global context
    if method == "UpdateActiveWindow":
        try:
            owner = bus.call_sync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus",
                                  "GetNameOwner", GLib.Variant("(s)", ("org.kde.KWin",)),
                                  GLib.VariantType("(s)"), Gio.DBusCallFlags.NONE, 1000, None).unpack()[0]
        except Exception:
            owner = None
        if _sender != owner:
            invocation.return_dbus_error("org.kde.KWin.HyperWhisper.AccessDenied", "Only KWin may publish active-window metadata.")
            return
        pid, app, title = params.unpack(); context = "CONTEXT|%d|%s|%s" % (pid, encoded(app), encoded(title)); invocation.return_value(None)
    elif method == "GetActiveWindow": invocation.return_value(GLib.Variant("(s)", (context or "UNAVAILABLE",)))
if not os.environ.get("DBUS_SESSION_BUS_ADDRESS"): raise SystemExit(2)
bus = Gio.bus_get_sync(Gio.BusType.SESSION, None); info = Gio.DBusNodeInfo.new_for_xml(XML).interfaces[0]
bus.register_object("/HyperWhisper", info, called, None, None)
Gio.bus_own_name_on_connection(bus, "org.kde.KWin.HyperWhisper", Gio.BusNameOwnerFlags.NONE, None, None)
GLib.MainLoop().run()
