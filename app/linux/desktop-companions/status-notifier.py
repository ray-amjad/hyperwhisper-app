#!/usr/bin/python3
"""Minimal StatusNotifierItem bridge with a closed, content-free action protocol."""

import json
import os
import stat
import sys
import unicodedata

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
<method name='GetGroupProperties'><arg type='ai' direction='in'/><arg type='as' direction='in'/><arg type='a(ia{sv})' direction='out'/></method>
<method name='EventGroup'><arg type='a(isvu)' direction='in'/><arg type='ai' direction='out'/></method>
<method name='AboutToShow'><arg type='i' direction='in'/><arg type='b' direction='out'/></method>
<method name='AboutToShowGroup'><arg type='ai' direction='in'/><arg type='ai' direction='out'/><arg type='ai' direction='out'/></method>
<signal name='LayoutUpdated'><arg type='u'/><arg type='i'/></signal>
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
    14: "transcribe-file",
    17: "help",
    18: "support",
    19: "feedback",
    21: "modes",
    22: "show",
    23: "hide",
    25: "quit",
}

# The helper cannot load .NET satellite assemblies, so it mirrors the small,
# closed tray subset by catalog key. Missing translations deliberately fall
# back to English, matching the application ResourceManager behavior.
TRAY_CATALOG = {
    "en": {
        "menu.microphone": "Microphone", "linux.tray.microphone.default": "Use system default",
        "linux.tray.microphone.previous": "Previous microphone", "linux.tray.microphone.next": "Next microphone",
        "linux.tray.record.start": "Start recording", "menu.recording.stop": "Stop Recording",
        "sidebar.history": "History", "sidebar.settings": "Settings", "menu.select.mode": "Select Mode",
        "menu.mode.none": "No modes available", "menu.mode.unnamed": "Unnamed", "menu.mode.more": "More...",
        "menu.transcribe.file": "Transcribe File", "settings.resources.help.center": "Help Center",
        "settings.resources.contact.support": "Contact Support", "settings.resources.feedback": "Send Feedback",
        "settings.api.show": "Show", "settings.api.hide": "Hide", "common.quit": "Quit",
    },
    "de": {
        "sidebar.history": "Geschichte", "sidebar.settings": "Einstellungen",
        "menu.select.mode": "Modus auswählen", "menu.mode.none": "Keine Modi verfügbar",
        "menu.mode.unnamed": "Unbenannt", "menu.mode.more": "Mehr...",
    },
    "ar": {
        "sidebar.history": "السجل", "sidebar.settings": "الإعدادات",
        "menu.select.mode": "تحديد الوضع", "menu.mode.unnamed": "بدون اسم", "menu.mode.more": "المزيد...",
    },
    "zh-Hans": {
        "menu.microphone": "麦克风", "linux.tray.microphone.default": "使用系统默认值",
        "linux.tray.microphone.previous": "上一个麦克风", "linux.tray.microphone.next": "下一个麦克风",
        "linux.tray.record.start": "开始录音", "menu.recording.stop": "停止录音",
        "sidebar.history": "历史记录", "sidebar.settings": "设置", "menu.select.mode": "选择模式",
        "menu.mode.none": "没有可用模式", "menu.mode.unnamed": "未命名", "menu.mode.more": "更多...",
        "menu.transcribe.file": "转写文件", "settings.resources.help.center": "帮助中心",
        "settings.resources.contact.support": "联系支持", "settings.resources.feedback": "发送反馈",
        "settings.api.show": "显示", "settings.api.hide": "隐藏", "common.quit": "退出",
    },
}


def culture_name():
    raw = next((os.environ.get(name) for name in ("LC_ALL", "LC_MESSAGES", "LANGUAGE", "LANG")
                if os.environ.get(name)), "en")
    normalized = raw.split(":", 1)[0].split(".", 1)[0].replace("_", "-")
    if normalized.lower().startswith("zh"):
        return "zh-Hans"
    if normalized.lower().startswith("ar"):
        return "ar"
    if normalized.lower().startswith("he"):
        return "he"
    if normalized.lower().startswith("de"):
        return "de"
    return "en"


