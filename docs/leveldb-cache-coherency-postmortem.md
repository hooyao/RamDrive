# Postmortem: leveldb / Chromium kernel-cache coherency bug

This document is the long-form record of the bug fixed by openspec change
[`fix-leveldb-cache-coherency`](../openspec/changes/fix-leveldb-cache-coherency/).
It is written so a future Claude Code session (or human reader) with no context
beyond this file, the source tree, and `tla/RamDiskSystem.tla` can pick up the
remaining work — most importantly the TLA+ modeling extension in §10 — without
re-deriving anything.

---

## 1. Symptom

Any Chromium-based browser launched with `--user-data-dir` pointing at the
mounted RAM drive (`Z:\` in the user's environment) crashed during startup
with exit code `0x80000003` (`STATUS_BREAKPOINT`, i.e. `__debugbreak()`):

```text
[repro] EXIT code=2147483651
[repro] hex=0x80000003
[repro] STATUS_BREAKPOINT — repro confirmed
```

The dying process printed exactly one diagnostic line to stderr that mattered:

```text
[WARNING:components\leveldb_proto\internal\leveldb_database.cc:124]
    Unable to open Z:\Temp\<userdir>\Default\<some-leveldb>:
    Corruption: CURRENT file does not end with newline
```

The leveldb directory varied by run (`Local Storage\leveldb`, `Sync Data\LevelDB`,
`GCM Store`, `shared_proto_db`, `Site Characteristics Database`, …) — every
component that uses leveldb was vulnerable. Not a Chromium bug; the message is
a sentinel and elsewhere the same condition trips a `CHECK()` that
`__debugbreak`s.

Reproduced via Playwright initially, then minimised to a Node.js launcher that
spawns `chrome.exe --remote-debugging-pipe` with `stdio: [ignore, pipe, pipe, pipe, pipe]`
(see §3). On ImDisk's `V:\` and physical NTFS the same command worked. So:
RamDrive-side, not Chromium-side.

## 2. Environment

| Component | Value |
|---|---|
| OS | Windows 11 Pro for Workstations 26200 |
| WinFsp | 2.x with Developer files |
| RamDrive mount | `Z:\` via Mount Manager (`MountUseMountmgrFromFSD=1`), running as Windows service |
| Chromium | Playwright bundle `chromium-1208`, `C:\Users\HuYao\AppData\Local\ms-playwright\chromium-1208\chrome-win64\chrome.exe` |
| Critical config | `EnableKernelCache=true`, which in the broken build set `FileInfoTimeout=uint.MaxValue` |
| Comparison FS | ImDisk on `V:\` (works), `F:\` physical NTFS (works) |

## 3. Repro recipe

The full failure mode requires `--remote-debugging-pipe`, which in turn requires
the parent process to inherit fds 3 and 4. PowerShell can't easily do this; Node
can. The script `repro_chrome.js` lives at the repo root specifically so future
sessions can re-run it; it is `.gitignore`d-adjacent (kept in the tree because
it is documentation-as-code).

```js
// repro_chrome.js — minimal Playwright-style launcher for the leveldb crash.
// Usage: node repro_chrome.js Z:\Temp\<some-userdir>
const { spawn } = require('child_process');
const fs = require('fs');

const exe = 'C:\\Users\\HuYao\\AppData\\Local\\ms-playwright\\chromium-1208\\chrome-win64\\chrome.exe';
const userDataDir = process.argv[2] || 'Z:\\Temp\\rarbg_browser';

const args = [
  // Same flag set Playwright uses; the only ones that matter for the bug are
  // --no-sandbox, --user-data-dir=..., --remote-debugging-pipe.
  '--no-sandbox', '--no-first-run', '--no-default-browser-check',
  '--disable-features=DestroyProfileOnBrowserClose,RenderDocument',
  `--user-data-dir=${userDataDir}`,
  '--remote-debugging-pipe',
  '--enable-logging=stderr', '--v=0',
  'about:blank',
];

try { fs.rmSync(userDataDir, { recursive: true, force: true }); } catch {}

const child = spawn(exe, args, {
  // CRITICAL: fd 3 = chrome→us, fd 4 = us→chrome. Without these chrome exits 13
  // with "Remote debugging pipe file descriptors are not open" and the bug
  // never fires because chrome dies before it touches leveldb.
  stdio: ['ignore', 'pipe', 'pipe', 'pipe', 'pipe'],
  windowsHide: true,
});

child.stderr.on('data', d => process.stderr.write(d));
child.stdio[3].on('error', () => {});
child.stdio[4].on('error', () => {});

setTimeout(() => { try { child.kill(); } catch {} }, 30_000);
child.on('exit', (code) => {
  if ((code >>> 0) === 0x80000003) console.log('STATUS_BREAKPOINT — bug repro\'d');
});
```

Pre-fix expected outcome: chrome stderr contains the `Corruption: CURRENT does
not end with newline` line and the process exits `0x80000003`. Post-fix
expected outcome: chrome reaches `about:blank` and is killed by the 30s timer.

## 4. Diagnostic dead-ends — do not redo these

The original bug report from the user listed eight suspicious WinFsp behaviours.
Every one was verified non-issue on this repo. **A future session should not
re-investigate any of these unless something has changed:**

| # | Suspicion | Verification | Verdict |
|---|---|---|---|
| 1 | `GetDriveType("Z:\\")` not `DRIVE_FIXED` | P/Invoke direct call returned `3` (DRIVE_FIXED) | OK |
| 2 | Mount Manager hasn't registered the volume GUID | `mountvol Z: /L` returned `\\?\Volume{...}\` | OK |
| 3 | `GetVolumeInformation("Z:\\")` returns garbage | Returned `label='RamDrive' fs='NTFS'` | OK |
| 4 | `LockFileEx` byte-range lock has wrong semantics | `FileStream.Lock()` + second-process conflict returned `ERROR_LOCK_VIOLATION (33)` — identical to NTFS | OK |
| 5 | Adapter is missing a `Lock`/`Unlock` callback | The `WinFsp.Native.IFileSystem` interface **has no Lock/Unlock members** — WinFsp implements byte-range locks in the kernel; user-mode FS is not in the path. **Do not look for this.** | N/A |
| 6 | `FileIdInfo` returns non-unique IDs | Not actually used by leveldb on the failure path | OK |
| 7 | `FlushFileBuffers` returns wrong status | Returns `STATUS_SUCCESS` synchronously (correct for a RAM disk) | OK |
| 8 | Sandbox path checks reject `Z:\\` | `--no-sandbox` was already in the failing command line — sandbox is not even initialised | N/A |

## 5. What didn't reproduce the bug under unit tests

A naive integration test that did `File.WriteAllBytes(target, []); File.WriteAllBytes(tmp, payload); File.Move(tmp, target, true); File.ReadAllBytes(target)` **passed**. The bug is fragile against high-level .NET wrappers — they introduce extra opens, releases, and timing pauses that mask the race.

Actually triggering the fault requires **all** of:

1. **Win32 `CreateFile` with default cached I/O** — no `FILE_FLAG_NO_BUFFERING`, no `FILE_FLAG_WRITE_THROUGH`. .NET's `FileStream` defaults to cached too, but goes through a different open sequence.
2. **Buffered `WriteFile` followed by `FlushFileBuffers`** — forces the cache manager to do read-modify-write on the page, so the user-mode FS sees a paging-write IRP.
3. **Atomic rename via `MoveFileExW(REPLACE_EXISTING)` or `SetFileInformationByHandle(FileRenameInfo)`** — Chromium's leveldb env uses both depending on the path. The unit tests cover both.
4. **A previous negative open** of the new name on the same handle table — chrome opens `CURRENT` with `Disposition: Open` three times before deciding to rename `dbtmp` over it. Those negative `OpenFile` calls **populate** the kernel's `FileInfo` cache with `size=0, exists=false`.
5. **`FileInfoTimeout = uint.MaxValue`** — without this the cache expires before the post-rename read happens.

The reduced repro lives in `tests/RamDrive.IntegrationTests/LevelDbReproTests.cs`. It uses raw P/Invoke (`CreateFile`, `WriteFile`, `MoveFileEx`, `SetFileInformationByHandle`) and the integration fixture is pinned at `FileInfoTimeoutMs = uint.MaxValue` for exactly this reason — it must keep catching this regression.

## 6. How procmon was used (and a caveat)

**Setup** (from an admin shell):

```cmd
"C:\Users\HuYao\Desktop\Procmon - Copy.exe" /AcceptEula /Quiet /Minimized ^
    /BackingFile F:\procmon_chrome.pml /Runtime 35
```

While that 35-second window is open, in another (non-admin) shell:

```cmd
node F:\MyProjects\RamDrive\repro_chrome.js Z:\Temp\rarbg_proc
```

After procmon stops, export to CSV (admin not required):

```cmd
"C:\Users\HuYao\Desktop\Procmon - Copy.exe" /OpenLog F:\procmon_chrome.pml /SaveAs F:\procmon_chrome.csv
```

The CSV is ~150 MB. Useful filters:

```bash
# Operations on the leveldb CURRENT files only
grep '"chrome.exe"' F:/procmon_chrome.csv | grep '\\CURRENT' | grep -v SUCCESS

# Per-file event timeline, sorted by time
grep '"chrome.exe"' F:/procmon_chrome.csv | grep -F 'LOCAL STORAGE\LEVELDB' \
  | awk -F'","' '{ ts=$1; gsub(/^"/,"",ts); printf "%s  %-30s  %-32s  [%s]\n", ts, $5, $4, $6 }'
```

### CRITICAL CAVEAT — procmon masks the BREAKPOINT but the underlying corruption persists

With procmon attached, IRP timing stretches enough that Chromium often **does
not** crash to `STATUS_BREAKPOINT`. **However**, the `Corruption: CURRENT does
not end with newline` warning is still printed to stderr and the leveldb
database is still empty. **Diagnose against the warning, not the exit code,
when procmon is attached.** Without procmon, both signals are present.

## 7. The smoking-gun trace

These five rows from the procmon CSV nailed the bug. They are all on the same
file inside `…\GCM Store\` and span ~2 milliseconds:

```text
…58722  CURRENT       CreateFile               [NAME NOT FOUND]   Disposition: Open
…58755  CURRENT       CreateFile               [NAME NOT FOUND]   Disposition: Open
…58781  CURRENT       CreateFile               [NAME NOT FOUND]   Disposition: Open + Delete
…58812  000001.dbtmp  CreateFile               [SUCCESS]          Disposition: Open + Delete
…58861  000001.dbtmp  SetRenameInformationFile [SUCCESS]          ReplaceIfExists:False, ⇒ CURRENT
…58908  CURRENT       CreateFile               [SUCCESS]          Generic Read
…58921  CURRENT       ReadFile                 [END OF FILE]      Length: 8192
```

The three `[NAME NOT FOUND]` opens populate the kernel's negative cache for
`CURRENT`. The rename succeeds (the underlying `RamFileSystem` node moves
correctly — confirmed via in-process trace). The post-rename open returns
`SUCCESS`, but the read returns **end-of-file** at offset 0 — meaning the
kernel served the read from the cached `size=0` and **never called user-mode
`ReadFile` at all**.

`SetRenameInformationFile` returning success while `ReadFile` returns EOF is
the unmistakable kernel-cache-vs-user-mode-state divergence.

## 8. Root cause

`WinFspRamAdapter.Init` (pre-fix) set:

```csharp
if (_options.EnableKernelCache)
    host.FileInfoTimeout = unchecked((uint)(-1));   // = uint.MaxValue
```

This tells WinFsp's kernel driver to cache `FileInfo` (existence, size,
attributes) **forever**. The adapter then performed every path-mutating
operation (`CreateFile`, `MoveFile`, `Cleanup` with delete, `OverwriteFile`,
`SetFileSize`, `SetFileAttributes`) **without notifying the kernel**.

Result: any negative cache entry, or any stale size/attributes entry,
survived the lifetime of the mount. leveldb's atomic-rename pattern landed
exactly on this: it negative-probes, renames into the cache slot, then reads.

Note also that the `WinFsp.Native` binding **only exposed `FspFileSystemNotifyBegin`
and `FspFileSystemNotifyEnd`** — the actual `FspFileSystemNotify` was not in
the P/Invoke layer. Even if the adapter author had wanted to send
notifications, they couldn't, which is presumably how the bug got in.

## 9. Fix summary

Three coordinated changes shipped in
[`fix-leveldb-cache-coherency`](../openspec/changes/fix-leveldb-cache-coherency/):

1. **Binding** (`winfsp-native` repo, package `0.1.2-pre.x`): added
   `FspFileSystemNotify` P/Invoke + `FspFsctlNotifyInfo` struct + `FileNotify`
   constants + public `FileSystemHost.Notify(filter, action, fileName)`.
   Buffer is stack-allocated for paths up to ~2030 chars; longer paths use
   `ArrayPool<byte>`. Path is upper-cased in place when the host is
   case-insensitive (the WinFsp driver's cache key is the upper-cased form —
   verified against the official `fuse.c` reference implementation).
2. **Adapter** (`WinFspRamAdapter.cs`): every path-mutating callback now sends
   the appropriate `Notify` after the user-mode mutation commits and outside
   any locks the kernel could re-enter. The full matrix is documented in the
   class XML doc and in `openspec/changes/fix-leveldb-cache-coherency/design.md`
   §Decision 4. Notification failures are logged at `Trace` and **never** fail
   the originating IRP.
3. **Config** (`RamDriveOptions.FileInfoTimeoutMs`, default `1000`): replaces
   the unconditional `uint.MaxValue` and acts as defence in depth for any path
   that escapes the notification matrix. The integration fixture pins
   `uint.MaxValue` so a missed notification fails CI rather than only failing
   against real Chromium with the production default.

Regression test: `tests/RamDrive.IntegrationTests/LevelDbReproTests.cs`. Three
test methods (basic, mixed-case rename, default-timeout fixture) cover the
matrix.

### 9.1 Known follow-up: a SEPARATE pre-existing pipe-mode crash

After landing the leveldb fix and verifying it works (`CURRENT` ends with
`\n` correctly on disk, integration tests pass at `FileInfoTimeout=uint.MaxValue`),
manual end-to-end testing surfaced a **different** bug that was always there
but masked by chrome dying earlier on the leveldb issue:

- **Trigger**: `chrome.exe ... --remote-debugging-pipe ...` against a RamDrive
  mount with `EnableKernelCache=true` (any non-zero `FileInfoTimeoutMs`).
- **Symptom**: chrome exits `STATUS_BREAKPOINT` very early — only one stderr line
  (`Command line too long for RegisterApplicationRestart`) before death. No
  fatal/check-failed line is printed; chrome dies before its logging is set up.
- **Bisection**:
  - Direct `chrome.exe` (no `--remote-debugging-pipe`) on H: with cache enabled
    runs fine end-to-end (full UI, network activity, USB enumeration).
  - `--remote-debugging-pipe` on physical NTFS (F:) runs fine.
  - `--remote-debugging-pipe` on H: with `EnableKernelCache=false` runs fine.
  - `--remote-debugging-pipe` on H: with `EnableKernelCache=true` and **all
    `Notify` calls disabled** still crashes — confirming Notify is not the
    cause.
  - Reproduces identically on the unfixed Z: production mount.
- **Conclusion**: Independent of the leveldb cache-coherency fix shipped here.
  Likely another kernel-cache-vs-user-mode-state divergence on a different
  callback (suspect: `WriteFile` returning a stale `FspFileInfo` to the FSD),
  but specific to whatever IRP sequence chrome's pipe-mode init does.
- **Status**: Should be filed as a separate openspec change. Leveldb fix is
  complete and shippable on its own; this second bug doesn't regress anything
  the leveldb fix promised.

---

## 10. TLA+ modeling extension (next-session work)

This section spec's the TLA+ work that should land as a follow-up. It is
written to be self-sufficient — a fresh session with `tla/RamDiskSystem.tla`
and this file should be able to implement, run, and write up the model
without re-deriving anything.

### 10.1 Why model this

`tla/RamDiskSystem.tla` already verifies internal data integrity of the
RamDrive (page pool accounting, three-phase write, sparse SetLength, etc.) —
but it treats the kernel as a passive observer. The bug above sat between
user-mode FS state and the **kernel's `FileInfo` cache**, which the existing
model does not have. The same gap caused an earlier `FreeBytes` pollution bug
mentioned in the model's "Modeling guidelines" section. The fix is to add the
kernel cache as a first-class state component.

The notification matrix from §9 becomes a verifiable contract: every action
that mutates user-mode state must dispatch the corresponding `Notify` action
that invalidates the kernel cache.

### 10.2 New variables

Add to the `VARIABLES` declaration in `tla/RamDiskSystem.tla`:

```tla
VARIABLES
    ...                          \* existing variables
    kernelCache,                 \* [Path -> CacheEntry]
    cacheMode                    \* {"Permanent", "Bounded"}
```

with type definitions:

```tla
CacheEntry == [size: Nat, exists: BOOLEAN] \cup {NotCached}
NotCached == [size |-> -1, exists |-> FALSE]   \* sentinel; or use a separate symbol
```

`cacheMode = "Permanent"` corresponds to `FileInfoTimeoutMs = uint.MaxValue`
and disables the `CacheExpire` action (see below). `cacheMode = "Bounded"`
corresponds to a finite timeout and enables it. **Default the model to
"Permanent" for fastest counterexample-finding**: if the matrix is wrong,
"Permanent" produces a violation; "Bounded" can hide it.

### 10.3 New actions

Five new top-level actions. Add them to the `Next` disjunction.

#### `KernelOpenFile(path)`

Models the kernel's `IRP_MJ_CREATE` cache hit/miss decision.

```tla
KernelOpenFile(path) ==
  IF kernelCache[path] # NotCached
  THEN  \* cache hit — kernel returns cached value WITHOUT calling user-mode
        UNCHANGED <<userModeState..., kernelCache>>
  ELSE  \* cache miss — perform user-mode open, populate cache
        /\ DoOpen(path)                               \* existing user-mode action
        /\ kernelCache' = [kernelCache EXCEPT ![path] = MkEntry(path)]
```

`MkEntry(path)` reads the post-`DoOpen` user-mode state and produces a
`CacheEntry`.

#### `KernelReadFile(path)`

```tla
KernelReadFile(path) ==
  /\ kernelCache[path] # NotCached
  /\ LET cached == kernelCache[path]
     IN \* kernel returns the cached size; if it says 0 it returns EOF
        \* WITHOUT calling user-mode. THIS is the bug we are modelling.
        UNCHANGED <<userModeState..., kernelCache>>
```

The asymmetry with `KernelOpenFile` is intentional and matches reality: a read
on a cached file goes straight to the kernel page cache; only an open with a
"misses-cache" disposition like `OPEN` will trigger a user-mode call. This is
exactly the behaviour that returned `END OF FILE` in the smoking gun trace.

#### `Notify(path, action)`

```tla
NotifyActions == {"Added", "Removed", "Modified", "RenamedOldName", "RenamedNewName"}

Notify(path, action) ==
  /\ action \in NotifyActions
  /\ kernelCache' = [kernelCache EXCEPT ![path] = NotCached]
  /\ UNCHANGED <<userModeState..., cacheMode>>
```

(For modeling purposes the `action` parameter is decorative — it is recorded
in the action label so counterexample traces are readable. The effect is the
same.)

#### `CacheExpire(path)`

```tla
CacheExpire(path) ==
  /\ cacheMode = "Bounded"
  /\ kernelCache[path] # NotCached
  /\ kernelCache' = [kernelCache EXCEPT ![path] = NotCached]
  /\ UNCHANGED <<userModeState..., cacheMode>>
```

This non-deterministically clears any cached entry, modelling the
`FileInfoTimeoutMs` countdown.

### 10.4 Modify existing actions to call Notify

The notification matrix from `WinFspRamAdapter` becomes:

```tla
DoCreateFile(f) == \E ... :
  /\ <existing body>
  /\ kernelCache' = [kernelCache EXCEPT ![f] = NotCached]   \* equivalent to Notify(f, "Added")

DoMove(f, g) == \E ... :
  /\ <existing body>
  /\ kernelCache' = [kernelCache EXCEPT
                       ![f] = NotCached,    \* RenamedOldName
                       ![g] = NotCached]   \* RenamedNewName

