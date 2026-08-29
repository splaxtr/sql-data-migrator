# Graph Report - sql-data-migrator  (2026-08-30)

## Corpus Check
- Corpus is ~46,314 words - fits in a single context window. You may not need a graph.

## Summary
- 587 nodes · 1277 edges · 23 communities
- Extraction: 98% EXTRACTED · 2% INFERRED · 0% AMBIGUOUS · INFERRED: 26 edges (avg confidence: 0.84)
- Token cost: 133,874 input · 0 output

## Community Hubs (Navigation)
- Migration Engine Core
- PDF Report Generation
- Docs, CI & Safety Concepts
- Server Admin API Types
- SQL Server Admin
- Migration UI Frontend
- Postgres Admin
- Admin Panel Frontend
- Launch Settings Config
- Batch Migration Runner
- Connection Store
- Claude Permissions Settings
- Desktop Shell & Run Modes
- User Provisioner
- Job Registry
- NuGet Packages & Solution
- Admin Request DTOs
- Schema Mirror
- Target Database Setup
- verify-ui Package Config
- UI Audit Script
- Migration Options
- Progress Kinds

## God Nodes (most connected - your core abstractions)
1. `SqlServerAdmin` - 29 edges
2. `PostgresAdmin` - 28 edges
3. `MigrationReportPdf` - 26 edges
4. `MigrationEngine` - 24 edges
5. `IServerAdmin` - 21 edges
6. `Job` - 20 edges
7. `MigrationReport` - 19 edges
8. `allow` - 15 edges
9. `ConnectionStore` - 13 edges
10. `RoleAttributes` - 13 edges

## Surprising Connections (you probably didn't know these)
- `Local-Only Operation as Security Property` --semantically_similar_to--> `Localhost Trust Boundary`  [INFERRED] [semantically similar]
  docs/ROADMAP.md → SECURITY.md
- `Management Tab (viewAdmin / Yönetim)` --implements--> `Server Management Panel (No Migration Guarantees)`  [INFERRED]
  src/Migrator.App/wwwroot/index.html → docs/SAFETY.md
- `SQL Data Migrator` --references--> `Contributor Covenant Code of Conduct v2.1`  [EXTRACTED]
  README.md → CODE_OF_CONDUCT.md
- `SQL Data Migrator` --references--> `Architecture Overview`  [EXTRACTED]
  README.md → docs/ARCHITECTURE.md
- `SQL Data Migrator` --references--> `Stored Credentials (Machine-Keyed DataProtection)`  [EXTRACTED]
  README.md → docs/SAFETY.md

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **Migration Safety Model** — docs_architecture_migration_pipeline, docs_architecture_single_transaction, docs_safety_source_read_only, docs_safety_atomic_rollback, docs_safety_verified_success, docs_safety_cascade_closure_check [EXTRACTED 1.00]
- **Release Automation Flow (Conventional Commits to Binaries)** — contributing_conventional_commits, _github_workflows_release_please_release_please_workflow, _github_workflows_release_release_workflow, changelog_changelog [EXTRACTED 1.00]
- **Provider Extensibility Plan** — docs_architecture_provider_seam, docs_architecture_type_mapping, docs_roadmap_interface_extraction, docs_roadmap_sql_to_sql_goal [EXTRACTED 1.00]

## Communities (23 total, 0 thin omitted)

### Community 0 - "Migration Engine Core"
Cohesion: 0.07
Nodes (39): Child, Migrator.Core, IReadOnlySet, NpgsqlBinaryImporter, NpgsqlTransaction, Parent, Schema, Source (+31 more)

### Community 1 - "PDF Report Generation"
Cohesion: 0.07
Nodes (36): Column, Migrator.App.Reporting, CultureInfo, DateTimeOffset, FontResolverInfo, IFontResolver, Layout, Lazy (+28 more)

### Community 2 - "Docs, CI & Safety Concepts"
Cohesion: 0.05
Nodes (51): .claude Configuration Directory, Interface Palette (Contrast-Mapped Teal Set), verify-ui Skill (Playwright UI Audit), Dependabot Configuration, CI Workflow (three-OS build), Release Please Workflow, Release Workflow (single-file binaries), Changelog (release-please generated) (+43 more)

### Community 3 - "Server Admin API Types"
Cohesion: 0.10
Nodes (20): Migrator.Core.Admin, GrantRequest, IReadOnlyList, AdminCapabilities, DatabaseDropPreview, DatabaseGrant, DatabaseSummary, OwnedObjects (+12 more)

### Community 4 - "SQL Server Admin"
Cohesion: 0.20
Nodes (6): AdminIdentifier, CancellationToken, IReadOnlyList, Task, SqlServerAdmin, Capabilities

### Community 5 - "Migration UI Frontend"
Cohesion: 0.12
Nodes (32): api(), appendLog(), checkOrmManaged(), clearForm(), fillServerSelects(), fillTargetSuggestions(), fold(), followJob() (+24 more)

### Community 6 - "Postgres Admin"
Cohesion: 0.26
Nodes (7): RoleAttributes, CancellationToken, IReadOnlyList, NpgsqlConnection, Task, PostgresAdmin, Capabilities

### Community 7 - "Admin Panel Frontend"
Cohesion: 0.16
Nodes (27): action(), admin, ADMIN_WORDS, adminApi(), cell(), count(), fillAdminServers(), fillRoleSelect() (+19 more)

