//! `hw-input` — shared input-handling logic for every HyperWhisper head.
//!
//! Milestone: the push-to-talk state machine (issue #287). The same five-state
//! machine was written three times — `BareModifierKeyMonitor.swift` (macOS),
//! `PushToTalkMonitor.cs` (Windows) and `LinuxPushToTalkMonitor.cs` (Linux) —
//! and had already drifted apart. This crate owns the transition table; the
//! platform owns the event source, the timer primitive and the clock.
//!
//! The crate is deliberately dependency-free and UniFFI-free. `hw-core`'s
//! `ffi_input` module mirrors these types as UniFFI records/enums, the same way
//! `hw-catalog` and `hw-net` are mirrored.

pub mod ptt;

pub use ptt::*;
