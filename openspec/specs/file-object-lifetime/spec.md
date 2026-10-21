# file-object-lifetime Specification

## Purpose
Keep file backing content and identity valid while Windows retains open contexts,
including delayed paging I/O after namespace deletion or replacement. Reclaim
native capacity on final context release or after the host stops, and verify
these lifetime boundaries with regression tests and formal models.

## Requirements
### Requirement: Open contexts retain backing objects atomically

The filesystem SHALL acquire a node reference atomically with successful create/open and SHALL retain its content until all open-context references and namespace ownership have been released. Namespace lookups SHALL expose only linked nodes.

#### Scenario: Opening races with deletion
- **WHEN** a node is concurrently opened and deleted
- **THEN** the open either fails because the name is absent or returns a retained, usable object that deletion cannot dispose

#### Scenario: Multiple contexts close in different orders
- **WHEN** an unlinked node has multiple retained contexts and some of them close
- **THEN** remaining contexts can read and write the original content and native pages are returned only after the final context closes

### Requirement: Unlink permits delayed paging I/O

Cleanup(Delete) SHALL unlink the context's node by identity without disposing content retained by that or another context. Existing contexts SHALL continue to support valid reads, constrained paging writes, metadata queries and flush responses after unlink.

#### Scenario: File handle closes before a dirty mapping
- **WHEN** a delete-on-close file handle is closed while its writable mapping still exists
- **THEN** its name disappears and subsequent paging writes succeed without accessing disposed content or synchronization objects

#### Scenario: Old cleanup follows same-name recreation
- **WHEN** an old unlinked node receives cleanup after a new node is created under its former name
- **THEN** the new node remains linked and its identity and data remain unchanged

### Requirement: Rename replacement preserves object identity

Rename/replace SHALL transfer namespace ownership without invalidating existing contexts of either object. A rename resolving to the source object itself SHALL preserve its content and identity.

#### Scenario: Replaced destination still has an open context
- **WHEN** a source replaces a destination whose old object is still open
- **THEN** the destination name resolves to the source while the old destination context continues accessing its own retained content until Close

#### Scenario: Case-only rename
- **WHEN** a linked node is renamed to a name that resolves case-insensitively to itself
- **THEN** the node keeps its identity, data and open references

### Requirement: Final release and shutdown reclaim ownership safely

The filesystem SHALL dispose an unlinked node exactly once after the last context release, and SHALL reclaim linked and retained-unlinked nodes on shutdown after callbacks stop. Repeated shutdown or Close of an already cleared context SHALL not double-return pages.

#### Scenario: Final close returns capacity
- **WHEN** the final context of an unlinked or replaced object closes
- **THEN** its pages and reservations are returned and volume free capacity reflects the release

#### Scenario: Shutdown with retained kernel contexts
- **WHEN** the host has stopped and the filesystem is disposed with retained unlinked nodes
- **THEN** all backing content is reclaimed and subsequent repeated disposal or context cleanup cannot double-release it

### Requirement: Unexpected callback failures are observable

The adapter SHALL log unexpected callback exceptions and return STATUS_UNEXPECTED_IO_ERROR rather than concealing a paging-write failure as success.

#### Scenario: Failure handler is invoked
- **WHEN** an unexpected exception reaches the filesystem exception handler
- **THEN** it is logged and the handler returns the error status

### Requirement: Formal verification includes post-unlink lifetime

The formal models SHALL distinguish namespace linkage from backing-object lifetime, permit I/O through retained contexts after unlink, and cover last release, replacement and same-name recreation. The old eager-disposal behavior SHALL be expressible as a counterexample configuration.

#### Scenario: Fixed lifecycle verification
- **WHEN** TLC explores the fixed lifecycle and memory configurations
- **THEN** referenced objects remain alive, paging writes preserve object identity, pool accounting holds and fair completion drains pending writes and releases unlinked objects

#### Scenario: Original bug is enabled
- **WHEN** the focused model disposes backing content at unlink while a dirty mapping retains a context
- **THEN** TLC reports a paging-write safety violation with a counterexample trace
