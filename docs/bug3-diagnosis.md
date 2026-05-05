# Bug #3: chrome `STATUS_BREAKPOINT` on RamDrive — Diagnosis Story & Lessons

> One-line summary: `WinFspRamAdapter.FlushFileBuffers` returned an empty
> `FspFileInfo`, causing the WinFsp kernel cache to believe `FileSize = 0`,
> which made chrome's leveldb / SQLite reads return zeros and trip
> `IMMEDIATE_CRASH()`.

This document is the post-mortem of a multi-day bug hunt. It exists for two
reasons:

1. **For future debugging of similar problems** — the techniques (especially the
   Tee/ReverseTee per-method response routing) are the takeaway, not the bug.
2. **To explain why the diagnostic infrastructure under `src/RamDrive.Diagnostics.*`
   exists** — that infrastructure is the productionised version of the
   throwaway tools used during the hunt.

---

## TL;DR

**Symptom**. chrome (Playwright bundle, chromium 145) launched with
`--user-data-dir` pointing at a WinFsp volume crashes with
`STATUS_BREAKPOINT (0xC0000005)` 100% of the time when `FileInfoTimeout > 0`
(kernel cache enabled). Disabling cache (`EnableKernelCache=false`) makes the
crashes go away but cuts throughput ~3×.

**Root cause**. `WinFspRamAdapter.FlushFileBuffers` returned
`FsResult.Success()` — i.e. `default(FspFileInfo)` with `FileSize = 0`,
`AllocationSize = 0`. The WinFsp kernel uses callback responses to keep its
Cache Manager (`Cc`) view of the file in sync, so it dutifully wrote
`FileSize := 0` into its cache. Subsequent reads through the cache returned
zero bytes, leveldb / SQLite saw garbage, chrome's internal `DCHECK` fired,
`STATUS_BREAKPOINT`.

**Fix**. One line in `src/RamDrive.Cli/WinFspRamAdapter.cs:313`:

```diff
 public ValueTask<FsResult> FlushFileBuffers(
     string? fileName, FileOperationInfo info, CancellationToken ct)
 {
-    return V(FsResult.Success());
+    var node = Node(info);
+    return V(node != null ? FsResult.Success(MakeFileInfo(node)) : FsResult.Success());
 }
```

**Verification**. RamDrive cache-on + the fix → 5/5 chrome runs LIVE. Same
defect existed and was patched in two diagnostic ports as well.

**Why it survived so long**. The bug looks like *nothing*. `return Success();`
is the most innocent-looking line of code possible. It looks like "I did
nothing and that's fine." It actually means "the file is zero bytes long, please
update your cache accordingly." See [Lessons](#lessons).

---

## Full timeline

What follows is a chronological list of every attempt. Many steps were
**dead ends**, included on purpose: every refuted hypothesis was part of the
convergence, and the same shape of dead-end will recur in any future
cache-coherency hunt.

### Phase 0: Symptom confirmation

Inherited from a previous session: ~80% crash rate, four ranked hypotheses
(write-extending Notify / FSCTL / Notify race / oplock), known workaround
`EnableKernelCache=false`.

### Phase 1: Baseline calibration + ruling out the obvious

#### 1.1 Measurement gotcha that burned a half-day

The "80%" turned out to be a measurement artefact:

- 128 MB capacity exhausted by earlier chrome runs → "Failed To Create Data
  Directory" popup → harness counted that as LIVE.
- Re-using the same RamDrive between runs meant chrome saw a "warm" filesystem
  and took different code paths.

**Corrected baseline** (16 GB capacity, fresh user-data-dir per run):

| Filesystem | Cache | Result |
|---|---|---|
| WinFsp official `memfs.exe` (C reference) | INFINITE | 0 / 19 crash |
| RamDrive | 1000 ms | 20 / 20 crash |
| RamDrive | OFF | 0 / 5 crash (workaround works) |

So the bug is in our code, not in WinFsp itself.

**Other procedural gotchas that ate time**:

- chrome zombies persist after `child.kill()` because RamDrive holds locks. The
  test loop must `taskkill /F /IM chrome.exe /T` between RamDrive remounts.
- When chrome lives long enough to show a "Profile error occurred" popup,
  Windows queues that dialog and it blocks the next chrome run.
- Use 16 GB capacity + fresh user-data-dir each run, always.

#### 1.2 Per-hypothesis testing in the adapter — every one failed

