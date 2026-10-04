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

#[cfg(test)]
mod tests {
    //! These drive the exported functions, not `hw_modes`, so a field dropped
    //! or swapped in a `From` impl — or an enum variant mapped to the wrong
    //! twin — fails here before any head sees it.
    use super::*;

    fn row(id: &str, is_default: bool, sort_order: i32) -> HwModeFlags {
        HwModeFlags {
            id: id.to_string(),
            is_default,
            sort_order,
        }
    }

    #[test]
    fn a_preferred_mode_wins_and_the_old_default_is_cleared() {
        // Windows spells the id in lowercase; the row came from macOS in
        // uppercase. The answer keeps the row's own spelling.
        let plan = mode_plan_default(
            vec![row("AAAA-1", true, 0), row("BBBB-2", false, 1)],
            Some("bbbb-2".to_string()),
        );
        assert_eq!(plan.default_id.as_deref(), Some("BBBB-2"));
        assert_eq!(plan.clear_ids, vec!["AAAA-1".to_string()]);
        assert!(plan.changed);
    }

    #[test]
    fn the_sort_order_crosses_the_boundary_and_breaks_a_two_default_tie() {
        // Two flagged rows; the second one passed has the lower sort order,
        // so it is the one the user's own list shows first.
        let plan = mode_plan_default(vec![row("late", true, 5), row("early", true, 2)], None);
        assert_eq!(plan.default_id.as_deref(), Some("early"));
        assert_eq!(plan.clear_ids, vec!["late".to_string()]);
        assert!(plan.changed);
    }

    #[test]
    fn a_set_with_no_default_is_repaired_to_the_lowest_sort_order() {
        let plan = mode_plan_default(vec![row("second", false, 1), row("first", false, 0)], None);
        assert_eq!(plan.default_id.as_deref(), Some("first"));
        assert!(plan.clear_ids.is_empty());
        assert!(plan.changed);
    }

    #[test]
    fn a_consistent_set_reports_no_change() {
        let plan = mode_plan_default(vec![row("only", true, 0), row("other", false, 1)], None);
        assert_eq!(plan.default_id.as_deref(), Some("only"));
        assert!(plan.clear_ids.is_empty());
        assert!(!plan.changed);
    }

    #[test]
    fn an_empty_set_yields_an_empty_plan() {
        let plan = mode_plan_default(Vec::new(), Some("ghost".to_string()));
        assert_eq!(plan.default_id, None);
        assert!(plan.clear_ids.is_empty());
        assert!(!plan.changed);
    }

    #[test]
    fn the_default_mode_cannot_be_renamed() {
        assert!(matches!(
            mode_check_name_change(true, "Default".to_string(), "Email".to_string()),
            HwModeNameChange::RejectedDefaultIsFixed
        ));
    }

    #[test]
    fn re_sending_the_default_mode_name_is_not_a_rename() {
        assert!(matches!(
            mode_check_name_change(true, "Default".to_string(), " Default ".to_string()),
            HwModeNameChange::Allowed
        ));
    }

    #[test]
    fn any_other_mode_renames_freely() {
        assert!(matches!(
            mode_check_name_change(false, "Notes".to_string(), "Email".to_string()),
            HwModeNameChange::Allowed
        ));
    }

    #[test]
    fn clearing_the_last_default_is_refused() {
        let rows = vec![row("aaaa", true, 0), row("bbbb", false, 1)];
        assert!(matches!(
            mode_check_default_flag(rows, "AAAA".to_string(), false),
            HwDefaultFlagChange::RejectedLastDefault
        ));
    }

    #[test]
    fn clearing_a_default_is_allowed_while_another_row_carries_it() {
        let rows = vec![row("aaaa", true, 0), row("bbbb", true, 1)];
        assert!(matches!(
            mode_check_default_flag(rows, "aaaa".to_string(), false),
            HwDefaultFlagChange::Allowed
        ));
    }

    #[test]
    fn setting_the_flag_is_always_allowed() {
        // Re-asserting the flag on the only default is a write, not a clear.
        let rows = || vec![row("aaaa", true, 0), row("bbbb", false, 1)];
        assert!(matches!(
            mode_check_default_flag(rows(), "aaaa".to_string(), true),
            HwDefaultFlagChange::Allowed
        ));
        assert!(matches!(
            mode_check_default_flag(rows(), "bbbb".to_string(), true),
            HwDefaultFlagChange::Allowed
        ));
    }
}
