//! `hw-modes` — the default-mode invariant, in one place, for all three heads.
//!
//! # The rule
//!
//! > Exactly one mode carries the default flag, and that mode's name is fixed.
//!
//! Both halves of that sentence were, until issue #536, enforced only by the
//! three mode editors. Each head disabled the Name field when the mode it was
//! showing had the flag set, and nothing below the UI checked either half: no
//! write path refused a rename, and nothing constrained the flag to one row.
//! The Local API could therefore rename the default, or set the flag on a
//! second mode, and the editor would then show a greyed-out field reading the
//! new name directly above a caption saying the name cannot be changed. PR #535
//! added that caption; this crate is what makes it true.
//!
//! # Why the decision is here and the write is not
//!
//! Applying the rule is inherently per-head: macOS mutates a Core Data context,
//! Windows and Linux an EF Core `DbContext`, and each must do it inside its own
//! transaction. What is *not* per-head is the decision — above all the
//! tie-break. When a restored backup leaves no row flagged, or two, some row has
//! to be chosen, and if the three heads choose differently then the same backup
//! restores to a different default mode on each machine. That is a
//! cross-platform contract, so it lives with the other cross-platform contracts
//! (issue #292), next to `hw-localapi`, which shares a decision of exactly this
//! shape.
//!
//! # What the caller passes
//!
//! [`plan_default`] wants **every** mode that will exist after the write — not
//! the delta. It is a whole-set rule; a plan computed from a subset can leave
//! the set with two defaults. Pass the rows in the head's own display order:
//! the crate sorts them by [`ModeFlags::sort_order`] with a *stable* sort, so
//! the caller's order is the tie-break between equal sort orders and the answer
//! never depends on a mode's id — which is uppercase on macOS
//! (`UUID.uuidString`) and lowercase on Windows (`Guid.ToString("D")`) for the
//! very same mode.
//!
//! # Panic-free by construction
//!
//! No indexing, no `unwrap`, no `expect` — see the `[lints.clippy]` table in
//! `Cargo.toml`. The row list comes out of a database that a backup written on
//! another machine may have filled, and `panic = "abort"` in the workspace
//! release profile would take the whole app down with a mode half-written.
#![cfg_attr(
    test,
    allow(clippy::indexing_slicing, clippy::unwrap_used, clippy::expect_used)
)]

/// The only three columns the invariant reads.
///
/// Deliberately not the whole mode: a rule that took every field would have to
/// be re-taught every time a head adds one, and the heads' mode rows are not
/// the same shape anyway.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ModeFlags {
    /// The mode's id, as the head spells it. Compared case-insensitively.
    pub id: String,
    /// Whether the row carries the default flag *now*.
    pub is_default: bool,
    /// The head's ordering column. Ties break on the order the rows are passed.
    pub sort_order: i32,
}

/// What a head must write to satisfy the invariant.
///
/// Apply it as one unit inside the write's own transaction: clear every id in
/// [`clear_ids`](Self::clear_ids), then set the flag on
/// [`default_id`](Self::default_id).
#[derive(Clone, Debug, PartialEq, Eq, Default)]
pub struct DefaultModePlan {
    /// The row that must carry the flag when the write completes, or `None`
    /// when there are no modes at all. Set it whether or not it already carries
    /// the flag: the operation is idempotent, and a head that skips it has to
    /// re-derive "was it already set", which is the comparison this type exists
    /// to remove.
    pub default_id: Option<String>,
    /// Every row whose flag must be cleared, in the order the rows were passed.
    pub clear_ids: Vec<String>,
    /// Whether applying this plan changes anything. `false` means the set
    /// already satisfies the invariant, so a head can skip the write and, more
    /// importantly, skip the change notification that would redraw the UI.
    pub changed: bool,
}

/// Why a mode's name may not be written.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ModeNameChange {
    /// The name may be written.
    Allowed,
    /// The mode carries the default flag, and the default mode's name is fixed.
    ///
    /// This is the one place the product rule is stated. PR #535 records that
    /// whether the default should be renameable at all is a product call; if it
    /// is reversed, this variant is what stops being returned, and the three
    /// heads follow without one of them being forgotten.
    RejectedDefaultIsFixed,
}

/// Why the default flag may not be cleared.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DefaultFlagChange {
    /// The flag may be written as asked.
    Allowed,
    /// Clearing it would leave no default at all. A caller that wants a
    /// different default sets the flag on that mode instead — which clears this
    /// one, by [`plan_default`].
    RejectedLastDefault,
}

