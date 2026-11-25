import type { BlogPost } from "@/src/content/blog";

// HyperWhisper facts come from this site's own copy (messages/en.json:
// hero.*, features.*, faq.questions.*) and in-repo docs (src/content/docs/models.mdx,
// system-requirements.mdx, windows-getting-started.mdx, transcription-modes.mdx).
// Built-in Windows dictation is the public Win+H / Voice typing feature; this
// page does not invent competitor prices, star ratings, or accuracy percentages.
// Re-read those sources before editing a claim.
const html = `
<p><strong>Updated November 25, 2026.</strong> Windows dictation is built in: put the cursor in a text field, press <kbd>Win</kbd>+<kbd>H</kbd>, and speak. That is enough for occasional emails. If you want offline transcription, custom vocabulary, or the same shortcut in every app, install a dedicated dictation app. <a href="/en/download">HyperWhisper</a> is free and open source, runs locally on Windows 10 and 11, and does not need an account.</p>

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

<p>If you also want a free speech-to-text app on Mac or your phone, see <a href="/en/blog/free-speech-to-text-app">Free speech to text app in 2026</a>. For the cross-platform roundup, see <a href="/en/blog/best-dictation-software">best dictation software (2026)</a>. For Apple Dictation vs third-party apps, see <a href="/en/blog/mac-voice-dictation-software">best dictation app for Mac</a>. For how HyperWhisper compares with other free desktop dictation apps, see <a href="/en/blog/hyperwhisper-vs-spokenly">HyperWhisper vs Spokenly</a> and <a href="/en/blog/hyperwhisper-vs-handy-vs-fluidvoice">HyperWhisper vs Handy vs FluidVoice</a>.</p>

<h2>Windows Voice Access / Speech Recognition vs Dragon vs HyperWhisper</h2>

<p><strong>Windows 10 dictation vs Dragon</strong> is a different question from Win+H vs a dedicated app. Windows ships more than one speech feature, and Dragon is a paid professional product, not a built-in toggle. This section puts the four things people actually mean next to each other. Dragon facts below are from <a href="https://dragon.nuance.com/en-us/dragon-professional" rel="nofollow noopener" target="_blank">Nuance's Dragon Professional page</a> (read November 25, 2026). Voice typing, Voice Access, and Windows Speech Recognition facts are from Microsoft Support, including the note that Voice Access replaced Windows Speech Recognition on Windows 11 22H2 and later in September 2024. This page does not repeat vendor accuracy percentages or quote a Dragon price.</p>

<table>
  <thead>
    <tr>
      <th></th>
      <th>Voice Access (Windows 11)</th>
      <th>Speech Recognition (legacy)</th>
      <th>Dragon Professional</th>
      <th>HyperWhisper</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td>What it is</td>
      <td>Windows 11 hands-free control plus dictation</td>
      <td>The older Windows Speech Recognition command-and-dictation feature (WSR)</td>
      <td>Paid Windows desktop speech recognition (v16 on Nuance's current page)</td>
      <td>Dedicated dictation app with local models and optional cloud</td>
    </tr>
    <tr>
      <td>Windows 10</td>
      <td>No — Voice Access needs Windows 11 22H2+</td>
      <td>Yes</td>
      <td>Yes — Nuance lists v16 as backwards-compatible to Windows 10</td>
      <td>Yes (x64 and ARM64)</td>
    </tr>
    <tr>
      <td>Windows 11</td>
      <td>Yes</td>
      <td>No on 22H2 and later — Microsoft replaced WSR with Voice Access in September 2024. WSR remains on older Windows versions</td>
      <td>Yes — Nuance lists v16 as optimized for Windows 11</td>
      <td>Yes</td>
    </tr>
    <tr>
      <td>Offline</td>
      <td>Designed to work without internet</td>
      <td>Local after language files are installed</td>
      <td>Desktop editions are typically on-device; confirm the SKU you buy</td>
      <td>Yes, with local Whisper, Parakeet, and other on-device models</td>
    </tr>
    <tr>
      <td>Voice typing (Win+H)</td>
      <td>Separate feature: Win+H is online dictation into a text field, on Windows 10 and 11</td>
      <td>Separate from Speech Recognition</td>
      <td>Not related — Dragon is third-party software</td>
      <td>Not related — HyperWhisper uses its own shortcut</td>
    </tr>
    <tr>
      <td>Custom commands / vocab</td>
      <td>Add to vocabulary in Voice Access settings</td>
      <td>Classic Windows speech commands</td>
      <td>Custom words, Auto-Texts, macros, voice commands</td>
      <td>Custom vocabulary; formatting modes; no Dragon-style command language</td>
    </tr>
    <tr>
      <td>Cost</td>
      <td>Included with Windows 11</td>
      <td>Included with Windows</td>
      <td>Paid license; check Nuance for current editions and prices</td>
      <td>App and local transcription free; optional pay-as-you-go cloud credits</td>
    </tr>
  </tbody>
</table>

<p>On Windows 10, “dictation” usually means Win+H voice typing (online) or the legacy Speech Recognition UI. Dragon is what you buy when that is not enough: Nuance sells Dragon Professional v16 for live speech-to-text and for transcribing recorded audio, with custom commands and boilerplate, on Windows 10 and 11. It is a workstation product. Expect setup time and a license fee; confirm both on Nuance's site before you budget.</p>

<p>On current Windows 11 (22H2 and later), use Voice Access, not Windows Speech Recognition. Microsoft's <a href="https://support.microsoft.com/en-us/windows/use-voice-recognition-in-windows-83ff75bd-63eb-0b6c-18d4-6fae94050571" rel="nofollow noopener" target="_blank">voice recognition support page</a> states that Voice Access replaced WSR there in September 2024; WSR stays available on older Windows versions. Voice Access is the built-in hands-free layer Microsoft documents as working without internet, with dictation and commands. It is not Dragon. If you already run Voice Access, Microsoft says starting Win+H tells you to dictate with Voice Access instead.</p>

<p>HyperWhisper is the other dedicated-app path: free local transcription in any text field, no Dragon command language, and the same app on Mac and Linux. Pick Dragon when you want a mature Windows command-and-macro system and will pay for it. Pick HyperWhisper when you want offline dictation without a seat license. Pick Voice Access or Win+H when you are still testing whether voice input helps.</p>

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

<h3>Is Windows dictation better than Dragon?</h3>
<p>They solve different jobs. Win+H and Voice Access are free and already on the PC; Win+H uses online speech recognition, and Voice Access is Windows 11 only. Dragon Professional is a paid Windows product with custom commands, vocabulary, and macros — Nuance lists v16 for Windows 10 and 11. HyperWhisper is the free local-app path if you want offline dictation without a Dragon license. See the comparison table above.</p>
`;

export const bestDictationSoftwareForWindows: BlogPost = {
  locale: "en",
  slug: "best-dictation-software-for-windows",
  frontMatter: {
    title: "Windows Dictation in 2026: Win+H vs Dragon",
    description:
      "Compare Windows Voice Access, Speech Recognition, and Dragon with HyperWhisper. Win+H dictation vs Dragon on Windows 10 and 11.",
    date: "2026-11-25T14:00:00.000Z",
    tags: ["Windows", "Dictation", "Dragon"],
    slug: "best-dictation-software-for-windows",
  },
  content: "",
  html,
};
