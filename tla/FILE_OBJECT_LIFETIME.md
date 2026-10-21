# File-object lifetime verification

Issue #24 requires distinguishing a namespace name from the backing object retained
by WinFsp contexts. Closing a user handle triggers Cleanup; mappings and paging I/O
can still use that context until Close. Unlink must not dispose its content or lock.

## Models and implementation mapping

| Model action/state | Implementation boundary |
| --- | --- |
| `fileLinked`, `fileOpenRefs`, `fileAlive` | Namespace membership, context count, backing content lifetime |
| `DoOpen` | `RamFileSystem.OpenNode`, under the structure lock |
| `DoCreateFile` | Core namespace creation; the focused model also covers atomic create-plus-open |
| `DoDelete` | Identity-based unlink; retains content when contexts still exist |
| `DoClose` / `DoDispose` | Final context release; return pages and reservations only after unlink and last Close |
| `FileObjectLifetime.Create/Open` | Adapter create/open obtains a retained context atomically with lookup/insertion |
| `Cleanup` / `RepeatCleanup` | Unlink the context's identity, including cleanup after the former name is reused |
| `Rename` | Move the source name and drop only the replaced object's namespace ownership |
| `Dirty`, `Unmap`, `PagingWrite`, `Close` | A mapping can outlive the user handle; dirty data must remain writable before final release |

`RamDiskSystem` retains the existing three-phase write, page-pool, reservation,
read and volume-observer checks. `LifetimeSafe` requires backing content to remain
alive exactly while namespace ownership or context references exist. Unlink can
interleave with write phases when a context owns the object; final Close cannot
overlap an active callback.

`FileObjectLifetime` separately models names and object identities, so a new object
can reuse the name of an older, unlinked object. It checks context/namespace safety,
identity-preserving reads and paging writes, and final release. Under fair client
cleanup/unmap and kernel paging-write/close completion it also checks dirty-page
drain and eventual release of unlinked objects. These are protocol assumptions,
not a wall-clock guarantee that Windows immediately flushes or closes clean cache
references. Clean kernel references can continue charging native capacity until Close.

Shutdown is outside the running-callback model: the host first stops callbacks,
then the filesystem deterministically disposes the linked tree and retained set.
Core tests verify this boundary and idempotent cleanup.

## Callback-ordering review

The no-I/O-after-final-Close boundary is an external WinFsp contract, not a
property proved about Windows by TLC. Review against the installed WinFsp
2.1.25156 driver and the pinned WinFsp.Native 0.1.3-pre.1 binding confirms:

- WinFsp's [Cleanup contract](https://github.com/winfsp/winfsp/blob/ddca7bd5481857a65ba552f643b8776fd070836f/inc/winfsp/winfsp.h#L357-L360)
  explicitly requires accepting more operations until Close, even after deletion.
  The [kernel close path](https://github.com/winfsp/winfsp/blob/ddca7bd5481857a65ba552f643b8776fd070836f/src/sys/close.c#L62-L115)
  posts the user-mode Close from IRP_MJ_CLOSE, separately from Cleanup. Close can
  be omitted during volume teardown, so shutdown must reclaim retained nodes.
- The binding [retains the same handle state through Cleanup and releases it in Close](https://github.com/hooyao/winfsp-native/blob/5a0dd4d4888dcccaf355127faeed804d2b7f3875/src/WinFsp.Native/FileSystemHost.cs#L423-L447).
  RamDrive retains one node reference per successful create/open context and
  releases it in that Close callback. Late synchronous paging I/O uses the
  retained context directly; Cleanup cancellation does not skip those writes.
- Shutdown stops dispatch before disposing native content. The binding
  [stops the dispatcher before deleting the filesystem](https://github.com/hooyao/winfsp-native/blob/5a0dd4d4888dcccaf355127faeed804d2b7f3875/src/WinFsp.Native/WinFspFileSystem.cs#L167-L175);
  WinFsp [joins its dispatcher](https://github.com/winfsp/winfsp/blob/ddca7bd5481857a65ba552f643b8776fd070836f/src/dll/fs.c#L417-L434)
  and each dispatcher [joins its child](https://github.com/winfsp/winfsp/blob/ddca7bd5481857a65ba552f643b8776fd070836f/src/dll/fs.c#L346-L359).
  The CLI stops its host before DI disposes the filesystem/pool; the test fixture
  uses explicit host, filesystem, pool disposal order.

Structure-lock ownership prevents open/unlink/final-release races. Mounted
mapping regressions and real-drive VS acceptance exercise the external callback
boundary; core regressions test final-release accounting and stopped-host
shutdown. Changes to the binding's callback lifetime or asynchronous RamDrive
callbacks would require reviewing these assumptions again.

## Reproducible bounded checks

Requires Java 11+ and the TLC JAR documented in `RUN_FULL_VERIFICATION.md`.

```powershell
./tla/Verify-Lifetime.ps1
# Or provide a Java executable explicitly:
./tla/Verify-Lifetime.ps1 -JavaPath 'C:\path\to\java.exe'
```

The script runs all of:

| Configuration | Bound | Expected result |
| --- | --- | --- |
| `FileObjectLifetime_Minimal.cfg` | 2 names, 3 object identities, 2 contexts | All safety and liveness checks pass |
| `RamDiskSystem_Lifecycle.cfg` | 1 shared page, 2 object slots, up to 2 contexts per object | Pool/accounting/lifetime invariants and WriteTerminates pass |
| `FileObjectLifetime_BugRepro.cfg` | 1 name/object/context; eager disposal enabled | `PagingWritesSucceed` fails with a counterexample |

The negative configuration deliberately enables the old bug. Its nonzero exit is
required; a parser/tool failure or a different invariant is not accepted as success.
The trace contains create, map/dirty, Cleanup/Delete disposing a still-referenced
object, and a failed paging write. Logs and machine-readable results are written to
`artifacts/tla-lifetime/`. CI runs the same script on model/core changes.

## Larger memory-protocol runs

Existing memory configurations explicitly set `MaxOpenReferences=0` to retain the
original core-memory state space. `RamDiskSystem_LifecycleExtended.cfg` enables
2 shared pages, 2 slots and 2 contexts per slot for a larger combined check.

```powershell
Set-Location tla
java -XX:+UseParallelGC -Xmx16g -jar tla2tools.jar -workers auto -config RamDiskSystem_Minimal.cfg RamDiskSystem.tla
java -XX:+UseParallelGC -Xmx16g -jar tla2tools.jar -workers auto -config RamDiskSystem_LifecycleExtended.cfg RamDiskSystem.tla
```

The older approximate runtime estimates are not bounds on the current state
space. These configurations can explore millions of states and need extended
time, especially for temporal-property checks. A stopped exploration with no
error is **not** a completed verification. Keep its status separate from the
bounded checks above; Standard/Medium/Stress remain available for longer runs.
