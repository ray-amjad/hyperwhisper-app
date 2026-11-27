import type { BlogPost } from "@/src/content/blog";

// HyperWhisper facts: messages/en.json and in-repo docs (models.mdx,
// system-requirements.mdx). Built-in OS tools: Apple Dictation, Windows Win+H.
// This is the desktop *software* page; phones live on free-speech-to-text-app.
// Do not invent ratings, user counts, competitor prices, or a paid HyperWhisper
// app tier. Re-read those sources before editing a claim.
const html = `
<p><strong>Updated November 27, 2026.</strong> <strong>Free speech to text software</strong> on a computer is either built into the OS or a desktop program you install. Windows includes <kbd>Win</kbd>+<kbd>H</kbd> voice typing. macOS includes Apple Dictation. For a dedicated desktop program that types into every app, with unlimited local transcription and no account, <a href="/en/download">HyperWhisper</a> is free on Windows, Mac, and Linux. This page is desktop software only. For phone apps and keyboards, see <a href="/en/blog/free-speech-to-text-app">free speech to text app</a>.</p>

<h2>Free speech to text software compared (Windows and Mac)</h2>

<table>
  <thead>
    <tr>
      <th>Software</th>
      <th>Best for</th>
      <th>Windows</th>
      <th>Mac</th>
      <th>Linux</th>
      <th>Offline?</th>
      <th>What is free</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td>HyperWhisper</td>
      <td>Desktop dictation in every window</td>
      <td>Windows 10/11</td>
      <td>macOS 14.6+</td>
      <td>Ubuntu 22.04+ / Debian 12+</td>
      <td>Yes, with on-device models</td>
      <td>The program and local transcription, forever; no account</td>
    </tr>
    <tr>
      <td>Windows voice typing (Win+H)</td>
      <td>Quick voice to text in a text field</td>
      <td>Windows 10 and 11</td>
      <td>No</td>
      <td>No</td>
      <td>Uses online speech recognition</td>
      <td>Included with Windows</td>
    </tr>
    <tr>
      <td>Apple Dictation</td>
      <td>Everyday dictation on a Mac you already own</td>
      <td>No</td>
      <td>Built in</td>
      <td>No</td>
      <td>On-device for many languages, depending on hardware and settings</td>
      <td>Included with macOS</td>
    </tr>
    <tr>
      <td>Google Docs voice typing</td>
      <td>Drafting inside a Google Doc</td>
      <td>In Chrome</td>
      <td>In Chrome</td>
      <td>In Chrome</td>
      <td>No</td>
      <td>Included with Docs</td>
    </tr>
  </tbody>
</table>

<p>Built-in software is the right default for a short email. Install a dedicated program when you need one shortcut in every window, custom vocabulary, or audio that stays on the machine. Meeting products and phone keyboards are different categories; they are on the <a href="/en/blog/free-speech-to-text-app">app page</a>.</p>

<h2>Windows vs Mac</h2>

<p>On Windows, free speech to text software usually means Win+H or a desktop app. Win+H uses online speech recognition. For offline Windows dictation, install a local program. See <a href="/en/blog/best-dictation-software-for-windows">Windows dictation vs Dragon</a> for Voice Access and paid Dragon Professional (Dragon is not free).</p>

<p>On a Mac, Apple Dictation is already there and can run on-device for many languages. A third-party program is worth it for a global shortcut, file import, or the same software on Windows and Linux. See <a href="/en/blog/mac-voice-dictation-software">best dictation app for Mac</a>.</p>

<h2>HyperWhisper as free speech to text software</h2>

<p>HyperWhisper is free and open source under Apache-2.0. It is desktop software: macOS 14.6+, Windows 10/11, Linux (Ubuntu 22.04+ or Debian 12+ on amd64). Local transcription needs no account. Press a shortcut, speak, and the text is pasted at the cursor. You can also import a file with <strong>Transcribe File</strong>.</p>

<p>The <a href="/docs/models">model library</a> includes Whisper (99+ languages), NVIDIA Parakeet, and other on-device engines. Cloud models are generally more accurate than local ones. Optional HyperWhisper Cloud credits start at $5; unused credits expire after 12 months. Or bring your own API key. There is no HyperWhisper subscription.</p>

<p><a href="/en/download">Download HyperWhisper</a> for Windows, Mac, or Linux.</p>

<p>Phone and keyboard apps: <a href="/en/blog/free-speech-to-text-app">free speech to text app</a>. Broader software roundup: <a href="/en/blog/best-voice-recognition-software">best voice recognition software</a>.</p>

<h2>FAQ</h2>

<h3>What is the best free speech to text software for Windows?</h3>
<p>Start with Win+H. For unlimited local transcription in every app, with no account, use <a href="/en/download">HyperWhisper</a>. Dragon Professional is paid Windows software, not a free option.</p>

<h3>What is the best free speech to text software for Mac?</h3>
<p>Apple Dictation is free and built in. HyperWhisper is free desktop software on macOS 14.6+ if you want custom vocabulary, file import, or the same program on Windows and Linux.</p>

<h3>Is free speech to text software the same as a free speech to text app?</h3>
<p>Almost. “Software” on this page means a desktop program or OS feature. “App” on the <a href="/en/blog/free-speech-to-text-app">companion page</a> also covers phones and keyboards.</p>

<h3>Can free speech to text software run offline?</h3>
<p>HyperWhisper can, with a local model. Apple Dictation can be on-device depending on your Mac and Keyboard settings. Windows Win+H uses online speech recognition.</p>
`;

export const freeSpeechToTextSoftware: BlogPost = {
  locale: "en",
  slug: "free-speech-to-text-software",
  frontMatter: {
    title: "Free Speech to Text Software (2026)",
    description:
      "Free speech to text software for Windows and Mac. Compare built-in dictation with desktop software you can run offline.",
    date: "2026-11-27T16:00:00.000Z",
    tags: ["Speech to text", "Software", "Free"],
    slug: "free-speech-to-text-software",
  },
  content: "",
  html,
};
