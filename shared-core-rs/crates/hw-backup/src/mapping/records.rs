//! Layer 1: universal ⇄ records — the generic, platform-agnostic projection
//! ([`to_records`] / [`from_records`]) and the mode cloud-routing migrations.

use crate::migrate::{migrate_cloud_accuracy_tier, migrate_cloud_pp_model};
use crate::records::*;
use serde_json::{Map, Value};
use std::collections::BTreeMap;

/// Project a parsed [`UniversalBackup`] into platform-neutral [`BackupRecords`].
pub fn to_records(backup: &UniversalBackup) -> BackupRecords {
    BackupRecords {
        schema_version: backup.schema_version,
        export_date: backup.export_date.clone(),
        app_version: backup.app_version.clone(),
        platform: backup.platform.clone(),
        settings: backup.settings.as_ref().map(settings_to_record),
        modes: backup
            .modes
            .as_ref()
            .map(|ms| ms.iter().map(mode_to_record).collect())
            .unwrap_or_default(),
        vocabulary: backup.vocabulary.clone().unwrap_or_default(),
        api_keys: backup.api_keys.clone().unwrap_or_default(),
        license_key: backup.license_key.clone(),
        platform_extensions: backup.platform_extensions.clone().unwrap_or_default(),
        extra: backup.extra.clone(),
    }
}

/// Rebuild a [`UniversalBackup`] from platform-neutral [`BackupRecords`]. Inverse
/// of [`to_records`]. Absent optional sections stay absent.
pub fn from_records(records: &BackupRecords) -> UniversalBackup {
    let platform_extensions = if records.platform_extensions.is_empty() {
        None
    } else {
        Some(records.platform_extensions.clone())
    };

    UniversalBackup {
        schema_version: records.schema_version,
        export_date: records.export_date.clone(),
        app_version: records.app_version.clone(),
        platform: records.platform.clone(),
        settings: records.settings.as_ref().map(record_to_settings),
        modes: if records.modes.is_empty() {
            None
        } else {
            Some(records.modes.iter().map(record_to_mode).collect())
        },
        vocabulary: if records.vocabulary.is_empty() {
            None
        } else {
            Some(records.vocabulary.clone())
        },
        api_keys: if records.api_keys.is_empty() {
            None
        } else {
            Some(records.api_keys.clone())
        },
        license_key: records.license_key.clone(),
        platform_extensions,
        extra: records.extra.clone(),
    }
}

fn settings_to_record(s: &UniversalSettings) -> SettingsRecord {
    SettingsRecord {
        general: s.general.clone(),
        text_output: s.text_output.clone(),
        storage: s.storage.clone(),
        streaming: s.streaming.clone(),
        advanced: s.advanced.clone(),
        // Generic path: top-level platformExtensions lives on BackupRecords, not
        // here. The macOS adapter populates this field for its own purposes.
        platform_extensions: BTreeMap::new(),
        extra: s.extra.clone(),
    }
}

fn record_to_settings(r: &SettingsRecord) -> UniversalSettings {
    UniversalSettings {
        general: r.general.clone(),
        text_output: r.text_output.clone(),
        storage: r.storage.clone(),
        streaming: r.streaming.clone(),
        advanced: r.advanced.clone(),
        extra: r.extra.clone(),
    }
}

