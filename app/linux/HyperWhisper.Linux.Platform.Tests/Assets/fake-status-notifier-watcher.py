#!/usr/bin/python3
import gi
gi.require_version("Gio", "2.0")
from gi.repository import Gio, GLib

XML = """<node><interface name='org.kde.StatusNotifierWatcher'>
<method name='RegisterStatusNotifierItem'><arg type='s' direction='in'/></method>
</interface></node>"""
loop = GLib.MainLoop()

def invoke(bus, service):
    try:
        bus.call_sync(service, "/Menu", "com.canonical.dbusmenu", "GetLayout",
                      GLib.Variant("(iias)", (-1, -1, [])), None, Gio.DBusCallFlags.NONE, 2000, None)
        bus.call_sync(service, "/StatusNotifierItem", "org.kde.StatusNotifierItem", "Activate",
                      GLib.Variant("(ii)", (0, 0)), None, Gio.DBusCallFlags.NONE, 2000, None)
        bus.call_sync(service, "/StatusNotifierItem", "org.kde.StatusNotifierItem", "SecondaryActivate",
                      GLib.Variant("(ii)", (0, 0)), None, Gio.DBusCallFlags.NONE, 2000, None)
        for item in (1, 2, 3):
            bus.call_sync(service, "/Menu", "com.canonical.dbusmenu", "Event",
                          GLib.Variant("(isvu)", (item, "clicked", GLib.Variant("s", ""), 0)),
                          None, Gio.DBusCallFlags.NONE, 2000, None)
        print("WATCHER|ok", flush=True)
    except Exception as error:
        print("WATCHER|failed|" + type(error).__name__, flush=True)
    GLib.timeout_add(100, lambda: (loop.quit(), False)[1])
    return False

def called(bus, _sender, _path, _iface, _method, params, invocation):
    service, = params.unpack()
    invocation.return_value(None)
    GLib.idle_add(invoke, bus, service)

bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
info = Gio.DBusNodeInfo.new_for_xml(XML).interfaces[0]
bus.register_object("/StatusNotifierWatcher", info, called, None, None)
Gio.bus_own_name_on_connection(bus, "org.kde.StatusNotifierWatcher", Gio.BusNameOwnerFlags.NONE, None, None)
loop.run()
