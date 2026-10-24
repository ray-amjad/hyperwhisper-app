import type { BlogPost } from "@/src/content/blog";

// Every Spokenly fact below comes from https://spokenly.app/pricing, read on
// 2026-10-23. Every HyperWhisper fact comes from this site's own copy
// (messages/en.json: faq.questions.*, hero.*). Re-read both before editing a
// number: a comparison that goes stale is worse than none.
const html = `
<p>Spokenly and HyperWhisper are both desktop dictation apps that run on macOS, Windows and Linux, and both let you dictate offline with local models for free. Spokenly now also sells <strong>Local+</strong>, a $29 one-time plan, next to its <strong>Pro</strong> subscription. If you are comparing cheap dictation that works on more than one operating system, this page puts the two side by side.</p>

<p><em>Spokenly's plans and prices are taken from <a href="https://spokenly.app/pricing" rel="nofollow noopener" target="_blank">spokenly.app/pricing</a>, as published on October 23, 2026. Prices change; check their page before you buy.</em></p>

<h2>Pricing at a glance</h2>

<table>
  <thead>
    <tr><th></th><th>HyperWhisper</th><th>Spokenly</th></tr>
  </thead>
  <tbody>
    <tr><td>Free plan</td><td>Free forever: local, offline transcription</td><td>Free forever: local Whisper and Parakeet models, offline, unlimited use</td></tr>
    <tr><td>One-time purchase</td><td>Not needed: the app itself is free and open source</td><td>Local+, $29 once: Notch and Capsule recording styles, settings sync across devices, priority support</td></tr>
    <tr><td>Subscription</td><td>None, ever</td><td>Pro, $99.99 billed yearly (from $8.33/month); a monthly option is also offered</td></tr>
    <tr><td>Built-in cloud transcription</td><td>HyperWhisper Cloud: pay-as-you-go credits from $5, spent at the providers' own processing cost, plus a 6% payment-processing fee at checkout</td><td>Included with Pro only</td></tr>
    <tr><td>Bring your own API keys</td><td>Yes, free</td><td>Yes, free on every plan</td></tr>
    <tr><td>Platforms</td><td>macOS, Windows, Linux</td><td>macOS, Windows, Linux, iOS</td></tr>
    <tr><td>Source code</td><td>Open source (Apache-2.0), including the cloud backend</td><td>Not stated on the pricing page</td></tr>
    <tr><td>Refunds</td><td>14-day money-back guarantee on credit purchases</td><td>Pro bought on spokenly.app: 14-day money-back guarantee</td></tr>
  </tbody>
</table>

<h2>What Spokenly's $29 Local+ plan includes</h2>

<p>According to Spokenly's pricing page, Local+ is for people who dictate with local models or their own API keys. For $29, paid once, it adds three things to the free plan:</p>

<ul>
  <li>the Notch and Capsule recording styles;</li>
  <li>settings sync across devices;</li>
  <li>priority support.</li>
</ul>

<p>Spokenly's cloud models and hosted AI are <strong>not</strong> part of Local+; they belong to Pro. With Local+ you still need your own API keys for cloud transcription and AI cleanup. Spokenly says Local+ works in its latest Mac app from spokenly.app, its latest iPhone app, and its latest Windows and Linux apps, and that the Mac App Store version will get it in a future update.</p>

<p>Dictation itself does not need Local+: Spokenly's free plan already includes unlimited offline dictation with Whisper and Parakeet models, in 100+ languages, with no account needed.</p>

<h2>What HyperWhisper costs</h2>

<p>HyperWhisper has no paid app tier to compare Local+ against. The app is free and open source under Apache-2.0, and local, offline transcription costs nothing, forever.</p>

<p>You pay only if you want HyperWhisper's own cloud transcription. HyperWhisper Cloud is pay-as-you-go: you top up from $5 (1,000 credits per $1) and spend credits only as you transcribe, at the providers' own processing cost with no markup on transcription. Checkout adds a flat 6% payment-processing fee. There is no monthly fee and nothing to cancel. Credits expire 12 months after purchase.</p>

<p>Or skip the cloud credits and bring your own API key from providers such as OpenAI, Groq, Deepgram, AssemblyAI, ElevenLabs, Mistral, Soniox or Google Gemini, and pay the provider directly.</p>

<h2>Cloud transcription: subscription or pay-as-you-go</h2>

<p>This is where the two differ most.</p>

<ul>
  <li><strong>Spokenly</strong> includes managed cloud transcription (its pricing page names GPT-4o Transcribe, Deepgram Nova 3 and Soniox) and hosted AI text processing in <strong>Pro</strong>, a subscription billed $99.99 a year.</li>
  <li><strong>HyperWhisper</strong> sells cloud transcription as credits you buy when you need them, with 12 transcription providers to choose from. If you dictate a little, you pay a little; nothing renews.</li>
</ul>

<p>Both apps let you avoid paying them for cloud use at all by connecting your own provider API keys.</p>

<h2>Platforms</h2>

<p>Both apps cover all three desktop operating systems. HyperWhisper runs on macOS 14.6+ (Apple Silicon or Intel), Windows 10 and 11 (x64 or ARM64), and Linux (Ubuntu 22.04+ or Debian 12+ on amd64). Spokenly lists apps for macOS, Windows, Linux and iOS. If you need dictation on an iPhone, Spokenly has an app there; HyperWhisper is a desktop app.</p>

<h2>Which should you pick?</h2>

<ul>
  <li><strong>You only dictate offline:</strong> both are free. Spokenly's Local+ adds recording styles, settings sync and priority support for $29; HyperWhisper has no paid tier for local use.</li>
  <li><strong>You want built-in cloud accuracy without a subscription:</strong> HyperWhisper Cloud's pay-as-you-go credits start at $5 and never renew.</li>
  <li><strong>You want an iPhone app as well:</strong> Spokenly lists one; HyperWhisper is desktop-only.</li>
  <li><strong>You want to read the source:</strong> HyperWhisper, app and cloud backend, is open source on <a href="https://github.com/ray-amjad/hyperwhisper-app" target="_blank" rel="noopener">GitHub</a>.</li>
</ul>

<p><a href="/en/download">Download HyperWhisper</a> for free on macOS, Windows or Linux and try it alongside whatever you use today.</p>
`;

export const hyperwhisperVsSpokenly: BlogPost = {
  locale: "en",
  slug: "hyperwhisper-vs-spokenly",
  frontMatter: {
    title:
      "HyperWhisper vs Spokenly: Free and Cheap Dictation for Mac, Windows and Linux",
    description:
      "HyperWhisper vs Spokenly compared: Spokenly's $29 one-time Local+ plan and Pro subscription against HyperWhisper's free, open-source app and pay-as-you-go cloud credits.",
    date: "2026-10-23T00:00:00.000Z",
    tags: ["Comparison", "Spokenly", "Pricing"],
    slug: "hyperwhisper-vs-spokenly",
  },
  content: "",
  html,
};