CULTURE = culture_name()
RTL_CULTURES = {"ar", "he"}


def label(key):
    return TRAY_CATALOG.get(CULTURE, {}).get(key, TRAY_CATALOG["en"][key])


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


def properties(label=None, separator=False, children=False, enabled=True, toggle=None):
    if separator:
        return {"type": GLib.Variant("s", "separator")}
    result = {}
    if label is not None:
        result.update({"label": GLib.Variant("s", label), "enabled": GLib.Variant("b", enabled)})
    if children:
        result["children-display"] = GLib.Variant("s", "submenu")
    if toggle is not None:
        result.update({"toggle-type": GLib.Variant("s", "radio"), "toggle-state": GLib.Variant("i", 1 if toggle else 0)})
    return result


def node(item_id, label=None, children=None, separator=False, enabled=True, toggle=None):
    return (item_id, properties(label, separator, bool(children), enabled, toggle), children or [])


def layout_value(item, depth=-1):
    """The (ia{sv}av) for one item, down to the recursionDepth the host asked for:
    -1 is everything, 0 the item alone (KDE asks for 1 and fetches each submenu itself)."""
    item_id, props, children = item
    if depth == 0:
        return (item_id, props, [])
    below = depth - 1 if depth > 0 else -1
    return (item_id, props, [GLib.Variant("(ia{sv}av)", layout_value(child, below)) for child in children])


def served_submenus(item, depth=-1):
    """Ids whose children a GetLayout of this depth put in front of the host."""
    item_id, _props, children = item
    if depth == 0 or not children:
        return []
    below = depth - 1 if depth > 0 else -1
    ids = [item_id]
    for child in children:
        ids += served_submenus(child, below)
    return ids


def item_properties(item):
    item_id, props, children = item
    table = {item_id: props}
    for child in children:
        table.update(item_properties(child))
    return table


def find_item(item, item_id):
    if item[0] == item_id:
        return item
    for child in item[2]:
        found = find_item(child, item_id)
        if found is not None:
            return found
    return None


# Mode list pushed by the application on stdin, one JSON object per line:
#   {"v":1,"generation":<int>,"active":<int>,"enabled":<bool>,"more":<bool>,"modes":[<str>,...]}
# This is the ONLY input the helper reads, and it is output-only from the user's
# point of view: names become dbusmenu labels on the user's own desktop. Nothing
# from here, and nothing from D-Bus, is ever echoed back to the application. A
# click on a mode row emits the closed token "mode-select|<generation>|<index>",
# two bounded integers the application resolves against the list it sent.
MAXIMUM_INPUT_LINE = 65536
MAXIMUM_MODES = 51            # the app's 50-row cap plus the active mode past it
MAXIMUM_LABEL_LENGTH = 60     # MenuItemText.MaxLength on Windows
MAXIMUM_GENERATION = 2 ** 31 - 1
ELLIPSIS = "…"
KEPT_FORMAT_CHARACTERS = {"‌", "‍"}  # ZWNJ/ZWJ: Persian words and emoji sequences
HANGUL_FILLERS = {"\u115f", "\u1160", "\u3164", "\uffa0"}  # default-ignorable letters that draw nothing
# Variation selectors (FE00-FE0F and the supplementary E0100-E01EF) and tags (E0000-E007F) included.
INVISIBLE_CHARACTERS = (KEPT_FORMAT_CHARACTERS | {" ", "\u034f"} | {chr(c) for c in range(0xFE00, 0xFE10)}
                        | {chr(c) for c in range(0xE0000, 0xE0080)} | {chr(c) for c in range(0xE0100, 0xE01F0)})
SELECT_MODE_ID = 12
MODE_ROW_FIRST_ID = 1000
MODE_ROW_LAST_ID = 2 ** 31 - 1


