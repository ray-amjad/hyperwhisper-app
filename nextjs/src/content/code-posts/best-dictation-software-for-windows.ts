import type { BlogPost } from "@/src/content/blog";

// HyperWhisper facts come from this site's own copy (messages/en.json:
// hero.*, features.*, faq.questions.*) and in-repo docs (mintlify-help/models.mdx,
// system-requirements.mdx, windows-getting-started.mdx, transcription-modes.mdx).
// Built-in Windows dictation is the public Win+H / Voice typing feature; this
// page does not invent competitor prices, star ratings, or accuracy percentages.
// Re-read those sources before editing a claim.
const html = `
<p><strong>Updated November 23, 2026.</strong> Windows dictation is built in: put the cursor in a text field, press <kbd>Win</kbd>+<kbd>H</kbd>, and speak. That is enough for occasional emails. If you want offline transcription, custom vocabulary, or the same shortcut in every app, install a dedicated dictation app. <a href="/en/download">HyperWhisper</a> is free and open source, runs locally on Windows 10 and 11, and does not need an account.</p>

<h2>How to use Windows dictation (Win+H)</h2>

<ol>
  <li>Click in any text field (Notepad, Word, a browser, a chat box).</li>
  <li>Press the Windows logo key and <kbd>H</kbd> together.</li>
  <li>Speak clearly. Voice typing inserts the words at the cursor.</li>
  <li>Press <kbd>Win</kbd>+<kbd>H</kbd> again, or select the microphone, to stop.</li>
</ol>

<p>That shortcut is Microsoft's <strong>voice typing</strong>, sometimes called Windows dictation. It is not the same as <strong>Voice Access</strong>, which is a Windows 11 accessibility feature for controlling the PC by voice. Voice typing is the fast way to dictate; Voice Access is the hands-free way to click, switch apps, and dictate.</p>

<p>Built-in Windows speech often needs an internet connection, depending on your PC and settings. If the audio must stay on the machine, use an app with local models instead of relying on Win+H alone. See Microsoft's own voice-typing help if the shortcut does nothing on your PC.</p>

<h2>Windows dictation options compared</h2>

<table>
  <thead>
    <tr>
      <th>Option</th>
      <th>Best for</th>
      <th>Where it runs</th>
      <th>Offline?</th>
      <th>Cost</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td>Windows voice typing (Win+H)</td>
      <td>Quick dictation in any text field</td>
      <td>Windows 10 and 11</td>
      <td>Usually needs internet</td>
      <td>Included with Windows</td>
    </tr>
    <tr>
      <td>Windows 11 Voice Access</td>
      <td>Hands-free control plus dictation</td>
      <td>Windows 11</td>
      <td>Designed as an on-device accessibility feature</td>
      <td>Included with Windows</td>
    </tr>
    <tr>
      <td>Microsoft 365 dictation</td>
      <td>Drafting inside Word, Outlook, PowerPoint</td>
      <td>Office apps with a qualifying Microsoft 365 plan</td>
      <td>Cloud</td>
      <td>Included with qualifying Microsoft 365 plans</td>
    </tr>
    <tr>
      <td>HyperWhisper</td>
      <td>App-wide dictation, privacy, and optional cloud accuracy</td>
      <td>Windows 10/11 (x64 and ARM64); also macOS 14.6+ and Linux</td>
      <td>Yes, with local Whisper, Parakeet, and other on-device models</td>
      <td>App free forever; optional pay-as-you-go cloud credits</td>
    </tr>
    <tr>
      <td>Specialized desktop dictation (e.g. Dragon)</td>
      <td>High-volume professional vocab and commands</td>
      <td>Windows desktop products</td>
      <td>Often on-device in the desktop editions</td>
      <td>Paid license; check the vendor</td>
    </tr>
    <tr>
      <td>Meeting transcription apps</td>
      <td>Shared call notes, not typing into apps</td>
      <td>Cloud services</td>
      <td>No</td>
      <td>Free tier and subscriptions vary</td>
    </tr>
  </tbody>
</table>

<h2>When Win+H is enough</h2>

<p>Start with the built-in shortcut. If you dictate a few emails a week, stay inside Word, or you are testing whether voice input even helps, Windows dictation is already on the PC. No install, no account, no extra fee.</p>

<p>You will feel the ceiling quickly if you:</p>

<ul>
  <li>need the same workflow in Slack, a browser, a code editor, and a CRM;</li>
  <li>work with names, product jargon, or code that the built-in recognizer mangles;</li>
  <li>cannot send microphone audio to a cloud service;</li>
  <li>want dictation on a train, a plane, or a locked-down network.</li>
</ul>

<h2>HyperWhisper on Windows</h2>

<p>HyperWhisper is a native Windows app (WPF on .NET) for Windows 10 and 11, with installers for x64 and ARM64. Press a shortcut, speak, and the cleaned-up text is pasted where the cursor is. It works in any app where you can type.</p>

<p>Local, offline transcription is free forever. You do not need an account. After you download a model, audio stays on the device. The <a href="/docs/models">model library</a> includes Whisper (99+ languages), NVIDIA Parakeet, and other on-device options; which engine you pick is a trade between privacy, language coverage, speed, and accuracy. Cloud models are generally more accurate than local ones. Custom vocabulary helps with names and jargon on any model.</p>

<p>You pay only if you want zero-setup cloud transcription. HyperWhisper Cloud is pay-as-you-go credits from $5 (1,000 credits per $1), spent at the providers' own processing cost plus a 6% payment-processing fee at checkout. Credits expire 12 months after purchase. Or bring your own API key from providers such as OpenAI, Groq, Deepgram, AssemblyAI, ElevenLabs, Mistral, Soniox, or Google Gemini. There is no subscription.</p>

<p>Modes rewrite what you said for where it is going (email, notes, meetings, code, and similar workflows). Screen OCR can read on-screen spellings. You can also import audio or video files. The app is open source under Apache-2.0, including the cloud backend. 8 GB of RAM is recommended. For install steps, see the <a href="/docs/windows-getting-started">Windows getting started</a> guide.</p>

<p><a href="/en/download">Download HyperWhisper for Windows</a> (and for Mac or Linux if you use those too).</p>

<h2>Other Windows dictation tools</h2>

<p>The rest of the field is real, but the right pick depends on the job. This page does not rank them with star scores or quote prices we cannot verify.</p>

<ul>
  <li><strong>Microsoft 365 dictation</strong> is the low-friction choice if almost all writing happens in Word, Outlook, or PowerPoint and you already pay for Microsoft 365.</li>
  <li><strong>Dragon</strong> and similar professional Windows products still make sense when dictation is the job: custom commands, large vocabularies, and all-day use. Expect a paid license and setup time. Confirm current editions and prices with the vendor.</li>
  <li><strong>Meeting tools</strong> (cloud transcription for calls) are for shared notes, speaker labels, and search after the meeting. They are a poor substitute for typing into the app in front of you, and they send audio to a service.</li>
  <li><strong>Creator / media editors</strong> that transcribe a recording so you can edit the media as text are for podcasts and video, not for filling in a form.</li>
  <li><strong>Scriptable voice control</strong> (for example Talon-style setups) is for developers and accessibility users who will invest in a command language. It is not a one-click dictation app.</li>
</ul>

<p>If you also want a free speech-to-text app on Mac or your phone, see <a href="/en/blog/free-speech-to-text-app">Free speech to text app in 2026</a>. For how HyperWhisper compares with other free desktop dictation apps, see <a href="/en/blog/hyperwhisper-vs-spokenly">HyperWhisper vs Spokenly</a> and <a href="/en/blog/hyperwhisper-vs-handy-vs-fluidvoice">HyperWhisper vs Handy vs FluidVoice</a>.</p>

<h2>FAQ</h2>

<h3>How do I turn on Windows dictation?</h3>
<p>Place the cursor in a text field and press <kbd>Win</kbd>+<kbd>H</kbd>. That opens voice typing. If nothing happens, check that the microphone is allowed for the app you are in, and that Windows speech features are enabled in Settings. Voice Access, if you need hands-free control of Windows 11, is a separate feature in Accessibility settings.</p>

<h3>Is Windows dictation free?</h3>
<p>Yes. Voice typing (Win+H) and Windows 11 Voice Access ship with Windows. Microsoft 365 dictation is free only in the sense that it is bundled with a qualifying Microsoft 365 plan, not as a standalone extra you buy from a dictation vendor.</p>

<h3>Is there free speech-to-text on Windows besides Win+H?</h3>
<p>Yes. <a href="/en/download">HyperWhisper</a> is a free, open-source Windows app with unlimited local, offline transcription. You can also use browser tools such as Google Docs voice typing, which stay inside the browser and need the internet.</p>

<h3>Is Windows dictation offline?</h3>
<p>Not reliably. Voice typing commonly uses online speech recognition. Some newer PCs can run more speech on-device, but that depends on hardware and settings. If you need dictation that works with the network off, on every Windows 10 or 11 PC, use local models in an app such as HyperWhisper. Details of each on-device engine are in the <a href="/docs/models">models docs</a>.</p>

<h3>What is the difference between Win+H, Voice Access, and a dictation app?</h3>
<p><kbd>Win</kbd>+<kbd>H</kbd> is voice typing into the focused text field. Voice Access is Windows 11 hands-free control of the desktop. A dictation app adds a global shortcut, optional offline models, vocabulary, and formatting modes, and it keeps working when you leave Office.</p>

<h3>Does HyperWhisper work on Windows ARM (Snapdragon)?</h3>
<p>Yes. There is a native ARM64 installer as well as x64. Local Whisper runs on Windows 11 ARM64; on Windows 10 ARM64, use other local engines or cloud transcription. See <a href="/docs/system-requirements">system requirements</a>.</p>
`;

export const bestDictationSoftwareForWindows: BlogPost = {
  locale: "en",
  slug: "best-dictation-software-for-windows",
  frontMatter: {
    title: "Windows Dictation in 2026: Win+H vs Best Software",
    description:
      "Use Windows dictation with Win+H, or pick a better app. Compare Voice Access, offline tools, and free speech-to-text for Windows 10 and 11.",
    date: "2026-11-23T12:00:00.000Z",
    tags: ["Windows", "Dictation", "Voice typing"],
    slug: "best-dictation-software-for-windows",
  },
  content: "",
  html,
};
