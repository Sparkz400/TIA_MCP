# WT-05 Bool creation inputs under TIA Portal V21 Update 3

These files back the hashes pinned in `src/TiaMcp.Core/WatchTables/WatchAuthoringProfile.cs` since the PC moved to V21 Update 3. Every native export embeds `DocumentInfo/InstalledProducts`, so the block, force-table, peer-profile and candidate hashes changed with the update; the profile's scope did not.

They come unmodified from run `author2` on 28 October 2026: `scripts/qualify_watch_authoring.py` on a frozen Debug build of `4cd016d` (the `DocumentInfo` fix, still the Update 2 pins), a fresh owned scratch CPU 1511-1 PN V4.1 project and active culture `de-DE`. That run passed creation, readback with peer preservation, saved fresh reopen, checkpoint recovery and reconciliation under the independent `verify_watch_authoring` oracle.

- `profile-candidate-review.json` and `creation.reviewed.xml`: the review and candidate as prepared. The managed profile test (`tests/TiaMcp.Engineering.Tests/WatchAuthoringProfileChecks.cs`) reads only these two files.
- `baseline/`: the six peer exports, the native receipt `snapshot.json` and the absence receipt. `force-0.xml` is the same-session export whose `DocumentInfo` the candidate carries.
- `source/`: the FB and instance DB captured by `watch_table_generate`.

The [manifest](manifest.json) lists each file's hash and the derived pins. The run `author1` (same day, failed apply) produced the same FB, DB, force-table and peer-profile hashes, and its readback hash equals the candidate pin. An independent reviewer recomputed every pin from these exports alone.

The Update 2 inputs in [`native-authoring-v21`](../native-authoring-v21/README.md) stay as historical evidence. This is not a production qualification: production authoring stays disabled.
