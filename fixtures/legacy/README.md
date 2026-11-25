# Legacy Library Compatibility Fixtures (Layer 5 & ROADMAP 0.5)

Every subdirectory `fixtures/legacy/<version>-sample/` holds a frozen minimal library created by an older release of Yuyue (`library.db`, `books/`, `snapshot.yuyue`, and `expected.json`).

`core/yuyue-core/tests/legacy_compat.rs` automatically runs on every `cargo test --workspace`:
1. Copies each frozen legacy library to a temp folder, opens it with the current `YuyueCore::open`, and asserts that every book, sentence count, reading progress, highlight/thought, vocabulary word, and `ReaderSettings` survives intact.
2. Restores each `snapshot.yuyue` into a clean new library and asserts zero data loss.

## Adding a new release sample

Before tagging a release (e.g. `v0.2.0`), generate a small sample with `beiying.txt`, progress, one highlight + thought, one word, and custom settings, commit it under `fixtures/legacy/v0.2.0-sample/`, and run `cargo test -p yuyue-core --test legacy_compat`.
