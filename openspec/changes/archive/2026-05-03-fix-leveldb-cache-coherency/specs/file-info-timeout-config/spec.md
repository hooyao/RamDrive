## ADDED Requirements

### Requirement: Operators SHALL be able to bound the kernel FileInfo cache lifetime

The `RamDriveOptions` configuration object MUST expose a `FileInfoTimeoutMs` property (uint, milliseconds) that the adapter SHALL pass to `FileSystemHost.FileInfoTimeout` during `Init`. The default value MUST be `1000` ms.

This setting acts as defence in depth on top of the explicit cache-invalidation notifications defined by the `cache-invalidation` capability. Notifications are the primary correctness mechanism; the timeout bounds the impact of any path that escapes notification (e.g. a future code path that mutates state without an accompanying `Notify` call).

The setting MUST be wired through the standard configuration binding (`appsettings.jsonc` under the `RamDrive` section, overridable via CLI as `--RamDrive:FileInfoTimeoutMs=N`). It MUST accept the value `0` (cache disabled) and `uint.MaxValue` (cache effectively permanent — operator opt-in to "trust the notifications completely"). Values in between are passed through unchanged.

The previously-shipped `EnableKernelCache` boolean MUST be preserved for backward compatibility:
- `EnableKernelCache=true` (the default) makes the adapter apply `FileInfoTimeoutMs` to the host as `FileInfoTimeout`.
- `EnableKernelCache=false` forces the adapter to set `FileInfoTimeout = 0` regardless of `FileInfoTimeoutMs`, preserving the documented "no kernel cache" mode.

#### Scenario: Default mount uses 1000 ms timeout
- **WHEN** the service starts with the default `appsettings.jsonc` (which does not set `FileInfoTimeoutMs`)
- **THEN** `FileInfoTimeoutMs` equals `1000`
- **AND** the mounted host has `FileInfoTimeout = 1000`

#### Scenario: Explicit override via CLI
- **WHEN** the service is started with `--RamDrive:FileInfoTimeoutMs=5000`
- **THEN** the mounted host has `FileInfoTimeout = 5000`

#### Scenario: Setting EnableKernelCache=false zeroes the timeout
- **WHEN** the service is started with `--RamDrive:EnableKernelCache=false --RamDrive:FileInfoTimeoutMs=10000`
- **THEN** the mounted host has `FileInfoTimeout = 0`
- **AND** the explicit `FileInfoTimeoutMs` value is ignored (the boolean toggle wins for compatibility)

#### Scenario: Operator opts in to permanent cache
- **WHEN** the service is started with `--RamDrive:FileInfoTimeoutMs=4294967295`
- **THEN** the mounted host has `FileInfoTimeout = uint.MaxValue`
- **AND** correctness depends entirely on the notification matrix from the `cache-invalidation` capability (this is the configuration the integration test fixture pins for regression testing)

#### Scenario: Documented in operator-facing config table
- **WHEN** an operator reads `CLAUDE.md` for the configuration table or `appsettings.jsonc` comments
- **THEN** `FileInfoTimeoutMs` appears with its default (`1000`), unit (milliseconds), and a note that `0` disables the cache and `uint.MaxValue` makes it permanent
- **AND** the existing `EnableKernelCache` row is updated to clarify that `false` overrides `FileInfoTimeoutMs` to `0`