def mode_label(name):
    """One bounded, single-line label. Control, separator, bidi-override and other
    format characters go; whitespace runs collapse; '_' is doubled because dbusmenu
    reads a single underscore as a mnemonic marker."""
    kept = []
    for character in name:
        category = unicodedata.category(character)
        if character.isspace() or category in ("Cc", "Zl", "Zp"):
            kept.append(" ")
        elif (category in ("Cf", "Cs", "Co", "Cn") and character not in KEPT_FORMAT_CHARACTERS
              or character in HANGUL_FILLERS):
            continue
        else:
            kept.append(character)
    text = " ".join("".join(kept).split())
    # Joiners and variation selectors alone draw nothing: an empty row is "Unnamed".
    if all(character in INVISIBLE_CHARACTERS for character in text):
        return label("menu.mode.unnamed")
    if len(text) > MAXIMUM_LABEL_LENGTH:
        text = text[:MAXIMUM_LABEL_LENGTH].rstrip() + ELLIPSIS
    return text.replace("_", "__")


def parse_mode_message(line):
    """A validated (generation, active, enabled, more, labels) tuple, or None."""
    try:
        message = json.loads(line)
    except (ValueError, RecursionError):
        return None
    if type(message) is not dict or message.get("v") != 1:
        return None
    generation, active = message.get("generation"), message.get("active")
    enabled, more, modes = message.get("enabled"), message.get("more"), message.get("modes")
    if type(generation) is not int or not 0 <= generation <= MAXIMUM_GENERATION:
        return None
    if type(enabled) is not bool or type(more) is not bool:
        return None
    if type(modes) is not list or len(modes) > MAXIMUM_MODES or any(type(m) is not str for m in modes):
        return None
    if type(active) is not int or not -1 <= active < len(modes):
        return None
    return generation, active, enabled, more, [mode_label(m) for m in modes]


class ModeMenu:
    def __init__(self):
        self.generation = None
        self.active = -1
        self.enabled = False
        self.more = False
        self.labels = []
        self.next_row_id = MODE_ROW_FIRST_ID
        self.rows = {}  # dbusmenu id -> index, for the current layout only

    def apply(self, parsed):
        self.generation, self.active, self.enabled, self.more, self.labels = parsed
        self.rows = {}

    def allocate_row_ids(self):
        # Fresh ids per layout, so a click a desktop host sends against a menu it
        # fetched before the last update matches nothing instead of the row that
        # now sits at the same position.
        if self.next_row_id + MAXIMUM_MODES > MODE_ROW_LAST_ID:
            self.next_row_id = MODE_ROW_FIRST_ID
        ids = list(range(self.next_row_id, self.next_row_id + len(self.labels)))
        self.next_row_id += max(len(self.labels), 1)
        self.rows = {item_id: index for index, item_id in enumerate(ids)}
        return ids

    def children(self):
        if self.generation is None or not self.labels:
            self.rows = {}
            return [node(16, label("menu.mode.none"), enabled=False)]
        ids = self.allocate_row_ids()
        rows = [node(item_id, text, enabled=self.enabled, toggle=index == self.active)
                for index, (item_id, text) in enumerate(zip(ids, self.labels))]
        if self.more:
            rows += [node(4, separator=True), node(21, label("menu.mode.more"))]
        return rows

    def token(self, item_id):
        index = self.rows.get(item_id)
        if index is None or not self.enabled or self.generation is None:
            return None
        return "mode-select|%d|%d" % (self.generation, index)


MODES = ModeMenu()


