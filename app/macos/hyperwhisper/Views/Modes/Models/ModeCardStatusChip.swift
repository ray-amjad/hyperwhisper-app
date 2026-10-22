//
//  ModeCardStatusChip.swift
//  HyperWhisper
//
//  The chip beside a mode card's name.
//

import SwiftUI

/// The chip beside a mode card's name (`ModeData.cardStatusChip`).
enum ModeCardStatusChip: Equatable {
    /// Green "Offline": every part of the mode runs on this Mac.
    case offline
    /// Orange "Model missing": the mode's local model is not downloaded.
    case modelMissing
    /// No chip.
    case none
}
