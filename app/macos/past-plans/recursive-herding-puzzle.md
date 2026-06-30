# Preset Picker Polish

## Context

Follow-up to the Mode Editor UX refactor. Four tweaks:
1. Revert "Formatting Style" label back to "Preset"
2. Remove the "Voice to Text" preset option (redundant — users can just set post-processing to Off)
3. Add provider-specific hint messages for Anthropic ("Claude 4.5 Haiku") and Gemini ("Gemini free flash"), matching the existing OpenAI hint pattern
4. Restyle the preset dropdown to use `Picker(.menu)` with 120px label to match the other pickers in the card

## Changes

### 1. Revert label: "Formatting Style" → "Preset"

**`Base.lproj/Localizable.strings`**:
- `"modes.preset.title"` → `"Preset"`
- `"modes.preset.select"` → `"Select preset"`
- `"modes.help.preset"` → `"Learn more about this preset"`
- Remove `modes.preset.voiceToText.*` keys (name, tooltip, preview)
- Add two new hint keys:
  - `"modes.postProcessing.anthropicRecommendation"` = `"We recommend using Claude 4.5 Haiku for the best balance of speed and quality."`
  - `"modes.postProcessing.geminiRecommendation"` = `"Gemini 3 Flash is available for free with a Google API key."`

### 2. Remove Voice to Text preset

**`ModeModels.swift`**:
- Remove `case voiceToText` from `PresetType` enum
- Remove its entries from `displayName`, `tooltipDescription`, `previewDescription`
- Any existing modes with `preset == "voiceToText"` will fall through to the `else` branch in `PresetPickerView` showing "Select preset" — this is acceptable since voiceToText was effectively just "Off" post-processing

**`ModeEditorView.swift`**:
- Remove `.onChange(of: preset)` handler that auto-sets postProcessingMode to `.off` for voiceToText (the preset no longer exists)

### 3. Add Anthropic + Gemini hint messages

**`ModePostProcessingSettings.swift`** (around line 346, the `else if currentProvider == .openai` block):
- Add `else if currentProvider == .anthropic` block with hint text referencing Claude 4.5 Haiku
- Add `else if currentProvider == .gemini` block with hint text referencing Gemini free flash
- Same styling pattern as the existing OpenAI hint (lightbulb icon, blue background)

### 4. Restyle PresetPickerView dropdown

**`ModePresetPicker.swift`**:
- Replace the custom `Menu` + `HStack` dropdown with a standard `Picker("", selection:)` using `.pickerStyle(.menu)` + `.labelsHidden()` — matching Provider/Model pickers
- Change label width from 80 to 120 to match other rows in the card
- Keep the info button and custom instructions editor

## Files to Modify

| File | Change |
|------|--------|
| `app/macos/hyperwhisper/Localizations/Base.lproj/Localizable.strings` | Revert label, remove voiceToText keys, add hint keys |
| `app/macos/hyperwhisper/Views/Modes/Models/ModeModels.swift` | Remove `voiceToText` case from PresetType |
| `app/macos/hyperwhisper/Views/Modes/ModeEditorView.swift` | Remove voiceToText onChange handler |
| `app/macos/hyperwhisper/Views/Modes/Components/ModePresetPicker.swift` | Restyle to Picker(.menu), 120px label |
| `app/macos/hyperwhisper/Views/Modes/Components/ModePostProcessingSettings.swift` | Add Anthropic + Gemini hint messages |

## Verification

1. Build the macOS app
2. Open mode editor — preset dropdown should look like a standard Picker, aligned with Mode/Provider/Model rows
3. "Voice to Text" should not appear in the preset list
4. Select Anthropic as provider → hint about Claude 4.5 Haiku appears
5. Select Gemini as provider → hint about Gemini free flash appears
6. Run localisation-syncer afterward
