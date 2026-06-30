//! `hw-net` — sans-I/O networking core for the 12 cloud STT providers.
//! Builds `HttpRequest` values and parses `HttpResponse` values; the platform
//! performs the I/O. Audio is never marshalled across FFI (a multipart `FileRef`
//! names a path the platform streams). Plain Rust — `hw-core` mirrors these
//! types for UniFFI.
pub mod contract;
pub mod helpers;
pub mod health;
pub mod providers;
pub mod retry;

pub use contract::*;
