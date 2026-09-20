#!/usr/bin/env bash
# A cloud model name is written in the catalog and nowhere else (#837).
#
# Before #837 the same model was named in 6 places: `models-catalog.json`,
# `cloud-stt-catalog.json`, `CloudTranscriptionModels.swift`,
# `CloudTranscriptionModel.cs`, `STTCapabilities.swift` and 80 locale files.
# They had drifted — `Whisper Large v3` on macOS against `Whisper Large V3` on
# Windows, `Nova-3 General` against `Nova 3 General`, `Scribe v2` against
# `Scribe V2`, and `(Preview)` glued onto 4 names beside the `previewStatus`
# field that already said so. Each head now reads the name through the shared
# core, and this gate is what stops a copy coming back.
#
# SCOPE. The gate holds the name of a CLOUD SPEECH model. It does not hold:
#
#   - a TIER or company name (`Gemini 3.5 Transcribe`, `Grok STT`). Those are
#     still written in 10 provider enums and converters. Unifying them is the
#     same 5 steps again and it is filed separately.
#   - a POST-PROCESSING model name (`Gemini 2.5 Flash`). A `kind: "text"` row in
#     `models-catalog.json` carries no `displayName` at all, so those 3
#     registries have nothing to read yet. Filed with the tier names.
#   - a LOCAL model name (`Whisper Base`, `Whisper Large v3 Turbo`). No shared
#     catalog lists a local model, so onboarding is its only owner.
#
# A name inside a comment is fine — a comment saying which name drifted is worth
# keeping. Test sources are excluded on purpose: a test that pins a name against
# the catalog is exactly the guard this rule wants, not a second owner of it.
#
# Run it with no arguments from anywhere in the repo.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

names="$(node -e '
const models = require("./shared-models/models-catalog.json").models;
const stt = require("./shared-app-classification/cloud-stt-catalog.json");

// Every name this gate will NOT hold, for the reasons in the header above.
const skip = new Set();
for (const e of (stt.entries || stt.providers || [])) {
  if (e.displayName) skip.add(e.displayName);        // a tier name
  if (e.vendorDisplayName) skip.add(e.vendorDisplayName);
  // A tier that ships ONE model is called after it: the tier `geminiTranscribe`
  // is `Google Gemini 3.5 Transcribe` and its model is `Gemini 3.5 Transcribe`.
  // A provider enum that writes that string is naming the PROVIDER, which is
  // the tier-name job filed separately, not a second owner of a model name.
  for (const m of (e.models || []))
    if (m.displayName && e.displayName && e.displayName.endsWith(m.displayName)) skip.add(m.displayName);
}

const out = new Set();
for (const m of models) if (m.kind === "voice" && m.id !== "*" && m.displayName) out.add(m.displayName);
for (const e of (stt.entries || stt.providers || []))
  for (const m of (e.models || [])) if (m.displayName) out.add(m.displayName);

// A one-word name is not evidence of a copy: "Whisper" and "Dictation" are
// ordinary words that appear in unrelated strings. The gate holds the
// multi-word names, which is every name a registry actually duplicated.
for (const n of out) if (n.trim().includes(" ") && !skip.has(n)) console.log(n);
' | sort -u)"

found=0
while IFS= read -r name; do
  [ -z "$name" ] && continue
  hits="$(grep -rnF "\"$name\"" \
      --include='*.swift' --include='*.cs' --include='*.axaml' \
      --include='*.xaml' --include='*.resx' --include='*.strings' \
      app/ 2>/dev/null \
    | grep -v '/bin/' | grep -v '/obj/' \
    | grep -vi 'Tests/' | grep -vi 'Tests\.cs' | grep -vi 'SmokeTests' \
    | grep -v 'Views/Onboarding/OnboardingSourceViews\.swift' \
    | grep -v 'Services/Onboarding/OnboardingLiveDependencies\.cs' \
    | grep -v 'Models/PostProcessingModels\.swift' \
    | grep -v 'PostProcessingModelCatalog\.cs' \
    | grep -v 'Models/LanguageModelInfo\.cs' \
    | grep -vE ':[0-9]+: *(//|///|\*|<!--)' \
    || true)"
  if [ -n "$hits" ]; then
    found=1
    echo "A model name belongs in the catalog only: \"$name\""
    echo "$hits" | sed 's/^/    /'
  fi
done <<< "$names"

if [ "$found" -ne 0 ]; then
  echo
  echo "Delete the literal and read the name through the shared core:"
  echo "  Swift  SharedModelsCatalog.entry(provider:kind:id:)?.displayName"
  echo "  C#     SharedModelsCatalog.DisplayName(provider, kind, id)"
  echo "  Linux  SharedCoreBridge.CloudSttVendorGroups()"
  exit 1
fi

echo "No cloud model name is duplicated in app source."
