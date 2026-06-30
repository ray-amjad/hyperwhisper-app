//! Per-provider sans-I/O request builders and response parsers.
//!
//! Each module exposes `build_transcribe_request(&TranscribeParams)` and
//! `parse_transcribe_response(&HttpResponse)` (polling providers add upload/poll
//! steps in Wave 1). Wave 0 ships compiling stubs; Wave 1 fills each in.

pub mod assemblyai;
pub mod azure_mai;
pub(crate) mod common;
pub mod deepgram;
pub mod elevenlabs;
pub mod gemini;
pub mod google_chirp;
pub mod groq;
pub mod grok;
pub mod hyperwhisper_cloud;
pub mod mistral;
pub mod openai;
pub mod soniox;
