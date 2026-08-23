# Avalonia localization bridge

`AvaloniaLocalizationBridge` adapts the shared, authoritative Windows resource
catalog to Avalonia without copying translations or changing provider/model IDs.
Create one bridge on the UI thread and keep it for the application lifetime.

Linux-only desktop wording lives in `Resources/LinuxStrings.resx`. Every supported
culture has a Linux satellite catalog. `generate-satellites.py` copies only values
whose invariant English text is an exact semantic match in the human-reviewed macOS
catalog; it never calls a translation service. Linux-specific copy deliberately uses
the invariant English fallback until a native-speaker review supplies that locale.
Shared keys continue to use the existing 40 translated satellite catalogs.

Do not treat catalog presence as human translation approval. A release claiming full
Linux UI-language parity must record native-speaker review of every Linux-specific key
as an external content gate. Provider/model IDs, paths, shortcut tokens, protocol
values, and command examples remain opaque and must not be translated.

- Bind `bridge["common.cancel"]` for simple dynamic text. The bridge raises the
  `Item[]` property notification after `SetCulture`.
- Use `bridge.Bind(...)` or `bridge.BindFormat(...)` when a view needs an
  independently observable `LocalizedResource.Value`; dispose that resource
  with the view.
- Bind the window's `FlowDirection` to `bridge.FlowDirection` so Arabic and
  other RTL cultures mirror Avalonia layout automatically.
- Use `GetRequired`/`Format` for validated keys and placeholder counts. The
  indexer is intentionally the only missing-key-tolerant surface.
- Keep routing identifiers outside localization. `ProviderIdentifier` and
  `ModelIdentifier` document and enforce that boundary.