def menu_layout():
    microphone = node(8, label("menu.microphone"), [
        node(9, label("linux.tray.microphone.default")),
        node(10, label("linux.tray.microphone.previous")),
        node(11, label("linux.tray.microphone.next")),
    ])
    children = [
        node(1, label("linux.tray.record.start")),
        node(2, label("menu.recording.stop")),
        node(3, separator=True),
        node(5, label("sidebar.history")),
        node(6, label("sidebar.settings")),
        node(7, separator=True),
        microphone,
        node(SELECT_MODE_ID, label("menu.select.mode"), MODES.children()),
        node(14, label("menu.transcribe.file")),
        node(15, separator=True),
        node(17, label("settings.resources.help.center")),
        node(18, label("settings.resources.contact.support")),
        node(19, label("settings.resources.feedback")),
        node(20, separator=True),
        node(22, label("settings.api.show")),
        node(23, label("settings.api.hide")),
        node(24, separator=True),
        node(25, label("common.quit")),
    ]
    return node(0, children=children)


class Menu:
    """The whole layout is rebuilt only when the mode list changes; every other
    label depends on the module constant CULTURE alone."""

    def __init__(self):
        self.revision = 0
        self.served = {}   # submenu id -> the revision whose children a host last fetched
        self.changed = {}  # submenu id -> the revision its children last changed (default 1)
        self.rebuild()

    def rebuild(self):
        self.revision += 1
        self.root = menu_layout()
        self.items = item_properties(self.root)
        # Every rebuild is a new mode list; nothing else in the menu ever changes.
        self.changed[SELECT_MODE_ID] = self.revision

    def mark_served(self, item, depth):
        for item_id in served_submenus(item, depth):
            self.served[item_id] = self.revision

    def needs_update(self, item_id):
        """AboutToShow's answer: True for a submenu whose children changed since a host
        last fetched them. KDE (libdbusmenu-qt) re-fetches a submenu it already built
        only on LayoutUpdated naming it or on this answer; libdbusmenu-glib and the
        GNOME AppIndicator extension re-fetch the whole layout either way."""
        item = find_item(self.root, item_id)
        if item is None or not item[2]:
            return False
        return self.served.get(item_id, 0) < self.changed.get(item_id, 1)


MENU = Menu()
BUS = None


def update_modes(line):
    parsed = parse_mode_message(line)
    if parsed is None:
        return False
    MODES.apply(parsed)
    MENU.rebuild()
    if BUS is not None:
        # Only the Select Mode submenu's children change. KDE refreshes exactly the
        # subtree the signal names (parent 0 would leave a built submenu stale);
        # libdbusmenu-glib and the GNOME extension ignore the parent and re-fetch all.
        BUS.emit_signal(None, "/Menu", "com.canonical.dbusmenu", "LayoutUpdated",
                        GLib.Variant("(ui)", (MENU.revision, SELECT_MODE_ID)))
    return True


class InputReader:
    """Newline framing over a bounded buffer; an oversized line is discarded up
    to its newline rather than truncated into something that parses."""

    def __init__(self, handler):
        self.handler = handler
        self.buffer = b""
        self.discarding = False

    def feed(self, data):
        self.buffer += data
        while True:
            newline = self.buffer.find(b"\n")
            if newline < 0:
                if len(self.buffer) > MAXIMUM_INPUT_LINE:
                    self.buffer = b""
                    self.discarding = True
                return
            line, self.buffer = self.buffer[:newline], self.buffer[newline + 1:]
            if self.discarding:
                self.discarding = False
                continue
            if len(line) <= MAXIMUM_INPUT_LINE:
                try:
                    self.handler(line.decode("utf-8"))
                except UnicodeDecodeError:
                    pass


def stdin_is_app_pipe(fd):
    """The app starts the helper with stdin redirected to a pipe it holds open for the
    helper's whole life. A TTY, /dev/null or a file (a person running the script by
    hand) is not that pipe, and its end means nothing."""
    try:
        mode = os.fstat(fd).st_mode
    except OSError:
        return False
    return stat.S_ISFIFO(mode) or stat.S_ISSOCK(mode)


