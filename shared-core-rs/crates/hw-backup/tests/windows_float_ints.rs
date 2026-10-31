//! #2925: a schema-valid backup that spells an integer setting as a whole float
//! (`600.0`) must restore on Windows exactly like its integer spelling, instead
//! of failing the whole settings struct.

use hw_backup::{universal_to_windows_settings, validate_str, UniversalSettings};
use serde_json::json;

fn doc(settings: serde_json::Value) -> String {
    json!({
        "schemaVersion": 2,
        "exportDate": "2026-01-01T00:00:00Z",
        "appVersion": "1.0",
        "platform": "linux",
        "settings": settings
    })
    .to_string()
}

#[test]
fn whole_float_max_recording_duration_restores_other_settings() {
    let settings = json!({
        "general": { "launchMinimized": true },
        "advanced": { "maxRecordingDuration": 600.0 }
    });
    assert!(
        validate_str(&doc(settings.clone())).is_empty(),
        "validator accepts the file"
    );
    let record: UniversalSettings = serde_json::from_value(settings).unwrap();
    let out = universal_to_windows_settings(&record);
    assert!(
        out.is_ok(),
        "schema-valid settings rejected: {:?}",
        out.err()
    );
    let out = out.unwrap();
    assert_eq!(out.launch_minimized, Some(true));
    assert_eq!(out.max_recording_duration, Some(600));
}

#[test]
fn whole_float_typing_speed_restores_other_settings() {
    let settings = json!({
        "general": { "launchMinimized": true },
        "advanced": { "typingSpeedWPM": 95.0 }
    });
    assert!(
        validate_str(&doc(settings.clone())).is_empty(),
        "validator accepts the file"
    );
    let record: UniversalSettings = serde_json::from_value(settings).unwrap();
    let out = universal_to_windows_settings(&record);
    assert!(
        out.is_ok(),
        "schema-valid settings rejected: {:?}",
        out.err()
    );
    assert_eq!(out.unwrap().typing_speed_wpm, Some(95));
}
