# ParquetViewer — Agent Guide

## Project structure

Solution at `src/ParquetViewer.sln` (5 projects):

| Project | Target | Role |
|---------|--------|------|
| `ParquetViewer` | `net10.0-windows` | WinForms app, entrypoint `Program.cs` → `MainForm` |
| `ParquetViewer.Engine` | `net10.0` | Interfaces: `IParquetEngine`, `IParquetMetadata`, schema types |
| `ParquetViewer.Engine.ParquetNET` | `net10.0` | Parquet.Net engine (always included) |
| `ParquetViewer.Engine.DuckDB` | `net10.0` | DuckDB engine (only in `Release_SelfContained` builds) |
| `ParquetViewer.Tests` | `net10.0-windows` | MSTest tests |

All NuGet versions pinned centrally in `src/Directory.Packages.props`.

## Build & test

```powershell
dotnet restore src/ParquetViewer.sln
dotnet build src/ParquetViewer.sln --configuration Debug
dotnet test src/ParquetViewer.sln --no-build --logger trx
```

- CI uses `dotnet-version: 8.0.x` (runner image may bundle newer SDK; adjust if build fails)
- `EnforceCodeStyleInBuild` = true — editorconfig violations are build errors
- Three configurations: `Debug`, `Release`, `Release_SelfContained`

## Code style (editorconfig — enforced at build)

- **No `var`** — explicit types required everywhere (`csharp_style_var_* = false`)
- Allman braces (`csharp_new_line_before_open_brace = all`)
- CRLF line endings, 4-space indent
- Interface prefix `I`, PascalCase for types/methods/properties/events
- No qualification with `this.` for fields/properties/methods/events
- Prefer primary constructors, pattern matching, switch expressions

## Engine architecture

`IParquetEngine` abstraction with two implementations:
- `ParquetNET` — used in Debug & Release; Parquet.Net library
- `DuckDB` — **only** referenced in `Release_SelfContained` (saves ~30 MB); DuckDB.NET.Data.Full

The `ParquetViewer` project conditionally includes DuckDB:
```xml
<ItemGroup Condition="'$(Configuration)' == 'Release_SelfContained'">
  <ProjectReference Include="..\ParquetViewer.Engine.DuckDB\..." />
</ItemGroup>
```

Tests test both engines (abstract `EngineTests` base → `ParquetNETEngineTests` + `DuckDBEngineTests`).

## Testing quirks (MSTest v4)

- **Framework:** MSTest (`Microsoft.NET.Test.Sdk`, `MSTest.TestAdapter`, `MSTest.TestFramework`)
- Custom `SkippableTestMethod` + `SkipWhenAttribute` for conditional test skipping per engine
- Methods run in parallel by default (`[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]`)
- Test fixtures: 31 `.parquet` files in `src/ParquetViewer.Tests/Data/` (copied to output dir on build)
- `RichardSzalay.MockHttp` for HTTP mocking (analytics tests)
- Test report: `src/ParquetViewer.Tests/TestResults/*.trx`

## Localization

- Resources in `src/ParquetViewer/Resources/` (`Strings.resx`, `Errors.resx`, `Icons.resx`)
- Turkish translations via `.tr.resx` suffix files
- Generated designers checked in; edit the `.resx` XML, regenerate with ResXFileCodeGenerator

## Settings & analytics

- Settings stored in Windows Registry: `HKCU\ParquetViewer`
- Analytics via Amplitude (opt-in, prompted on 2nd+ launch 24h apart)
- API key injected at CI build time via `AMPLITUDE_API_KEY` secret into `Analytics/AmplitudeEvent.cs`

## Notable flows in Program.cs

- **File association mode:** first arg `AboutBox.PERFORM_FILE_ASSOCIATION` → register .parquet handler
- **Dark mode:** automatically offered after 30/300 opens if system is in dark mode
- **First-arg as path:** if `args[0]` is a valid file/directory, `MainForm(pathToOpen)` opens it
