import type { BlogPost } from "@/src/content/blog";

// File-import facts: src/content/docs/file-transcription.mdx, menu-bar.mdx,
// windows/faq.mdx (Transcribe File; formats; History). HyperWhisper product
// facts: messages/en.json and models.mdx. This page does not invent formats,
// file-size limits, or a phone transcription app. Re-read those docs before
// editing a claim.
const html = `
<p><strong>Updated November 27, 2026.</strong> <strong>How to transcribe audio to text:</strong> put the recording on your computer, run speech recognition on the file, then copy the transcript. In <a href="/en/download">HyperWhisper</a> that is three steps: open <strong>Transcribe File</strong>, pick the audio, copy the text from History. Local models keep the audio on the device and have no file-size limit; cloud transcription is optional and each provider has its own size cap.</p>

<ol>
  <li><strong>Open Transcribe File.</strong> On a Mac, click the menu bar icon, hover over <strong>Transcribe File</strong>, and select a mode. On Windows, right-click the tray icon and do the same. On Linux, use <strong>Transcribe file</strong> in the tray menu. On a Mac you can also right-click the file in Finder and choose <strong>Open With → HyperWhisper</strong>.</li>
  <li><strong>Pick the recording.</strong> macOS accepts <code>.wav</code>, <code>.mp3</code>, <code>.m4a</code>, <code>.aiff</code>, <code>.ogg</code>, <code>.flac</code>, and video containers <code>.mp4</code>, <code>.mov</code>, <code>.m4v</code> (it extracts the audio track). Windows accepts <code>.wav</code>, <code>.mp3</code>, and <code>.m4a</code> — not video; extract the audio first. HyperWhisper does not support WebM. Choose a local mode if the file must stay on the machine, or a cloud mode if you want a cloud engine.</li>
  <li><strong>Copy the transcript from History.</strong> A progress popup runs while it works. The result lands in History with the original audio, where you can copy it, edit it, or run a different mode on the same file.</li>
</ol>

<h2>What HyperWhisper accepts</h2>

<table>
  <thead>
    <tr>
      <th>Platform</th>
      <th>How to open</th>
      <th>Audio</th>
      <th>Video</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td>macOS</td>
      <td>Menu bar → Transcribe File, or Finder Open With</td>
      <td><code>.wav</code>, <code>.mp3</code>, <code>.m4a</code>, <code>.aiff</code>, <code>.ogg</code>, <code>.flac</code></td>
      <td><code>.mp4</code>, <code>.mov</code>, <code>.m4v</code> (audio extracted)</td>
    </tr>
    <tr>
      <td>Windows</td>
      <td>Tray → Transcribe File</td>
      <td><code>.wav</code>, <code>.mp3</code>, <code>.m4a</code></td>
      <td>Not yet — extract the audio first</td>
    </tr>
    <tr>
      <td>Linux</td>
      <td>Tray → Transcribe file</td>
      <td><code>.wav</code>, <code>.mp3</code>, <code>.m4a</code></td>
      <td>Not documented — extract the audio first</td>
    </tr>
  </tbody>
</table>

<p>HyperWhisper does not support WebM. Local Whisper and Parakeet have no file-size limit. HyperWhisper Cloud accepts files up to 128 MB (some engines are smaller). Full provider limits and the progress popup are in the <a href="/docs/file-transcription">Transcribe a File</a> docs.</p>

<h2>If you are not using HyperWhisper</h2>

<p>The same three-step job exists in other tools. Upload the file to a cloud transcription product, drop it into a meeting app, or play it back and type. Cloud uploads send the recording to a vendor. Manual typing keeps the audio with you and is slow. A dedicated desktop app with a local model is the middle path: automated text, audio on the disk.</p>

<h2>Local vs cloud for a file</h2>

<p>HyperWhisper local models (Whisper, Parakeet, and the other on-device engines in the <a href="/docs/models">model library</a>) have no file-size limit. After you download a model, the file does not leave the computer. HyperWhisper Cloud accepts files up to 128 MB (some engines are smaller; OpenAI Whisper is 25 MB, for example). For a file over 128 MB, use a local model or a bring-your-own-key provider with a higher limit.</p>

<p>Cloud models are generally more accurate on noise and jargon. Use them when the recording may leave the machine. Use local when it may not.</p>

<p><a href="/en/download">Download HyperWhisper</a> for Mac, Windows, or Linux, then use <strong>Transcribe File</strong> on the recording you already have.</p>

<p>Live dictation (speak into the app in front of you) is a different job: see <a href="/en/blog/best-dictation-software">best dictation software</a> and <a href="/en/blog/best-voice-to-text-app">best voice to text app</a>.</p>

<h2>FAQ</h2>

<h3>How do I transcribe audio to text on a computer?</h3>
<p>Open a transcription app, choose the file, wait for the text, copy it. In HyperWhisper: menu bar or tray → <strong>Transcribe File</strong> → pick the file → copy from History. Details: <a href="/docs/file-transcription">Transcribe a File</a>.</p>

<h3>Can I transcribe a video to text?</h3>
<p>On a Mac, yes: HyperWhisper extracts audio from <code>.mp4</code>, <code>.mov</code>, and <code>.m4v</code>. On Windows, not yet — extract the audio (for example with ffmpeg) to <code>.wav</code>, <code>.mp3</code>, or <code>.m4a</code> first.</p>

<h3>Does the audio stay on my computer?</h3>
<p>If you pick a local model, yes. If you pick HyperWhisper Cloud or your own API key, the file is sent to that provider. Local models have no file-size limit; HyperWhisper Cloud’s limit is 128 MB.</p>

<h3>Is there a file-size limit?</h3>
<p>Not for local Whisper or Parakeet. Cloud providers each have a cap. HyperWhisper shows the size and the limit if the file is too large, instead of uploading it first.</p>
`;

export const howToTranscribeAudioToText: BlogPost = {
  locale: "en",
  slug: "how-to-transcribe-audio-to-text",
  frontMatter: {
    title: "How to Transcribe Audio to Text (3 Steps)",
    description:
      "How to transcribe audio to text from a recording: pick the file, transcribe it, copy the text. Works on Mac, Windows, and Linux.",
    date: "2026-11-27T18:00:00.000Z",
    tags: ["Transcription", "Audio to text", "How to"],
    slug: "how-to-transcribe-audio-to-text",
  },
  content: "",
  html,
};
