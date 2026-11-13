param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?$')]
    [string]$Version
)

# Builds from the current working tree. Commit/tag selection remains the owner's job.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Initialize-DotNet.ps1"
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$name = "WaterMarker-$Version-win-x64"
$root = Join-Path $repoRoot ('artifacts/releases/' + $name + '-' + [guid]::NewGuid().ToString('N'))
$output = Join-Path $root 'app'
$archive = Join-Path $root ($name + '.zip')
New-Item -ItemType Directory -Path $output -Force | Out-Null

Push-Location $repoRoot
try {
    Invoke-DotNet restore src/WaterMarker.App/WaterMarker.App.csproj --runtime win-x64 --disable-parallel '-m:1' '-nr:false'
    Invoke-DotNet publish src/WaterMarker.App/WaterMarker.App.csproj --configuration Release --runtime win-x64 --self-contained true --no-restore --output $output '-m:1' '-nr:false' '-p:UseSharedCompilation=false' '-p:DebugType=None' '-p:DebugSymbols=false' '-p:IncludeSourceRevisionInInformationalVersion=false' "-p:Version=$Version"
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $output 'README.md')
    New-Item -ItemType Directory -Path (Join-Path $output 'docs') | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/images') -Destination (Join-Path $output 'docs') -Recurse

    $runtimeLicenses = Join-Path $output 'licenses/runtime'
    New-Item -ItemType Directory -Path $runtimeLicenses | Out-Null
    $assets = Get-Content -Raw -Encoding UTF8 'src/WaterMarker.App/obj/project.assets.json' | ConvertFrom-Json
    $dependencies = @($assets.project.frameworks.'net10.0-windows'.downloadDependencies)
    foreach ($runtime in @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64')) {
        $dependency = @($dependencies | Where-Object { $_.name -eq $runtime })
        if ($dependency.Count -ne 1) { throw "Cannot identify the exact $runtime package." }
        $runtimeVersion = ([regex]::Match($dependency[0].version, '[0-9]+\.[0-9]+\.[0-9]+')).Value
        if (-not $runtimeVersion) { throw "Cannot identify the version of $runtime." }
        $package = Join-Path $env:NUGET_PACKAGES ($runtime.ToLowerInvariant() + '/' + $runtimeVersion)
        $licenseCopied = $false
        foreach ($file in @('LICENSE', 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
            $source = Join-Path $package $file
            if (Test-Path -LiteralPath $source -PathType Leaf) {
                Copy-Item -LiteralPath $source -Destination (Join-Path $runtimeLicenses ($runtime + '-' + $file))
                if ($file -like 'LICENSE*') { $licenseCopied = $true }
            }
        }
        if (-not $licenseCopied) { throw "No license found for $runtime $runtimeVersion." }
    }
    $coreNotices = Join-Path $runtimeLicenses 'Microsoft.NETCore.App.Runtime.win-x64-THIRD-PARTY-NOTICES.TXT'
    if (-not (Test-Path -LiteralPath $coreNotices -PathType Leaf)) { throw 'The .NET runtime third-party notices are missing.' }

    $required = @('WaterMarker.App.exe', 'hostfxr.dll', 'coreclr.dll', 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md',
        'docs/images/watermarker-main.png', 'docs/images/watermarker-journal.png', 'docs/images/watermarker-video-progress.png')
    foreach ($file in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $output $file) -PathType Leaf)) { throw "Release file missing: $file" }
    }
    $files = @(Get-ChildItem -LiteralPath $output -Recurse -File)
    $forbidden = @($files | Where-Object {
        $_.Name -in @('ffmpeg.exe', 'ffprobe.exe', 'WaterMarker_TZ.md', 'DEVELOPMENT.md', 'AGENTS.md') -or
        $_.Name -match '(?i)\.(db|sqlite|sqlite3)(-.+)?$' -or $_.Extension -eq '.pdb' -or
        $_.FullName.Substring($output.Length) -match '(?i)[\\/](\.local|\.git|test-data|private)[\\/]'
    })
    if ($forbidden.Count) { throw "Release contains forbidden files: $($forbidden.FullName -join ', ')" }
    $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $files) {
            $relative = $file.FullName.Substring($output.Length).TrimStart('\', '/').Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $zip.Dispose() }
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name.zip" | Set-Content -LiteralPath (Join-Path $root 'SHA256SUMS.txt') -Encoding ASCII
    Write-Output "Release archive: $archive"
    Write-Output "SHA-256: $hash"
}
finally { Pop-Location }
