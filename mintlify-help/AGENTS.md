# Mintlify documentation

HyperWhisper's documentation site, built on Mintlify. Content is MDX files with YAML frontmatter; `docs.json` defines navigation, theme, and settings; pages use Mintlify components.

<important if="you are writing or editing any documentation content">
- Document just enough for user success — not too much, not too little; prioritize accuracy and usability
- Make content evergreen when possible
- Search for existing information before adding new content; avoid duplication unless strategic
- Check existing patterns and match the style/formatting of existing pages
- Start by making the smallest reasonable changes
- Never lie, guess, or make up information — ask for clarification rather than assuming
</important>

<important if="you are creating or editing an MDX page">
- Frontmatter is required on every MDX file: `title` (clear, descriptive) and `description` (concise summary for SEO/navigation)
- Use second-person voice ("you")
- Put prerequisites at the start of procedural content; include both basic and advanced use cases
- Add language tags to all code blocks and alt text to all images
- Use relative paths for internal links (never absolute URLs)
- Test all code examples before including them
</important>

<important if="you are committing changes to this repo">
- Ask how to handle uncommitted changes before starting
- Create a new branch when no clear branch exists for the changes
- Commit frequently throughout development
- Never use `--no-verify`; never skip or disable pre-commit hooks
</important>

<important if="the user proposes a documentation idea or approach">
You can push back on ideas — it can lead to better documentation. Cite sources and explain your reasoning when you do.
</important>
