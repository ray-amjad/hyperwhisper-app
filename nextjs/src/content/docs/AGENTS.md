# Docs content

HyperWhisper's documentation, served by the Next.js site at `/docs` (`nextjs/app/docs`). One `.mdx` file per page, with YAML frontmatter. The path under this folder is the URL under `/docs`: `models.mdx` is `/docs/models`, `windows/faq.mdx` is `/docs/windows/faq`, `index.mdx` is `/docs`.

<important if="you are adding, moving, or removing a docs page">
- Add the page to `nextjs/lib/docs/nav.ts` too. It is the one ordered tree for the sidebar, prev/next, the sitemaps and `/docs/llms.txt`; `nextjs/tests/docs-content.test.ts` fails when a file and the nav disagree.
- Never delete or rename a published URL without a redirect: add it to `docsRedirects` in `nextjs/config/docs.mjs`, and keep the old URL in `nextjs/tests/fixtures/docs-old-urls.json`.
- The Local API endpoint pages (`/docs/api-reference/<operation>`) are generated from `nextjs/public/docs/api-reference/local-api/openapi.yaml`; an operation added there needs its `endpoint(...)` entry in the nav.
</important>

<important if="you are writing or editing any documentation content">
- Document just enough for user success — not too much, not too little; prioritize accuracy and usability
- Make content evergreen when possible
- Search for existing information before adding new content; avoid duplication unless strategic
- Check existing patterns and match the style/formatting of existing pages
- Never lie, guess, or make up information — ask for clarification rather than assuming
</important>

<important if="you are creating or editing an MDX page">
- Frontmatter is required on every page: `title` (it becomes `<title> - HyperWhisper`) and `description` (the meta description)
- Use second-person voice ("you")
- Put prerequisites at the start of procedural content
- Add language tags to all code blocks and alt text to all images
- Internal links are root-relative with the `/docs` prefix: `[Models](/docs/models)`. Images live in `nextjs/public/docs/images/` and are linked as `/docs/images/<file>`
- Components available (Mintlify-compatible, `nextjs/components/docs/mdx-components.tsx`): `Note`, `Tip`, `Warning`, `Info`, `Card`, `CardGroup`, `Steps`/`Step`, `Tabs`/`Tab`, `Accordion`/`AccordionGroup`, `Frame`, `CodeGroup`. A new one needs an entry there first; the test fails on an unknown tag
- MDX, not Markdown: a bare `<` or `{` in prose must be escaped or put in backticks
</important>
