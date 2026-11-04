# Reparse-point models

Run all bounded checks and both negative controls with:

```powershell
./tla/Verify-Reparse.ps1 -Workers 4
```

Pass `-JarPath` to use another TLC release for an independent run.

## Metadata and locking

`ReparseMutation.tla` models one file, one directory, a file writer, two possible
link tags, a directory child, valid/invalid set requests, deletion, and metadata
observations. Its lock boundaries map to the implementation:

| Model | Implementation |
| --- | --- |
| `StartFileSet`, `CheckFile`, `CommitFile`, `ReleaseFile` | `RamFileSystem.SetReparsePoint`, holding `_structureLock` and then `PagedFileContent.TryWhenEmpty`'s write lock through publication |
| `StartWrite`, `EnterWrite`, `CommitWrite`, `Truncate` | The content write/length paths; page allocation between scan and commit is abstracted |
| `StartDirSet`, `CheckDir`, `CommitDir`, `CreateChild`, `RemoveChild` | Directory mutations under `_structureLock`; `ResolvePath` rejects creating a child beneath a link |
| `DeleteTag` | Matching reparse-tag deletion under `_structureLock` |
| `Observe` | `FileNode.GetReparseMetadata` reads one immutable buffer and derives both the attribute and tag from that snapshot |

The invariants check no child exists beneath a directory link, a successful file
set occurs while the file is empty, wrong-tag and invalid requests cannot commit,
and each metadata snapshot agrees on tag/data/attribute. An existing file handle
may write **after** a successful set, so the model does not assert that a linked
file stays empty. It checks lock ownership and, under the stated weak/strong
scheduler fairness assumptions, that file and directory set operations finish.

The `BugFile` and `BugDirectory` configurations intentionally release the relevant
lock between validation and publication. TLC must find `FileSetWasEmpty` and
`DirectoryLinkHasNoChildren` violations respectively; these negative controls
ensure both race invariants can detect the intended bugs.

This is bounded verification of one writer, one file, one directory and one child;
it is not a proof for arbitrary node or writer counts. It abstracts the page pool
and three-phase allocation, whose existing model is `RamDiskSystem.tla`. The tag
stands for an already-validated immutable buffer; byte-level validation itself is
covered by unit and mounted integration tests.

## Resolution

`ReparseResolution.tla` explores every target graph of three local links, with
missing, ordinary and external destinations, and permits targets to change while
resolution runs. With a 32-hop bound, it checks that the resolver terminates under
weak scheduling fairness, preserves the bound, and reports the appropriate terminal
category, handing external paths off without resolving them locally.

This is an abstract model of the local loop and its bound, not a model of WinFsp's
native implementation or a proof of Windows/MUP behavior. Path string parsing,
volume-prefix conversion, ACL enforcement, kernel caching, and cross-volume
reparsing are tested through real mounts and are outside the TLA+ model.