def watch_stdin(loop):
    reader = InputReader(update_modes)
    try:
        fd = sys.stdin.fileno()
    except (OSError, ValueError, AttributeError):
        return
    exit_on_end = stdin_is_app_pipe(fd)

    def readable(fd, condition):
        try:
            data = os.read(fd, 65536)
        except OSError:
            data = b""
        if data:
            reader.feed(data)
            return True
        # End of the app's pipe: the app exited or crashed without stopping us. Quit,
        # which drops the bus name, so the watcher removes the icon instead of leaving
        # a tray that drives nothing.
        if exit_on_end:
            loop.quit()
        return False

    try:
        GLib.io_add_watch(fd, GLib.PRIORITY_DEFAULT,
                          GLib.IOCondition.IN | GLib.IOCondition.HUP | GLib.IOCondition.ERR, readable)
    except (OSError, ValueError):
        pass


def menu_event(item_id, event_id):
    if event_id != "clicked" or item_id not in MENU.items:
        return
    if item_id in ACTIONS:
        emit(ACTIONS[item_id])
        return
    token = MODES.token(item_id)
    if token is not None:
        print("ACTION|" + token, flush=True)


def menu_call(_conn, _sender, _path, _iface, method, params, invocation):
    # libdbusmenu-glib sends EventGroup and AboutToShowGroup, never Event or
    # AboutToShow, to a server whose Version is 3 or more, with no fallback.
    items = MENU.items
    if method == "Event":
        item_id, event_id, _data, _timestamp = params.unpack()
        menu_event(item_id, event_id)
        invocation.return_value(None)
    elif method == "EventGroup":
        events = params.unpack()[0]
        for item_id, event_id, _data, _timestamp in events:
            menu_event(item_id, event_id)
        invocation.return_value(GLib.Variant("(ai)", ([e[0] for e in events if e[0] not in items],)))
    elif method == "GetLayout":
        parent, depth, _names = params.unpack()
        # A negative parent is the root: some watchers ask for -1, and the root is what
        # every request got before the menu had rows that come and go. A positive id
        # that is not in this layout (a mode row from an older revision) is an error,
        # as libdbusmenu-glib's own server answers, never the whole menu under it.
        item = MENU.root if parent < 0 else find_item(MENU.root, parent)
        if item is None:
            invocation.return_dbus_error("org.freedesktop.DBus.Error.InvalidArgs",
                                         "No menu item with that id")
            return
        depth = depth if depth >= 0 else -1
        MENU.mark_served(item, depth)
        invocation.return_value(GLib.Variant("(u(ia{sv}av))", (MENU.revision, layout_value(item, depth))))
    elif method == "GetGroupProperties":
        ids, names = params.unpack()
        result = [(i, {k: v for k, v in items[i].items() if not names or k in names})
                  for i in (ids or sorted(items)) if i in items]
        invocation.return_value(GLib.Variant("(a(ia{sv}))", (result,)))
    elif method == "AboutToShow":
        invocation.return_value(GLib.Variant("(b)", (MENU.needs_update(params.unpack()[0]),)))
    elif method == "AboutToShowGroup":
        ids = params.unpack()[0]
        invocation.return_value(GLib.Variant("(aiai)", ([i for i in ids if i in items and MENU.needs_update(i)],
                                                        [i for i in ids if i not in items])))
    else:
        invocation.return_dbus_error("org.freedesktop.DBus.Error.UnknownMethod", "Unknown method")


def menu_property(_conn, _sender, _path, _iface, prop):
    values = {
        "Version": GLib.Variant("u", 4),
        "TextDirection": GLib.Variant("s", "rtl" if CULTURE in RTL_CULTURES else "ltr"),
        "Status": GLib.Variant("s", "normal"),
    }
    return values[prop]


if not os.environ.get("DBUS_SESSION_BUS_ADDRESS"):
    print("CAPABILITY|unavailable", flush=True)
    raise SystemExit(2)

try:
    bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
    BUS = bus
    loop = GLib.MainLoop()
    watch_stdin(loop)
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
    loop.run()
except Exception:
    print("CAPABILITY|unsupported", flush=True)
    raise SystemExit(3)
