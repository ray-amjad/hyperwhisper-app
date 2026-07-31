# Content Selection Guide

How to choose which changes to include in appcast descriptions.

## ⚠️ CRITICAL: macOS App Changes ONLY

**This appcast is for macOS app users.** They do NOT care about:
- Backend/cloud changes (backend-v1-cloudflare/, backend-v2-flyio/)
- Website changes (nextjs/)
- Database changes (drizzle migrations)
- Documentation or skills (.claude/, *.md)

**ALWAYS filter git commits to `app/macos/` directory only:**
```bash
git log PREV_VERSION..HEAD --oneline --no-merges -- app/macos/
```

If the only changes are backend/website, use a generic description like "Bug fixes and improvements" rather than listing irrelevant backend changes.

## Overview

Appcast descriptions appear in update notifications shown to users. They must be:
- **Concise**: 4-6 bullet points maximum
- **User-focused**: Only changes users will notice
- **Scannable**: Quick to read and understand
- **Compelling**: Give users a reason to update

## Selection Criteria

### Priority 1: New Features
Major new capabilities that expand functionality.

**Include**:
- ✅ New transcription providers
- ✅ New major features (auto-volume, media control)
- ✅ New UI sections or workflows

**Skip**:
- ❌ Minor UI additions (new button, new label)
- ❌ Developer-only features

**Example**:
```
- New Preview Features settings with opt-in Developer Mode toggle
```

### Priority 2: Major Improvements
Significant enhancements to existing features.

**Include**:
- ✅ Performance improvements with measurable impact
- ✅ UX improvements users will notice
- ✅ Quality improvements (better accuracy, reliability)

**Skip**:
- ❌ Internal refactoring
- ❌ Code quality improvements
- ❌ Minor spacing/layout tweaks

**Example**:
```
- Increased responsiveness from all API and cloud providers
```

### Priority 3: Important Bug Fixes
Fixes for issues that affected users.

**Include**:
- ✅ Crash fixes
- ✅ Fixes for common bugs
- ✅ Data loss prevention

**Skip**:
- ❌ Rare edge case fixes
- ❌ Developer-only bug fixes
- ❌ Fixes for unreleased features

**Example**:
```
- Fixed transcript editing textarea
```

### Priority 4: Credit/Trial Changes
Changes to credit system or trial limitations.

**Include**:
- ✅ Credit increases
- ✅ New trial features
- ✅ Pricing changes

**Example**:
```
- Increased trial credits from 100 to 150 (~24 minutes)
```

## How Many to Include?

**Target: 4-6 bullet points**

### Small Release (1-2 key changes)
Use 2-3 bullets, focus on the most important:

```xml
<b>Improved Design</b>
<ul>
    <li>New Preview Features settings with opt-in Developer Mode toggle</li>
    <li>Standardized UI headers and improved spacing consistency across all views</li>
</ul>
```

### Medium Release (3-5 key changes)
Use 4-5 bullets:

```xml
<b>Error Handling and Transcription Improvements</b>
<ul>
    <li>Non-activating error toast notifications for improved visibility when app is minimized</li>
    <li>Retry functionality for failed audio transcriptions with easy recovery</li>
    <li>Fixed transcript editing textarea</li>
    <li>Fixed Push to Talk toggle behavior to prevent accidental recording cancellation</li>
</ul>
```

### Large Release (6+ key changes)
Use 6 bullets, choose highest impact:

```xml
<b>New Providers and Audio Improvements</b>
<ul>
    <li>Added Mistral Voxtral Mini and ElevenLabs Scribe v2 transcription providers</li>
    <li>Enhanced Parakeet model support with V2 and language badges</li>
    <li>Media control dropdown with pause/resume for media players</li>
    <li>Auto-increase microphone volume during recording</li>
    <li>Faster recording startup by removing health checks</li>
    <li>Improved error handling and microphone selection persistence</li>
</ul>
```

## Decision Matrix

Use this to decide what to include:

| Change Type | User-Visible? | High Impact? | Include? |
|-------------|---------------|--------------|----------|
| New transcription provider | Yes | Yes | ✅ Yes |
| Major performance improvement | Yes | Yes | ✅ Yes |
| New UI feature | Yes | Yes | ✅ Yes |
| Important bug fix | Yes | Yes | ✅ Yes |
| Minor UI tweak | Yes | No | ⚠️ Maybe |
| Internal refactoring | No | No | ❌ No |
| Dependency update | No | No | ❌ No |
| Code quality improvement | No | No | ❌ No |

## Combining Related Changes

Sometimes multiple changes should be combined into one bullet:

### Example 1: Multiple Provider Updates
**Individual changes**:
- Added Mistral Voxtral Mini
- Added ElevenLabs Scribe v2
- Added Parakeet V2

