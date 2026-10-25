# Third-party notices

ClipNest's own code and its icons (`src/ClipNest.App/Assets/*.ico`, drawn by
`tools/make_icons.py`) are original work under the MIT license in `LICENSE`.

## Shipped with the app

| Component | Version | License | Notes |
|---|---|---|---|
| Microsoft.Data.Sqlite | 10.0.13 | MIT | ADO.NET provider for SQLite, by Microsoft (.NET Foundation). https://github.com/dotnet/efcore |
| SQLitePCLRaw (bundle_e_sqlite3, core, provider) | 2.1.x (pulled in by Microsoft.Data.Sqlite) | Apache-2.0 | By SourceGear / Eric Sink. https://github.com/ericsink/SQLitePCL.raw |
| SQLite (e_sqlite3 native library) | bundled by SQLitePCLRaw | Public domain | https://sqlite.org/copyright.html |
| .NET runtime and Windows Desktop runtime (only in self-contained builds) | 10.0.x | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf |

## Used only for tests

| Component | Version | License |
|---|---|---|
| xunit | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.5 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 18.0.1 | MIT |

## Referenced, not redistributed

These fonts ship with Windows 11. ClipNest only refers to them by name and
does not include any font files:

- Segoe UI Variable / Segoe UI (interface text)
- Segoe Fluent Icons / Segoe MDL2 Assets (interface icons)
- Cascadia Mono / Consolas (code preview)

## Used only to create the icons

- Pillow (HPND license) runs `tools/make_icons.py`. It is not part of the app.
