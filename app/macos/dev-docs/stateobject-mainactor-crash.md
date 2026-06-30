# SwiftUI @StateObject + @MainActor Singleton Crash

## Issue

Version 2.19.0 crashed when opening Settings with `EXC_BAD_ACCESS` at address `0x3`.

## Crash Signature

```
Exception Type:  EXC_BAD_ACCESS (SIGSEGV)
Exception Subtype: KERN_INVALID_ADDRESS at 0x0000000000000003

Thread 0 Crashed (main thread):
  objc_msgSend + 56
  swift_getObjectType + 204
  swift_task_isMainExecutorImpl + 36
  DesignLibrary
  ZStack.init(alignment:content:)
  ViewBodyAccessor.updateBody(of:changed:)
```

## Root Cause

Using `@StateObject` with a `@MainActor` isolated singleton causes crashes during SwiftUI view initialization.

1. `@MainActor` singletons have actor-isolated static properties
2. When SwiftUI initializes the view, it evaluates the `.shared` property
3. During certain SwiftUI update cycles, view initialization may occur in a context where actor isolation can't be verified
4. Swift's runtime calls `swift_task_isMainExecutorImpl` to check actor isolation
5. This check fails, causing a null pointer dereference

## Before (Crashes)

```swift
@MainActor
final class USBPedalMonitor: ObservableObject {
    static let shared = USBPedalMonitor()
    @Published var connectedPedals: [USBPedalDevice] = []
}

struct ShortcutsSettingsSection: View {
    // WRONG: @StateObject with @MainActor singleton
    @StateObject private var pedalMonitor = USBPedalMonitor.shared

    var body: some View { ... }  // CRASH during init
}
```

## After (Fixed)

```swift
struct ShortcutsSettingsSection: View {
    // CORRECT: @ObservedObject for singletons (view doesn't own it)
    @ObservedObject private var pedalMonitor = USBPedalMonitor.shared

    var body: some View { ... }  // Works correctly
}
```

## The Rule

| Wrapper | Use Case |
|---------|----------|
| `@StateObject` | Objects the view **creates and owns** |
| `@ObservedObject` | Objects **created elsewhere** (singletons, passed-in) |
| `@EnvironmentObject` | Objects **injected from parent** views |

A singleton is never owned by a single view—always use `@ObservedObject`.

## Files Fixed

- `ShortcutsSettingsSection.swift` — `USBPedalMonitor.shared`
- `USBPedalSettingsSection.swift` — `USBPedalMonitor.shared`
- `BackupSettingsSection.swift` — `BackupManager.shared`
- `GeneralSettingsSection.swift` — removed `.onChange` for MusicBlocker
- `GeneralSettingsManager.swift` — handle MusicBlocker toggle in `didSet`

## Broader Rule (macOS 26.2+)

Avoid `@MainActor` on singleton `ObservableObject` types that are accessed during
SwiftUI view initialization or layout. Prefer explicit main-thread hops for UI
state updates (e.g., `MainActor.run` / `Task { @MainActor in ... }`) to avoid
executor-check recursion in SwiftUI.

Recent examples:
- `HomebrewDetection.swift`
- `USBPedalMonitor.swift`
- `BackupManager.swift`
- `UpdateLogger.swift`
