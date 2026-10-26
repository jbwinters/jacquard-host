//! The published crate carries rust/NOTICE but not the repository's root
//! NOTICE, so the two must stay identical.

#[test]
fn notice_matches_the_repository_notice() {
    let Ok(root) = std::fs::read(concat!(env!("CARGO_MANIFEST_DIR"), "/../NOTICE")) else {
        return; // outside a checkout
    };
    let crate_notice = std::fs::read(concat!(env!("CARGO_MANIFEST_DIR"), "/NOTICE")).unwrap();
    assert!(root == crate_notice, "rust/NOTICE differs from the repository NOTICE; copy it again");
}