/// Decide which row carries the default flag once the write completes.
///
/// `rows` is every mode that will exist after the write, in the head's display
/// order. `preferred` is the row the caller is trying to make the default —
/// `None` when the caller is only repairing, deleting, or writing a mode it has
/// no opinion about.
///
/// The choice, in order:
///
/// 1. `preferred`, when it names a row in `rows`. An explicit request wins,
///    which is what makes "set the flag on a second mode" clear the first
///    rather than produce two.
/// 2. Otherwise the first flagged row, by `sort_order` then input order. Two
///    flagged rows means one of them is a stray, and the earlier one is the one
///    the user's own list shows first.
/// 3. Otherwise the first row at all. No flagged row means a restore dropped
///    it, and the app already *behaves* as if the first mode were the default —
///    both `ModeService.GetDefaultMode` on Windows and the macOS onboarding
///    fallback end in "the lowest sort order". This makes that stored fact
///    rather than a guess each reader repeats.
///
/// An empty `rows` yields an empty plan, not a panic: a head that deletes its
/// last mode is refused elsewhere, but this crate is not the place to find out.
pub fn plan_default(rows: &[ModeFlags], preferred: Option<&str>) -> DefaultModePlan {
    let winner = preferred
        .and_then(|id| rows.iter().find(|row| ids_equal(&row.id, id)))
        .or_else(|| first_in_order(rows, true))
        .or_else(|| first_in_order(rows, false));

    let Some(winner) = winner else {
        return DefaultModePlan::default();
    };

    let clear_ids: Vec<String> = rows
        .iter()
        .filter(|row| row.is_default && !ids_equal(&row.id, &winner.id))
        .map(|row| row.id.clone())
        .collect();

    DefaultModePlan {
        changed: !clear_ids.is_empty() || !winner.is_default,
        default_id: Some(winner.id.clone()),
        clear_ids,
    }
}

/// Whether a mode's name may be changed to `new_name`.
///
/// `stored_name` is the name on disk and `new_name` the one the caller asks
/// for; both are compared trimmed, so a PATCH that re-sends the mode's own name
/// — which every "save the whole object" client does — is not a rename and is
/// allowed. A change of case *is* a rename: it changes what the user sees, and
/// the editor's field is disabled outright, so there is no keystroke the GUI
/// could produce that this refuses.
pub fn check_name_change(is_default: bool, stored_name: &str, new_name: &str) -> ModeNameChange {
    if is_default && stored_name.trim() != new_name.trim() {
        return ModeNameChange::RejectedDefaultIsFixed;
    }
    ModeNameChange::Allowed
}

/// Whether `id` may have its default flag written to `requested_is_default`.
///
/// Only one combination is refused: clearing the flag on the one row that
/// carries it. Setting the flag is always allowed — [`plan_default`] clears the
/// others — and clearing a row that is not the last default is a no-op the
/// caller is welcome to make.
///
/// `rows` is the set as it stands *before* the write.
pub fn check_default_flag(
    rows: &[ModeFlags],
    id: &str,
    requested_is_default: bool,
) -> DefaultFlagChange {
    if requested_is_default {
        return DefaultFlagChange::Allowed;
    }
    let others_carry_it = rows
        .iter()
        .any(|row| row.is_default && !ids_equal(&row.id, id));
    if others_carry_it {
        return DefaultFlagChange::Allowed;
    }
    let target_carries_it = rows
        .iter()
        .any(|row| row.is_default && ids_equal(&row.id, id));
    if target_carries_it {
        DefaultFlagChange::RejectedLastDefault
    } else {
        // Nothing carries it, so clearing this row takes nothing away. The
        // repair is `plan_default`'s job, not a refusal's.
        DefaultFlagChange::Allowed
    }
}

/// The first row by `sort_order`, then by the order the caller passed.
///
/// `flagged_only` restricts the search to rows that already carry the flag.
/// Written as a fold rather than a sort so the crate allocates nothing and
/// indexes nothing.
fn first_in_order(rows: &[ModeFlags], flagged_only: bool) -> Option<&ModeFlags> {
    rows.iter()
        .filter(|row| !flagged_only || row.is_default)
        .fold(None, |best: Option<&ModeFlags>, row| match best {
            // Strictly less than, so an equal sort_order keeps the earlier row
            // — the stable-sort tie-break the module docs promise.
            Some(current) if current.sort_order <= row.sort_order => Some(current),
            _ => Some(row),
        })
}

