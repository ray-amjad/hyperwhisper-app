import type { BlogPost } from "@/src/content/blog";

// HyperWhisper facts come from this site's own copy (messages/en.json:
// hero.*, features.*, faq.questions.*) and in-repo docs (mintlify-help/models.mdx,
// system-requirements.mdx, transcription-modes.mdx). Built-in OS and browser
// tools are described in general terms. This page does not invent ratings,
// user counts, competitor prices, or a paid HyperWhisper app tier.
// Re-read those sources before editing a claim.
const html = `
<p><strong>Updated November 23, 2026.</strong> Yes: you can get a free speech to text app on Windows, Mac, Linux, and phones. Built-in voice to text (Win+H, Apple Dictation, Google Docs voice typing) costs nothing. For unlimited offline dictation on a computer, <a href="/en/download">HyperWhisper</a> is free forever with local models and no account. You pay only if you later want optional cloud transcription.</p>

<h2>Free speech to text apps compared</h2>

<table>
  <thead>
    <tr>
      <th>App</th>
      <th>Best for</th>
      <th>Platforms</th>
      <th>Offline?</th>
      <th>What is free</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td>HyperWhisper</td>
      <td>Desktop dictation in every app, including private work</td>
      <td>Windows 10/11, macOS 14.6+, Linux</td>
      <td>Yes, with on-device models</td>
      <td>The app and local transcription, forever; no account</td>
    </tr>
    <tr>
      <td>Windows voice typing (Win+H)</td>
      <td>Quick voice to text on a PC</td>
      <td>Windows 10 and 11</td>
      <td>Usually needs internet</td>
      <td>Included with Windows</td>
    </tr>
    <tr>
      <td>Apple Dictation</td>
      <td>Everyday dictation on iPhone, iPad, and Mac</td>
      <td>Apple devices</td>
      <td>On-device for many languages</td>
      <td>Included with the OS</td>
    </tr>
    <tr>
      <td>Google Docs voice typing</td>
      <td>Drafting inside a Google Doc</td>
      <td>Chrome and a Google account</td>
      <td>No</td>
      <td>Included with Docs</td>
    </tr>
    <tr>
      <td>Gboard / mobile keyboards</td>
      <td>Short messages and search on a phone</td>
      <td>Android (and similar keyboard dictation on other phones)</td>
      <td>On-device on some phones; otherwise cloud</td>
      <td>Included with the keyboard</td>
    </tr>
    <tr>
      <td>Meeting transcription apps</td>
      <td>Shared call notes, not typing into apps</td>
      <td>Web and mobile</td>
      <td>No</td>
      <td>Limited free plans; paid tiers for heavy use</td>
    </tr>
  </tbody>
</table>

<h2>Voice to text vs speech to text</h2>

<p>People search both phrases. They mean the same job: spoken words become written words. <strong>Voice to text</strong> and <strong>dictation</strong> usually mean you are writing into the app in front of you. <strong>Speech to text</strong> and <strong>transcription</strong> also cover turning a recording or a meeting into a document afterwards. Pick the tool for the job: a keyboard shortcut for drafting, a meeting app for calls, a file importer for interviews.</p>

<h2>Built-in free options</h2>

<ul>
  <li><strong>Windows:</strong> press <kbd>Win</kbd>+<kbd>H</kbd> to start voice typing. Details and limits are in our <a href="/en/blog/best-dictation-software-for-windows">Windows dictation</a> guide.</li>
  <li><strong>Apple:</strong> Apple Dictation is on iPhone, iPad, and Mac. Many languages can run on-device. It is the no-install answer if you already live in that ecosystem and you dictate everyday language.</li>
  <li><strong>Google Docs:</strong> voice typing in Chrome is free if you draft in Docs. It does not follow you into a local code editor, a native mail client, or an offline folder.</li>
  <li><strong>Phones:</strong> Gboard and the built-in iPhone keyboard are the fastest free voice to text for messages. They are not a replacement for desktop dictation.</li>
</ul>

<p>These tools are the right default for casual use. They are the wrong default when you need one shortcut everywhere, when the work is confidential, or when you are offline.</p>

<h2>Offline vs cloud</h2>

<p>Offline (on-device) speech to text downloads a model, then processes audio on your computer. Nothing is sent to a vendor for that transcription. Cloud speech to text sends audio to a provider. Cloud is often more accurate on accents, noise, and technical terms; offline is the privacy and availability guarantee.</p>

<p>HyperWhisper supports both. Local Whisper and Parakeet models (and other on-device engines listed in the <a href="/docs/models">models docs</a>) work without internet after the download. Optional cloud is either HyperWhisper Cloud pay-as-you-go credits or your own API key. In local mode, no data leaves the device.</p>

<h2>Best speech to text for a small business</h2>

<p>A small business usually needs three things a consumer keyboard does not provide: dictation into whatever tool the team already uses, a clear answer to where audio goes, and a cost model that is not another per-seat subscription.</p>

<ul>
  <li><strong>Client and internal talk:</strong> use on-device transcription for anything you would not paste into a random web app.</li>
  <li><strong>Cost:</strong> HyperWhisper's desktop app is free. There is no monthly fee to cancel. Cloud credits start at $5 when you want them, and unused credits expire after 12 months.</li>
  <li><strong>Fit:</strong> one global shortcut across email, docs, chat, and the browser beats a tool that only works inside one suite.</li>
  <li><strong>Meetings:</strong> if the artifact you need is a shared call transcript, a meeting app can still be the right extra. It is a different product from dictation.</li>
</ul>

<h2>HyperWhisper as a free speech to text app</h2>

<p>HyperWhisper is free and open source under Apache-2.0 (desktop apps and the cloud backend). It runs on macOS 14.6+ (Apple Silicon or Intel), Windows 10 and 11 (x64 or ARM64), and Linux (Ubuntu 22.04+ or Debian 12+ on amd64). Local transcription needs no account and no API key.</p>

<p>You get app-wide dictation, 100+ languages with automatic detection on most models, custom vocabulary, screen OCR, file import, and modes that reformat what you said for email, notes, meetings, or code. Real-time streaming is designed for sub-700ms latency. 8 GB of RAM is recommended.</p>

<p>Cloud transcription is optional. Bring your own key (Groq, OpenAI, Deepgram, AssemblyAI, ElevenLabs, Mistral, Soniox, Google Gemini, and others listed in the FAQ), or buy HyperWhisper Cloud credits from $5 with no markup on transcription and a 6% payment-processing fee at checkout. New Cloud installs start on ElevenLabs Scribe v2; post-processing starts on Claude Haiku 5.5. There is never a HyperWhisper subscription.</p>

<p><a href="/en/download">Download HyperWhisper free</a> for Windows, Mac, or Linux.</p>

<h2>Other free-tier tools</h2>

<p>Plenty of other products have a free tier. Treat the limits as part of the product:</p>

<ul>
  <li><strong>Browser notepads and extensions</strong> are fine for dumping a paragraph into a web box. They inherit the browser's speech service, need the internet, and are a weak fit for confidential work.</li>
  <li><strong>Meeting recorders</strong> help a team search a call later. Free plans usually cap minutes. They do not paste into your IDE.</li>
  <li><strong>Mac-only local transcribers</strong> are useful if your job is dropping files onto a Mac, not dictating all day across Windows and Linux as well.</li>
</ul>

<p>For Windows-only built-in vs dedicated apps, see <a href="/en/blog/best-dictation-software-for-windows">Windows dictation in 2026</a>. For side-by-side desktop comparisons, see <a href="/en/blog/hyperwhisper-vs-spokenly">HyperWhisper vs Spokenly</a> and <a href="/en/blog/hyperwhisper-vs-handy-vs-fluidvoice">HyperWhisper vs Handy vs FluidVoice</a>.</p>

<h2>FAQ</h2>

<h3>Is there a free speech to text app for Windows and Mac?</h3>
<p>Yes. Windows includes Win+H voice typing; Apple includes Dictation. For a dedicated desktop app that is free for unlimited local use on Windows, Mac, and Linux, download <a href="/en/download">HyperWhisper</a>. No account is required for offline models.</p>

<h3>Is voice to text the same as speech to text?</h3>
<p>For searchers, yes. Both mean converting speech into writing. Use dictation / voice to text when you want words at the cursor; use transcription when you have a recording or a meeting to turn into a document.</p>

<h3>What is the best speech to text for a small business?</h3>
<p>Start with where the text has to land and whether audio may leave the building. If people dictate into many apps and handle client material, a free local desktop app is the lowest-risk default. HyperWhisper matches that: free offline use, optional cloud credits instead of a seat license. If the need is shared meeting memory, add a meeting transcriber rather than stretching a dictation app.</p>

<h3>Can I use speech to text fully offline?</h3>
<p>Yes, if the app runs a local model. HyperWhisper can be used entirely offline on macOS, Windows, or Linux with local AI models. Built-in Windows voice typing typically needs internet. Apple Dictation can be on-device for many languages. Browser tools generally cannot work offline.</p>

<h3>Do I need a subscription for free speech to text?</h3>
<p>Not for HyperWhisper. The app is free; local transcription stays free. There is no HyperWhisper subscription. Optional Cloud credits are pay-as-you-go, with a 14-day money-back guarantee on credit purchases. OS-level dictation also has no extra subscription, though Microsoft 365 dictation follows your Office plan.</p>
`;

export const freeSpeechToTextApp: BlogPost = {
  locale: "en",
  slug: "free-speech-to-text-app",
  frontMatter: {
    title: "Free Speech to Text App in 2026: Voice to Text, Offline",
    description:
      "Find a free speech to text app for Mac, Windows, or phone. Compare voice to text, offline vs cloud, and options that work for small business.",
    date: "2026-11-23T00:00:00.000Z",
    tags: ["Speech to text", "Voice to text", "Free"],
    slug: "free-speech-to-text-app",
  },
  content: "",
  html,
};
