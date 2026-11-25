import type { BlogPost } from "@/src/content/blog";

// VoiceInk facts: https://tryvoiceink.com/pricing and
// https://github.com/beingpax/VoiceInk (README: macOS 15+, source on GitHub),
// read 2026-11-25. HyperWhisper facts: messages/en.json and in-repo docs.
// This page does not invent ratings, user counts, or a paid HyperWhisper app
// tier. Re-read both sources before editing a number.
const html = `
<p><strong>Updated November 25, 2026.</strong> Looking for a <strong>VoiceInk alternative</strong>? VoiceInk is a native macOS voice-to-text app with a one-time Mac license and public source on GitHub. HyperWhisper is a free, open-source dictation app for Mac, Windows, and Linux. This HyperWhisper vs VoiceInk comparison is the page a “voiceink alternative” search should open, not the blog index.</p>

<p><em>VoiceInk's prices and macOS floor are taken from <a href="https://tryvoiceink.com/pricing" rel="nofollow noopener" target="_blank">tryvoiceink.com/pricing</a> and the <a href="https://github.com/beingpax/VoiceInk" rel="nofollow noopener" target="_blank">VoiceInk GitHub README</a>, as published when this page was read on November 25, 2026. Prices change; check their pages before you buy.</em></p>

<h2>HyperWhisper vs VoiceInk at a glance</h2>

<table>
  <thead>
    <tr><th></th><th>HyperWhisper</th><th>VoiceInk</th></tr>
  </thead>
  <tbody>
    <tr><td>Desktop platforms</td><td>macOS 14.6+, Windows 10/11, Linux</td><td>macOS 15 or later (their pricing page and README)</td></tr>
    <tr><td>Windows / Linux</td><td>Yes</td><td>No Windows or Linux desktop app on those pages</td></tr>
    <tr><td>Free use</td><td>App and local transcription free forever, no account</td><td>Free trial from their site; you can also build from source on GitHub</td></tr>
    <tr><td>Paid license</td><td>Not needed for the app. Optional cloud credits from $5</td><td>One-time: $25 (1 Mac), $39 (2 Macs), $49 (3 Macs), with lifetime updates</td></tr>
    <tr><td>Subscriptions</td><td>None</td><td>None on the pricing page</td></tr>
    <tr><td>Cloud transcription</td><td>HyperWhisper Cloud credits, or bring your own key</td><td>Not listed as a bundled cloud service on the pricing page</td></tr>
    <tr><td>Source</td><td>Apache-2.0, app and cloud backend</td><td>Public GitHub repo; README says GNU GPLv3</td></tr>
    <tr><td>Refunds</td><td>14-day money-back guarantee on credit purchases</td><td>14-day money-back guarantee on the pricing page</td></tr>
  </tbody>
</table>

<h2>Pricing</h2>

<p>HyperWhisper does not sell a Mac license. Local, offline transcription is free. Optional HyperWhisper Cloud is pay-as-you-go credits from $5, spent at the providers' own processing cost plus a 6% payment-processing fee, expiring 12 months after purchase. Or bring your own API key and pay that provider directly.</p>

<p>VoiceInk's pricing page is a one-time Mac license: Solo $25 for one Mac, Personal $39 for two, Extended $49 for three, with lifetime updates and a 14-day refund. Their README also says you can build the app from source, and that a purchased license funds ongoing work, automatic updates, and support.</p>

<h2>Platforms</h2>

<p>This is the largest product difference. VoiceInk is a Mac app (macOS 15+ on their current pages). HyperWhisper is the same desktop app on macOS 14.6+ (Apple Silicon or Intel), Windows 10 and 11 (x64 and ARM64), and Linux (Ubuntu 22.04+ or Debian 12+ on amd64). If “VoiceInk alternative” means you also sit at a Windows PC, VoiceInk cannot follow you there.</p>

<h2>Which VoiceInk alternative should you pick?</h2>

<ul>
  <li><strong>You only use Macs on macOS 15+, and you want to pay once for VoiceInk's Mac app:</strong> VoiceInk. Confirm the current license on their pricing page.</li>
  <li><strong>You want a free local dictation app, or you need Windows or Linux:</strong> <a href="/en/download">HyperWhisper</a>.</li>
  <li><strong>You want to read Apache-2.0 source including the cloud backend:</strong> HyperWhisper on <a href="https://github.com/ray-amjad/hyperwhisper-app" target="_blank" rel="noopener">GitHub</a>.</li>
  <li><strong>You want VoiceInk's GitHub project to build yourself:</strong> that path is on their README; it is still a Mac app.</li>
</ul>

<p><a href="/en/download">Download HyperWhisper</a> for Mac, Windows, or Linux. For Superwhisper, see <a href="/en/blog/hyperwhisper-vs-superwhisper">Superwhisper alternative</a>. For Mac dictation in general, see <a href="/en/blog/mac-voice-dictation-software">best dictation app for Mac</a>.</p>

<h2>FAQ</h2>

<h3>What is the best VoiceInk alternative?</h3>
<p>If you need Windows or Linux, or a free local desktop app with no Mac-seat license, use <a href="/en/download">HyperWhisper</a>. If you want VoiceInk's Mac-only paid app, buy it from tryvoiceink.com and confirm the current device tiers.</p>

<h3>Is there a VoiceInk for Windows?</h3>
<p>Not on VoiceInk's pricing page or GitHub README. HyperWhisper is the Windows (and Linux) alternative in this comparison.</p>

<h3>Is VoiceInk cheaper than HyperWhisper?</h3>
<p>VoiceInk's Solo license is $25 once for one Mac. HyperWhisper's app is $0. You only pay HyperWhisper if you buy optional cloud credits.</p>
`;

export const hyperwhisperVsVoiceink: BlogPost = {
  locale: "en",
  slug: "hyperwhisper-vs-voiceink",
  frontMatter: {
    title: "VoiceInk Alternative: HyperWhisper vs VoiceInk",
    description:
      "VoiceInk alternative compared: VoiceInk's Mac licenses versus HyperWhisper's free local dictation on Mac, Windows, and Linux.",
    date: "2026-11-25T19:30:00.000Z",
    tags: ["Comparison", "VoiceInk", "Pricing"],
    slug: "hyperwhisper-vs-voiceink",
  },
  content: "",
  html,
};