/// macOS spells a `UUID` uppercase and Windows spells the same `Guid`
/// lowercase, and a backup carries ids from one to the other, so an id compare
/// that respected case would silently stop matching after a cross-platform
/// restore.
fn ids_equal(left: &str, right: &str) -> bool {
    left.eq_ignore_ascii_case(right)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn row(id: &str, is_default: bool, sort_order: i32) -> ModeFlags {
        ModeFlags {
            id: id.to_string(),
            is_default,
            sort_order,
        }
    }

    #[test]
    fn a_healthy_set_is_left_alone() {
        let rows = [row("a", true, 0), row("b", false, 1)];
        let plan = plan_default(&rows, None);
        assert_eq!(plan.default_id.as_deref(), Some("a"));
        assert!(plan.clear_ids.is_empty());
        assert!(!plan.changed);
    }

    #[test]
    fn a_second_default_is_cleared_and_the_first_one_kept() {
        let rows = [row("a", true, 0), row("b", true, 1), row("c", false, 2)];
        let plan = plan_default(&rows, None);
        assert_eq!(plan.default_id.as_deref(), Some("a"));
        assert_eq!(plan.clear_ids, vec!["b".to_string()]);
        assert!(plan.changed);
    }

    #[test]
    fn the_first_flagged_row_wins_even_when_it_is_not_first_overall() {
        let rows = [row("a", false, 0), row("b", true, 5), row("c", true, 2)];
        let plan = plan_default(&rows, None);
        assert_eq!(plan.default_id.as_deref(), Some("c"));
        assert_eq!(plan.clear_ids, vec!["b".to_string()]);
    }

    #[test]
    fn no_default_at_all_promotes_the_first_row() {
        let rows = [row("a", false, 3), row("b", false, 1), row("c", false, 2)];
        let plan = plan_default(&rows, None);
        assert_eq!(plan.default_id.as_deref(), Some("b"));
        assert!(plan.clear_ids.is_empty());
        assert!(plan.changed);
    }

    #[test]
    fn an_equal_sort_order_breaks_on_the_order_the_caller_passed() {
        let rows = [row("a", false, 0), row("b", false, 0)];
        assert_eq!(plan_default(&rows, None).default_id.as_deref(), Some("a"));
        let reversed = [row("b", false, 0), row("a", false, 0)];
        assert_eq!(
            plan_default(&reversed, None).default_id.as_deref(),
            Some("b")
        );
    }

    #[test]
    fn a_preferred_row_takes_the_flag_and_the_old_default_loses_it() {
        let rows = [row("a", true, 0), row("b", false, 1)];
        let plan = plan_default(&rows, Some("b"));
        assert_eq!(plan.default_id.as_deref(), Some("b"));
        assert_eq!(plan.clear_ids, vec!["a".to_string()]);
        assert!(plan.changed);
    }

    #[test]
    fn a_preferred_row_that_is_not_in_the_set_is_ignored() {
        let rows = [row("a", true, 0), row("b", false, 1)];
        let plan = plan_default(&rows, Some("gone"));
        assert_eq!(plan.default_id.as_deref(), Some("a"));
        assert!(!plan.changed);
    }

    #[test]
    fn an_id_matches_across_the_case_the_two_platforms_spell_it_in() {
        let rows = [row("2C6A9C0E-0000-0000-0000-000000000001", false, 0)];
        let plan = plan_default(&rows, Some("2c6a9c0e-0000-0000-0000-000000000001"));
        assert_eq!(
            plan.default_id.as_deref(),
            Some("2C6A9C0E-0000-0000-0000-000000000001")
        );
    }

    #[test]
    fn an_empty_set_yields_an_empty_plan() {
        let plan = plan_default(&[], None);
        assert_eq!(plan.default_id, None);
        assert!(plan.clear_ids.is_empty());
        assert!(!plan.changed);
    }

    #[test]
    fn the_plan_is_idempotent() {
        let rows = [row("a", true, 0), row("b", true, 1)];
        let first = plan_default(&rows, None);
        let repaired = [row("a", true, 0), row("b", false, 1)];
        let second = plan_default(&repaired, None);
        assert_eq!(first.default_id, second.default_id);
        assert!(!second.changed);
    }

    #[test]
    fn the_default_modes_name_is_fixed() {
        assert_eq!(
            check_name_change(true, "Hyper", "Zebra"),
            ModeNameChange::RejectedDefaultIsFixed
        );
        assert_eq!(
            check_name_change(true, "Hyper", "hyper"),
            ModeNameChange::RejectedDefaultIsFixed
        );
    }

    #[test]
    fn resending_the_default_modes_own_name_is_not_a_rename() {
        assert_eq!(
            check_name_change(true, "Hyper", "Hyper"),
            ModeNameChange::Allowed
        );
        assert_eq!(
            check_name_change(true, "Hyper", "  Hyper  "),
            ModeNameChange::Allowed
        );
    }

    #[test]
    fn any_other_mode_renames_freely() {
        assert_eq!(
            check_name_change(false, "Email", "Zebra"),
            ModeNameChange::Allowed
        );
    }

    #[test]
    fn the_last_default_flag_cannot_be_cleared() {
        let rows = [row("a", true, 0), row("b", false, 1)];
        assert_eq!(
            check_default_flag(&rows, "a", false),
            DefaultFlagChange::RejectedLastDefault
        );
        assert_eq!(
            check_default_flag(&rows, "b", false),
            DefaultFlagChange::Allowed
        );
        assert_eq!(
            check_default_flag(&rows, "a", true),
            DefaultFlagChange::Allowed
        );
    }

    #[test]
    fn a_stray_second_flag_can_still_be_cleared() {
        let rows = [row("a", true, 0), row("b", true, 1)];
        assert_eq!(
            check_default_flag(&rows, "b", false),
            DefaultFlagChange::Allowed
        );
        assert_eq!(
            check_default_flag(&rows, "a", false),
            DefaultFlagChange::Allowed
        );
    }

    #[test]
    fn clearing_a_flag_nothing_carries_is_not_a_refusal() {
        let rows = [row("a", false, 0), row("b", false, 1)];
        assert_eq!(
            check_default_flag(&rows, "a", false),
            DefaultFlagChange::Allowed
        );
    }
}