| Hypothesis | Change in adapter | Result |
|---|---|---|
| Notify on size-extending writes | `WriteFile` → Notify | 5/5 crash |
| Snapshot fix returning FileInfo via out-params | `PagedFileContent.Write` | 5/5 crash |
| Notify on EVERY write | unconditional Notify | 5/5 crash |
| Synchronous Notify | revert ThreadPool dispatch | 5/5 crash |
| Lockless Read | bypass `_lock` | 5/5 crash |
| Lockless metadata | `Volatile.Read` on Length / Allocated | 5/5 crash |
| `FILE_ATTRIBUTE_ARCHIVE` on create | | 5/5 crash |
| `AllocationSize` ≥ `FileSize` round up | | 5/5 crash |
| `PageSize = 4 KB` | | 5/5 crash |
| `PageSize = 64 MB` | | OOM, invalid |
| Distinguish Path-NotFound vs Name-NotFound | | 5/5 crash |
| `VolumeSerialNumber + CreationTime` | | 5/5 crash |
| `SectorSize = 512` | | 5/5 crash |
| All memfs `VolumeParams` flags | | 5/5 crash |
| Notify completely disabled | `Notify() = no-op` | 5/5 crash (proves Notify is not the source) |
| `EnableKernelCache = false` | | 0/5 crash ✓ (workaround) |

**Conclusion**: this isn't a single-point fix in the RamDrive adapter.

#### 1.3 cdb minidump analysis

WinDbg `cdb.exe` against the chromium symbol server
(`srv*F:\symbols*https://chromium-browser-symsrv.commondatastorage.googleapis.com`):

- Crash address `chrome.dll+0x3869854` aka
  `chrome!IsSandboxedProcess+0x947704`.
- Crashing instruction `cc 0f 0b` (int 3 + ud2) — chromium `IMMEDIATE_CRASH()`
  pattern.
- Caller-passed source location `components\history\core\browser\download_database.cc:411`,
  near the SQL `"SELECT max(id) FROM downloads"`.
- crash hash `{be0d4058-7058-b7fb-5291-62cb1f25d882}`.

**But**: pulling the post-crash SQLite history DB out of RamDrive and querying
it directly showed it was completely intact: `SELECT max(id) FROM downloads`
returned NULL (empty table), `PRAGMA integrity_check` passed.

**Contradiction**: chrome dies in the SQL call but the on-disk data is fine →
chrome must be reading bytes through the kernel `Cc` page cache that don't
match what's actually on disk. This planted the seed of the right direction
but we didn't grasp it until Phase 6.

### Phase 2: Plan I — programmatic IRP diff tooling

Wrote Python tools to diff WinFsp debug logs (`memfs-x64.exe -d -1 -D file.log`)
keyed by `(op, path)`, finding response divergences between memfs.exe (LIVE)
and our adapter (CRASH).

#### 2.1 Decisive experiment: a clean C# memfs replica (`MemfsClone`)

