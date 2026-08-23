#!/usr/bin/python3
"""Minimal StatusNotifierItem bridge with a closed, content-free action protocol."""

import os
import sys

import gi

gi.require_version("Gio", "2.0")
from gi.repository import Gio, GLib


ITEM_XML = """<node><interface name='org.kde.StatusNotifierItem'>
<method name='Activate'><arg type='i' direction='in'/><arg type='i' direction='in'/></method>
<method name='SecondaryActivate'><arg type='i' direction='in'/><arg type='i' direction='in'/></method>
<method name='ContextMenu'><arg type='i' direction='in'/><arg type='i' direction='in'/></method>
<method name='Scroll'><arg type='i' direction='in'/><arg type='s' direction='in'/></method>
<property name='Category' type='s' access='read'/><property name='Id' type='s' access='read'/>
<property name='Title' type='s' access='read'/><property name='Status' type='s' access='read'/>
<property name='IconName' type='s' access='read'/><property name='Menu' type='o' access='read'/>
<property name='ItemIsMenu' type='b' access='read'/></interface></node>"""
MENU_XML = """<node><interface name='com.canonical.dbusmenu'>
<method name='GetLayout'><arg type='i' direction='in'/><arg type='i' direction='in'/><arg type='as' direction='in'/><arg type='u' direction='out'/><arg type='(ia{sv}av)' direction='out'/></method>
<method name='Event'><arg type='i' direction='in'/><arg type='s' direction='in'/><arg type='v' direction='in'/><arg type='u' direction='in'/></method>
<property name='Version' type='u' access='read'/><property name='TextDirection' type='s' access='read'/><property name='Status' type='s' access='read'/></interface></node>"""

# IDs and output tokens are compile-time constants. D-Bus event data, menu labels,
# coordinates, and caller identity are never forwarded to the application.
ACTIONS = {
    1: "record-start",
    2: "record-stop",
    5: "history",
    6: "settings",
    9: "microphone-default",
    10: "microphone-previous",
    11: "microphone-next",
    13: "mode-cycle",
    14: "transcribe-file",
    17: "help",
    18: "support",
    19: "feedback",
    22: "show",
    23: "hide",
    25: "quit",
}


def emit(action):
    if action in ACTIONS.values():
        print("ACTION|" + action, flush=True)


def item_call(_conn, _sender, _path, _iface, method, _params, invocation):
    if method == "Activate":
        emit("show")
    elif method == "SecondaryActivate":
        emit("hide")
    invocation.return_value(None)


def item_property(_conn, _sender, _path, _iface, prop):
    values = {
        "Category": "ApplicationStatus",
        "Id": "hyperwhisper",
        "Title": "HyperWhisper",
        "Status": "Active",
        "IconName": "hyperwhisper",
        "Menu": "/Menu",
        "ItemIsMenu": False,
    }
    return GLib.Variant({"Menu": "o", "ItemIsMenu": "b"}.get(prop, "s"), values[prop])


def properties(label=None, separator=False, children=False):
    if separator:
        return {"type": GLib.Variant("s", "separator")}
    result = {}
    if label is not None:
        result.update({"label": GLib.Variant("s", label), "enabled": GLib.Variant("b", True)})
    if children:
        result["children-display"] = GLib.Variant("s", "submenu")
    return result


def node(item_id, label=None, children=None, separator=False):
    descendants = [GLib.Variant("(ia{sv}av)", child) for child in (children or [])]
    return (item_id, properties(label, separator, bool(children)), descendants)


def menu_layout():
    microphone = node(8, "Microphone", [
        node(9, "Use system default"),
        node(10, "Previous microphone"),
        node(11, "Next microphone"),
    ])
    children = [
        node(1, "Start recording"),
        node(2, "Stop recording"),
        node(3, separator=True),
        node(5, "History"),
        node(6, "Settings"),
        node(7, separator=True),
        microphone,
        node(13, "Switch mode"),
        node(14, "Transcribe file…"),
        node(15, separator=True),
        node(17, "Help center"),
        node(18, "Contact support"),
        node(19, "Send feedback"),
        node(20, separator=True),
        node(22, "Show"),
        node(23, "Hide"),
        node(24, separator=True),
        node(25, "Quit"),
    ]
    return node(0, children=children)


def menu_call(_conn, _sender, _path, _iface, method, params, invocation):
    if method == "Event":
        item_id, event_id, _data, _timestamp = params.unpack()
        if event_id == "clicked" and item_id in ACTIONS:
            emit(ACTIONS[item_id])
        invocation.return_value(None)
    elif method == "GetLayout":
        invocation.return_value(GLib.Variant("(u(ia{sv}av))", (1, menu_layout())))


def menu_property(_conn, _sender, _path, _iface, prop):
    values = {
        "Version": GLib.Variant("u", 4),
        "TextDirection": GLib.Variant("s", "ltr"),
        "Status": GLib.Variant("s", "normal"),
    }
    return values[prop]


if not os.environ.get("DBUS_SESSION_BUS_ADDRESS"):
    print("CAPABILITY|unavailable", flush=True)
    raise SystemExit(2)

try:
    bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
    item_info = Gio.DBusNodeInfo.new_for_xml(ITEM_XML).interfaces[0]
    menu_info = Gio.DBusNodeInfo.new_for_xml(MENU_XML).interfaces[0]
    bus.register_object("/StatusNotifierItem", item_info, item_call, item_property, None)
    bus.register_object("/Menu", menu_info, menu_call, menu_property, None)
    name = "org.hyperwhisper.StatusNotifierItem.p%d" % os.getpid()
    Gio.bus_own_name_on_connection(bus, name, Gio.BusNameOwnerFlags.NONE, None, None)
    bus.call_sync(
        "org.kde.StatusNotifierWatcher",
        "/StatusNotifierWatcher",
        "org.kde.StatusNotifierWatcher",
        "RegisterStatusNotifierItem",
        GLib.Variant("(s)", (name,)),
        None,
        Gio.DBusCallFlags.NONE,
        3000,
        None,
    )
    print("CAPABILITY|available", flush=True)
    GLib.MainLoop().run()
except Exception:
    print("CAPABILITY|unsupported", flush=True)
    raise SystemExit(3)