**Combined**:
```
- Added Mistral Voxtral Mini, ElevenLabs Scribe v2, and Parakeet V2 transcription options
```

### Example 2: UI Consistency Updates
**Individual changes**:
- Standardized headers
- Improved spacing
- Better alignment

**Combined**:
```
- Standardized UI headers and improved spacing consistency across all views
```

## Sourcing Changes

### From GitHub Release Notes

If release notes exist, use the **Highlights** section as your primary source:

1. Read highlights section
2. Extract 4-6 most important items
3. Condense each to one sentence
4. Maintain user-facing language

**Example mapping**:

**Highlight**:
> **Faster Recording Startup**: Removed provider health checks that could delay recording by up to 60 seconds. Provider errors now surface during transcription instead of blocking the recording button, making HyperWhisper feel instantly responsive.

**Appcast bullet**:
```
- Faster recording startup by removing health checks that could delay up to 60 seconds
```

### From Git Commits (Primary Method)

Auto-generate descriptions from commit history between version tags:

**Step 1: Find previous version**
```bash
git tag -l | sort -V | grep -B1 "^VERSION$" | head -1
```

**Step 2: Get commits**
```bash
git log PREV_VERSION..VERSION --oneline --no-merges
```

**Step 3: Filter commits by path**

Only include commits affecting app code:
```bash
git log PREV_VERSION..VERSION --oneline --no-merges -- "*.swift" "app/"
```

Exclude commits only touching:
- `nextjs/` - Website changes
- `backend-v1-cloudflare/` - Backend changes (v1)
- `backend-v2-flyio/` - Backend changes (v2)
- `.claude/` - Skill/documentation
- `*.md` - Documentation

**Step 4: Categorize by commit message prefix**

| Prefix | Category | Priority |
|--------|----------|----------|
| Add, Implement, Introduce | New Feature | 1 |
| Improve, Update, Enhance | Enhancement | 2 |
| Fix, Resolve, Correct | Bug Fix | 3 |
| Refactor, Clean, Optimize | Technical | 4 (skip unless major) |

**Step 5: Group and summarize**

1. Identify user-facing changes
2. Group related commits (e.g., multiple "Add custom endpoint" commits → one bullet)
3. Focus on New Features and Enhancements
4. Create 4-6 bullet points

### From User Input

If user provides changes:

1. Take their list as starting point
2. Expand or condense as needed
3. Make language user-friendly
4. Organize by importance

## Examples by Release Type

### Feature Release
Focus on new capabilities:

```xml
<b>New Features and Audio Enhancements</b>
<ul>
    <li>Added Mistral Voxtral Mini transcription provider with 8-language support</li>
    <li>Media control dropdown with pause/resume for Spotify and Apple Music</li>
    <li>Auto-increase microphone volume during recording</li>
    <li>Enhanced Parakeet model support with V2 and language badges</li>
</ul>
```

### Performance Release
Emphasize speed and responsiveness:

```xml
<b>Performance Optimizations and Improvements</b>
<ul>
    <li>Faster recording startup by removing provider health checks</li>
    <li>Increased responsiveness from all API and cloud providers</li>
    <li>Improved audio processing with intelligent fallback conversion</li>
</ul>
```

### Bug Fix Release
Highlight reliability improvements:

```xml
<b>Stability and Bug Fixes</b>
<ul>
    <li>Fixed app crashes on macOS 26.1 for button handlers</li>
    <li>Resolved race condition in background validation</li>
    <li>Improved error handling and recovery</li>
</ul>
```

### Mixed Release
Balance across categories:

```xml
<b>New Features and Stability Improvements</b>
<ul>
    <li>New Preview Features settings with Developer Mode toggle</li>
    <li>Standardized UI headers across all views</li>
    <li>Improved error handling and logging</li>
    <li>Fixed transcript editing textarea</li>
</ul>
```

## What to Always Skip

Never include in appcast descriptions:

- Version number bumps
- Merge commits
- Internal code refactoring
- Dependency updates (unless user-facing)
- Documentation changes
- Build system changes
- Test additions
- Comment updates
- Typo fixes in code

## Testing Your Selection

Before finalizing, ask:

1. **Would this make me want to update?** If not, reconsider choices
2. **Can I understand each point in 3 seconds?** If not, simplify
3. **Are all points user-facing?** If not, remove technical items
4. **Is anything critical missing?** If yes, add it
5. **Is anything redundant?** If yes, combine or remove

## Final Checklist

- [ ] 4-6 bullet points (not more, not less)
- [ ] All points are user-facing
- [ ] Most important changes included
- [ ] Technical details removed
- [ ] Related changes combined
- [ ] Clear, concise language
- [ ] No duplicate information
- [ ] Compelling reasons to update
