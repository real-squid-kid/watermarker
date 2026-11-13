# Third-party notices

WaterMarker's own code is licensed under MIT; see [LICENSE](LICENSE). The components
below retain their respective licenses. This inventory describes the current dependency
graph and the separately installed FFmpeg development build; review it again for each release.

## Application dependencies

| Component | Version | Copyright / author | License | Source |
| --- | --- | --- | --- | --- |
| Microsoft.Data.Sqlite and Microsoft.Data.Sqlite.Core | 10.0.14 | Microsoft Corporation; .NET Foundation and contributors | [MIT](licenses/DotNet-MIT.txt) | [dotnet/efcore](https://github.com/dotnet/efcore) |
| SQLitePCLRaw.core, bundle_e_sqlite3, provider.e_sqlite3 and lib.e_sqlite3 | 2.1.13 | Copyright 2014–2024 SourceGear, LLC; Eric Sink | [Apache-2.0](licenses/Apache-2.0.txt) | [SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw) |
| SQLite | Included in SQLitePCLRaw native library | SQLite authors | Public domain | [SQLite copyright](https://www.sqlite.org/copyright.html) |
| .NET and WPF | .NET 10 | .NET Foundation and contributors | [MIT](licenses/DotNet-MIT.txt), with separate notices for additional components | [.NET license information](https://github.com/dotnet/core/blob/main/license-information.md) |

If a self-contained distribution includes the .NET runtime, include the notices and
licenses for that exact runtime distribution as well.

The interface requests the Windows system fonts Segoe UI, Consolas and Georgia.
WaterMarker does not bundle these font files; their use depends on the Windows installation.

## FFmpeg and x264

WaterMarker invokes [FFmpeg and FFprobe](https://ffmpeg.org/), including the libx264
encoder, as separate executables. WaterMarker does not include these executables in its
distribution. When they are unavailable, the application can offer to install the
separate `Gyan.FFmpeg` package via Windows Package Manager; alternatively, the user
can install a compatible build independently. The development build observed here is
`8.0.1-full_build-www.gyan.dev`, built with GPL, version 3, static linking and libx264;
its `ffmpeg -L` identifies GPL version 3 or later. The license text provided with that
build is [GPL-3.0](licenses/FFmpeg-GPL-3.0.txt). [x264](https://www.videolan.org/developers/x264.html)
is a separate component; FFmpeg documents its GPL requirements in its
[configuration documentation](https://ffmpeg.org/general.html#x264).

For the locally inspected build, the supplier identifies FFmpeg source at
<https://github.com/FFmpeg/FFmpeg/commit/894da5ca7d>. SHA-256 values are
`74DB6C184A03DBA2BDFE23E1A1F41CF5A8385BC1DE6A7A1B26DB1DC541ABEF93` for
`ffmpeg.exe` and `55BB6C6289367AE2383EFA86B26BF2596F8ADB72AC747360EB13DF162354161C`
for `ffprobe.exe`. The supplier's [build information](licenses/FFmpeg-8.0.1-build-info.txt)
lists the build configuration and many external library versions. These hashes describe the inspected development executables, not
an approved release package; the supplier's source link alone does not identify the
corresponding source of every statically linked third-party library.

The development build contains many other components. If a future release includes
FFmpeg binaries, revisit their sources, licenses and distribution obligations. Consult
[FFmpeg's licensing guidance](https://ffmpeg.org/legal.html).

## Development and test dependencies

The test projects additionally use Microsoft test platform and coverage packages
17.14.1, Microsoft Testing Platform packages 1.8.2, Microsoft.Bcl.AsyncInterfaces 6.0.0,
Microsoft.Win32.Registry 5.0.0 (MIT), Newtonsoft.Json 13.0.3 (MIT, James Newton-King),
and xUnit v3 3.0.1, xunit.runner.visualstudio 3.1.4 and xunit.analyzers 1.24.0
(Apache-2.0, .NET Foundation). These are development dependencies and are not assumed
to be part of the application distribution.
