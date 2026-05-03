# Bug #3 handoff: chrome `--remote-debugging-pipe` STATUS_BREAKPOINT on RamDrive

**Read this first.** If you're a fresh Claude Code session being asked to continue
debugging the RamDrive STATUS_BREAKPOINT issue, this single document is meant to
give you complete context. The repo plus the references at the bottom is
everything you need; you do not have to re-derive anything below.

---

## 0. Where things stand right now

Three RamDrive bugs were uncovered in sequence; the first two are shipped, the
third is the focus of this document.

| # | Bug | Status | PR / change |
|---|---|---|---|
| 1 | leveldb `CURRENT` reads 0 bytes after rename (kernel cache coherency) | **shipped** | #9 / `archive/2026-05-03-fix-leveldb-cache-coherency` |
| 2 | newly-created files have empty DACL → ACCESS_DENIED on reopen (root SDDL missing OICI) | **shipped** | #10 / `archive/2026-05-03-fix-acl-inheritance` |
| 3 | chrome `--remote-debugging-pipe` + RamDrive cache enabled → STATUS_BREAKPOINT | **OPEN — this doc** | not started |

Main HEAD: `3bb00d7 chore: archive fix-acl-inheritance...` (post #11).
WinFsp.Native package: `0.1.2-pre.3` on nuget.org.
RamDrive build: works on default config (`EnableKernelCache=true`,
`FileInfoTimeoutMs=1000`).

Task list `#8 Diagnose bug #2 (chrome pipe-mode early crash)` is the in-flight
task; it's marked completed for the *diagnosis phase* but the bug itself is
not fixed.

---

## 1. The bug — symptoms

**One-line summary**: Chromium-based browser launched with
`--remote-debugging-pipe` against a `--user-data-dir` on a mounted RamDrive
(with default cache config `EnableKernelCache=true`,
`FileInfoTimeoutMs=1000`) crashes very early with exit code `0x80000003`
(`STATUS_BREAKPOINT`, i.e. `__debugbreak()`).

**stderr output**: only one line, the `Command line too long for
RegisterApplicationRestart` warning. Logging never gets initialised; no
`FATAL`, no `Check failed`, no leveldb corruption — chrome dies before
anything else prints.

**On physical NTFS / cache-disabled RamDrive**: same chrome command runs to
SIGTERM at 30s timeout (i.e. lives normally).

**Failure mode is non-deterministic**: among 5–20 consecutive runs against the
same mount, ~80–100% reproduce STATUS_BREAKPOINT. Earlier in the diagnostic
session a 20/20 LIVE run was observed once after many warmup launches; the
crash rate appears to depend on RamDrive state in some way that's not yet
characterised.

**Profile dialog variant** (different surface of presumably the same root
cause): when the crash *doesn't* fire, chrome instead shows a dialog "Profile
error occurred — Something went wrong when opening your profile. Some
features may be unavailable. [OK]". stderr in that case contains:

```
ERROR top_sites_backend.cc:78        Failed to initialize database
ERROR login_database_async_helper.cc:79  Could not create/open login database
ERROR backend_impl.cc:1446           Unable to map Index file
ERROR cache_util_win.cc:25           Unable to move the cache: Access is denied. (0x5)
ERROR simple_version_upgrade.cc:107  Failed to write a new fake index
```

Note the `Access is denied (0x5)` on `MoveFileEx` — that one was the
fingerprint that led to bug #2 (root SDDL OICI missing). Bug #2 is fixed; the
remaining errors above are still observed and likely tied to the
STATUS_BREAKPOINT bug.

---

## 2. What's NOT the cause (don't redo these)

| Hypothesis | Verified | Verdict |
|---|---|---|
| leveldb CURRENT corruption | bug #1 fixed; no `Corruption` warning in stderr | ✗ unrelated |
| Empty-DACL ACL inheritance | bug #2 fixed; ACL test confirms inheritance works | ✗ unrelated |
| The `Notify` matrix | crash reproduces with all `Notify` calls commented out | ✗ unrelated |
| chrome flags subset | minimum 5-flag chrome (`--no-sandbox --no-first-run --no-default-browser-check --user-data-dir=H:\\... --remote-debugging-pipe about:blank`) reproduces | ✗ flags are not the trigger |
| `--remote-debugging-pipe` itself | physical disk + same flag works fine; cache-disabled RamDrive + same flag works fine | ✗ pipe alone is not the trigger |
| Path separator (`/` vs `\`) | both reproduce | ✗ |
| PID collision / fixture sharing | each `node repro_chrome.js` spawns own chrome | ✗ |

The combination that **does** trigger: **`EnableKernelCache=true` AND chrome with `--remote-debugging-pipe` AND RamDrive`**. Drop any one of the three and it's gone.

---

## 3. Reproduction

### Quick repro (manual, ~30s)

From an admin shell:

```cmd
cd F:\MyProjects\RamDrive
dotnet run --project src/RamDrive.Cli --no-build -- --RamDrive:MountPoint=H:\\ --RamDrive:CapacityMb=128 --RamDrive:VolumeLabel=Diag
```

Wait for `Drive mounted at H:\` log, then in another shell:

```cmd
node F:\MyProjects\RamDrive\repro_chrome.js H:\Temp\repro_test
```

Expected output ends in:

```
[repro] EXIT code=2147483651 signal=null
[repro] hex=0x80000003
[repro] STATUS_BREAKPOINT — repro confirmed
```

### Confirm-not-RamDrive control

Same `repro_chrome.js` against `F:\Temp\repro_test` (physical disk) →
`signal=SIGTERM` after 30s (chrome lives, we kill it).

### Confirm-it's-cache control

```cmd
dotnet run --project src/RamDrive.Cli --no-build -- --RamDrive:MountPoint=H:\\ --RamDrive:CapacityMb=128 --RamDrive:EnableKernelCache=false
```

Then `node repro_chrome.js H:\Temp\nocache` → `signal=SIGTERM` (lives).

### Diagnostic helpers in the working tree (gitignored)

These files exist locally but are in `.gitignore`. They were created during
the bug #3 diagnosis and are kept for future investigation:

- `repro_chrome.js` — full Playwright-style flag set, 30s timeout, fd 3/4 pipe wiring
- `repro_chrome_min.js` — minimum 5-flag variant, takes `<userdir> <variant>` args
- `bisect_chrome.js` — runs first-half / second-half / full / none flag groups
- `debug_batch.js` — orchestrates 4 chrome variants (baseline RamDisk, full RamDisk, port-mode RamDisk, baseline physical) with sentinel files between them
- `debug_batch.cmd` — admin-shell driver for the above; mounts RamDrive, starts procmon background, runs `debug_batch.js`, exports CSV

The Playwright-derived flag list lives inline in `repro_chrome.js` and starts
with `--disable-field-trial-config --disable-background-networking ...`. The
critical realisation during bisection was that **flag count doesn't matter** —
even the 5-flag minimum reproduces.

---

## 4. Procmon evidence captured so far

A procmon trace was captured during a `debug_batch.js` run. PML and CSV are
**not** committed (gitignored as `procmon_*.pml`/`procmon_*.csv`). The CSV
that was analysed is at `F:\procmon_chrome2.csv` (~1.85 GB, 6.7M rows,
covering 4:06:58–4:07:56 PM, 4 variants A–D).

### How variants were sliced

The orchestrator writes sentinel files at `F:\ProcmonSentinels\VARIANT_*.txt`
between variants. `grep 'ProcmonSentinels' F:/procmon_chrome2.csv` gives
exact timestamps. For variant A (`A_baseline_ramdisk`, 5-flag minimum):

- Start: `4:07:03.5713 PM`
- End:   `4:07:06.2718 PM`

### Failure-IRP breakdown for variant A

19,031 chrome IRPs touching `H:\DIAG\A_BASELINE\...`. Breakdown of
**non-`SUCCESS` results**:

| Result | Count | Op | Verdict |
|---|---|---|---|
| `FAST IO DISALLOWED` | 1087 | mostly `QueryOpen` | benign — WinFsp lacks fast IO; kernel falls back to IRP |
| `NAME NOT FOUND` | 603 | `CreateFile` probes | benign — chrome probing for files |
| `END OF FILE` | 103 | `ReadFile` past EOF | benign |
| `INVALID PARAMETER` (`QueryRemoteProtocolInformation`) | 75 | benign — WinFsp doesn't implement; chrome treats as "not remote" |
| `INVALID PARAMETER` (`SetDispositionInformationEx`) | 14 | benign — chrome retries with old `SetDispositionInformationFile` (bug #1 confirmed) |
| `NO SUCH FILE` | 66 | `QueryDirectory` empty | benign |
| `INVALID DEVICE REQUEST` (`FileSystemControl`, `FSCTL_QUERY_FILE_REGIONS`) | 46 | benign — chrome treats as "not supported" |
| `NO MORE FILES` | 40 | benign |
| `ACCESS DENIED` | 16 | **was bug #2 — fixed in PR #10** |
| `NAME COLLISION` | 6 | benign — Crashpad re-registration probes |

**Crucially, no IRP failure correlates with chrome's death.** The last 15
IRPs on the dying chrome PID before it dies are all `CloseFile` /
`IRP_MJ_CLOSE` on shader cache files, all returning SUCCESS. **chrome dies
without any failed IRP** — meaning it dies from some non-IRP reason: a CHECK
on internal state, a CRT abort, or a plain `__debugbreak()` in initialisation
code. The IRP layer cannot directly tell us what.

### Critical caveat: procmon attached masks one symptom

When procmon is attached, IRP timing stretches enough that chrome
sometimes survives long enough to print a `Corruption` warning. Without
procmon, chrome dies even faster (often before logging is set up). **Always
diagnose against the warning if it's present, NOT against the exit code.**

---

## 5. Root-cause hypotheses (ranked by likelihood)

These are working hypotheses based on the evidence so far. None has been
proven yet.

### Hypothesis A: kernel-cache state read by chrome at startup is inconsistent

The pattern is identical in shape to bug #1 (leveldb): chrome reads
*something* via the WinFsp kernel cache and gets a stale/inconsistent answer
that fails an internal CHECK. Bug #1 was specifically about leveldb's
CURRENT-after-rename pattern; this would be a **different cache-coherency
pattern that the bug #1 notification matrix doesn't cover**.

Suspect callbacks not currently sending Notify:

- `WriteFile` — bug #1 design.md §Decision 4 explicitly excluded this on the
  reasoning that the FSD already updates its FileInfo cache from the
  `FspFileInfo` returned by `Write`. **This may be wrong** under
  `--remote-debugging-pipe` startup pressure. The notification matrix
  might need an entry for size-extending writes.
- `Cleanup` without `CleanupFlags.Delete` (a normal Close) — currently does
  not Notify. If chrome's pipe-mode startup creates a file, writes to it,
  and another open expects to see the new size before our Notify-on-write
  has propagated, the kernel could serve a stale FileInfo.

### Hypothesis B: a FsCtl chrome treats as supported but RamDrive returns INVALID_DEVICE_REQUEST

`FSCTL_QUERY_FILE_REGIONS` returned `INVALID_DEVICE_REQUEST` 46 times. We
classified that as benign because chrome usually has fallback paths. **It
might not be benign in pipe mode**: maybe chrome's pipe initialisation has a
hard dependency on this FSCTL succeeding (or returning a specific error like
`STATUS_INVALID_PARAMETER`).

The list of all FSCTLs chrome called and got `INVALID_DEVICE_REQUEST` for:

- `FSCTL_QUERY_FILE_REGIONS` (most likely candidate; introduced for sparse
  detection in Windows 8+)
- check the procmon CSV for the full list under variant A

### Hypothesis C: race between RamDrive Notify dispatch and chrome's internal state

Bug #1 fix dispatched `FspFileSystemNotify` off the WinFsp dispatcher thread
via `ThreadPool.UnsafeQueueUserWorkItem(..., preferLocal: false)` to avoid a
deadlock under `TortureTests.DirectoryTreeStress`. This trades ordering for
no-blocking: notifications can land *after* the IRP that triggered them
returns. Under normal apps that's fine; under chrome's pipe-mode
initialisation it might not be.

If chrome:
1. opens a file
2. reads it (WinFsp serves cached FileInfo)
3. closes
4. reopens, expecting to see the post-modification state
5. but a Notify that was supposed to invalidate the cache is still queued
   on a busy threadpool thread

…it gets stale state and CHECKs.

This hypothesis has lowest likelihood because the reverse (synchronous Notify)
caused a different deadlock; the off-thread dispatch was the lesser evil. But
it's worth considering whether a hybrid (synchronous for some IRP types,
async for the high-volume DirectoryTreeStress pattern) would help.

### Hypothesis D: shareMode / oplock mismatch on a startup-critical file

chrome's pipe init opens specific files (`Local State`, `Preferences`) with
particular ShareMode and oplock requests. WinFsp adapter doesn't implement
oplocks. If chrome's pipe-mode startup explicitly relies on an oplock break
notification arriving in a window, it would hang or CHECK on RamDrive.
Lowest likelihood — chrome usually treats oplocks as best-effort.

---

## 6. Concrete next steps

These are the steps a fresh session should take, in order.

### 6.1 Re-read this doc and the postmortem

- This file
- `docs/leveldb-cache-coherency-postmortem.md` (especially §9.0.1 about
  Notify off-thread dispatch and §9.1 about pipe-mode crash)

### 6.2 Re-establish baseline reproduction

Mount H:, run `repro_chrome.js`, confirm STATUS_BREAKPOINT still happens on
current main. Verify on physical disk and on cache-off RamDrive that crash
does NOT happen. **If repro is gone, either the bug is environmental (Windows
update?) or recently-merged code changed something — check git log against
`src/RamDrive.Cli/`.**

### 6.3 Capture a fresh procmon trace targeted at the dying PID

Goal: find the *last* call chrome made before STATUS_BREAKPOINT, and look at
WinFsp adapter behaviour in the surrounding 100ms.

The existing CSV `F:\procmon_chrome2.csv` (if still present) has variant A
covered. If you need fresh data:

```cmd
F:\MyProjects\RamDrive\debug_batch.cmd
```

This orchestrates RamDrive + procmon + 4 chrome variants. Output:
`F:\procmon_chrome2.csv` and `F:\debug_batch_stdout.log`.

Sentinels: `grep 'ProcmonSentinels' F:/procmon_chrome2.csv` for variant
boundaries.

For each chrome PID that died with STATUS_BREAKPOINT (find from
`debug_batch_stdout.log` `exit=2147483651` lines), extract its IRPs in the
last 200ms window and look for **any** non-SUCCESS that wasn't on the benign
list above. Even a single new failure mode is a strong lead.

### 6.4 Add adapter trace + run chrome

If procmon doesn't pin it down, instrument the adapter directly. The
integration fixture already has `RamDriveFixture.SetTraceFilter(string?)` and
`Trace(op, path, extra)` plumbing (added during bug #1 diagnosis, kept as a
no-op when filter is null). Mirror that into `WinFspRamAdapter.cs` behind a
feature flag (`--RamDrive:TracePathFilter=...`) and dump every callback for
paths matching the filter. Run chrome → capture log.

This gives in-process visibility (no procmon needed). Specifically log:
- `OpenFile` requested access vs returned MakeFileInfo size
- `WriteFile` constrainedIo flag, before-len, after-len
- `Cleanup` flags
- Any `Notify` calls (filter, action, path) and the NTSTATUS they return

### 6.5 Test hypothesis A first

Hypothesis A is highest-likelihood. Quickest test:

1. Add a Notify call to `WriteFile` (`ChangeSize | ChangeLastWrite,
   ActionModified, fileName`) when the write extends file length.
2. Re-run `repro_chrome.js` 20×. If crash rate drops significantly, you've
   localised it.

If it doesn't help, try Notify on every Cleanup (not just delete-flagged).

### 6.6 If hypothesis A doesn't pan out, test hypothesis B

Look at chrome source for `FSCTL_QUERY_FILE_REGIONS` callers. The
function in Chromium is in `base/files/file_util_win.cc` or
`net/base/file_stream_win.cc`. Check if it's called during pipe-mode init
and whether it has a fallback for `INVALID_DEVICE_REQUEST`. Maybe RamDrive
should return a different status (e.g. SUCCESS with empty regions) to keep
chrome happy.

### 6.7 Open `/opsx:explore` once a hypothesis has evidence

Use the explore mode (`/opsx:explore`) to formalise the diagnosis. Don't
jump straight to `/opsx:new` — bug #3 has more design space than bug #2 had
(multiple possible fixes per hypothesis), and the explore phase is where
that gets sorted out.

---

## 7. Useful commands cheat sheet

```bash
# Mount H: (admin shell, default config = repro)
cd F:/MyProjects/RamDrive
dotnet run --project src/RamDrive.Cli --no-build -- \
    --RamDrive:MountPoint='H:\\' --RamDrive:CapacityMb=128 --RamDrive:VolumeLabel=Diag

# Mount H: with cache OFF (control = no repro)
dotnet run --project src/RamDrive.Cli --no-build -- \
    --RamDrive:MountPoint='H:\\' --RamDrive:CapacityMb=128 \
    --RamDrive:EnableKernelCache=false

# Reproduce (in another shell)
node F:/MyProjects/RamDrive/repro_chrome.js 'H:\Temp\repro_test'

# Bisect chrome flags
node F:/MyProjects/RamDrive/bisect_chrome.js 'H:\Temp\bisect' first_half
node F:/MyProjects/RamDrive/bisect_chrome.js 'H:\Temp\bisect' second_half
node F:/MyProjects/RamDrive/bisect_chrome.js 'H:\Temp\bisect' full

# Procmon orchestration (admin)
F:/MyProjects/RamDrive/debug_batch.cmd

# Slice a procmon CSV by chrome PID and time window
awk -F'","' '/chrome.exe/ && $5 ~ /^H:/ && $3=="<PID>" {print}' \
    F:/procmon_chrome2.csv > /tmp/chrome_pid.csv

# Find non-SUCCESS in window
grep -v '"SUCCESS"' /tmp/chrome_pid.csv | \
    awk -F'","' '{print $4 " | " $6}' | sort | uniq -c | sort -rn

# Verify leveldb files (bug #1 sanity check)
xxd 'H:/Temp/repro_test/Default/Local Storage/leveldb/CURRENT' | head -2

# Direct ACL probe (bug #2 sanity check)
powershell -Command "Get-Acl 'H:\Temp\repro_test\Default' | Format-List"
```

---

## 8. References (in this repo)

- `docs/leveldb-cache-coherency-postmortem.md` — full bug #1 postmortem,
  procmon usage caveats, smoking-gun trace pattern, TLA+ modelling plan,
  §9.1 about bug #3 surfacing during bug #1 diagnosis
- `openspec/changes/archive/2026-05-03-fix-leveldb-cache-coherency/` —
  proposal/design/specs/tasks for bug #1
- `openspec/changes/archive/2026-05-03-fix-acl-inheritance/` —
  proposal/design/specs/tasks for bug #2
- `openspec/specs/cache-invalidation/spec.md` — the active capability spec
  for the Notify matrix
- `openspec/specs/file-info-timeout-config/spec.md` — `FileInfoTimeoutMs`
- `openspec/specs/default-security-descriptor/spec.md` — root SDDL OICI
- `tests/RamDrive.IntegrationTests/LevelDbReproTests.cs` — bug #1 regression
- `tests/RamDrive.IntegrationTests/AclInheritanceTests.cs` — bug #2 regression
- `tests/RamDrive.IntegrationTests/CacheCoherencyTests.cs` — bug #1 unit-level
  variants
- `tests/RamDrive.IntegrationTests/RamDriveFixture.cs` — `TestAdapter`
  mirrors the production adapter; `SetTraceFilter` for in-fixture tracing
- `src/RamDrive.Cli/WinFspRamAdapter.cs` — production adapter; XML doc at
  the top lists the Notify matrix
- `repro_chrome.js`, `repro_chrome_min.js`, `bisect_chrome.js`,
  `debug_batch.js`, `debug_batch.cmd` — diagnostic scripts (gitignored)

External:

- `winfsp-native/winfsp/inc/winfsp/winfsp.h` and `fsctl.h` — IFileSystem
  interface, `FspFileSystemNotify`, `FSP_FSCTL_NOTIFY_INFO`
- `winfsp-native/winfsp/src/dll/fuse/fuse.c` — reference single-shot
  Notify usage (we follow the same pattern)
- `winfsp-native/winfsp/src/dll/fsop.c::FspCreateSecurityDescriptor` — where
  ACE inheritance happens; relevant if hypothesis revisits ACL behaviour
- `winfsp-native/winfsp/src/sys/write.c:530` — `Request->Req.Write.ConstrainedIo
  = !!PagingIo` (paging writes ALWAYS have ConstrainedIo=TRUE)

---

## 9. Open questions you may need to ask the user

- Whether to invest in a more sophisticated procmon harness (record on
  STATUS_BREAKPOINT only, not 60-second blanket capture) — current setup is
  manual and produces 1-2 GB CSVs
- Whether to widen scope to fix all `Profile error occurred` symptoms (the
  `top_sites` / `login_database` / `disk_cache` errors) in the same change,
  or only the STATUS_BREAKPOINT
- Whether to add an opt-in `--RamDrive:DisableNotifyAsync` flag to test
  hypothesis C without permanently changing dispatch behaviour

---

## 10. Final notes for compaction continuation

If this document is being read in a fresh session post-compaction:

1. Run `git log --oneline -5` first to see what's on main right now
2. Skim §1, §3, §5, §6 here — those are the action-relevant parts
3. Check `openspec list --json` to see if there are any active changes
   already (if yes, continue them with `/opsx:apply`; if no, start with
   re-establishing repro per §6.2)
4. Don't re-derive bisection from scratch. The minimum repro is 5 flags
   and the trigger is `cache=true + pipe + RamDrive`. That's settled.
5. The `Notify` off-thread dispatch is non-negotiable for `TortureTests` —
   don't revert it without re-checking that test
6. The TLA+ modelling task (`fix-leveldb-cache-coherency` tasks 7.3-7.5)
   is **explicitly deferred to user's other PC** — do not start it
