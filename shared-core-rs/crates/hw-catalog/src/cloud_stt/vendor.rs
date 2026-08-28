//! Vendor grouping — the Provider dropdown's rows.
//!
//! Folds the cloud-tier entries by `vendor` so each company appears once, and
//! answers the group lookups the two-level picker needs.

use super::{CloudSttCatalog, SttEntry, SttModel};

impl CloudSttCatalog {
    // -- Vendor grouping (Provider dropdown, catalog v7+) --------------------

    /// The Provider dropdown's rows: cloud-tier entries folded by `vendor` and
    /// sorted by company name, so the list reads alphabetically and each company
    /// appears exactly once. Google owns two entries (Chirp + Gemini) and so
    /// contributes one row whose model list spans both.
    ///
    /// The fold is case-INSENSITIVE on `vendor` (the Windows answer; macOS keyed
    /// its dictionary case-sensitively, which would have split a `google` row
    /// from a `Google` one). The group's `vendor_key` keeps the FIRST spelling
    /// seen in catalog order. The sort is stable, so two companies with the same
    /// display name keep catalog order rather than an arbitrary one.
    pub fn cloud_tier_vendor_groups(&self) -> Vec<VendorGroup> {
        let mut groups: Vec<VendorGroup> = Vec::new();
        for entry in self.cloud_tier_entries() {
            match groups
                .iter_mut()
                .find(|g| g.vendor_key.eq_ignore_ascii_case(&entry.vendor))
            {
                Some(group) => group.entries.push(entry.clone()),
                None => groups.push(VendorGroup {
                    vendor_key: entry.vendor.clone(),
                    display_name: entry.vendor_label().to_string(),
                    entries: vec![entry.clone()],
                }),
            }
        }
        groups.sort_by(|a, b| {
            a.display_name
                .to_lowercase()
                .cmp(&b.display_name.to_lowercase())
        });
        groups
    }

    /// The vendor group with the given `vendor` key (case-insensitive), or
    /// `None`. Matches Windows `VendorGroupForVendorKey`.
    pub fn vendor_group_for_vendor_key(&self, vendor_key: &str) -> Option<VendorGroup> {
        if vendor_key.is_empty() {
            return None;
        }
        self.cloud_tier_vendor_groups()
            .into_iter()
            .find(|g| g.vendor_key.eq_ignore_ascii_case(vendor_key))
    }

    /// The vendor group a cloud-tier entry id belongs to, or `None` for an
    /// unknown id — or for a known id that is not cloud-tier eligible, since
    /// only cloud-tier rows are grouped. Matches macOS `vendorGroup(forEntryId:)`
    /// / Windows `VendorGroupForId`.
    pub fn vendor_group(&self, id: &str) -> Option<VendorGroup> {
        let vendor = &self.entry(id)?.vendor;
        self.vendor_group_for_vendor_key(vendor)
    }
}

/// One row of the Provider dropdown: a company and every cloud-tier entry it
/// owns, in catalog order. Produced only by
/// [`CloudSttCatalog::cloud_tier_vendor_groups`], so `entries` is never empty.
#[derive(Debug, Clone, PartialEq)]
pub struct VendorGroup {
    /// The catalog `vendor` key — the dropdown's selection tag. Carries the
    /// first spelling seen in catalog order.
    pub vendor_key: String,
    /// Plain company name shown in the dropdown.
    pub display_name: String,
    pub entries: Vec<SttEntry>,
}

impl VendorGroup {
    /// The entry a fresh selection lands on — the first in catalog order.
    pub fn default_entry(&self) -> Option<&SttEntry> {
        self.entries.first()
    }

    /// Every model in the group, each paired with the id of the entry that owns
    /// it. The owning entry is what becomes the `X-STT-Provider` header, so a
    /// merged row (Google) can still route each model correctly. Ordered by
    /// entry, then by each entry's own model order.
    pub fn models(&self) -> Vec<(&str, &SttModel)> {
        self.entries
            .iter()
            .flat_map(|e| e.models.iter().map(move |m| (e.id.as_str(), m)))
            .collect()
    }
}