fn mode_to_record(m: &UniversalMode) -> ModeRecord {
    ModeRecord {
        id: m.id.clone(),
        name: m.name.clone(),
        preset: m.preset.clone(),
        language: m.language.clone(),
        model: m.model.clone(),
        is_default: m.is_default,
        sort_order: m.sort_order,
        punctuation: m.punctuation,
        capitalization: m.capitalization,
        profanity_filter: m.profanity_filter,
        remove_trailing_period: m.remove_trailing_period,
        english_spelling: m.english_spelling.clone(),
        cloud_provider: m.cloud_provider.clone(),
        cloud_transcription_model: m.cloud_transcription_model.clone(),
        cloud_transcription_domain: m.cloud_transcription_domain.clone(),
        post_processing_mode: m.post_processing_mode,
        post_processing_provider: m.post_processing_provider.clone(),
        language_model: m.language_model.clone(),
        local_post_processing_model: m.local_post_processing_model.clone(),
        user_system_prompt: m.user_system_prompt.clone(),
        custom_instructions: m.custom_instructions.clone(),
        gemini_custom_prompt: m.gemini_custom_prompt.clone(),
        cloud_accuracy_tier: m.cloud_accuracy_tier.clone(),
        cloud_post_processing_model: m.cloud_post_processing_model.clone(),
        // Mirror the wire Option exactly: absent stays None, explicit `{}` stays
        // Some(empty). Do NOT collapse to a plain map (that erased the key).
        platform_extensions: m.platform_extensions.clone(),
        extra: m.extra.clone(),
    }
}

fn record_to_mode(r: &ModeRecord) -> UniversalMode {
    UniversalMode {
        id: r.id.clone(),
        name: r.name.clone(),
        preset: r.preset.clone(),
        language: r.language.clone(),
        model: r.model.clone(),
        is_default: r.is_default,
        sort_order: r.sort_order,
        punctuation: r.punctuation,
        capitalization: r.capitalization,
        profanity_filter: r.profanity_filter,
        remove_trailing_period: r.remove_trailing_period,
        english_spelling: r.english_spelling.clone(),
        cloud_provider: r.cloud_provider.clone(),
        cloud_transcription_model: r.cloud_transcription_model.clone(),
        cloud_transcription_domain: r.cloud_transcription_domain.clone(),
        post_processing_mode: r.post_processing_mode,
        post_processing_provider: r.post_processing_provider.clone(),
        language_model: r.language_model.clone(),
        local_post_processing_model: r.local_post_processing_model.clone(),
        user_system_prompt: r.user_system_prompt.clone(),
        custom_instructions: r.custom_instructions.clone(),
        gemini_custom_prompt: r.gemini_custom_prompt.clone(),
        cloud_accuracy_tier: r.cloud_accuracy_tier.clone(),
        cloud_post_processing_model: r.cloud_post_processing_model.clone(),
        // Mirror the record Option exactly (inverse of mode_to_record): preserves
        // both the absent key (None) and an explicit empty object (Some(empty)).
        platform_extensions: r.platform_extensions.clone(),
        extra: r.extra.clone(),
    }
}

/// Apply the legacy cloud-routing migration to a [`ModeRecord`] in place,
/// canonicalizing `cloud_accuracy_tier` and `cloud_post_processing_model`.
/// Call this on import when the source may carry legacy single-token values
/// (e.g. the Windows example's `"claudeHaiku"` / `"grokFast"`). Only rewrites a
/// field when it is present, so an absent field stays absent (round-trip safe).
pub fn migrate_mode_cloud_routing(m: &mut ModeRecord) {
    if let Some(tier) = m.cloud_accuracy_tier.as_deref() {
        m.cloud_accuracy_tier = Some(migrate_cloud_accuracy_tier(Some(tier)));
    }
    if let Some(pp) = m.cloud_post_processing_model.as_deref() {
        m.cloud_post_processing_model = Some(migrate_cloud_pp_model(Some(pp)));
    }
}

/// PRESENT-ONLY tier migration: `None` for a missing/blank source, so an absent
/// field stays absent and the CALLER keeps its own entity default. Mirrors
/// `UniversalBackupMapper.MigrateCloudAccuracyTierPresent`.
fn migrate_tier_present(value: Option<&str>) -> Option<String> {
    let v = value?;
    if v.trim().is_empty() {
        return None;
    }
    Some(migrate_cloud_accuracy_tier(Some(v)))
}

/// PRESENT-ONLY post-processing-model migration. See [`migrate_tier_present`].
fn migrate_pp_present(value: Option<&str>) -> Option<String> {
    let v = value?;
    if v.trim().is_empty() {
        return None;
    }
    Some(migrate_cloud_pp_model(Some(v)))
}

