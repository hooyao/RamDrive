# TLA+ Full-System Verification — Run Guide for Claude Code

This guide instructs Claude Code on a beefy PC to run the full TLA+ verification
suite for the RamDrive project. The CI machine and dev machines only run the
Minimal config (~12 minutes); the larger configs need ~60 GB RAM and many hours
of wall-clock time, so they live on a dedicated workstation.

---

## What you are verifying

Two TLA+ specs:

1. **`tla/RamDiskSystem.tla`** — the full concurrency protocol of `PagedFileContent`
   + `PagePool` + `RamFileSystem` + the `WinFspRamAdapter` callbacks that read
   externally observable state. Configs of increasing size:
   - `RamDiskSystem_Minimal.cfg` (3 pages × 2 files) — already run on CI
   - `RamDiskSystem_Medium.cfg` (3 pages × 3 files)
   - `RamDiskSystem_Standard.cfg` (4 pages × 2 files, no liveness)
   - `RamDiskSystem_Stress.cfg` (6 pages × 3 files, no `ReadConsistent`)

2. **`tla/KernelCcCache.tla`** — the WinFsp kernel cache vs callback-response
   coherency model that captures bug #3. Already trivial to run; included for
   completeness. Two configs:
   - `KernelCcCache_Minimal.cfg` (BuggyCallbacks={}) → must pass
   - `KernelCcCache_Bug3Repro.cfg` (BuggyCallbacks={"FlushFileBuffers"}) → must
     deterministically violate `CacheFileSizeMatchesActual` in 4 steps

The architecture, invariants, and what each TLA+ action maps to in the C# code
is documented in **`CLAUDE.md`** (search for "Formal Verification (`tla/`)").

---

## Prerequisites on the runner PC

- **Java 11 or later** — verify with `java -version`
- **`tla/tla2tools.jar`** — TLC model checker. If missing, download:
  ```bash
  curl -sL -o tla/tla2tools.jar \
    https://github.com/tlaplus/tlaplus/releases/download/v1.8.0/tla2tools.jar
  ```
- **RAM**: 64 GB minimum for Standard with liveness; 32 GB is enough for
  invariant-only Standard and for Stress.
- **Disk**: ~50 GB free (TLC writes a state-fingerprint file under `tla/states/`).
- **Time**: budget overnight for Standard. Run in `screen` / `tmux` / nohup so
  an SSH disconnect doesn't kill TLC.

---

## What to run, in order

Start with the cheapest config to confirm the runner works, then move up.
**TLC must be invoked from the `tla/` directory** (it expects the spec file
name to match the module name with no path prefix — running from elsewhere
fails with `File name '...' does not match the name '...' of the top level
module`).

### Step 1: Re-validate Minimal (smoke; ~12 min, ~3 GB RAM)

```bash
cd <repo-root>/tla
java -XX:+UseParallelGC -jar tla2tools.jar -workers auto \
     -config RamDiskSystem_Minimal.cfg RamDiskSystem.tla \
  | tee RamDiskSystem_Minimal.log
```

Expected: `Model checking completed. No error has been found.` Exit 0. If this
fails, fix the environment before going further.

### Step 2: Medium (~30–90 min, ~10 GB RAM)

```bash
cd <repo-root>/tla
java -XX:+UseParallelGC -jar tla2tools.jar -workers auto \
     -config RamDiskSystem_Medium.cfg RamDiskSystem.tla \
  | tee RamDiskSystem_Medium.log
```

Includes `WriteTerminates` (liveness) — needs more RAM than just invariants.

### Step 3: Standard, invariants only (~5 hours, ~60 GB RAM)

The committed `RamDiskSystem_Standard.cfg` already has liveness disabled. Run as is:

```bash
cd <repo-root>/tla
java -XX:+UseParallelGC -Xmx100g -jar tla2tools.jar -workers auto \
     -config RamDiskSystem_Standard.cfg RamDiskSystem.tla \
  | tee RamDiskSystem_Standard.log
```

This is the headline run. ~66 M distinct states. Expected: invariants hold,
exit 0. Save the log either way.

### Step 4: Stress — only if Standard passes and you have spare cycles (~12+ hours)

```bash
cd <repo-root>/tla
java -XX:+UseParallelGC -Xmx100g -jar tla2tools.jar -workers auto \
     -config RamDiskSystem_Stress.cfg RamDiskSystem.tla \
  | tee RamDiskSystem_Stress.log
```

6 pages × 3 files. Note `ReadConsistent` is intentionally excluded from this
config (state space too large with it on). Don't be surprised if this OOMs —
report the failure mode rather than trying to chase it.

### Step 5: KernelCcCache (~5 seconds total)

Sanity check the bug #3 spec:

```bash
cd <repo-root>/tla
# Fixed-code config — must pass
java -XX:+UseParallelGC -jar tla2tools.jar -workers auto \
     -config KernelCcCache_Minimal.cfg KernelCcCache.tla \
  | tee KernelCcCache_Minimal.log

# Buggy-code config — must produce a 4-step counterexample
java -XX:+UseParallelGC -jar tla2tools.jar -workers auto \
     -config KernelCcCache_Bug3Repro.cfg KernelCcCache.tla \
  | tee KernelCcCache_Bug3Repro.log
```

Expected:
- `KernelCcCache_Minimal.log`: `No error has been found.`
- `KernelCcCache_Bug3Repro.log`: `Invariant CacheFileSizeMatchesActual is
  violated.` and a 4-state trace ending with `FlushFileBuffers(f1)` flipping
  `CcCache[f1].FileSize` from 1 to 0 while `ActualFileSize[f1]` stays 1. This
  failure is the *expected* outcome of that config — do not "fix" it.

---

## What to report back

When all the runs you intend to do are finished, post back:

1. **Which configs ran to completion**, with TLC's final summary line for each
   (states explored, distinct states, depth, wall-clock time).
2. **The runner machine spec**: CPU model, core count, RAM, OS.
3. **Anything that went wrong**: OOM, wall-clock exceeded, parse error,
   invariant violation. For invariant violations, include the full counterexample
   trace from the log so it can be triaged.
4. **The full `*.log` file for any failing run**, attached or pasted.

Do **not** edit the `.tla` or `.cfg` files unless you find a bug in them. If
you find a real invariant violation in `RamDiskSystem.tla`, that is a serious
finding — capture the trace and stop, do not modify the spec to make it pass.

---

## Quick reference — commands you might want

```bash
# How many states checked so far (peek at a running TLC)
tail -f tla/RamDiskSystem_Standard.log | grep -E "states|Progress"

# Resume after an interrupted run (TLC checkpoint)
java -XX:+UseParallelGC -Xmx100g -jar tla2tools.jar -workers auto \
     -recover states/<timestamp> \
     -config RamDiskSystem_Standard.cfg RamDiskSystem.tla

# Clean up state directory between runs (optional; ~50 GB)
rm -rf tla/states/

# Background-run with logs
nohup java -XX:+UseParallelGC -Xmx100g -jar tla2tools.jar -workers auto \
     -config RamDiskSystem_Standard.cfg RamDiskSystem.tla \
     > RamDiskSystem_Standard.log 2>&1 &
```

---

## Don't

- Don't lower MaxPages / NumFiles in the existing `.cfg` files to "make it
  finish faster" — those are calibrated to the bug surface they were built to
  catch. Create a new `.cfg` if you want a custom config; don't mutate the
  committed ones.
- Don't disable invariants to make a run finish — if an invariant violates,
  that's the answer.
- Don't run `Standard` and `Stress` in parallel on the same machine; both
  expect to use most of available RAM.
