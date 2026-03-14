# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

MS2Tools is a set of .NET 8.0 CLI tools for extracting and creating MapleStory 2 game archives (.m2h/.m2d file pairs). Originally by Miyuyami, now maintained as a fork.

## Build & Test Commands

```bash
# Build entire solution
dotnet build MS2Tools.sln

# Build in release mode
dotnet build MS2Tools.sln -c Release

# Run tests (test project is inside the MS2Lib submodule)
dotnet test MS2Lib/MS2Lib.Tests/MS2Lib.Tests.csproj

# Run a specific tool
dotnet run --project MS2Extract -- <source> <destination> [syncMode] [logMode]
dotnet run --project MS2Create -- <source> <destination> <archiveName> <mode> [syncMode] [logMode]
```

## Architecture

### Solution Structure

- **MS2Extract** — CLI tool that extracts .m2h/.m2d archives to disk. Supports batch extraction of entire directories.
- **MS2Create** — CLI tool that packages directories into .m2h/.m2d archives. Auto-detects compression type by file extension (.png, .usm, .zlib).
- **MS2FileHeaderExporter** — Debug utility for dumping archive metadata (file type maps, root folder ID mappings).
- **MS2Lib** — Core library (git submodule from Miyuyami/MS2Lib). Contains all archive parsing, crypto, and compression logic.
- **MiscUtils** — Utility library (nested submodule inside MS2Lib). Provides extensions, endian-aware I/O, and logging.

### Archive Format

Archives consist of paired files: `.m2h` (header with encrypted metadata) and `.m2d` (encrypted/compressed file data). The header begins with a 4-byte crypto mode identifier.

### Crypto System

Four encryption modes exist: **MS2F**, **NS2F**, **OS2F**, **PS2F** (identified by 32-bit magic numbers in `MS2CryptoMode` enum). The crypto layer uses a repository pattern:

- `IMS2ArchiveCryptoRepository` — interface for crypto operations per mode
- `CryptoRepositoryMS2F` / `CryptoRepositoryNS2F` — concrete implementations
- `Repositories.cs` — static registry mapping `MS2CryptoMode` → repository instance
- Separate crypto classes handle archive headers vs file headers vs file info encryption

Dependencies: DotNetZip (compression), BouncyCastle (cryptography).

### Key Types

- `MS2Archive` — main container; implements `IMS2Archive` (Load, Save, SaveConcurrently, Add, Remove)
- `MS2File` — individual file within an archive; async stream access via `GetStreamAsync`
- `MS2FileHeader` / `MS2FileInfo` / `MS2SizeHeader` — metadata value types

### Build Output

Debug and Release builds output to `../Debug/net8.0/` and `../Release/net8.0/` respectively (relative to each project directory, so they land at repo root level).

## Code Conventions

- Private fields: `_camelCase` prefix
- 4-space indentation, CRLF line endings, UTF-8 BOM (see `.editorconfig`)
- Interface-first design: all core types have `IMS2*` interfaces
- Async/await with `Task`-based patterns; concurrent operations use `ConcurrentDictionary`
