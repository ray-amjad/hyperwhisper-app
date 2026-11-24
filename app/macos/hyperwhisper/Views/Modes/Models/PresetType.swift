//
//  PresetType.swift
//  HyperWhisper
//
//  The available preset types for transcription modes.
//

import SwiftUI

// MARK: - Preset Type

/// Defines the available preset types for transcription modes
enum PresetType: String, CaseIterable, Identifiable {
    case hyper = "hyper"
    case message = "message"
    case mail = "mail"
    case note = "note"
    case meeting = "meeting"
    case code = "code"
    case custom = "custom"

    var id: String { rawValue }

    /// Display name for the preset
    var displayName: String {
        switch self {
        case .hyper:
            return "modes.preset.hyper.name".localized
        case .message:
            return "modes.preset.message.name".localized
        case .mail:
            return "modes.preset.mail.name".localized
        case .note:
            return "modes.preset.note.name".localized
        case .meeting:
            return "modes.preset.meeting.name".localized
        case .code:
            return "modes.preset.code.name".localized
        case .custom:
            return "modes.preset.custom.name".localized
        }
    }

    /// Recommended badge for default preset
    var isRecommended: Bool {
        self == .hyper
    }

    /// Tooltip description for each preset
    var tooltipDescription: String {
        switch self {
        case .hyper:
            return "modes.preset.hyper.tooltip".localized
        case .message:
            return "modes.preset.message.tooltip".localized
        case .mail:
            return "modes.preset.mail.tooltip".localized
        case .note:
            return "modes.preset.note.tooltip".localized
        case .meeting:
            return "modes.preset.meeting.tooltip".localized
        case .code:
            return "modes.preset.code.tooltip".localized
        case .custom:
            return "modes.preset.custom.tooltip".localized
        }
    }

    /// Human-readable description of what the AI instructions do for this preset
    var previewDescription: String {
        switch self {
        case .hyper:
            return "modes.preset.hyper.preview".localized
        case .message:
            return "modes.preset.message.preview".localized
        case .mail:
            return "modes.preset.mail.preview".localized
        case .note:
            return "modes.preset.note.preview".localized
        case .meeting:
            return "modes.preset.meeting.preview".localized
        case .code:
            return "modes.preset.code.preview".localized
        case .custom:
            return "modes.preset.custom.preview".localized
        }
    }

}

// MARK: - Reporting a Mode's preset (issue #795)

extension PresetType {

    /// Reported when no `Mode` row could be resolved at all.
    ///
    /// Kept distinct from `unrecognizedPresetReportingValue` on purpose: a
    /// triager has to be able to tell "no Mode was resolved" apart from "a Mode
    /// was resolved and its preset is not one this app knows".
    static let noModeReportingValue = "no_mode"

    /// Reported when a `Mode` was resolved but its `preset` column holds
    /// something that is not a `PresetType` raw value — or holds nothing.
    ///
    /// Deliberately NOT `"custom"`: `custom` is itself a legitimate preset, so
    /// folding an unrecognised value onto it would make the report state
    /// something untrue.
    static let unrecognizedPresetReportingValue = "unrecognized_preset"

    /// Map a raw `Mode.preset` string onto a value that is safe to log or to
    /// send to Sentry.
    ///
    /// `Mode.preset` is an unvalidated `String` column. `hw-localapi` checks
    /// only its length, `ModesEndpoint` assigns a PATCH body raw, and a
    /// restored v2 backup writes whatever the file held — so the column can
    /// carry free text a person typed, which is the class of value issue #795
    /// is about. The return value is always one of the seven `PresetType` raw
    /// values or `unrecognizedPresetReportingValue`, and never a string that
    /// came out of a Mode row unchecked. (`noModeReportingValue` comes from the
    /// `Mode?` overload below — this one is only ever asked about a Mode that
    /// exists.)
    static func reportingValue(forRawPreset rawPreset: String?) -> String {
        guard let rawPreset, let known = PresetType(rawValue: rawPreset) else {
            return unrecognizedPresetReportingValue
        }
        return known.rawValue
    }

    /// `Mode?` overload of `reportingValue(forRawPreset:)`, so a nil Mode and an
    /// unrecognised preset do not collapse onto the same reported value.
    static func reportingValue(for mode: Mode?) -> String {
        guard let mode else { return noModeReportingValue }
        return reportingValue(forRawPreset: mode.preset)
    }

    /// Every value `reportingValue(...)` is allowed to return: the seven preset
    /// raw values plus the two sentinels.
    static let reportableValues: Set<String> = {
        var values = Set<String>(PresetType.allCases.map { $0.rawValue })
        values.insert(PresetType.noModeReportingValue)
        values.insert(PresetType.unrecognizedPresetReportingValue)
        return values
    }()

    /// Idempotent boundary check, for a value that should ALREADY have come out
    /// of `reportingValue(...)`.
    ///
    /// The Sentry payload builders run every `modePreset` through this before
    /// they store it, so a call site that regresses to handing over
    /// `mode?.preset` raw still cannot put free text into a payload. It is the
    /// only guard that does not depend on a future author remembering the rule.
    ///
    /// Known and accepted imprecision: a Mode whose `preset` column literally
    /// holds `no_mode` or `unrecognized_preset` is reported as that sentinel
    /// rather than as `unrecognized_preset` specifically. That mislabels a
    /// diagnostic field by one word in a case nothing in the app produces; it
    /// does not leak anything, which is what this guard is for.
    static func sanitizedReportingValue(_ value: String) -> String {
        reportableValues.contains(value) ? value : unrecognizedPresetReportingValue
    }
}
