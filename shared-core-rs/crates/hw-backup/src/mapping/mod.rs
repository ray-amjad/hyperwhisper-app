//! Bidirectional mapping between the universal-v2 wire format
//! ([`UniversalBackup`](crate::records::UniversalBackup)) and the platform-neutral
//! record PODs ([`BackupRecords`](crate::records::BackupRecords) /
//! [`ModeRecord`](crate::records::ModeRecord) /
//! [`SettingsRecord`](crate::records::SettingsRecord)).
//!
//! Four layers, one file each:
//!
//! 1. **Universal ⇄ records** (`records.rs`) ([`to_records`] / [`from_records`]) — the generic,
//!    platform-agnostic projection. Lossless: `from_records(to_records(b)) == b`
//!    for any well-formed universal backup (the golden round-trip).
//!
//! 2. **macOS settings adapter** (`macos.rs`) ([`macos_settings_to_universal`] /
//!    [`universal_to_macos_settings`]) — the macOS-specific path that ADDS v2:
//!    it maps macOS's 7 settings categories (`general`, `audio`, `storage`,
//!    `textOutput`, `shortcuts`, `aiModel`, `advanced` — see
//!    `BackupModels.swift`) onto the universal 5 (`general`, `textOutput`,
//!    `storage`, `streaming`, `advanced`) plus a `platformExtensions.macos`
//!    blob that carries every macOS-only setting (audio extras, aiModel,
//!    shortcuts, storage/advanced extras) so a mac→…→mac round-trip is lossless.
//!
//! 3. **Windows settings adapter** (`windows.rs`) ([`windows_settings_to_universal`] /
//!    [`universal_to_windows_settings`]) — Windows' settings are FLAT and
//!    PascalCase, and their names diverge from the universal ones, so this half
//!    is driven by `(native, universal)` PAIRS tables rather than the bare key
//!    lists macOS can use. It emits the five universal categories ONLY: the
//!    curated `platformExtensions.windows.settings` list stays native.
//!
//! 4. **Linux settings adapter** (`linux.rs`) ([`linux_settings_to_universal`] /
//!    [`universal_to_linux_settings`]) — near-identity: `PortableSettingsService`'s
//!    dotted storage keys ARE the universal keys. The tables carry the export
//!    DEFAULTS as well, because Linux's export emits every shared key.
//!
//! Parity note: macOS is the verified platform. The Windows and Linux adapters
//! (3, 4) were ported from the shipping native mappers against
//! `shared-conformance/backup-vectors.json`, which captured those mappers'
//! answers before they were replaced (issue #277).
//!
//! The small JSON helpers the adapters share live in `helpers.rs`.

mod helpers;
mod linux;
mod macos;
mod records;
mod windows;

pub use linux::{
    linux_settings_to_universal, universal_to_linux_settings, LinuxSettingDefault,
    LinuxSettingPair, LINUX_ADVANCED_PAIRS, LINUX_GENERAL_PAIRS, LINUX_STORAGE_PAIRS,
    LINUX_STREAMING_PAIRS, LINUX_TEXT_OUTPUT_PAIRS,
};
pub use macos::{macos_settings_to_universal, universal_to_macos_settings, MacosSettings};
pub use records::{
    from_records, migrate_mode_cloud_routing, normalize_universal_mode_value, to_records,
};
pub use windows::{
    universal_to_windows_settings, windows_settings_to_universal, WindowsSettings,
    WINDOWS_ADVANCED_PAIRS, WINDOWS_GENERAL_PAIRS, WINDOWS_MAX_RECORDING_DURATION_CEILING_SECS,
    WINDOWS_STORAGE_PAIRS, WINDOWS_STREAMING_PAIRS, WINDOWS_TEXT_OUTPUT_PAIRS,
};
