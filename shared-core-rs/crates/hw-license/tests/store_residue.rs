//! Store-residue tests: what the licence core leaves in the platform
//! `KeyValueStore` after each step.
//!
//! The per-key tests elsewhere read back the keys they know about. These count
//! every key in the store instead, so a key that one step writes and sign-out
//! forgets to delete fails here even when no test reads it by name.

use hw_license::cache::{
    clear_stored_license, offline_fallback_outcome, persist_validation_verdict,
    remote_override_if_fresh, should_revalidate, store_license_key, store_remote_override,
    stored_license_key, TrialLimits, K_OVERRIDE_MAX_AGE,
};
use hw_license::{KeyValueStore, LicenseStatus, MemoryStore};

/// The customer-id key the platforms write after a successful validation.
/// It is private to `cache.rs`; the string is the shared wire name.
const CUSTOMER_ID_KEY: &str = "com.hyperwhisper.license.customerId";

const T0: i64 = 1_700_000_000;

#[test]
fn a_new_store_is_empty() {
    let store = MemoryStore::new();
    assert!(store.is_empty());
    assert_eq!(store.len(), 0);
}

#[test]
fn sign_out_after_activation_leaves_no_licence_keys() {
    let store = MemoryStore::new();

    persist_validation_verdict(&store, LicenseStatus::Active, "  ABCD-1234  ", T0);
    store.set(CUSTOMER_ID_KEY.to_string(), "cust_1".to_string());
    // Key, last validation, cached status, customer id.
    assert_eq!(store.len(), 4);
    assert_eq!(stored_license_key(&store).as_deref(), Some("ABCD-1234"));

    clear_stored_license(&store);

    assert!(
        store.is_empty(),
        "sign-out left {} key(s) behind",
        store.len()
    );
    assert_eq!(stored_license_key(&store), None);
    assert!(should_revalidate(&store, T0 + 60));
    let offline = offline_fallback_outcome(&store, T0 + 60);
    assert!(!offline.is_valid);
    assert_eq!(offline.status, LicenseStatus::Invalid);
    assert_eq!(offline.customer_id, None);
}

#[test]
fn sign_out_keeps_only_the_remote_override() {
    let store = MemoryStore::new();
    let limits = TrialLimits {
        daily_seconds: 600,
        model_downloads: 5,
    };

    store_remote_override(&store, limits, T0);
    store.set(K_OVERRIDE_MAX_AGE.to_string(), "3600".to_string());
    persist_validation_verdict(&store, LicenseStatus::Active, "ABCD-1234", T0);
    store.set(CUSTOMER_ID_KEY.to_string(), "cust_1".to_string());
    assert_eq!(store.len(), 8);

    clear_stored_license(&store);

    // Daily limit, model limit, fetch time, max-age: the 4 config keys only.
    assert_eq!(store.len(), 4);
    assert!(!store.is_empty());
    assert_eq!(remote_override_if_fresh(&store, T0 + 60), Some(limits));
}

#[test]
fn a_rejected_key_on_a_fresh_install_writes_nothing() {
    let store = MemoryStore::new();

    persist_validation_verdict(&store, LicenseStatus::Invalid, "TYPO-0000", T0);
    persist_validation_verdict(&store, LicenseStatus::Expired, "TYPO-0000", T0);

    assert!(
        store.is_empty(),
        "a rejected key wrote {} key(s)",
        store.len()
    );
    assert!(should_revalidate(&store, T0 + 60));
}

#[test]
fn a_rejected_replacement_key_does_not_add_or_change_keys() {
    let store = MemoryStore::new();
    persist_validation_verdict(&store, LicenseStatus::Active, "GOOD-1111", T0);
    assert_eq!(store.len(), 3);

    persist_validation_verdict(&store, LicenseStatus::Invalid, "TYPO-0000", T0 + 10);

    assert_eq!(store.len(), 3);
    assert_eq!(stored_license_key(&store).as_deref(), Some("GOOD-1111"));
    assert!(!should_revalidate(&store, T0 + 20));
}

#[test]
fn a_blank_licence_key_is_never_stored() {
    let store = MemoryStore::new();

    store_license_key(&store, "");
    store_license_key(&store, "   \t");

    assert!(store.is_empty());
}

#[test]
fn delete_removes_one_key_and_ignores_a_missing_key() {
    let store = MemoryStore::new();
    store.set("a".to_string(), "1".to_string());
    store.set("b".to_string(), "2".to_string());
    store.set("a".to_string(), "3".to_string());
    assert_eq!(store.len(), 2);

    store.delete("a".to_string());
    store.delete("missing".to_string());

    assert_eq!(store.len(), 1);
    assert_eq!(store.get("a".to_string()), None);
    assert_eq!(store.get("b".to_string()).as_deref(), Some("2"));
}
