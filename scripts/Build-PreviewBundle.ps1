param([string]$FfmpegDirectory = $env:WATERMARKER_FFMPEG_DIRECTORY)

# This is a local review bundle, not a release: FFmpeg's complete corresponding
# sources and the licenses of its statically linked libraries still need an audit.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Initialize-DotNet.ps1"

function Resolve-MediaTool([string]$name) {
    $path = if ($FfmpegDirectory) { Join-Path $FfmpegDirectory "$name.exe" } else { (Get-Command $name -CommandType Application).Source }
    $item = Get-Item -LiteralPath $path
    while ($item.LinkType -eq 'SymbolicLink') {
        $target = $item.Target
        if (-not [IO.Path]::IsPathRooted($target)) { $target = Join-Path $item.DirectoryName $target }
        $item = Get-Item -LiteralPath $target
    }
    return $item.FullName
}

$ffmpeg = Resolve-MediaTool 'ffmpeg'
$ffprobe = Resolve-MediaTool 'ffprobe'
$knownHashes = @{
    $ffmpeg = '74DB6C184A03DBA2BDFE23E1A1F41CF5A8385BC1DE6A7A1B26DB1DC541ABEF93'
    $ffprobe = '55BB6C6289367AE2383EFA86B26BF2596F8ADB72AC747360EB13DF162354161C'
}
foreach ($tool in @($ffmpeg, $ffprobe)) {
    if ((Get-FileHash -LiteralPath $tool -Algorithm SHA256).Hash -ne $knownHashes[$tool]) {
        throw 'FFmpeg differs from the inspected build. Update the provenance, licenses and hashes before creating a preview bundle.'
    }
}

$output = Join-Path $repoRoot ('.local/acceptance/stage3-bundle-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
Push-Location $repoRoot
try {
    Invoke-DotNet restore src/WaterMarker.App/WaterMarker.App.csproj --runtime win-x64 --disable-parallel '-m:1' '-nr:false'
    Invoke-DotNet publish src/WaterMarker.App/WaterMarker.App.csproj --configuration Release --runtime win-x64 --self-contained true --no-restore --output $output '-m:1' '-nr:false' '-p:UseSharedCompilation=false'
    $toolDirectory = Join-Path $output 'ffmpeg'
    New-Item -ItemType Directory -Path $toolDirectory | Out-Null
    Copy-Item -LiteralPath $ffmpeg -Destination (Join-Path $toolDirectory 'ffmpeg.exe')
    Copy-Item -LiteralPath $ffprobe -Destination (Join-Path $toolDirectory 'ffprobe.exe')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $output 'README.md')
    $docsDirectory = Join-Path $output 'docs'
    New-Item -ItemType Directory -Path $docsDirectory | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/images') -Destination $docsDirectory -Recurse
    $runtimeLicenses = Join-Path $output 'licenses/runtime'
    New-Item -ItemType Directory -Path $runtimeLicenses | Out-Null
    $assets = Get-Content -Raw -Encoding UTF8 'src/WaterMarker.App/obj/project.assets.json' | ConvertFrom-Json
    $dependencies = $assets.project.frameworks.'net10.0-windows'.downloadDependencies
    foreach ($dependency in $dependencies | Where-Object { $_.name -in @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64') }) {
        $version = ([regex]::Match($dependency.version, '[0-9]+\.[0-9]+\.[0-9]+')).Value
        $package = Join-Path $env:NUGET_PACKAGES ($dependency.name.ToLowerInvariant() + '/' + $version)
        foreach ($file in @('LICENSE', 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
            $source = Join-Path $package $file
            if (Test-Path -LiteralPath $source -PathType Leaf) {
                Copy-Item -LiteralPath $source -Destination (Join-Path $runtimeLicenses ($dependency.name + '-' + $file))
            }
        }
    }
    @'
LOCAL REVIEW BUILD — NOT FOR DISTRIBUTION.
The exact corresponding sources and notices for all statically linked FFmpeg
components, the combined distribution terms and H.264 patent questions remain
to be reviewed. See THIRD-PARTY-NOTICES.md and the development plan in the source tree.
'@ | Set-Content -LiteralPath (Join-Path $output 'DRAFT-NOT-FOR-DISTRIBUTION.txt') -Encoding UTF8
    Write-Output "Local review bundle: $output"
}
finally { Pop-Location }
