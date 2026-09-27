# PeerOnQ brand migration

R0 makes PeerOnQ the only active product identity. The former product name was **Linkora**. It is
permitted only in this register, immutable historical evidence, and migration fixtures named by
`docs/brand-compatibility-allowlist.txt`.

## Canonical component names

| Surface | Canonical identity |
|---|---|
| Solution and .NET namespace root | `PeerOnQ.slnx`, `PeerOnQ.*` |
| Windows client | `PeerOnQ.App`, executable `PeerOnQ.exe` |
| Core runtime | `PeerOnQ.Domain`, `.Application`, `.Infrastructure`, `.Transport`, `.Media`, `.Platform.Windows` |
| Real-time services | `PeerOnQ.Signaling.Server`, `PeerOnQ.Turn.Configuration`, `PeerOnQ.Realtime.Deployment` |
| Cloud services | `PeerOnQ.Cloud.*`, `.Presence.Server`, `.Admin.Api`, `.Downloads.Service`, `.Observability` |
| Tests | `PeerOnQ.*.Tests` |
| Data and configuration | `%LOCALAPPDATA%\PeerOnQ`, `peeronq.db`, `PEERONQ_*`, `VITE_PEERONQ_*` |
| Installer/release | PeerOnQ display names and `PeerOnQ-*` artifacts |

## Controlled migration matrix

| Former identifier | Canonical behavior | Compatibility decision | Evidence |
|---|---|---|---|
| `Linkora.*` projects, namespaces and types | renamed to `PeerOnQ.*` | no active alias | 30-project build and full test suite |
| `%LOCALAPPDATA%\Linkora` | copied transactionally to `%LOCALAPPDATA%\PeerOnQ` | isolated constants in `LegacyBrandCompatibility`; source is preserved | `DataDirectoryMigrationTests` |
| `linkora.db` and `linkora-*.log` | renamed inside the copied data tree | isolated constants; conflict fails closed | `DataDirectoryMigrationTests` |
| `Linkora.DeviceSecret.v1` DPAPI entropy | successful legacy read is immediately rewritten with PeerOnQ entropy | isolated constant; failed rewrite preserves source | `DpapiSecretStoreTests` |
| `linkora_*` browser keys and `linkoraId` fields | copied to `peeronq_*` and `peerOnQId` without deleting source keys | `legacyBrandStorageMigration.ts` | `storageMigration.test.ts` |
| `com.linkora.desktop` update product ID | accepted only while validating deployed update history | isolated constant; new manifests use `com.peeronq.desktop` | `Phase5UpdateAndAuditTests` |
| `LNK-` device ID prefix | retained as an opaque deployed wire/storage identifier | isolated in `LegacyIdentityCompatibility`; changing it would make prefix-free input ambiguous and break existing identities | domain, persistence, signaling and redaction tests |
| MSI upgrade/product identity GUIDs | retained as opaque installer compatibility identifiers | never presented as branding | installer extraction/upgrade tests |
| applied database migration IDs | retained | immutable database history | migration/integration tests |
| `LINKORA_*` and `VITE_LINKORA_*` configuration aliases | removed | configuration is not persisted user data; PeerOnQ keys are canonical | purity check and build |

The migration never renames installed device IDs, applied database history, or upgrade GUIDs. New
code must not add another compatibility identifier without updating this table, the allowlist, and
a migration test.

## Automated gate

Run:

```powershell
./scripts/quality/test-brand-purity.ps1
```

The script scans tracked and untracked non-ignored product text and paths, rejects the former brand
case-insensitively, and allows only exact manifest entries. Generated, vendored, immutable baseline
evidence, and designated migration fixtures are not treated as active branding.
