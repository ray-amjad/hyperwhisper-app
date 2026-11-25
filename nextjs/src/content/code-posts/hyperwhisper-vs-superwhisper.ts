import type { BlogPost } from "@/src/content/blog";

// Superwhisper facts: https://superwhisper.com/docs/billing/plans and
// superwhisper.com (platforms), read 2026-11-25. HyperWhisper facts:
// messages/en.json (hero.*, features.*, faq.questions.*) and in-repo docs.
// This page does not invent ratings, user counts, or a paid HyperWhisper app
// tier. Re-read both sources before editing a number.
const html = `
<p><strong>Updated November 25, 2026.</strong> Looking for a <strong>Superwhisper alternative</strong>? Superwhisper and HyperWhisper are both desktop voice-to-text apps with a free local tier. Superwhisper also ships iOS and Android apps. HyperWhisper is free and open source on Mac, Windows, and Linux, with no subscription. This page is the HyperWhisper vs Superwhisper comparison so a “superwhisper” search can land on a side-by-side instead of the homepage.</p>

<p><em>Superwhisper's plans are taken from <a href="https://superwhisper.com/docs/billing/plans" rel="nofollow noopener" target="_blank">superwhisper.com/docs/billing/plans</a>, as published when this page was read on November 25, 2026. Prices change; check their page before you buy.</em></p>

<h2>HyperWhisper vs Superwhisper at a glance</h2>

<table>
  <thead>
    <tr><th></th><th>HyperWhisper</th><th>Superwhisper</th></tr>
  </thead>
  <tbody>
    <tr><td>Free plan</td><td>Free forever: local, offline transcription, no account</td><td>Free forever: unlimited dictation with local Whisper models, custom vocabulary, text replacements, up to two modes without AI processing</td></tr>
    <tr><td>Paid plan</td><td>None for the app. Optional HyperWhisper Cloud credits from $5</td><td>Pro: $8.49/month, $84.99/year, or $249.99 once (website prices)</td></tr>
    <tr><td>What paid adds</td><td>Zero-setup cloud transcription (or bring your own key for free)</td><td>Cloud models, local Parakeet and Cohere Transcribe, AI-powered modes, file transcription, BYOK, more</td></tr>
    <tr><td>Platforms</td><td>macOS 14.6+, Windows 10/11, Linux</td><td>macOS, Windows, iOS, and Android (their homepage)</td></tr>
    <tr><td>Source code</td><td>Open source (Apache-2.0), including the cloud backend</td><td>Not stated as open source on the plans page</td></tr>
    <tr><td>Refunds</td><td>14-day money-back guarantee on credit purchases</td><td>14-day refund on individual plans, from the latest charge</td></tr>
  </tbody>
</table>

<h2>Pricing</h2>

<p>HyperWhisper has no paid app tier. Local transcription is free forever. You pay only if you want HyperWhisper Cloud: credits from $5 (1,000 credits per $1) at the providers' own processing cost, plus a 6% payment-processing fee at checkout. Credits expire 12 months after purchase. Or skip credits and bring your own API key.</p>

<p>Superwhisper's docs say new users get 3,000 words of Pro features, then the app falls back to Free. Pro on superwhisper.com is $8.49 per month, $84.99 per year, or $249.99 lifetime, with one license for all of a user's Macs, Windows PCs, iPhone, and Android phone. App Store and Google Play prices are higher, per that page. Students, educators, and non-profits can request 40% off website purchases.</p>

<h2>Local models and AI formatting</h2>

<p>On Superwhisper Free, dictation uses local Whisper models and stays on the device. Superwhisper's own FAQ says Intel Macs work best with cloud models; offline models run really well on Apple Silicon. Parakeet and Cohere Transcribe local models are listed as Pro features, as are AI-powered modes (Super, Mail, and similar) and bring-your-own-key.</p>

<p>HyperWhisper includes local Whisper, NVIDIA Parakeet, and other on-device engines in the free app. Local Gemma models can clean up text without a network. Optional cloud is pay-as-you-go, not a subscription. Details are in the <a href="/docs/models">model library</a>.</p>

<h2>Which Superwhisper alternative should you pick?</h2>

<ul>
  <li><strong>You want iPhone or Android dictation with the same product:</strong> Superwhisper lists those apps; HyperWhisper is desktop-only.</li>
  <li><strong>You want Linux, or a fully free local stack including Parakeet:</strong> HyperWhisper.</li>
  <li><strong>You want cloud AI modes and will pay Pro:</strong> Superwhisper's Pro plan is built for that. HyperWhisper Cloud is credits instead of a seat.</li>
  <li><strong>You want to read the source:</strong> HyperWhisper, app and cloud backend, is open source on <a href="https://github.com/ray-amjad/hyperwhisper-app" target="_blank" rel="noopener">GitHub</a>.</li>
</ul>

<p><a href="/en/download">Download HyperWhisper</a> for Mac, Windows, or Linux. For VoiceInk, see <a href="/en/blog/hyperwhisper-vs-voiceink">VoiceInk alternative</a>. For the category roundup, see <a href="/en/blog/best-voice-recognition-software">best voice recognition software</a>.</p>

<h2>FAQ</h2>

<h3>What is the best Superwhisper alternative?</h3>
<p>If you want a free, open-source desktop dictation app on Mac, Windows, and Linux, with local models and no subscription, use <a href="/en/download">HyperWhisper</a>. If you need Superwhisper's iOS and Android apps or its Pro cloud/AI modes, stay with Superwhisper and confirm current pricing on their plans page.</p>

<h3>Is Superwhisper free?</h3>
<p>Yes, for local Whisper dictation, with the limits on their plans page (including two non-AI modes). Pro is paid.</p>

<h3>Does HyperWhisper replace Superwhisper on Windows?</h3>
<p>Both have Windows apps. HyperWhisper also supports Linux and does not require Pro to use local Parakeet. Superwhisper's one Pro license also covers mobile devices; HyperWhisper does not.</p>
`;

export const hyperwhisperVsSuperwhisper: BlogPost = {
  locale: "en",
  slug: "hyperwhisper-vs-superwhisper",
  frontMatter: {
    title: "Superwhisper Alternative: HyperWhisper vs Superwhisper",
    description:
      "Superwhisper alternative compared: Superwhisper Free and Pro versus HyperWhisper's free local dictation on Mac, Windows, and Linux.",
    date: "2026-11-25T19:00:00.000Z",
    tags: ["Comparison", "Superwhisper", "Pricing"],
    slug: "hyperwhisper-vs-superwhisper",
  },
  content: "",
  html,
};
