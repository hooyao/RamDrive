## Context

Single-string SDDL change. Root cause and fix mechanism are fully captured in `proposal.md`. There is no design space:

- **The mechanism is a Windows SDDL grammar quirk**: ACE flags `OI`/`CI` mean `OBJECT_INHERIT_ACE` / `CONTAINER_INHERIT_ACE`. Without them, `FspCreateSecurityDescriptor` (in winfsp's kernel-side fsop) inherits zero ACEs from the parent.
- **The fix is mechanical**: append `OICI` to the flag field of each ACE in the canonical root SDDL.
- **There are no alternatives worth considering**: WinFsp does not expose a separate "default DACL for children" knob; the root SD is the only lever for inheritance behaviour on this volume.

## Goals / Non-Goals

**Goals:**
- Newly created files and directories inherit a DACL such that the creating principal can reopen them with the same access used at creation time.
- The change is a single-line SDDL edit in two locations (production adapter + integration fixture), guarded by a unit test that fails if the OI/CI flags are ever dropped.

**Non-Goals:**
- Per-user owners or custom group memberships.
- Honouring the `securityDescriptor` parameter that callers explicitly pass to `CreateFile` — that path already works because WinFsp computes and passes the inherited SD when the caller supplies a non-null one.
- Fixing the unrelated kernel-cache vs `--remote-debugging-pipe` STATUS_BREAKPOINT bug, which has different root cause and will be tracked in a separate change.

## Decisions

### Use `(A;OICI;FA;;;...)` rather than separate `OI`+`CI` ACEs

A single ACE with both inherit flags applies to children regardless of whether the child is a file or directory. The alternative (one ACE with `OI`, another with `CI`) doubles the SDDL length without changing behaviour.

### Apply to all three principals (SYSTEM, Administrators, Everyone)

The current SDDL grants all three. The fix preserves that grant; future tightening (e.g. dropping Everyone) is out of scope for this change but trivially additive in this capability later.

### Skip `design.md` content beyond this stub

Per OpenSpec spec-driven schema guidance, `design.md` is optional. The schema's task-graph treats it as a dependency for `tasks`, hence this stub file exists, but its content is intentionally minimal. Any future change touching this capability is free to author a real `design.md` if it has design space worth documenting.

## Risks / Trade-offs

- **Risk**: A test elsewhere depended on "new files have empty DACL" → silent behaviour change. → **Mitigation**: grepped `tests/` for `SecurityDescriptor`/`GetAccessControl`/`SetAccessControl` usage; only adapter implementation references found, no asserting tests.
- **Risk**: Operator's existing on-disk SDs (set via `SetFileSecurity`) are not updated by this change. → Not applicable — RAM drive, no persistence across mount.
- **Trade-off**: None worth recording.

## Migration Plan

Mount-time only. On the next mount the root SD has the corrected flags and all newly-created child objects inherit correctly. Existing nodes from the prior mount are gone (volatile storage). No rollout staging needed.

## Open Questions

None.
