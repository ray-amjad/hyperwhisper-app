#!/usr/bin/python3
import gi
gi.require_version("Gio", "2.0")
from gi.repository import Gio, GLib

bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
bus.call_sync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "RequestName",
              GLib.Variant("(su)", ("org.kde.KWin", 0)), GLib.VariantType("(u)"),
              Gio.DBusCallFlags.NONE, 1000, None)
for _attempt in range(20):
    try:
        bus.call_sync("org.kde.KWin.HyperWhisper", "/HyperWhisper", "org.kde.KWin.HyperWhisper",
                      "UpdateActiveWindow", GLib.Variant("(iss)", (4321, "org.example.Editor", "Ray Notes")),
                      None, Gio.DBusCallFlags.NONE, 1000, None)
        value = bus.call_sync("org.kde.KWin.HyperWhisper", "/HyperWhisper", "org.kde.KWin.HyperWhisper",
                              "GetActiveWindow", None, GLib.VariantType("(s)"),
                              Gio.DBusCallFlags.NONE, 1000, None).unpack()[0]
        print(value, flush=True)
        raise SystemExit(0)
    except GLib.Error:
        GLib.usleep(100_000)
raise SystemExit(1)
