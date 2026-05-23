# Postmortem: InitialDirectories null security descriptor → MSIX AppContainer launch failure

This document is the long-form record of the bug fixed by openspec change
[`fix-initialdirectories-null-sd`](../openspec/changes/fix-initialdirectories-null-sd/).
It is written so a future Claude Code session (or human reader) with no
context beyond this file, the source tree, and the diff that landed the fix
can pick up follow-on work — and so the reasoning behind the deliberate
NTFS divergence in §6 is preserved before it rots.

---

## 1. Symptom

User sets the Windows `TEMP` environment variable (via System Properties →
Environment Variables UI, which persists to the user's registry hive) to a
folder on the mounted RamDrive — typically `Z:\Temp` or `R:\Temp` where the
folder is pre-created by `RamDrive`'s `InitialDirectories` config option.
Logs out, logs back in so the new `TEMP` is picked up by user-session
processes.

Launches **Windows365.exe** (the new MSIX-packaged Microsoft Remote Desktop
client from the Store, `MicrosoftCorporationII.Windows365_*`). Process
appears in Task Manager for a few seconds, then disappears. No main window.
No on-screen error dialog. Repeated launches show the same pattern.

The user-visible side-effect was easier to spot than the crash itself: on
`Get-Acl Z:\Temp` PowerShell errors out with

```
Get-Acl : Method failed with unexpected error code 1338.
The security descriptor structure is invalid.
```

and `icacls Z:\Temp` says the same. But `Z:\` root and freshly-created
children of `Z:\Temp` are fine. Sibling RAM disk products (ImDisk's `V:\`)
work as `TEMP` with no issues.

Workaround: point `TEMP` at any folder *not* created by `InitialDirectories`
(e.g. `V:\Temp` on the other RAM disk, or `Z:\Sub\Temp` where `Sub` is
manually `mkdir`'d). Windows365 launches normally.

## 2. Initial wrong hypotheses

These were entertained before evidence ruled them out:

1. **"Some process corrupts the SD"**. Plausible because the user reported
   Chrome, Edge, VS Code, and Claude Code all opened the RAM drive during the
   diag window. → Falsified by trace (§4): there is no `SetFileSecurity` call
   to `\Temp` ever; the SD is born null at create time.
2. **"WinFsp's `FspCreateSecurityDescriptor` is buggy on bypass paths"**.
   Plausible given the OICI history from spec `default-security-descriptor`
   and commit `e5bc2e1`. → Falsified by trace: `WinFsp` is never even
   consulted for `\Temp`; the directory is created via a side channel.
3. **"AppContainer needs an explicit ACE for `S-1-15-2-1` (ALL_APP_PACKAGES)
   that we're not providing"**. → Falsified by direct comparison with ImDisk's
   `V:\Temp` SD: ImDisk also has only `Everyone (WD)` / `Authenticated Users (AU)`
   / `Users (BU)` ACEs, no AppContainer-specific SID, yet AppContainer apps
   work there. The decisive factor is whether the DACL contains an ACE for a
   well-known group that AppContainer tokens include; both products grant
   `Everyone` `FullControl`, so both should work. The difference must lie
   elsewhere.
4. **"Maybe `[Conditional("TRACE_FS")]` is firing as expected"**. → Falsified
   when the first diag trace log came back with header-only contents.
   `FsTracer` lives in `RamDrive.Core`, but `TRACE_FS` was only defined in
   `RamDrive.Cli.Diag`. `[Conditional]` is evaluated at the **callsite's**
   compile time, not the callee's, so every call site inside `RamDrive.Core`
   had been stripped from IL for years. We added a `Debug`-only
   `TRACE_FS` `DefineConstants` to `RamDrive.Core.csproj` to fix this.

## 3. Reproducer

Minimum repro:

1. Mount RamDrive with `InitialDirectories = { "Temp": {} }`.
2. `icacls <mount>\Temp` — fails with error 1338.

Or, for the user-visible end of the bug:

1. As above, set `TEMP=<mount>\Temp` via System Properties UI.
2. Log out, log back in.
3. Launch any MSIX-packaged app that runs in an AppContainer and writes to
   `%TEMP%` during startup. Windows365.exe is one such; many others exist.

Either is reliable, 100% reproducible.

## 4. Diagnosis via FsTracer

Added trace points on every SD-touching callback in `WinFspRamAdapter`:
`GetFileSecurityByName`, `GetFileSecurity`, `SetFileSecurity`, `CreateFile`,
plus an `InitialDir-Create` line in the host service. Output a `DescribeSd`
helper that renders an SD byte[] as `len=N ctrl=0xXXXX hex=..` so the
trace can be read at-a-glance for null/empty/structurally-valid distinctions.

Ran the reproducer with `RAMDRIVE_TRACE_PATH=Temp` and
`RAMDRIVE_TRACE_FILE=F:\MyProjects\RamDrive\diag-trace.log`. The first three
lines were decisive:

```
0.000808  T8    InitialDir-Create        \Temp  | sd=NULL (no SD passed)
0.156950  T12   GetFileSecurityByName-NF \Temp\claude\…\bznljrh9w.output
0.157254  T12   GetFileSecurityByName-NF \Temp\claude-5f31-cwd
...
6.189786  T12   GetFileSecurityByName    \Temp  | attr=0x10 sd=NULL
6.189792  T12   OpenFile                 \Temp  | co=0x1000020 ga=0x100081 size=0
...
```

Every subsequent `GetFileSecurityByName \Temp` (200+ of them as the user's
processes scrubbed the folder) returns `sd=NULL`. **No** `SetFileSecurity`
call to `\Temp` exists in the entire log. The directory's SD is born null
and stays null. Hypothesis 1 (external corruption) refuted.

## 5. Root cause

`WinFspHostedService.CreateDirectoriesRecursive` (in
`src/RamDrive.Cli/WinFspHostedService.cs:156`) walks the JSON
`InitialDirectories` tree and bootstraps each entry by calling

```csharp
if (_fs.CreateDirectory(path) != null) count++;
```

— note: **single-argument overload**. The second optional parameter
`byte[]? securityDescriptor = null` defaults to `null`. In
`RamFileSystem.CreateDirectory` the line

```csharp
node.SecurityDescriptor = securityDescriptor;  // pre-fix: stores null
```

faithfully stores that null. There is no fallback, no parent-inheritance
step, no validation. The node sits in the tree with `SecurityDescriptor = null`
forever.

When the WinFsp kernel asks our adapter for the SD —
`WinFspRamAdapter.GetFileSecurityByName` —

```csharp
securityDescriptor = node.SecurityDescriptor;  // null
return NtStatus.Success;
```

— we return `(STATUS_SUCCESS, null)`. The WinFsp kernel passes this up
to user-mode (`GetFileSecurityW`) which returns `needed=0`. Win32 callers
interpret that as "structure invalid" (error 1338). For AppContainer access
checks, the SRM treats a null SD on a securable object as "no access granted
to anyone" and denies the open. Windows365.exe gets `ACCESS_DENIED` opening
its TEMP working directory and crashes during startup.

Why this bypass path doesn't go through WinFsp: `InitialDirectories`
materialises directories **before mount completes** (or at least, from a
user-mode thread inside our own process, not in response to a kernel
`IRP_MJ_CREATE`). WinFsp's kernel-side `FspCreateSecurityDescriptor` —
which is what gives normal `CreateDirectoryW` calls a properly inherited
SD via the OICI mechanism fixed in commit `e5bc2e1` — never runs for these
nodes. The bypass dodges the entire NT security inheritance pipeline.

Crucially, *children* of `\Temp` created later via the normal
`CreateDirectoryW` path **are fine** — WinFsp asks us for the parent SD,
gets back null (because of the bug), and the kernel-side computation
produces a broken child SD too. So the corruption propagates downward, but
only via the kernel inheritance API; the immediate sibling files Chrome
puts under `\Temp` show the same issue. The user observed this empirically
when Chrome's profile directories under `Z:\Temp\<userdir>` also failed to
open.

## 6. Fix

Two layers, see `openspec/changes/fix-initialdirectories-null-sd/design.md`
Decisions 1 and 2:

1. **Write-time inheritance in `RamFileSystem`**. When
   `CreateFile`/`CreateDirectory` is called with `securityDescriptor == null`,
   assign `node.SecurityDescriptor = parent.SecurityDescriptor` — a *reference
   copy*, not a byte clone. This makes "no node in the tree has null SD" a
   structural invariant of `RamFileSystem`, enforced at every insertion site.

2. **Read-time defensive fallback in `WinFspRamAdapter`**. If
   `GetFileSecurityByName` or `GetFileSecurity` ever encounters a null SD
   anyway (regression of the structural invariant), substitute the cached
   root SD bytes and `LogWarning` once per path. Should be unreachable
   post-fix; exists to make any future regression fail-loud at the user-mode
   boundary instead of silently breaking AppContainer apps again.

### Why reference copy rather than deep copy

`SetFileSecurity` (the user-mode API for changing a file's ACL) allocates a
fresh `byte[]` from the merged `RawSecurityDescriptor` and assigns it to the
node. So any process that mutates a child's SD breaks the reference share
on first write — the standard copy-on-write contract. No callsite mutates
SD `byte[]`s in place, and a search of the codebase confirms it. The shared
reference is therefore safe; the alternative (a `byte[]` clone per node)
would allocate per `CreateDirectory` call on the hot path for zero
behavioural benefit.

## 7. Test coverage

Two automated layers, plus one manual acceptance check:

**Unit** (`tests/RamDrive.Core.Tests`):
- `RamFileSystemSdInheritanceTests.cs` — 8 tests covering:
  - `CreateDirectory` / `CreateFile` without SD inherit parent SD by reference
  - Deep nesting propagates root SD through every level
  - Explicit SD argument overrides inheritance
  - Child of a custom-SD parent inherits the custom SD, not root (i.e. the
    inherit-from-immediate-parent rule, not from-root)
  - Walk-the-tree invariant: after a mix of creates, no node has null SD
- `WinFspRamAdapterSecurityTests.cs` — 4 tests covering:
  - `GetFileSecurityByName` returns non-null SD for an InitialDirectories-style node
  - Root SD round-trip
  - Defensive fallback fires when forced-null SD is encountered, with
    once-per-path warning (a `ConcurrentDictionary<string,byte>` for
    suppression)
  - NotFound path

**Integration** (`tests/RamDrive.IntegrationTests`):
- `InitialDirectoriesSdTests.cs` — 6 tests with a private mount fixture
  that calls `_fs.CreateDirectory(path)` directly (mirroring exactly what
  `WinFspHostedService.CreateDirectoriesRecursive` does in production):
  - Win32 `GetFileSecurityW` on `\Temp` returns a self-relative SD with
    `SE_DACL_PRESENT | SE_SELF_RELATIVE`, passes `IsValidSecurityDescriptor`
  - Nested `\Cache\App1` is equally valid
  - Subdirectory created under `\Temp` via normal `CreateDirectoryW`
    inherits the Everyone `FullControl` ACE with `IsInherited = true`
  - `needed > 0` for every InitialDirectories node (direct null-SD probe)
  - Handle-based `GetFileSecurity` path (distinct from
    `GetFileSecurityByName`) also returns valid SD
  - **Known-divergence pin**, see §8

**Manual acceptance** (recorded in spec scenario, not automated):
- Set `TEMP=<mount>\Temp` via Windows env-var UI, mount with
  `InitialDirectories={"Temp":{}}`, re-login, launch Windows365.exe from
  Start menu (MSIX activation, not the `cd ...\WindowsApps\... & .\Windows365.exe`
  admin-PS bypass that would side-step the AppContainer). Main window must
  appear within 5s.

## 8. NTFS divergence — by design, pinned by test

The spec contract is **access-decision equivalence with NTFS**, not
**byte equivalence**. Our SD-inheritance shortcut introduces one observable
deviation:

> On the bypass-created node itself (`\Temp`, `\Cache`, etc.), the ACEs do
> NOT carry the `INHERITED_ACE` (ID) flag. They are byte-identical copies
> of the root's ACEs.

Real NTFS, when inheriting via `SeAssignSecurityEx`, sets the
`INHERITED_ACE` flag on each copied ACE so callers can distinguish
inherited from explicit ACEs. We skip that step — we share the parent SD
`byte[]` by reference. The flag is missing.

**Why this is OK**:
- `SeAccessCheck` (the NT access decision algorithm) **does not look at
  the `INHERITED_ACE` flag**. Allow/Deny decisions only consider the ACE's
  SID and access mask vs. the token's groups and requested access. The
  divergence is invisible to access decisions, which is the
  AppContainer-launch behaviour that motivated the fix.
- `INHERITED_ACE` matters to two kinds of caller: (a) `EditAcl` UIs that
  want to grey out the inherited ACEs as non-editable, (b) audit/forensics
  tools that distinguish explicit grants from inheritance. Both behaviours
  are cosmetic from a security standpoint.
- Children of bypass-path nodes that go through the normal WinFsp callback
  (`Directory.CreateDirectory(<mount>\Temp\Sub)`) DO get the flag, because
  WinFsp's kernel-side `FspCreateSecurityDescriptor` runs as usual and
  sets it. The divergence is contained to exactly the InitialDirectories
  nodes — typically a handful of folders, never the bulk of the tree.

The test
`InitialDirectory_AcesAreFlaggedNotInherited_KnownDivergence` in
`InitialDirectoriesSdTests.cs` pins this divergence: it asserts
`\Temp` has *no* inherited ACEs and `\Temp\Sub` has them. If a future
maintainer wants strict NTFS equivalence — e.g. because some new
`INHERITED_ACE`-sensitive caller emerges — the surgery point is
`RamFileSystem.CreateFile`/`CreateDirectory`: instead of
`node.SD = parent.SD`, build a fresh SD with `INHERITED_ACE` applied to
each ACE via `RawAcl` rewriting. Costs one allocation per `CreateDirectory`
call, which is acceptable for InitialDirectories (one-time) but not for
the WinFsp callback (hot path).

Other deliberate NTFS divergences inherited from earlier specs (not new
in this change, listed for completeness):

| Property | Real NTFS | RamDrive |
|---|---|---|
| Owner / Group | From creator token | Always `BUILTIN\Administrators` (per RootSddl `O:BA G:BA`) |
| SACL (audit) | Supported with `SeSecurityPrivilege` | API-supported but root has no SACL |
| Mandatory Integrity Label | Vista+ standard | Absent |

None of these affect AppContainer launches or the bug fixed here. They are
mentioned so a reader cataloguing all NTFS divergences has a complete list.

## 9. Lessons

1. **`[Conditional]` is callsite-evaluated**. The `TRACE_FS` trick has been
   in this codebase for months but didn't actually trace anything in
   `RamDrive.Core` — the call sites inside `Core.dll` were stripped at
   `Core.dll`'s compile time because `RamDrive.Core.csproj` didn't define
   `TRACE_FS`. Defining it in `RamDrive.Cli.Diag.csproj` made the runtime
   `_enabled` flag true (and the trace file open) but had no effect on
   what trace calls IL contained. The fix was a five-line `PropertyGroup
   Condition="'$(Configuration)' == 'Debug'"` in `RamDrive.Core.csproj`.
   Release/AOT builds still strip everything; Debug builds (used by
   `RamDrive.Cli.Diag` and tests) now actually trace. This single fact
   would have made the bug 3 (chrome STATUS_BREAKPOINT) hunt shorter.

2. **Bootstrap paths bypass kernel-managed invariants**. Every spec
   property that relies on "a kernel callback fires" is silently violated
   by any code path that doesn't trigger that callback. `InitialDirectories`
   was the easy-to-miss one here; future additions like "build this index
   at mount time" or "warm this cache before clients connect" would have
   the same risk. The mitigation pattern is **make the invariant
   structural in the data model**, enforced at every mutation point, rather
   than relying on the kernel to maintain it for us.

3. **Defence-in-depth with a noisy fallback beats no fallback**. The
   `GetFileSecurityByName` null-SD fallback should be unreachable
   post-fix, but if a future regression re-introduces a null-SD node it
   will fail loud (warning log) rather than silent (AppContainer breakage
   only observed in production by end users).

4. **Trace the data, not just the call**. The original `FsTracer` log
   format was `op  path  | extra=value` and `extra` rarely carried SD
   bytes. Adding `DescribeSd` (len + control flags + first 32 bytes hex)
   made the null/empty/structurally-valid distinction visible at a glance
   — bug found in two trace lines. For any future "is X corrupted?"
   debugging, render X in the trace, don't just confirm it was passed.

5. **Hypotheses 1 and 3 from §2 were both wrong but useful**. They
   eliminated whole categories of root cause (external corruption,
   AppContainer-specific ACE requirements) and constrained the search to
   "something at create-time produces an unusable SD that's still
   structurally enough not to throw at creation". That kind of negative
   evidence saves more time than any positive lead — record it.

## 10. Follow-on work

- The `Owner = BA` divergence (NTFS would use creator token) is the most
  visible remaining NTFS-equivalence gap. `takeown.exe` and certain
  "is this file mine" UI tooling notice it. Fix would require capturing
  the creator token at `CreateFile` time. Not in scope here; would warrant
  its own spec change if a real use-case appears.

- The defensive read-time fallback could in principle be removed once we
  have months of production data showing the warning never fires. Removing
  it saves one null check per security read. Trade-off: makes the next
  regression silent. Leaning toward keeping it indefinitely; cost is
  one branch.

- TLA+ model `tla/RamDiskSystem.tla` was deliberately not extended. SD
  bytes live below the model's abstraction (which tracks page-data tags
  `{0, 'f'}`). The new invariant ("no node has null SD") is a node-field
  bookkeeping property, not a concurrency property. Adding it to the
  model would not catch any bug the model is designed to catch.

## 11. Artefacts

- Change: `openspec/changes/fix-initialdirectories-null-sd/`
- Captured trace log used in diagnosis (deleted before commit; preserved
  as a multi-MB attachment on the originating issue if recovered).
- Spec delta: `default-security-descriptor` gains 6 new scenarios.
- Code touched: `RamFileSystem.cs` (write-time inheritance),
  `WinFspRamAdapter.cs` (read-time fallback + cached root SD bytes),
  `RamDrive.Core.csproj` (Debug-only `TRACE_FS`).
- Tests added: 8 unit + 6 integration covering the fix and the known
  NTFS divergence.
