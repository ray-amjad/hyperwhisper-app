#!/usr/bin/python3
import gi
gi.require_version("Gio", "2.0")
from gi.repository import Gio, GLib

XML = """<node><interface name='org.kde.StatusNotifierWatcher'>
<method name='RegisterStatusNotifierItem'><arg type='s' direction='in'/></method>
</interface></node>"""
loop = GLib.MainLoop()

def menu(bus, service, method, signature, args):
    return bus.call_sync(service, "/Menu", "com.canonical.dbusmenu", method, GLib.Variant(signature, args),
                         None, Gio.DBusCallFlags.NONE, 2000, None).unpack()

def check(method, actual, expected):
    if actual != expected:
        raise AssertionError(method)

def invoke(bus, service):
    try:
        menu(bus, service, "GetLayout", "(iias)", (-1, -1, []))
        bus.call_sync(service, "/StatusNotifierItem", "org.kde.StatusNotifierItem", "Activate",
                      GLib.Variant("(ii)", (0, 0)), None, Gio.DBusCallFlags.NONE, 2000, None)
        bus.call_sync(service, "/StatusNotifierItem", "org.kde.StatusNotifierItem", "SecondaryActivate",
                      GLib.Variant("(ii)", (0, 0)), None, Gio.DBusCallFlags.NONE, 2000, None)
        for item in (1, 2, 3):
            menu(bus, service, "Event", "(isvu)", (item, "clicked", GLib.Variant("s", ""), 0))
        # libdbusmenu-glib speaks only the group methods to a Version 3+ server.
        check("GetGroupProperties", menu(bus, service, "GetGroupProperties", "(aias)", ([17, 18, 19, 99], ["label"])),
              ([(17, {"label": "Help Center"}), (18, {"label": "Contact Support"}), (19, {"label": "Send Feedback"})],))
        check("AboutToShow", menu(bus, service, "AboutToShow", "(i)", (0,)), (False,))
        check("AboutToShowGroup", menu(bus, service, "AboutToShowGroup", "(ai)", ([0, 99],)), ([], [99]))
        check("EventGroup", menu(bus, service, "EventGroup", "(a(isvu))",
                                 ([(5, "clicked", GLib.Variant("s", ""), 0), (99, "clicked", GLib.Variant("s", ""), 0)],)),
              ([99],))
        print("WATCHER|ok", flush=True)
    except AssertionError as error:
        print("WATCHER|failed|" + str(error), flush=True)
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
Gio.bus_own_name_on_connection(bus, "org.kde.StatusNotifierWatcher", Gio.BusNameOwnerFlags.NONE,
                               lambda *_: print("WATCHER|ready", flush=True), None)
loop.run()