To rule out PagedFileContent / PagePool: built a **completely clean C# port of
memfs**: single `byte[]` per file (like memfs.cpp's `FileData`), Dictionary path
map, no PagePool, no paging, through our `WinFsp.Native` binding.

**Result**: MemfsClone cache-on = 5/5 CRASH. Bug is **NOT** in
`PagedFileContent` / `PagePool` / `RamFileSystem` / `WinFspRamAdapter`. It's
either in the binding layer or in something both MemfsClone and RamDrive do
that real memfs doesn't.

#### 2.2 Iteratively shrinking the IRP diff

Initially 76 response mismatches between MemfsClone and memfs.exe; after
several rounds of fixes, the diff was nearly zero — but chrome still crashed
5/5. The fixes that landed:

1. PathNotFound vs NameNotFound (OpenFile + GetFileSecurityByName)
2. `FileInfoTimeout = uint.MaxValue` (match memfs default)
3. Binding fix: `GetSecurityByName` BUFFER_OVERFLOW handling
4. `FILE_ATTRIBUTE_ARCHIVE` not NORMAL on new files
5. **Sector-aligned `AllocationSize` (independent of `FileSize`)**
6. Unique `IndexNumber` per file
7. `host.SectorSize = 512`
8. `host.PassQueryDirectoryFileName = true` + `GetDirInfoByName` impl
9. `OpenFile-NF` returns PATH_NOT_FOUND when parent missing

Still 5/5 crash, identical crash hash.

#### 2.3 Smoking gun: Read byte-size distribution diverges wildly

```
memfs:   78× Read[2048], 63× Read[512], 24× Read[516], ...
clone:  106× Read[2048],  75× Read[16],  71× Read[12], ...
```

Lots of tiny 12 / 16 byte reads on clone — chrome's retry / failure paths.
Right direction (chrome reads wrong bytes) but we still didn't know where the
wrong bytes came from.

### Phase 3: Plan Q — 1:1 translation of memfs.cpp (the first decisive breakthrough)

Translated `winfsp/tst/memfs/memfs.cpp` (2567 lines C++) 1:1 into C# in one
pass before any testing — to avoid the MemfsClone-style "fix one diff, find
another" tail.

**chrome on `MemfsExact`, cache-on, 10 / 10 LIVE.** As stable as memfs.exe.

🎯 **The bug is NOT in the WinFsp.Native binding.** If it were, MemfsExact
would also crash. The bug is in some difference between MemfsExact and
MemfsClone.

#### 3.1 Reverse bisect MemfsExact → MemfsClone

Added 16 `--no-X` toggles controlling MemfsExact behaviour
(`--no-wsl/kmopen/posix/postdisp/streams/reparse/devctl/vol-time/streamcb/`
`reparsecb/simple-clean/simple-over/no-changetime/no-mfn-comp/strip-dir-attr/`
`label-clone`). **Every toggle alone, and all-OFF combination, = LIVE.**

Bug is not expressible as any IFileSystem behaviour I can toggle.

### Phase 4: Plan R — forward port (MemfsClone gets MemfsExact's fixes)

Forked MemfsClone → MemfsCloneR with 15 `--R-X` toggles to apply
MemfsExact's fixes one by one. Per-toggle / pairs / `--R-all` (everything ON) /
add 5 missing callbacks → all still **5/5 CRASH**.

Stuck on both sides. Outstanding hypotheses to try next: CLR layout of the
Node class, vtable identity of the FS class, assembly identity, GC pressure
profile.

### Phase 5: Plan AA + AB — class identity vs source identity

#### 5.1 Plan AA: empty derived class

`MemfsCloneRFs2 : MemfsExactZFs {}` — empty inheritance, mount it. **2 / 2
LIVE**. Class identity / vtable layout is not the trigger.

#### 5.2 Plan AB (the second decisive breakthrough): copy CloneR's source into MemfsExact's project

Renamed MemfsCloneR's `Program.cs` into the MemfsExactZ project verbatim
(class + Node renamed to avoid collisions). Same `.exe`, same `.dll`, same
csproj, same WinFsp.Native binding. Switch which FS to mount at runtime:

| Mount | Result |
|---|---|
| `MemfsExactZFs` (default) | 2 / 2 LIVE |
| **`CloneRFs` (CloneR.cs verbatim)** | **2 / 2 CRASH** |

🎯 **The bug is in CloneR's source code itself.** Not the csproj, not the
build artefact, not the GC mode, not the assembly identity. In the source.

### Phase 6: Continued bisect inside Plan AB (still didn't find it)

#### 6.1 Per-method body swap (env-var controlled)

Added `Z_CLONER_<X>_EXACT=1` for 7 methods to swap their bodies to
MemfsExact-style implementations: WriteFile, SetFileSize, OverwriteFile,
ReadFile, Cleanup-noop, ReadDirectory dotdot, SetFileAttributes. **All 2 / 2
CRASH**.

#### 6.2 Wrapper routing single methods to CloneR

`CloneRDelegatingWrapper(MemfsExactZFs)` baseline 2 / 2 LIVE. Per-method
routing of Init / GetVolumeInfo / GetFileSecurityByName to CloneR: **all 2 / 2
LIVE**. Couldn't find a single-method change that flipped the outcome.

#### 6.3 Byte-content diff — the critical insight

Wrote `ReadByteTracer`: every `ReadFile` traces `(path, offset, length,
sha256(bytes))` on both LIVE and CRASH sides.

**Result**: **0 byte-level differences on common reads**. Every
`(path, offset, length)` tuple returned identical bytes on both sides. LIVE
issued 526 reads, CRASH issued 425 — completely different sets, because chrome
took different code paths.

**🎯 The critical inference**: chrome isn't directly receiving wrong bytes
from `Read`. Chrome sees a wrong view **indirectly through the kernel cache**.

### Phase 7: ReverseTeeWrapper (the third and final breakthrough)

#### 7.1 Design

Every previous bisect could only toggle one axis. Since `wrapper-of-Exact` is
LIVE and `wrapper-of-CloneR` is CRASH:

- `TeeWrapper`: run both sides on every callback, return Exact's response. →
  **2 / 2 LIVE.** Confirms chrome only cares about RESPONSES, not the side
  effects.
- `ReverseTeeWrapper`: run both sides, but **per-method env var controls which
  side's response is returned**. Default = all Exact = LIVE. Setting
  `Z_TEE_<METHOD>_CR=1` flips that one method's response to CR.

22 methods, each individually flipped to CR, 2 chrome runs each:

| Method routed to CR | Result |
|---|---|
| `FlushFileBuffers` | **2 / 2 CRASH** |
| (every other method) | 0 / 2 CRASH |

**Sole trigger: `FlushFileBuffers`.**

#### 7.2 Look at the diff

```csharp
// CloneR (BUG):
public ValueTask<FsResult> FlushFileBuffers(string? fileName, FileOperationInfo info, CancellationToken ct)
    => new(FsResult.Success());                // returns default(FspFileInfo) — all zeros

// MemfsExactZ (CORRECT):
public ValueTask<FsResult> FlushFileBuffers(string? fileName, FileOperationInfo info, CancellationToken ct)
{
    var n = N(info);
    return new(n != null ? FsResult.Success(MkInfo(n)) : FsResult.Success());
}
```

#### 7.3 Verify

Production RamDrive + cache-on + the Flush fix → **5 / 5 LIVE** ✓.

---

## Why the bug hides

`FlushFileBuffers` in the WinFsp / memfs API **does need to return FileInfo**:
`FsResult` carries an `FspFileInfo` field. memfs.cpp's Flush also calls
`MemfsFileNodeGetFileInfo(FileNode, &FileInfo)`.

When chrome calls `FlushFileBuffers(handle)`, the kernel uses the response's
`FileInfo` to **update its Cache Manager view of the file's `FileSize` /
`AllocationSize`**. Returning `default(FspFileInfo)` (all zeros) makes Cc
believe `FileSize = 0`. Subsequent reads through the cache at offset > 0
return zero bytes. chrome's leveldb / SQLite reads back zeros → DCHECK fires
→ `IMMEDIATE_CRASH()` → `STATUS_BREAKPOINT`.

**Why it only triggers in cache-on mode**: with `FileInfoTimeout = 0` the
kernel never trusts cached metadata, so the bad FileInfo from Flush is
effectively ignored.

### Why this took so long to find

1. **No IRP shows a wrong response.** Every byte we returned from a Read was
   correct. chrome reads bad bytes from the **kernel `Cc` cache**, not from
   us.
2. **Single-method body swaps couldn't find it.** `FlushFileBuffers`'s body
   doesn't matter — it's intentionally a no-op. What matters is the
   `FsResult` it returns: empty FileInfo vs real FileInfo. Even after
   rewriting the body, as long as it still `return FsResult.Success();`, the
   bug stays.
3. **`FlushFileBuffers` looks harmless.** Success = nothing wrong.
   `FsResult.Success()` looks like "everything is OK." In reality it's
   sending a "FileSize = 0" pseudo-metadata record to the kernel.
4. **memfs.cpp's default impl** is one line of `MemfsFileNodeGetFileInfo`,
   which looks similar to our code in line count — but the GetFileInfo call is
   the entire point.

---

## Lessons

1. **When an IFileSystem callback returns `FsResult` (not `void`), the
   FileInfo really does get used by the kernel.** An empty FileInfo ≠ "I
   didn't change anything" ≠ "success". It = "FileSize = 0,
   AllocationSize = 0". The static audit codified in
   [the Method 1 audit](#appendix-method-1-static-audit) is the systematic
   fix for this class of bug.

2. **memfs.cpp is the reference; literal translation is safest.** `MemfsExact`
   (1:1 translation) was LIVE on its first run. Every deviation from memfs is
   a potential landmine. This is why
   `src/RamDrive.Diagnostics.MemfsReference/` exists as a permanent oracle.

3. **Cache-on bugs require looking at kernel state, not just IRPs.** Our IRPs
   were all correct; what was wrong is the `Cc` page cache becoming invalid
   because we told it the wrong metadata. The
   [TLA+ KernelCcCache model](#appendix-tla-model) captures this invariant
   formally.

4. **The Tee + ReverseTee pattern is the strongest IFileSystem-level bisect
   tool.** When you have one LIVE implementation and one CRASH implementation
   and single-method swaps fail, per-method response routing pinpoints the
   culprit in one shot. This is why
   `src/RamDrive.Diagnostics.DifferentialChecker/DifferentialAdapter.cs`
   exists — it's the productionised, always-on version of ReverseTeeWrapper.

5. **Don't trust "harmless-looking" code.** `return FsResult.Success();` is
   the culprit here — the simplest, most symmetric, most "I'm done" line of
   code possible. Any IFileSystem callback that returns `FsResult` and writes
   `Success()` without a populated `FileInfo` is suspect.

6. **Process discipline that paid off**:
   - Always 16 GB capacity + fresh user-data-dir per chrome run.
   - Always `taskkill /F /IM chrome.exe /T` between iterations.
   - Always have a "control" comparison (memfs.exe, MemfsExact) to anchor
     "what does correct look like."

---

## Where the diagnostic infrastructure lives now

The throwaway tools used during the hunt have been **promoted into permanent
projects** (or deleted as superseded):

| Hunt-time tool | Outcome | Where it lives |
|---|---|---|
| `MemfsExact` (1:1 memfs.cpp port) | promoted | `src/RamDrive.Diagnostics.MemfsReference/` |
| `ReverseTeeWrapper` (per-method response routing) | promoted | `src/RamDrive.Diagnostics.DifferentialChecker/DifferentialAdapter.cs` |
| Source-generated drift assertion | new | `src/RamDrive.Diagnostics.DifferentialChecker.Generator/` |
| Manual diagnostic exe | new | `src/RamDrive.Cli.Diag/` (mounts the production adapter wrapped in DifferentialAdapter) |
| `FlushFileBuffers` cache invariant | formalised | `tla/KernelCcCache.tla` (+ `_Minimal.cfg`, `_Bug3Repro.cfg`) |
| `ChromeTracer` (path-filtered IRP trace) | renamed + relocated | `src/RamDrive.Core/Diagnostics/FsTracer.cs` (`[Conditional("TRACE_FS")]`; only emitted in `Cli.Diag`) |
| Integration regression nets | promoted | `tests/RamDrive.IntegrationTests/CacheCoherencyTests.cs`, `LevelDbReproTests.cs` |
| `MemfsClone`, `MemfsCloneR`, `MemfsExactZ`, all `.py` IRP analysers, bisect shell scripts | deleted | n/a — superseded by the projects above |

To opt-in to differential mode in integration tests:
`RAMDRIVE_DIFF=1 dotnet test tests/RamDrive.IntegrationTests`. The
`DifferentialAdapter` will throw a `DifferentialMismatchException` on the
first behavioural divergence between the production adapter and
`MemfsReferenceFs`.

---

## By the numbers

- **Hypotheses tried**: ~50 independent
- **Total chrome instances launched**: ~500
- **Span**: 4 sessions, ~2 days of wall-clock work
- **Code ported**: ~3 500 lines of C# across 3 memfs replicas
- **Wrong directions taken**: ≥ 15 (Notify, paged content, lockless, various
  attribute fixes, …)
- **Decisive breakthroughs**: 3
  1. **Plan Q** (MemfsExact 1:1 translation) → bug not in binding
  2. **Plan AB** (mount CloneR.cs from inside MemfsExactZ.exe) → bug is in
     source
  3. **Plan AE+** (ReverseTeeWrapper per-method response routing) → bug is in
     `FlushFileBuffers`

---

<a id="appendix-method-1-static-audit"></a>
## Appendix: Method 1 — static audit of every `Success(...)` in the adapter

After the fix, walked every `Success(...)` call in
`src/RamDrive.Cli/WinFspRamAdapter.cs` to confirm no other instances of the
same pattern. 11 call sites; all correct. Summary:

- Every callback returning `FsResult` / `CreateResult` / `WriteResult` and
  having access to a `node` correctly passes `MakeFileInfo(node)`.
- `ReadResult` / `ReadDirectoryResult` / `int` return types are by-design
  FileInfo-less (verified against memfs.cpp).
- `FlushFileBuffers` (line 313) was the only historical regression.

The DifferentialChecker provides runtime regression-prevention for the same
class of bug going forward.

<a id="appendix-tla-model"></a>
## Appendix: TLA+ KernelCcCache model

`tla/KernelCcCache.tla` formalises the invariant:

```
\A f \in Files: f \in OpenedFiles => CcCache[f].FileSize = ActualFileSize[f]
```

Two configs:

- `KernelCcCache_Minimal.cfg`: `BuggyCallbacks = {}` → fixed code; **TLC
  passes**, 561 states explored.
- `KernelCcCache_Bug3Repro.cfg`: `BuggyCallbacks = {"FlushFileBuffers"}` →
  **TLC reproduces the bug deterministically** in 4 steps (init →
  CreateFile → WriteFile → FlushFileBuffers).

To run:

```bash
cd tla && java -XX:+UseParallelGC -jar tla2tools.jar -workers auto \
    -config KernelCcCache_Minimal.cfg KernelCcCache.tla
```
