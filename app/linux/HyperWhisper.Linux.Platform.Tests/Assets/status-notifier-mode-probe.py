#!/usr/bin/python3
"""A StatusNotifierWatcher that a test drives one command at a time over stdin.

It stands in for the desktop host: it owns org.kde.StatusNotifierWatcher, learns
the helper's bus name from RegisterStatusNotifierItem, listens for dbusmenu
LayoutUpdated, and answers each command with one JSON line on stdout:

  layout              -> {"event":"layout","revision":r,"signals":[...],"menu":{...},"rows":[...]}
  wait <revision>     -> the layout, once a LayoutUpdated with a revision above <revision> arrived
  wait <revision> nofetch -> {"event":"signal","signals":[...]} at the same moment, without a GetLayout
  click <id>          -> {"event":"clicked","id":id}   (EventGroup, as libdbusmenu-glib sends it)
  layout-of <id> [d]  -> GetLayout with that parent id and recursionDepth d (default -1); a D-Bus
                         error comes back as {"event":"layout-of","error":...}, not raised
  about <id>          -> {"event":"about","need":AboutToShow(id),"group":AboutToShowGroup([id]) updates}
"""
import json
import os
import sys

import gi
gi.require_version("Gio", "2.0")
from gi.repository import Gio, GLib

WATCHER_XML = """<node><interface name='org.kde.StatusNotifierWatcher'>
<method name='RegisterStatusNotifierItem'><arg type='s' direction='in'/></method>
</interface></node>"""
SELECT_MODE_ID = 12

loop = GLib.MainLoop()
bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
state = {"service": None, "signals": [], "waiting": None, "commands": [], "buffer": b""}


def say(value):
    print(json.dumps(value, ensure_ascii=True), flush=True)


def menu_call(method, signature, args):
    return bus.call_sync(state["service"], "/Menu", "com.canonical.dbusmenu", method,
                         GLib.Variant(signature, args), None, Gio.DBusCallFlags.NONE, 3000, None).unpack()


def plain(props):
    return {key: value for key, value in props.items()}


def layout(parent=0):
    revision, root = menu_call("GetLayout", "(iias)", (parent, -1, []))
    item_id, props, children = root
    select = None
    for child in children:
        if child[0] == SELECT_MODE_ID:
            select = child
    rows = []
    if select is not None:
        rows = [dict(plain(child[1]), id=child[0]) for child in select[2]]
    return {"event": "layout", "revision": revision, "signals": list(state["signals"]),
            "root": item_id, "top": [child[0] for child in children],
            "menu": plain(select[1]) if select is not None else None, "rows": rows}


def run(command):
    parts = command.split()
    try:
        if parts[0] == "layout":
            say(layout())
        elif parts[0] == "layout-of":
            depth = int(parts[2]) if len(parts) > 2 else -1
            try:
                revision, root = menu_call("GetLayout", "(iias)", (int(parts[1]), depth, []))
            except GLib.Error as error:
                say({"event": "layout-of", "error": Gio.dbus_error_get_remote_error(error) or error.message})
            else:
                say({"event": "layout-of", "revision": revision, "root": root[0],
                     "children": [child[0] for child in root[2]],
                     "grandchildren": {str(child[0]): len(child[2]) for child in root[2]}})
        elif parts[0] == "about":
            item = int(parts[1])
            need, = menu_call("AboutToShow", "(i)", (item,))
            updates, _errors = menu_call("AboutToShowGroup", "(ai)", ([item],))
            say({"event": "about", "need": need, "group": list(updates)})
        elif parts[0] == "wait":
            threshold = int(parts[1])
            state["fetch"] = len(parts) < 3 or parts[2] != "nofetch"
            if any(revision > threshold for revision, _parent in state["signals"]):
                say(settled())
            else:
                state["waiting"] = threshold
                GLib.timeout_add(5000, timed_out, threshold)
                return
        elif parts[0] == "click":
            item = int(parts[1])
            unknown = menu_call("EventGroup", "(a(isvu))", ([(item, "clicked", GLib.Variant("s", "payload"), 0)],))
            say({"event": "clicked", "id": item, "unknown": list(unknown[0])})
        else:
            say({"event": "error", "detail": "unknown command"})
    except GLib.Error as error:
        say({"event": "error", "detail": error.message})
    next_command()


def timed_out(threshold):
    if state["waiting"] == threshold:
        state["waiting"] = None
        say({"event": "error", "detail": "no LayoutUpdated above %d" % threshold})
        next_command()
    return False


def next_command():
    if state["waiting"] is None and state["commands"] and state["service"] is not None:
        GLib.idle_add(lambda: (run(state["commands"].pop(0)), False)[1])


def layout_updated(_conn, _sender, _path, _iface, _signal, params):
    revision, parent = params.unpack()
    state["signals"].append([revision, parent])
    if state["waiting"] is not None and revision > state["waiting"]:
        state["waiting"] = None
        say(settled())
        next_command()


def settled():
    return layout() if state.get("fetch", True) else {"event": "signal", "signals": list(state["signals"])}


def registered(_conn, _sender, _path, _iface, _method, params, invocation):
    state["service"], = params.unpack()
    invocation.return_value(None)
    bus.signal_subscribe(state["service"], "com.canonical.dbusmenu", "LayoutUpdated", "/Menu", None,
                         Gio.DBusSignalFlags.NONE, layout_updated)
    say({"event": "registered"})
    next_command()


def readable(fd, _condition):
    data = os.read(fd, 65536)
    if not data:
        loop.quit()
        return False
    state["buffer"] += data
    *lines, state["buffer"] = state["buffer"].split(b"\n")
    for line in lines:
        if line.strip():
            state["commands"].append(line.decode("utf-8").strip())
    next_command()
    return True


info = Gio.DBusNodeInfo.new_for_xml(WATCHER_XML).interfaces[0]
bus.register_object("/StatusNotifierWatcher", info, registered, None, None)
Gio.bus_own_name_on_connection(bus, "org.kde.StatusNotifierWatcher", Gio.BusNameOwnerFlags.NONE,
                               lambda *_: say({"event": "ready"}), None)
GLib.io_add_watch(sys.stdin.fileno(), GLib.PRIORITY_DEFAULT, GLib.IOCondition.IN | GLib.IOCondition.HUP, readable)
loop.run()