DoDelete(f) ==
  /\ <existing body>
  /\ kernelCache' = [kernelCache EXCEPT ![f] = NotCached]

DoTruncate(f, n) ==
  /\ <existing body>
  /\ kernelCache' = [kernelCache EXCEPT ![f] = NotCached]

DoExtend(f, n) ==
  /\ <existing body>
  /\ kernelCache' = [kernelCache EXCEPT ![f] = NotCached]
```

`DoWriteP3(f)` deliberately **does not** invalidate the cache: WinFsp's FSD
already updates its `FspFileInfo` cache from the result struct returned by
`Write`, so notifications are not required. Document this asymmetry in a
comment in the model.

### 10.5 Invariants

```tla
\* Every cached entry agrees with the current user-mode state of the same path,
\* OR the cacheMode allows transient staleness.
CacheCoherent ==
  \A p \in Path:
    \/ kernelCache[p] = NotCached
    \/ /\ kernelCache[p].exists = FileExists(p)
       /\ (FileExists(p) => kernelCache[p].size = FileSize(p))
    \/ cacheMode = "Bounded"

\* After every rename, the kernel's view of the new path matches user-mode.
\* This is the specific invariant the leveldb bug violated.
NoStaleSizeAfterRename ==
  \A f, g \in Path:
    (LastAction = <<"DoMove", f, g>>) =>
      (kernelCache[g] = NotCached \/ kernelCache[g].size = FileSize(g))
