# Portable localization

This project links the 40 authoritative `Strings*.resx` catalogs from the
Windows app into a UI-neutral .NET assembly. Linking keeps one source of truth
and does not change the Windows resource assembly or its runtime lookup path.

`PortableLocalizer` provides explicit-culture lookup, invariant fallback,
right-to-left detection, validated composite formatting, and an identity-only
path for provider IDs, model IDs, and persisted enum/string values. Display
labels may be localized; identifiers used for routing or persistence must pass
through `PreserveIdentifier` unchanged.

The pre-build validator rejects missing/extra keys, malformed XML, duplicate
keys, and placeholder drift across all catalogs. It treats the two legacy `%d`
translations as the semantic equivalent of `{0}`; the portable formatter
normalizes those translations at lookup time without modifying Windows runtime
behavior.

Linux UI controls are not migrated in this foundation commit. They can move to
the portable accessor incrementally without duplicating or renaming catalog
keys.
