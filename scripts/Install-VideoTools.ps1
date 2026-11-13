# Interactive installation during development setup; WinGet presents any required terms.
$ErrorActionPreference = 'Stop'
if (-not (Get-Command winget -CommandType Application -ErrorAction SilentlyContinue)) {
    throw 'Windows Package Manager (App Installer) is required. Alternatively install FFmpeg and FFprobe with libx264 manually.'
}
& winget install --id Gyan.FFmpeg --exact --source winget --scope user
if ($LASTEXITCODE -ne 0) { throw "FFmpeg installation did not complete (WinGet exit code $LASTEXITCODE)." }
Write-Output 'FFmpeg is installed separately. Restart your terminal if its PATH has not refreshed.'