```

`CacheCoherent` is the strong invariant; `NoStaleSizeAfterRename` is the
narrower one targeted at the leveldb pattern. Run with both. Failures of the
narrow one will produce shorter counterexample traces, which is useful when
TLC is finding many bugs at once.

### 10.6 Liveness

```tla
\* In Bounded mode, an open after a rename eventually sees the new size.
\* In Permanent mode, the same property holds via the notification matrix.
RenameThenReadEventuallyConsistent ==
  \A f, g \in Path:
    [](DoMove(f, g) ~> KernelReadFile(g) returns SizeOf(g))
```

(Pseudocode — TLA+ doesn't have "returns" syntax; encode the consequent as
"there exists a Next state where the cached size equals the user-mode size".)

### 10.7 Bug-finding self-test

Before declaring victory, **deliberately break the model** to confirm TLC
catches it:

1. Remove `Notify(g, RenamedNewName)` from `DoMove`.
2. Run TLC against `RamDiskSystem_Minimal.cfg` with `cacheMode = "Permanent"`.
3. Expected: TLC produces a counterexample where after `DoMove(f, g)`,
   `KernelReadFile(g)` observes `kernelCache[g].size = 0` while `FileSize(g) > 0`.
4. Capture the counterexample trace into `tla/RamDiskSystem_CacheModel_results.txt`
   alongside the clean run.
5. Restore the missing `Notify` call and verify the counterexample disappears.

This is the same pattern as the "old buggy model" historical artefacts already
preserved in `tla/PagePoolReservation.tla`.

### 10.8 State-space estimate

Existing `RamDiskSystem_Minimal.cfg` (3 pages × 2 files): ≈ 12 minutes,
~3M states. Adding `kernelCache` (function `Path -> CacheEntry` with
`|Path| ≤ 2`, ≤ 4 distinct cache states each) multiplies the state space
by ≤ 16. Expected new minimal-config runtime: **≈ 1–2 hours**. Standard config
(4 pages × 2 files, ~5 h, 66M states): expected **1–2 days**; if TLC blows up,
restrict `Path` to a 2-element symmetry set first.

If total runtime becomes prohibitive, drop the liveness check (the safety
invariants alone catch the bug class).

### 10.9 Update CLAUDE.md mapping table

After the model changes land, append to the "How the model maps to code"
table in the project root `CLAUDE.md`:

| TLA+ Action | Code | Lock |
|---|---|---|
| `KernelOpenFile(p)` | WinFsp FSD (kernel) on `IRP_MJ_CREATE` cache hit | None |
| `KernelReadFile(p)` | WinFsp FSD (kernel) on `IRP_MJ_READ` cache hit | None |
| `Notify(p, a)` | `FileSystemHost.Notify` ↔ `FspFileSystemNotify` | None (called outside user-mode locks per matrix) |
| `CacheExpire(p)` | `FileInfoTimeoutMs` countdown in WinFsp FSD | None |

### 10.10 Concrete next-session task list

Mirrors `openspec/changes/fix-leveldb-cache-coherency/tasks.md` §7.3–7.5:

- [ ] Edit `tla/RamDiskSystem.tla`: add the variables, actions, invariants, and liveness from §10.2–10.6.
- [ ] Edit `tla/RamDiskSystem_Minimal.cfg`: declare `cacheMode = "Permanent"` and any new constants.
- [ ] Run TLC against `_Minimal.cfg`. Required: all invariants hold, no liveness violation. Capture output to `tla/RamDiskSystem_CacheModel_results.txt` under heading "Clean run".
- [ ] Apply the deliberate-bug self-test from §10.7. Capture the counterexample trace to the same file under "Self-test counterexample".
- [ ] Append a "Verification results" section to this postmortem document referencing the captured TLC output.
- [ ] Update the `CLAUDE.md` mapping table per §10.9.
