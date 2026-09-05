use super::HwTranscript;

// ===========================================================================
// Multi-step provider intermediate types
// ===========================================================================

/// AssemblyAI poll outcome. Mirrors `assemblyai::PollOutcome` (tuple variant
/// `Done(HwTranscript)` flattened to a named field for UniFFI).
#[derive(uniffi::Enum)]
pub enum AssemblyaiPollOutcome {
    Pending,
    Done { transcript: HwTranscript },
}
impl From<hw_net::providers::assemblyai::PollOutcome> for AssemblyaiPollOutcome {
    fn from(o: hw_net::providers::assemblyai::PollOutcome) -> Self {
        match o {
            hw_net::providers::assemblyai::PollOutcome::Pending => AssemblyaiPollOutcome::Pending,
            hw_net::providers::assemblyai::PollOutcome::Done(t) => AssemblyaiPollOutcome::Done {
                transcript: t.into(),
            },
        }
    }
}

/// A Gemini file resource. Mirrors `gemini::GeminiFile`.
#[derive(uniffi::Record)]
pub struct GeminiFile {
    pub name: Option<String>,
    pub uri: Option<String>,
    pub mime_type: Option<String>,
    pub state: Option<String>,
}

impl From<hw_net::providers::gemini::GeminiFile> for GeminiFile {
    fn from(f: hw_net::providers::gemini::GeminiFile) -> Self {
        GeminiFile {
            name: f.name,
            uri: f.uri,
            mime_type: f.mime_type,
            state: f.state,
        }
    }
}

impl From<GeminiFile> for hw_net::providers::gemini::GeminiFile {
    fn from(f: GeminiFile) -> Self {
        hw_net::providers::gemini::GeminiFile {
            name: f.name,
            uri: f.uri,
            mime_type: f.mime_type,
            state: f.state,
        }
    }
}

/// Gemini file-poll outcome. Mirrors `gemini::FilePollOutcome`.
#[derive(uniffi::Enum)]
pub enum GeminiFilePollOutcome {
    Pending,
    Active { file: GeminiFile },
}

impl From<hw_net::providers::gemini::FilePollOutcome> for GeminiFilePollOutcome {
    fn from(o: hw_net::providers::gemini::FilePollOutcome) -> Self {
        match o {
            hw_net::providers::gemini::FilePollOutcome::Pending => GeminiFilePollOutcome::Pending,
            hw_net::providers::gemini::FilePollOutcome::Active(f) => {
                GeminiFilePollOutcome::Active { file: f.into() }
            }
        }
    }
}

/// Soniox transcription job status. Mirrors `soniox::PollStatus`.
#[derive(uniffi::Enum)]
pub enum SonioxPollStatus {
    Pending,
    Completed,
}

impl From<hw_net::providers::soniox::PollStatus> for SonioxPollStatus {
    fn from(s: hw_net::providers::soniox::PollStatus) -> Self {
        match s {
            hw_net::providers::soniox::PollStatus::Pending => SonioxPollStatus::Pending,
            hw_net::providers::soniox::PollStatus::Completed => SonioxPollStatus::Completed,
        }
    }
}