/// Read a string field from a JSON object, treating an explicit `null` and a
/// non-string value as absent (what `JsonNode`/`JsonElement` do on both heads).
fn str_field<'a>(obj: &'a Map<String, Value>, key: &str) -> Option<&'a str> {
    obj.get(key).and_then(Value::as_str)
}

/// Canonicalize the two cloud-ROUTING fields (`cloudAccuracyTier`,
/// `cloudPostProcessingModel`) of a wire-shaped universal mode object, IN PLACE.
///
/// `folded_accuracy_tier` is the tier the catalog's `cloudProvider` fold produced
/// (`hw_catalog::CloudSttCatalog::normalize_cloud_provider(...).accuracy_tier`).
/// It is passed in rather than computed here because `hw-backup` is deliberately
/// sans-catalog (`shared-core-rs/README.md`); `hw-core` composes the two.
///
/// # Precedence — ported from `UniversalBackupMapper.MapToMode`, which assigns
/// `CloudAccuracyTier` TWICE
///
/// The object initializer sets `folded ?? migrated(universal) ?? Mode default`,
/// and a post-initializer arm then reassigns UNCONDITIONALLY from
/// `migrated(universal.cloudAccuracyTier)` or, when `platformExtensions.windows`
/// is present, from `migrated(windowsExt.cloudAccuracyTier)`. Since the
/// present-only migration returns `None` only for a null/blank source, ANY
/// present tier overwrites the folded one; the folded tier survives only when
/// its arm's source is absent or blank. Collapsed:
///
/// - `platformExtensions.windows` present → `winExt ?? folded ?? universal`
/// - otherwise                            → `universal ?? folded`
///
/// and for `cloudPostProcessingModel` (never folded):
///
/// - `platformExtensions.windows` present → `winExt ?? universal`
/// - otherwise                            → `universal`
///
/// **Absent stays absent.** When a chain yields nothing the key is REMOVED, not
/// stamped with the core default — the caller applies its own entity default
/// afterwards. Writing `deepgramNova3` / `grok:grok-4.3` here would regress both
/// heads, whose shared native default pair is `elevenLabsScribeV2` /
/// `anthropic:claude-haiku-4-5`.
///
/// Non-object input is left untouched.
pub fn normalize_universal_mode_value(mode: &mut Value, folded_accuracy_tier: Option<&str>) {
    let Some(obj) = mode.as_object_mut() else {
        return;
    };

    // `platformExtensions.windows` is the second assignment's source when the
    // slice exists at all — mirroring `winExt != null` on Windows, which is
    // decided by the KEY's presence, not by whether the slice carries a tier.
    let win_ext = obj
        .get("platformExtensions")
        .and_then(Value::as_object)
        .and_then(|p| p.get("windows"))
        .and_then(Value::as_object)
        .cloned();

    let universal_tier = migrate_tier_present(str_field(obj, "cloudAccuracyTier"));
    let universal_pp = migrate_pp_present(str_field(obj, "cloudPostProcessingModel"));
    let folded = folded_accuracy_tier.map(str::to_string);

    let (tier, pp) = match win_ext {
        Some(ext) => {
            let win_tier = migrate_tier_present(str_field(&ext, "cloudAccuracyTier"));
            let win_pp = migrate_pp_present(str_field(&ext, "cloudPostProcessingModel"));
            (
                win_tier.or(folded).or(universal_tier),
                win_pp.or(universal_pp),
            )
        }
        None => (universal_tier.or(folded), universal_pp),
    };

    set_or_remove(obj, "cloudAccuracyTier", tier);
    set_or_remove(obj, "cloudPostProcessingModel", pp);
}

/// Write `value` at `key`, or REMOVE the key when there is nothing to write.
/// Removal (rather than a JSON `null`) is what keeps "absent stays absent" true
/// through a serialize/deserialize hop on either head.
fn set_or_remove(obj: &mut Map<String, Value>, key: &str, value: Option<String>) {
    match value {
        Some(v) => {
            obj.insert(key.to_string(), Value::String(v));
        }
        None => {
            obj.remove(key);
        }
    }
}
