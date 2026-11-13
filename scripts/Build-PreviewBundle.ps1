param()

# Create an isolated local review build. FFmpeg is installed separately by the user.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Initialize-DotNet.ps1"
$output = Join-Path $repoRoot ('.local/acceptance/stage3-bundle-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
Push-Location $repoRoot
try {
    Invoke-DotNet restore src/WaterMarker.App/WaterMarker.App.csproj --runtime win-x64 --disable-parallel '-m:1' '-nr:false'
    Invoke-DotNet publish src/WaterMarker.App/WaterMarker.App.csproj --configuration Release --runtime win-x64 --self-contained true --no-restore --output $output '-m:1' '-nr:false' '-p:UseSharedCompilation=false'
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
    if (Get-ChildItem -LiteralPath $output -Recurse -File | Where-Object { $_.Name -in @('ffmpeg.exe', 'ffprobe.exe') }) {
        throw 'Review bundle unexpectedly contains FFmpeg executables.'
    }
    Write-Output "Local review bundle: $output"
}
finally { Pop-Location }
