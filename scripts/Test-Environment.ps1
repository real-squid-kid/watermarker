param([switch]$RequireVideo)

. "$PSScriptRoot/Initialize-DotNet.ps1"
Push-Location $repoRoot
try {
    if (-not [Environment]::Is64BitOperatingSystem -or $env:OS -ne 'Windows_NT') {
        throw 'WaterMarker requires Windows x64.'
    }
    Invoke-DotNet --version
    & git --version
    if ($LASTEXITCODE -ne 0) { throw 'Git is required.' }

    $videoAvailable = $true
    foreach ($tool in @('ffmpeg', 'ffprobe')) {
        $command = Get-Command $tool -CommandType Application -ErrorAction SilentlyContinue
        if ($null -eq $command) {
            $videoAvailable = $false
            Write-Warning "$tool is missing from PATH."
            continue
        }
        $version = & $command.Source -version 2>&1
        if ($LASTEXITCODE -ne 0) { throw "$tool could not start." }
        Write-Output ($version | Select-Object -First 1)
    }
    if ($videoAvailable) {
        $encoders = & ffmpeg -hide_banner -encoders 2>&1
        if ($LASTEXITCODE -ne 0) { throw 'Could not query FFmpeg encoders.' }
        $videoAvailable = [bool]($encoders | Select-String '\blibx264\s')
    }
    if (-not $videoAvailable) {
        if ($RequireVideo) { throw 'Video prerequisites are missing (FFmpeg, FFprobe, libx264).' }
        Write-Warning 'Video prerequisites are unavailable; image development remains available.'
    }
    Write-Output 'Environment check passed.'
}
finally {
    Pop-Location
}