### Community 8 - "Launch Settings Config"
Cohesion: 0.08
Nodes (25): ASPNETCORE_ENVIRONMENT, applicationUrl, commandName, dotnetRunMessages, environmentVariables, launchBrowser, applicationUrl, commandName (+17 more)

### Community 9 - "Batch Migration Runner"
Cohesion: 0.16
Nodes (11): Action, CancellationToken, IEnumerable, IProgress, Task, TimeSpan, BatchDatabase, BatchRunner (+3 more)

### Community 10 - "Connection Store"
Cohesion: 0.22
Nodes (12): IDataProtector, SemaphoreSlim, List, Task, ConnectionStore, StorePath, ServerKind, PostgreSql (+4 more)

### Community 11 - "Claude Permissions Settings"
Cohesion: 0.11
Nodes (17): permissions, allow, $schema, Bash(curl -s http://localhost:5099/*), Bash(dotnet build:*), Bash(dotnet format:*), Bash(dotnet publish:*), Bash(dotnet run --project src/Migrator.App:*) (+9 more)

### Community 12 - "Desktop Shell & Run Modes"
Cohesion: 0.18
Nodes (8): Migrator.App, Exception, AppMessageCode, DesktopShell, RunMode, Migrate, ProvisionOnly, VerifyOnly

### Community 13 - "User Provisioner"
Cohesion: 0.31
Nodes (6): CancellationToken, IProgress, NpgsqlConnection, Task, ProvisionedUser, UserProvisioner

### Community 14 - "Job Registry"
Cohesion: 0.16
Nodes (11): ConcurrentDictionary, Job, Done, Id, Report, ReportFileName, Succeeded, Summary (+3 more)

### Community 15 - "NuGet Packages & Solution"
Cohesion: 0.15
Nodes (10): Microsoft.AspNetCore.DataProtection (10.0.11), Microsoft.Data.SqlClient (7.0.2), Microsoft.Extensions.FileProviders.Embedded (8.0.11), Npgsql (10.0.3), PDFsharp (6.2.4), Photino.NET (4.0.16), Microsoft.NET.Sdk, Microsoft.NET.Sdk.Web (+2 more)

### Community 16 - "Admin Request DTOs"
Cohesion: 0.14
Nodes (13): CreateDatabaseRequest, CreateRoleRequest, List, DatabaseOwnerRequest, DropDatabaseRequest, DropRoleRequest, HistoryCheckRequest, MembershipRequest (+5 more)

### Community 17 - "Schema Mirror"
Cohesion: 0.24
Nodes (10): CancellationToken, Dictionary, IEnumerable, IProgress, IReadOnlyList, List, NpgsqlConnection, SqlConnection (+2 more)

### Community 18 - "Target Database Setup"
Cohesion: 0.25
Nodes (9): CancellationToken, IProgress, NpgsqlConnection, Task, TargetDatabase, TargetDatabaseState, AlreadyExisted, Created (+1 more)

### Community 19 - "verify-ui Package Config"
Cohesion: 0.18
Nodes (10): description, devDependencies, playwright, name, private, scripts, audit, postinstall (+2 more)

### Community 20 - "UI Audit Script"
Cohesion: 0.18
Nodes (6): args, REPO, SHOTS, URL, WIDTHS, WWWROOT

### Community 21 - "Migration Options"
Cohesion: 0.25
Nodes (8): MigrationOptions, AllowCollationMismatch, AllowSchemaRisk, AllowSourceOnlyTables, ExpectedIcuLocale, MigrateHistoryTables, MirrorMissingTables, VerifyOnly

### Community 22 - "Progress Kinds"
Cohesion: 0.33
Nodes (6): ProgressKind, Error, Info, Step, Success, Warning

## Knowledge Gaps
- **130 isolated node(s):** `$schema`, `Bash(dotnet build:*)`, `Bash(dotnet publish:*)`, `Bash(dotnet run --project src/Migrator.App:*)`, `Bash(dotnet format:*)` (+125 more)
  These have ≤1 connection - possible missing edges or undocumented components.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `Migrator.Core.Admin` connect `Server Admin API Types` to `Admin Request DTOs`?**
  _High betweenness centrality (0.109) - this node is a cross-community bridge._
- **Why does `Job` connect `Job Registry` to `Admin Request DTOs`, `Batch Migration Runner`?**
  _High betweenness centrality (0.100) - this node is a cross-community bridge._
- **Why does `ProgressMessage` connect `Batch Migration Runner` to `Migration Engine Core`, `User Provisioner`, `Job Registry`, `Schema Mirror`, `Target Database Setup`, `Progress Kinds`?**
  _High betweenness centrality (0.076) - this node is a cross-community bridge._
- **What connects `$schema`, `Bash(dotnet build:*)`, `Bash(dotnet publish:*)` to the rest of the system?**
  _130 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Migration Engine Core` be split into smaller, more focused modules?**
  _Cohesion score 0.07416267942583732 - nodes in this community are weakly interconnected._
- **Should `PDF Report Generation` be split into smaller, more focused modules?**
  _Cohesion score 0.06713286713286713 - nodes in this community are weakly interconnected._
- **Should `Docs, CI & Safety Concepts` be split into smaller, more focused modules?**
  _Cohesion score 0.050980392156862744 - nodes in this community are weakly interconnected._