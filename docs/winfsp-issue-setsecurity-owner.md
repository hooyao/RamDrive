# Enhancement: optional framework-level owner-change guard in SetSecurity (let in-memory file systems approximate NTFS's STATUS_INVALID_OWNER)

## What I'd like

An **opt-in, purely user-mode** way for a WinFsp file system to have the framework reject a
`SetSecurity` request that **reassigns the owner** when the caller could not have the privilege to
do so — returning `STATUS_INVALID_OWNER` (Win32 `ERROR_INVALID_OWNER`, 1307), the same status real
NTFS returns.

Concretely, one of:

- a `FSP_FSCTL_VOLUME_PARAMS` flag (e.g. `RejectOwnerChangeOnSetSecurity`) that makes
  `FspFileSystemOpSetSecurity` fail an owner-changing request before dispatching to the FS, **or**
- a `FspSetSecurityDescriptor` variant (e.g. `FspSetSecurityDescriptorEx`) that takes the object's
  *current* owner and returns `STATUS_INVALID_OWNER` when `OWNER_SECURITY_INFORMATION` would change
  it,

so that in-memory file systems (memfs and the many third-party ones built on it) can match NTFS's
owner-assignment behavior with a one-line opt-in, instead of each re-implementing the same check.

No kernel/driver or FSCTL protocol change is implied by either option — both live entirely in the
user-mode DLL.

## Why — the behavior gap this closes

On a WinFsp in-memory file system that implements `SetSecurity` via the documented helper
`FspSetSecurityDescriptor` (as bundled **memfs** does), a **non-privileged** caller can reassign a
file/directory **owner** to an arbitrary SID and the call **succeeds**. On real NTFS the identical
operation is refused with `ERROR_INVALID_OWNER` (1307), because owner assignment requires
`SeRestorePrivilege` / `SeTakeOwnershipPrivilege` the caller does not hold.

Because NTFS's refusal is **atomic** (the DACL bundled in the same request is also dropped), an
object there always keeps its creator-owner and therefore stays deletable/recoverable by that
creator. On memfs the owner change goes through, the creator loses the owner-implied
`READ_CONTROL` / `WRITE_DAC`, and the object can become **un-deletable and its ACL un-readable** by
the very principal that created it.

This is observable with real tools: profilers / ETW collectors that lock down their own temp
directories by tightening the SD (including changing the owner) leave **un-deletable, un-readable
leftovers** when `%TEMP%` is on a memfs-backed volume, then fail on a subsequent run when they try
to recreate the same `*_<GUID>` temp path (it still exists and they can't touch it). The same
workload on NTFS works, because NTFS refuses the owner reassignment up front.

## Repro (stock memfs vs NTFS, non-elevated)

Self-contained C reproducer attached below. Build with MSVC
(`cl /W4 winfsp-setsecurity-owner-repro.c advapi32.lib`), run from a **non-elevated** shell with a
memfs mount and an NTFS control path:

```
repro.exe M:\  C:\Temp
```

Observed (abridged) — owner reassignment to `BUILTIN\Administrators` (`S-1-5-32-544`):

```
==== root: M:\ (memfs) ====
  owner before:   S-1-5-21-...-1001        (the creator)
  attempting owner change -> S-1-5-32-544
  SetKernelObjectSecurity(OWNER) SUCCEEDED  <== owner reassigned
  owner after:    S-1-5-32-544

==== root: C:\Temp (NTFS) ====
  owner before:   S-1-5-21-...-1001
  attempting owner change -> S-1-5-32-544
  open(WRITE_OWNER) FAILED win32=5  => owner change refused (NTFS-like)
  owner after:    S-1-5-21-...-1001        (unchanged)
```

(NTFS may refuse at the SetSecurity call with `ERROR_INVALID_OWNER` 1307, or earlier at handle open
with `ACCESS_DENIED` 5, depending on the object's DACL; either way the owner is not reassigned. The
point is memfs *accepts* the reassignment while NTFS does not.)

## Why this can't be done correctly inside the FS today

The reason an in-memory FS can't already do this itself is that the **caller token is not available
at the SetSecurity path**, so it cannot perform the real privilege check NTFS performs:

- `src/dll/security.c` — `FspSetSecurityDescriptor` merges via
  `SetPrivateObjectSecurity(SecInfo, ModDesc, &InputDesc, &FspFileGenericMapping, /*Token*/ 0)`;
  the `Token` is hardcoded `0`, so owner-validity is never checked.
- `inc/winfsp/fsctl.h` — `FSP_FSCTL_TRANSACT_REQ.Req.Create` carries `UINT64 AccessToken`, but
  `Req.SetSecurity` has **no token field**.
- `src/sys/security.c` — `FspFsvolSetSecurity` posts the request with no `SeAccessCheck` / token
  capture (unlike `src/sys/create.c`, which fills `Req.Create.AccessToken`).
- `src/dll/fsop.c` — `FspFileSystemOpSetSecurity` calls `Interface->SetSecurity(...)` with no token
  parameter, and `FspFileSystemOpEnter` does not impersonate, so the handler can't recover the
  caller via `OpenThreadToken` either.

A passthrough FS sidesteps all this by forwarding to a real handle and letting the kernel SRM do
the check (`tst/ntptfs/ptfs.c` `SetSecurity` -> `NtSetSecurityObject(Handle, ...)`). An in-memory
FS has neither the caller token nor a real backing handle, so it cannot replicate the NTFS outcome
on its own.

The proposed enhancement deliberately avoids needing the token: instead of asking *"does the caller
hold the privilege to set this owner?"* (unanswerable without the token), it asks *"is the owner
being changed at all?"* and refuses if so. That is a strict, safe approximation — it never silently
strips an object's recoverability, and it exactly matches the NTFS-observable result for the
non-privileged case (owner stays with the creator). A file system that legitimately needs to
support owner changes simply does not opt in (or implements `SetSecurity` itself).

## Scope / non-goals

- **In user-mode only.** Both proposed shapes (a `VOLUME_PARAMS` flag handled in
  `FspFileSystemOpSetSecurity`, or a `FspSetSecurityDescriptorEx` helper) require no driver or
  FSCTL change.
- **Not proposing** to flow the caller token to SetSecurity (adding `AccessToken` to
  `Req.SetSecurity` + capturing it in the driver + `SetPrivateObjectSecurityEx`). That would enable
  an *exact* check but touches the kernel and the protocol; I'm noting it only for completeness and
  assume it's out of scope.
- Default behavior unchanged; existing file systems are unaffected unless they opt in.

## Happy to help

I have the attached repro and a working user-mode implementation of the "reject owner change ->
STATUS_INVALID_OWNER" approximation in my own file system. I'm glad to turn it into a draft PR for
either the `VOLUME_PARAMS` flag or the `FspSetSecurityDescriptorEx` helper if you think one of them
is the right shape.

---

<details>
<summary>Self-contained C reproducer (<code>winfsp-setsecurity-owner-repro.c</code>)</summary>

```c
// paste contents of winfsp-setsecurity-owner-repro.c here
// build: cl /W4 /nologo winfsp-setsecurity-owner-repro.c advapi32.lib
// usage: repro.exe <memfs-root> <ntfs-root>   (run non-elevated)
```

</details>
