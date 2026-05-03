## Why

The volume root security descriptor `O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;WD)` grants full access to SYSTEM, Administrators, and Everyone, but the ACE flags are empty — neither `OBJECT_INHERIT_ACE` nor `CONTAINER_INHERIT_ACE` is set. WinFsp's kernel-side `FspCreateSecurityDescriptor` walks the parent's DACL when a new file or directory is created and copies only ACEs whose flags say "yes, inherit me"; with the current SDDL no ACE qualifies, so every newly-created child receives a SD with an empty DACL, which the kernel then interprets as "deny everyone all access". Chrome creates `Default\Shared Dictionary\cache` (SUCCESS) and immediately reopens it (ACCESS_DENIED); the same pattern is observed for `Default\Network\<guid>.tmp` and many disk_cache temp files, manifesting end-user-visible as the "Profile error occurred — Something went wrong when opening your profile" dialog.

## What Changes

- Change the root SDDL constant in `WinFspRamAdapter.cs` and `RamDriveFixture.cs` from `(A;;FA;;;...)` to `(A;OICI;FA;;;...)` for all three ACEs (SYSTEM, Administrators, Everyone). This adds `OBJECT_INHERIT_ACE | CONTAINER_INHERIT_ACE` so the ACEs propagate to child files and directories.
- Add a unit test asserting every ACE in the canonical root SDDL carries `ObjectInherit | ContainerInherit` — guards against future SDDL string typos.
- Add an integration test that creates a directory + file under the mounted volume and verifies the file's effective DACL contains an entry granting `FullControl` to the calling user (or to Everyone). Captures the bug class at the FS-behaviour level.
- No `design.md` — single-string change, no design space (see scaffolding note below).

## Capabilities

### New Capabilities
- `default-security-descriptor`: Owns the root volume's default security descriptor and the rule that newly-created files and directories must inherit access from the root such that the creating principal can subsequently open them.

### Modified Capabilities
<!-- None. -->

## Impact

- **Code**: 1 SDDL string in `src/RamDrive.Cli/WinFspRamAdapter.cs` (line 47); same string in `tests/RamDrive.IntegrationTests/RamDriveFixture.cs` (line 100).
- **Tests**: new unit test class in `tests/RamDrive.Core.Tests/` (or co-located in IntegrationTests if the SDDL constant is internal) and a new integration test in `tests/RamDrive.IntegrationTests/`.
- **No binding changes** — `WinFsp.Native` package version is unaffected.
- **No on-disk persistence** — RamDrive is volatile, behavioural change is immediate on next mount.
- **User-visible impact**: Chromium-based browsers stop reporting "Profile error occurred" dialog when launched with `--user-data-dir` on the RAM drive. SQLite-backed Chromium components (top_sites, login_database, web_data) initialise correctly. (Note: the unrelated `--remote-debugging-pipe` STATUS_BREAKPOINT bug remains; it is tracked separately and does not depend on this fix.)
- **Backwards compatibility**: any existing on-disk security descriptors created with the old empty-flag root are gone on every remount (RAM drive); no migration. Operators who relied on "new files have empty DACL" — none expected, but called out so reviewers can flag.

## Note on skipping `design.md`

OpenSpec's spec-driven schema marks `design.md` as conditional ("create only if cross-cutting / new pattern / external dep / migration / ambiguity"). This change is a single SDDL string update with the fix mechanism fully understood. A stub `design.md` is included only because `tasks` declares it as a dependency in the schema graph; the file simply records "no design needed, see proposal."
