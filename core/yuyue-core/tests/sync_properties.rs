//! Layer 2: Property-based CRDT / sync tests powered by `proptest`.
//!
//! Verifies algebraic guarantees across devices:
//!  1. Idempotency: importing the same batch repeatedly is a no-op after the first import.
//!  2. Commutativity & Convergence: applying batches from multiple devices in any permutation
//!     converges to the exact same register state.
//!  3. 1-day clock skew resilience & offline divergent note edits: concurrent note edits never
//!     silently discard either side and are always preserved in `Store::conflicts()`.

use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};

use proptest::prelude::*;
use serde_json::json;
use yuyue_core::store::{Store, Version};

#[derive(Clone, Debug)]
enum Action {
    Write { dev: usize, entity_idx: u8, field_idx: u8, val: u16, tick_ms: u32 },
    Sync { from: usize, to: usize },
}

fn action_strategy() -> impl Strategy<Value = Action> {
    prop_oneof![
        3 => (0usize..3, 0u8..3, 0u8..3, any::<u16>(), 0u32..5000u32)
            .prop_map(|(dev, entity_idx, field_idx, val, tick_ms)| Action::Write { dev, entity_idx, field_idx, val, tick_ms }),
        1 => (0usize..3, 0usize..3)
            .prop_filter("distinct devices", |(a, b)| a != b)
            .prop_map(|(from, to)| Action::Sync { from, to }),
    ]
}

fn make_store(dev_id: &str, clock: &Arc<AtomicU64>) -> Store {
    let c = clock.clone();
    Store::in_memory(dev_id, Box::new(move || c.load(Ordering::SeqCst))).unwrap()
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(40))]

    #[test]
    fn sync_is_idempotent_and_order_independent(
        skew_b in -86_400_000i64..=86_400_000i64,
        skew_c in -86_400_000i64..=86_400_000i64,
        actions in prop::collection::vec(action_strategy(), 1..40),
    ) {
        let base: u64 = 1_750_000_000_000;
        let clocks = [
            Arc::new(AtomicU64::new(base)),
            Arc::new(AtomicU64::new((base as i64 + skew_b) as u64)),
            Arc::new(AtomicU64::new((base as i64 + skew_c) as u64)),
        ];
        let mut stores = [
            make_store("000000000000000a", &clocks[0]),
            make_store("000000000000000b", &clocks[1]),
            make_store("000000000000000c", &clocks[2]),
        ];

        for act in actions {
            match act {
                Action::Write { dev, entity_idx, field_idx, val, tick_ms } => {
                    clocks[dev].fetch_add(tick_ms as u64, Ordering::SeqCst);
                    let entity = format!("settings/e{entity_idx}");
                    let field = format!("f{field_idx}");
                    stores[dev].set(&entity, &field, json!(val)).unwrap();
                }
                Action::Sync { from, to } => {
                    let batch = stores[from].export(&stores[to].version().unwrap()).unwrap();
                    stores[to].import(&batch).unwrap();
                    // Idempotency: importing the exact same batch a second time changes 0 registers.
                    prop_assert_eq!(stores[to].import(&batch).unwrap(), 0);
                }
            }
        }

        // Export full batches from all 3 devices and import into two clean observers in opposite orders.
        let full_batches = [
            stores[0].export(&Version::new()).unwrap(),
            stores[1].export(&Version::new()).unwrap(),
            stores[2].export(&Version::new()).unwrap(),
        ];

        let obs_clock = Arc::new(AtomicU64::new(base));
        let mut obs_forward = make_store("00000000000000d1", &obs_clock);
        let mut obs_reverse = make_store("00000000000000d2", &obs_clock);

        for b in &full_batches {
            obs_forward.import(b).unwrap();
        }
        for b in full_batches.iter().rev() {
            obs_reverse.import(b).unwrap();
        }

        // Commutativity: forward and reverse arrival orders yield identical state.
        prop_assert_eq!(obs_forward.entities("settings/").unwrap(), obs_reverse.entities("settings/").unwrap());

        // Re-importing full batches again is strictly idempotent.
        for b in &full_batches {
            prop_assert_eq!(obs_forward.import(b).unwrap(), 0);
            prop_assert_eq!(obs_reverse.import(b).unwrap(), 0);
        }
    }

    #[test]
    fn offline_concurrent_note_edits_with_day_clock_skew_never_lose_either_thought(
        skew_ms in -86_400_000i64..=86_400_000i64,
        note_a in "[a-zA-Z0-9]{1,20}",
        note_b in "[a-zA-Z0-9]{1,20}",
    ) {
        prop_assume!(note_a != note_b);
        let base: u64 = 1_750_000_000_000;
        let clock_a = Arc::new(AtomicU64::new(base));
        let clock_b = Arc::new(AtomicU64::new((base as i64 + skew_ms) as u64));
        let mut a = make_store("aaaaaaaaaaaaaaaa", &clock_a);
        let mut b = make_store("bbbbbbbbbbbbbbbb", &clock_b);

        let entity = "mark/0123456789abcdef0123456789abcdef/m1";
        a.set(entity, "note", json!("initial")).unwrap();
        let seed = a.export(&Version::new()).unwrap();
        b.import(&seed).unwrap();

        // Both devices edit offline (clocks may differ by up to 24 hours).
        clock_a.fetch_add(1_000, Ordering::SeqCst);
        clock_b.fetch_add(1_000, Ordering::SeqCst);
        a.set(entity, "note", json!(note_a)).unwrap();
        b.set(entity, "note", json!(note_b)).unwrap();

        let from_a = a.export(&b.version().unwrap()).unwrap();
        let from_b = b.export(&a.version().unwrap()).unwrap();
        b.import(&from_a).unwrap();
        a.import(&from_b).unwrap();

        // Both devices agree on the winning value, and at least one side records the conflict
        // holding both distinct thoughts so neither is silently lost.
        prop_assert_eq!(a.get(entity, "note").unwrap(), b.get(entity, "note").unwrap());
        let conflicts_a = a.conflicts().unwrap();
        let conflicts_b = b.conflicts().unwrap();
        let all_conflicts: Vec<_> = conflicts_a.into_iter().chain(conflicts_b).collect();
        prop_assert!(!all_conflicts.is_empty(), "concurrent note edit must surface a conflict");
        let c = &all_conflicts[0];
        let pair = [c.older.0.as_str().unwrap(), c.newer.0.as_str().unwrap()];
        prop_assert!(pair.contains(&note_a.as_str()) && pair.contains(&note_b.as_str()));
    }
}
