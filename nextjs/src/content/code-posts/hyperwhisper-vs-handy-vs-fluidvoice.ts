import type { BlogPost } from "@/src/content/blog";

// Every Handy fact below comes from https://handy.computer and the
// cjpais/Handy repo (README.md, src-tauri/Cargo.toml, src-tauri/src/settings.rs,
// the v0.9.8 release), read on 2026-10-24. Every FluidVoice fact comes from
// https://fluidvoice.com and the altic-dev/FluidVoice repo (README.md, the
// windows-v0.0.12 release), read the same day. Every HyperWhisper fact comes
// from this site's own copy (messages/en.json) or this repo
// (shared-prompts/presets/code.txt, app/windows/HyperWhisper.WhisperWorker*).
// Re-read all three before editing a claim: a comparison that goes stale is
// worse than none.
const html = `
<p>Handy, FluidVoice and HyperWhisper are all free dictation apps you can run offline with local speech models, and all three publish source code on GitHub. They differ most in two places: how far along each one is on <strong>Windows</strong>, and what each does to clean up dictated text, especially when you are dictating <strong>code</strong>. This page puts the three side by side, citing each project's own website and GitHub repository.</p>

<p><em>Handy facts are taken from <a href="https://handy.computer" rel="nofollow noopener" target="_blank">handy.computer</a> and <a href="https://github.com/cjpais/Handy" rel="nofollow noopener" target="_blank">github.com/cjpais/Handy</a>; FluidVoice facts from <a href="https://fluidvoice.com" rel="nofollow noopener" target="_blank">fluidvoice.com</a> and <a href="https://github.com/altic-dev/FluidVoice" rel="nofollow noopener" target="_blank">github.com/altic-dev/FluidVoice</a>, as published on October 24, 2026. Apps change quickly; check their pages before you choose.</em></p>

<h2>At a glance</h2>

<table>
  <thead>
    <tr><th></th><th>HyperWhisper</th><th>Handy</th><th>FluidVoice</th></tr>
  </thead>
  <tbody>
    <tr><td>Platforms</td><td>macOS, Windows, Linux</td><td>macOS, Windows, Linux</td><td>macOS; Windows and iPhone in beta</td></tr>
    <tr><td>Windows status</td><td>Released, x64 and ARM64 (local Whisper is CPU-only on ARM64)</td><td>Released, x64 and ARM64 (ARM64 is CPU-only)</td><td>Beta (pre-release 0.0.12), x64 only</td></tr>
    <tr><td>Windows app</td><td>Native .NET (WPF) app</td><td>Tauri (Rust) app</td><td>Windows source not published in the repository</td></tr>
    <tr><td>Price</td><td>Free; optional pay-as-you-go cloud credits</td><td>Free</td><td>Free, no tiers</td></tr>
    <tr><td>License</td><td>Apache-2.0, including the cloud backend</td><td>MIT</td><td>GPL-3.0 (Apache-2.0 before 2026-02-23); its Fluid Intelligence runtime is maintained privately</td></tr>
    <tr><td>Text cleanup</td><td>Modes with their own AI prompts, including a Code mode</td><td>Filler-word removal on by default; optional LLM post-processing with your own prompts, off by default</td><td>Fluid Intelligence formatting and capitalization; prompt sets per app</td></tr>
    <tr><td>Dedicated coding mode</td><td>Yes</td><td>None documented</td><td>None documented</td></tr>
  </tbody>
</table>

<h2>Windows</h2>

<p>If you dictate on a Windows PC, the three apps are at very different stages.</p>

<ul>
  <li><strong>HyperWhisper</strong> ships a native Windows app built on .NET and WPF, for Windows 10 and 11 on x64 or ARM64. On-device Whisper runs in its own worker process: if that process dies in the middle of a transcription, the app restarts it once and retries the request instead of going down with it, and the worker exits when idle so your graphics card can drop back to a low-power state. On x64 the engine tries the Vulkan GPU runtime first, then the CPU, then a CPU build for processors without AVX; on ARM64, Whisper runs on the CPU.</li>
  <li><strong>Handy</strong> is a Tauri (Rust) app with Windows installers (setup .exe and .msi) for x64 and ARM64. Its build ships Vulkan GPU acceleration on x64; its own build file says "Windows-on-ARM is CPU-only". Its README notes that on Windows it tells the Vulkan loader to skip implicit layers "to avoid crashes caused by overlay and capture hooks", and offers an experimental "Reliable Paste (Beta)" setting on macOS and Windows.</li>
  <li><strong>FluidVoice</strong> is a macOS app first. Its website says "Windows is in beta", and every Windows build on its GitHub releases page is marked pre-release; the newest, 0.0.12 (October 22, 2026), ships as an x64 installer only. Its README still says "iOS and Windows are on the way", and the README of its Windows release branch says "The Windows source code is not published here.".</li>
</ul>

<h2>Cleaning up dictated code</h2>

<p>Raw speech-to-text writes "open paren" where you meant <code>(</code> and "use effect" where you meant <code>useEffect</code>. This is where the three differ most for developers.</p>

<ul>
  <li><strong>HyperWhisper</strong> has a <strong>Code</strong> mode, one of its transcription modes, each with its own AI prompt (its prompts are open source, in the <a href="https://github.com/ray-amjad/hyperwhisper-app/tree/main/shared-prompts" target="_blank" rel="noopener">shared-prompts</a> folder). Code mode turns spoken symbols into syntax ("open paren", "triple equals", "arrow"), applies case formatting when you ask for it ("camel case", "snake case", "kebab case" and others), and keeps identifiers and technical terms exactly as said. When you turn screen context on for a mode, it reads the text on your screen and prefers the spellings it sees there, so "use effect" becomes <code>useEffect</code> when <code>useEffect</code> is on screen.</li>
  <li><strong>Handy</strong> removes filler words by default, and you can edit the list. It can also send a transcript through an LLM with a prompt you write, using providers such as OpenAI, Anthropic, Groq, OpenRouter or Apple Intelligence; this post-processing is off by default. It documents no mode for code.</li>
  <li><strong>FluidVoice</strong> offers Fluid Intelligence, an on-device runtime for "smart formatting, context-aware capitalization, and post-processing", plus optional cloud providers. Its "Per-App Configuration" assigns different prompt sets to different apps, and it has Write Mode and Command Mode. It documents no mode for code.</li>
</ul>

<h2>Speech models and offline use</h2>

<p>All three work offline. Handy runs Whisper models (Small, Medium, Turbo and Large) "with GPU acceleration when available" and Parakeet V3 on the CPU, all on your own computer. FluidVoice lists Nemotron Speech, Parakeet, Cohere Transcribe, Apple Speech and Whisper models, needs macOS 15 or later, and needs an Apple Silicon Mac for its full model list; Intel Macs are supported through the Whisper models. HyperWhisper runs local models offline on macOS 14+, Windows 10 and 11, and Linux, and also lets you use cloud transcription with your own API keys or with HyperWhisper Cloud.</p>

<h2>Which should you pick?</h2>

<ul>
  <li><strong>You dictate on Windows, including on an ARM64 PC:</strong> HyperWhisper and Handy both ship released Windows apps for x64 and ARM64 (in both, Whisper runs on the CPU on ARM64); FluidVoice's Windows app is in beta and x64 only.</li>
  <li><strong>You dictate code:</strong> HyperWhisper is the only one of the three with a documented Code mode for symbols, casing and on-screen identifiers.</li>
  <li><strong>You want the smallest, simplest offline app:</strong> Handy is free, MIT-licensed and keeps LLM processing off unless you turn it on.</li>
  <li><strong>You only use a Mac on macOS 15 or later:</strong> FluidVoice is free and built for macOS first, with on-device formatting and a voice Command Mode.</li>
</ul>

<p><a href="/en/download">Download HyperWhisper</a> for free on macOS, Windows or Linux and try it alongside whatever you use today.</p>
`;

export const hyperwhisperVsHandyVsFluidvoice: BlogPost = {
  locale: "en",
  slug: "hyperwhisper-vs-handy-vs-fluidvoice",
  frontMatter: {
    title:
      "HyperWhisper vs Handy vs FluidVoice: Windows Support and Dictating Code",
    description:
      "HyperWhisper, Handy and FluidVoice compared: Windows support on x64 and ARM64, how each cleans up dictated text, and which has a mode for dictating code.",
    date: "2026-10-24T00:00:00.000Z",
    tags: ["Comparison", "Handy", "FluidVoice", "Windows"],
    slug: "hyperwhisper-vs-handy-vs-fluidvoice",
  },
  content: "",
  html,
};
