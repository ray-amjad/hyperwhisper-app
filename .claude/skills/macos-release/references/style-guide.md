# Appcast Description Style Guide

Writing style and language guidelines for appcast descriptions.

## Core Principles

1. **Ultra-Concise**: Users scan update notifications quickly
2. **User-Focused**: Only include changes users will notice
3. **Clear Value**: Make it obvious why they should update
4. **Professional**: Maintain credibility without being stuffy
5. **Scannable**: Each bullet should be graspable in 3 seconds

## Tone and Voice

### Professional but Approachable
**Goal**: Sound helpful and informative, not corporate or salesy.

✅ Good:
```
- Fixed app crashes on macOS 26.1 for button handlers
```

❌ Too formal:
```
- Resolved critical system-level failures affecting button handler subsystems on macOS version 26.1
```

❌ Too casual:
```
- We squashed those annoying crashes! No more problems on macOS 26.1 :)
```

### Direct and Specific
**Goal**: Get to the point immediately with concrete details.

✅ Good:
```
- Increased trial credits from 100 to 150 (~24 minutes of transcription)
```

❌ Too vague:
```
- Improved trial experience
```

❌ Too wordy:
```
- We're excited to announce that we've increased the number of trial credits available to new users from the previous limit of 100 credits up to a new generous limit of 150 credits
```

## Language Patterns

### Starting Phrases

**Use these action verbs**:
- Added, Introduced, Implemented (for new features)
- Improved, Enhanced, Optimized (for improvements)
- Fixed, Resolved, Corrected (for bug fixes)
- Increased, Decreased, Updated (for changes)

**Good examples**:
```
- Added Mistral Voxtral Mini transcription provider
- Improved microphone selection persistence
- Fixed race condition in background validation
- Increased device credits to 150
```

**Avoid these**:
- ❌ "Now you can..." (unnecessary words)
- ❌ "We've added..." (focus on feature, not "we")
- ❌ "Introducing..." (too marketing-y)
- ❌ "This release includes..." (obvious from context)

### Sentence Structure

**Pattern**: `[Action] [Feature/Component] [Key Detail]`

✅ Good:
```
- Media control dropdown with pause/resume for media players
```
- **Action**: (implied "Added")
- **Feature**: Media control dropdown
- **Key Detail**: with pause/resume for media players

✅ Good:
```
- Faster recording startup by removing health checks
```
- **Action**: (implied "Made")
- **Feature**: recording startup
- **Key Detail**: by removing health checks

### Length Guidelines

**Target**: 10-15 words per bullet

✅ Good (12 words):
```
- New Preview Features settings with opt-in Developer Mode toggle
```

✅ Good (10 words):
```
- Fixed app crashes on macOS 26.1 for button handlers
```

❌ Too short (3 words):
```
- Fixed bugs
```
(Not specific enough)

❌ Too long (28 words):
```
- Added a new Preview Features settings section that allows users to opt-in to Developer Mode which provides access to advanced features and experimental functionality currently under development
```
(Should be: "New Preview Features settings with opt-in Developer Mode toggle")

## Specificity Guidelines

### Include Concrete Details

**Version numbers**:
```
✅ Added ElevenLabs Scribe v2 transcription provider
✅ Enhanced Parakeet model support with V2 and language badges
```

**Numbers**:
```
✅ Increased trial credits from 100 to 150 (~24 minutes)
✅ Faster recording startup by removing 60-second health checks
```

**Names and Brands**:
```
✅ Media pause/resume for Spotify and Apple Music
✅ Added Mistral Voxtral Mini with 8-language support
```

### Avoid Vague Language

❌ Vague:
```
- Made improvements to the UI
- Enhanced performance
- Fixed various issues
- Updated some components
```

✅ Specific:
```
- Standardized UI headers and improved spacing consistency
- Faster recording startup by removing health checks
- Fixed transcript editing textarea and Push to Talk toggle
- Updated to ElevenLabs Scribe v2 with improved quality
```

## Technical vs. User-Facing Language

### Convert Technical to User-Friendly

| Technical | User-Friendly |
|-----------|---------------|
| "Implemented Swift Atomics for continuation safety" | Skip (too technical) |
| "Refactored audio engine architecture" | "Improved audio recording reliability" |
| "Added MediaRemoteAdapter package" | "Media pause/resume for Spotify and Apple Music" |
| "Fixed race condition in async validation" | "Fixed validation timing issues" |
| "Optimized Core Data fetch requests" | "Faster app performance" |

### When to Use Technical Terms

**Use technical terms when**:
- They're widely known: "API", "UI", "macOS"
- They're product-specific: "Push to Talk", "Developer Mode"
- There's no simpler alternative

**Avoid technical terms when**:
- Users won't understand them
- A simpler phrase exists
- They don't add value

✅ Good (appropriate technical terms):
```
- Fixed app crashes on macOS 26.1 for button handlers
- Improved API response times across all providers
- Enhanced UI consistency across all views
```

