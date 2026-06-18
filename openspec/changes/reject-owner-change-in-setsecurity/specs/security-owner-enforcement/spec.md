## ADDED Requirements

### Requirement: SetFileSecurity SHALL reject owner reassignment by a non-privileged caller

`WinFspRamAdapter.SetFileSecurity` MUST refuse any request that changes a node's owner SID, and MUST
apply no part of that request when it refuses. Specifically, when `securityInformation` has
`OWNER_SECURITY_INFORMATION` (bit `0x1`) set AND the modification descriptor's owner SID is non-null
AND differs from the target node's current owner SID, the call MUST return `STATUS_INVALID_OWNER`
(`0xC000005A`; WinFsp maps it to Win32 `ERROR_INVALID_OWNER` 1307) and MUST NOT mutate the node's
stored security descriptor — neither owner, group, nor the DACL/SACL carried in the same request
(atomic failure).

WinFsp does not surface the caller's access token to the SetSecurity callback: the kernel
`FSP_FSCTL_TRANSACT_REQ.Req.SetSecurity` has no `AccessToken` field (unlike `Req.Create`), and
`FspFileSystemOpEnter` does not impersonate the caller. A true `SeRestore`/`SeTakeOwnership` privilege
check is therefore impossible in a user-mode in-memory file system. Rejecting any owner **change** is
the chosen approximation of NTFS behaviour, under which a non-privileged caller's owner assignment
fails atomically. This is what preserves the invariant that an object remains deletable by its
creator (the owner retains the implied `READ_CONTROL`/`WRITE_DAC`).

When the owner is not being changed — `OWNER_SECURITY_INFORMATION` clear, OR the modification's owner
SID equals the current owner SID, OR no owner SID is supplied — the call MUST behave as before: merge
the requested OWNER/GROUP/DACL/SACL fields per `securityInformation` and return `STATUS_SUCCESS`.

The same rule MUST be implemented identically by the differential oracle
`MemfsReferenceFs.SetFileSecurity`, so that `DifferentialAdapter` — which compares the two NTSTATUS
results via `Comparators.CompareStatus("SetFileSecurity")` — observes no divergence. The
`ChaosTests` fuzzer does not exercise SetSecurity, so this lockstep MUST additionally be pinned by a
direct unit test that calls both adapters and asserts an equal status.

#### Scenario: Owner change to a different SID is refused atomically
- **WHEN** a caller invokes `SetFileSecurity` on an existing node with `OWNER_SECURITY_INFORMATION`
  set and a modification owner SID different from the node's current owner (and a modified DACL in the
  same descriptor)
- **THEN** the call returns `STATUS_INVALID_OWNER` (`0xC000005A`)
- **AND** the node's stored security descriptor is byte-for-byte unchanged (the bundled DACL change is
  also NOT applied)

#### Scenario: DACL-only change with owner unchanged succeeds
- **WHEN** a caller invokes `SetFileSecurity` with `securityInformation = DACL_SECURITY_INFORMATION`
  only and a modified DACL
- **THEN** the call returns `STATUS_SUCCESS`
- **AND** the node's DACL is updated while its owner SID is unchanged

#### Scenario: Re-applying the same owner is a permitted no-op
- **WHEN** a caller invokes `SetFileSecurity` with `OWNER_SECURITY_INFORMATION` set but the supplied
  owner SID equals the node's current owner SID (optionally together with a DACL change)
- **THEN** the call returns `STATUS_SUCCESS`
- **AND** any accompanying DACL/GROUP changes are applied

#### Scenario: Mounted directory stays deletable after a refused owner change
- **WHEN** a non-elevated process creates a directory under the mounted volume
- **AND** attempts, via `SetFileSecurity`/`SetKernelObjectSecurity`, to change its owner to a SID it
  does not hold (e.g. `BUILTIN\Administrators` or `LocalSystem`)
- **THEN** the attempt fails with `ERROR_INVALID_OWNER` / `STATUS_INVALID_OWNER`
- **AND** the directory's owner is unchanged (its ACL remains readable by its creator)
- **AND** the directory remains deletable by its creator
