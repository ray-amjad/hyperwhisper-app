//! UniFFI surface for the default-mode invariant (`hw_modes`, #536).
//!
//! Follows the `ffi_stats` / `ffi_localapi` shape: the leaf crate's types are
//! **mirrored** here as owned `uniffi::Record`/`uniffi::Enum` types with `From`
//! impls rather than re-exported, so `hw-modes` stays a plain, dependency-free
//! crate that can be unit-tested with no UniFFI in the way.
//!
//! # Why the whole row set crosses the boundary at once
//!
//! The rule is a whole-set rule — "exactly one" cannot be decided from one row
//! — and all three heads already materialise the full mode list to render the
//! Modes page and to answer `GET /modes`. A mode list is tens of rows with
//! three small fields each, and a write happens when a human clicks Save, so
//! one crossing per write is free. A per-row call could not answer the question
//! at all.
//!
//! # The `Hw` prefix is not cosmetic
//!
//! An unprefixed `ModeFlags` would generate `ModeFlags` in
//! `hyperwhisper_core.cs`, in the namespace every .NET head already imports
//! next to `HyperWhisper.Data.Entities.Mode`; `DefaultModePlan` would collide
//! with the `SharedCoreBridge` wrapper the heads actually call. `Hw` keeps the
//! FFI record and the host's persistence type visibly distinct at every call
//! site.

/// One mode, projected down to the three columns the invariant reads. Mirrors
/// `hw_modes::ModeFlags`.
#[derive(uniffi::Record)]
pub struct HwModeFlags {
    /// The mode's id as the head spells it — uppercase from Swift's
    /// `UUID.uuidString`, lowercase from .NET's `Guid.ToString("D")`. The
    /// comparison is case-insensitive, so either is fine, and the ids in the
    /// answer come back spelled exactly as they were passed.
    pub id: String,
    /// Whether the row carries the default flag now.
    pub is_default: bool,
    /// The head's ordering column. `i32` because Windows stores `int` and macOS
    /// `Int16`; every value either head can hold fits.
    pub sort_order: i32,
}

impl From<&HwModeFlags> for hw_modes::ModeFlags {
    fn from(flags: &HwModeFlags) -> Self {
        hw_modes::ModeFlags {
            id: flags.id.clone(),
            is_default: flags.is_default,
            sort_order: flags.sort_order,
        }
    }
}

/// What the head must write. Mirrors `hw_modes::DefaultModePlan`.
#[derive(uniffi::Record)]
pub struct HwDefaultModePlan {
    /// The row that must carry the flag when the write completes, or `None`
    /// when there are no modes at all.
    pub default_id: Option<String>,
    /// Every row whose flag must be cleared.
    pub clear_ids: Vec<String>,
    /// Whether applying the plan changes anything.
    pub changed: bool,
}

impl From<hw_modes::DefaultModePlan> for HwDefaultModePlan {
    fn from(plan: hw_modes::DefaultModePlan) -> Self {
        HwDefaultModePlan {
            default_id: plan.default_id,
            clear_ids: plan.clear_ids,
            changed: plan.changed,
        }
    }
}

/// Whether a name may be written. Mirrors `hw_modes::ModeNameChange`.
#[derive(uniffi::Enum)]
pub enum HwModeNameChange {
    Allowed,
    /// The mode carries the default flag, and the default mode's name is fixed.
    RejectedDefaultIsFixed,
}

impl From<hw_modes::ModeNameChange> for HwModeNameChange {
    fn from(change: hw_modes::ModeNameChange) -> Self {
        match change {
            hw_modes::ModeNameChange::Allowed => HwModeNameChange::Allowed,
            hw_modes::ModeNameChange::RejectedDefaultIsFixed => {
                HwModeNameChange::RejectedDefaultIsFixed
            }
        }
    }
}

/// Whether the default flag may be cleared. Mirrors
/// `hw_modes::DefaultFlagChange`.
#[derive(uniffi::Enum)]
pub enum HwDefaultFlagChange {
    Allowed,
    /// Clearing it would leave no default at all.
    RejectedLastDefault,
}

impl From<hw_modes::DefaultFlagChange> for HwDefaultFlagChange {
    fn from(change: hw_modes::DefaultFlagChange) -> Self {
        match change {
            hw_modes::DefaultFlagChange::Allowed => HwDefaultFlagChange::Allowed,
            hw_modes::DefaultFlagChange::RejectedLastDefault => {
                HwDefaultFlagChange::RejectedLastDefault
            }
        }
    }
}

/// Decide which mode carries the default flag once the write completes.
///
/// `rows` is every mode that will exist **after** the write, in the head's
/// display order; `preferred` is the mode the caller is trying to make the
/// default, or `None` when it is only repairing. See `hw_modes::plan_default`
/// for the choice and the tie-break.
#[uniffi::export]
pub fn mode_plan_default(rows: Vec<HwModeFlags>, preferred: Option<String>) -> HwDefaultModePlan {
    let rows: Vec<hw_modes::ModeFlags> = rows.iter().map(Into::into).collect();
    hw_modes::plan_default(&rows, preferred.as_deref()).into()
}

/// Whether a mode's name may be changed to `new_name`. The default mode's name
/// is fixed; every other mode renames freely.
#[uniffi::export]
pub fn mode_check_name_change(
    is_default: bool,
    stored_name: String,
    new_name: String,
) -> HwModeNameChange {
    hw_modes::check_name_change(is_default, &stored_name, &new_name).into()
}

/// Whether `id` may have its default flag written to `requested_is_default`.
/// `rows` is the set as it stands **before** the write.
#[uniffi::export]
pub fn mode_check_default_flag(
    rows: Vec<HwModeFlags>,
    id: String,
    requested_is_default: bool,
) -> HwDefaultFlagChange {
    let rows: Vec<hw_modes::ModeFlags> = rows.iter().map(Into::into).collect();
    hw_modes::check_default_flag(&rows, &id, requested_is_default).into()
}