❌ Too technical:
```
- Fixed race condition in AVAudioEngine initialization callback
- Optimized network request dispatch queue
- Refactored ViewModel observers using Combine framework
```

## Common Patterns by Change Type

### New Features
**Pattern**: `[Feature Name] with [Key Capability]`

```
- Media control dropdown with pause/resume for media players
- Auto-increase microphone volume during recording
- New Preview Features settings with Developer Mode toggle
```

### Improvements
**Pattern**: `[Improved Aspect] [Specific Benefit]`

```
- Faster recording startup by removing health checks
- Improved spacing consistency across all views
- Better error handling with user-friendly messages
```

### Bug Fixes
**Pattern**: `Fixed [Problem] [Location/Context]`

```
- Fixed app crashes on macOS 26.1
- Fixed transcript editing textarea
- Resolved race condition in background validation
```

### Provider/Integration Updates
**Pattern**: `[Provider Name] [Version/Capability]`

```
- Added Mistral Voxtral Mini with 8-language support
- Updated to ElevenLabs Scribe v2 for improved quality
- Enhanced Parakeet model support with V2 option
```

## Numbers and Statistics

### When to Include Numbers
Include numbers that add meaningful context:

✅ Good uses:
```
- Increased trial credits from 100 to 150 (~24 minutes)
- Faster recording startup by removing 60-second health checks
- Added 8-language support with Mistral provider
```

### When to Skip Numbers
Skip numbers that don't add value:

❌ Not helpful:
```
- Fixed 3 bugs in audio system
- Updated 15 UI components
- Improved performance by 0.03 seconds
```

Better without numbers:
```
- Fixed audio system bugs
- Improved UI consistency
- Faster performance
```

## Capitalization

### Title Case for Headings
```
<b>New Features and Stability Improvements</b>
<b>Enhanced UI and Clipboard Management</b>
<b>Performance Optimizations and Bug Fixes</b>
```

### Sentence case for Bullets
```
- New Preview Features settings with opt-in Developer Mode toggle
- Added Mistral Voxtral Mini transcription provider
- Fixed app crashes on macOS 26.1
```

### Proper Nouns
Always capitalize:
- Product names: "Mistral Voxtral Mini", "ElevenLabs Scribe v2"
- Features: "Developer Mode", "Preview Features", "Push to Talk"
- Platforms: "macOS", "Spotify", "Apple Music"
- Acronyms: "UI", "API", "UX"

## Punctuation

### No Periods on Single Sentences
```
✅ No period:
- Added new transcription provider

❌ With period:
- Added new transcription provider.
```

### Periods on Multiple Sentences (Rare)
```
✅ Use periods for multiple sentences:
- Fixed critical bug in audio engine. This prevents crashes on older hardware.
```

**However**: Try to combine into one sentence when possible:
```
Better:
- Fixed audio engine bug that caused crashes on older hardware
```

## Examples by Quality Level

### Excellent Descriptions

**v2.10.1**:
```xml
<b>Improved Design</b>
<ul>
    <li>New Preview Features settings with opt-in Developer Mode toggle</li>
    <li>Standardized UI headers and improved spacing consistency across all views</li>
</ul>
```

**Why excellent**:
- Clear, specific heading
- 2 concise bullets covering key changes
- User-facing language
- Concrete details

**v2.10**:
```xml
<b>Faster API Processing and Optional M4A</b>
<ul>
    <li>Increased responsiveness from all API and cloud providers</li>
    <li>Intelligent WAV fallback conversion when M4A compression fails, ensuring transcription success</li>
    <li>Settings for automatic WAV to M4A compression after transcription to reduce storage usage</li>
</ul>
```

**Why excellent**:
- Specific, benefit-focused heading
- Each bullet describes a clear value
- Technical details relevant to users
- Shows how features work together

### Poor Descriptions (to avoid)

❌ **Too vague**:
```xml
<b>Updates</b>
<ul>
    <li>Made improvements</li>
    <li>Fixed bugs</li>
    <li>Enhanced features</li>
</ul>
```

❌ **Too technical**:
```xml
<b>Code Refactoring</b>
<ul>
    <li>Implemented MVVM architecture pattern</li>
    <li>Refactored dependency injection container</li>
    <li>Updated to Swift Concurrency with async/await</li>
</ul>
```

❌ **Too marketing-y**:
```xml
<b>Amazing New Features!</b>
<ul>
    <li>We're thrilled to introduce revolutionary new capabilities!</li>
    <li>The best update ever with game-changing improvements!</li>
    <li>You're going to love these incredible enhancements!</li>
</ul>
```

## Final Quality Check

Before finalizing any description, ask:

1. **Is every word necessary?** Remove fluff
2. **Would I understand this as a user?** Avoid jargon
3. **Can I scan it in 10 seconds?** Keep it brief
4. **Does it make me want to update?** Show clear value
5. **Is it professional?** No excessive enthusiasm or formality
6. **Are details concrete?** Be specific, not vague
