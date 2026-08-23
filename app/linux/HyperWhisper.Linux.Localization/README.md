# Avalonia localization bridge

`AvaloniaLocalizationBridge` adapts the shared, authoritative Windows resource
catalog to Avalonia without copying translations or changing provider/model IDs.
Create one bridge on the UI thread and keep it for the application lifetime.

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
