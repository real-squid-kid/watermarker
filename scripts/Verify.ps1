param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

. "$PSScriptRoot/Initialize-DotNet.ps1"
Push-Location $repoRoot
try {
    # A single MSBuild node also works in restricted Windows development sessions.
    Invoke-DotNet restore WaterMarker.slnx --disable-parallel '-m:1' '-nr:false'
    Invoke-DotNet build WaterMarker.slnx --configuration $Configuration --no-restore '-m:1' '-nr:false' '-p:UseSharedCompilation=false'
    Invoke-DotNet format WaterMarker.slnx --verify-no-changes --no-restore

    $testProjects = @(Get-ChildItem -Path (Join-Path $repoRoot 'tests') -Filter '*Tests.csproj' -Recurse)
    if ($testProjects.Count -eq 0) {
        Write-Output 'No test projects yet; only build and formatting were verified.'
    }
    foreach ($project in $testProjects) {
        Invoke-DotNet test $project.FullName --configuration $Configuration --no-build --no-restore --logger trx --results-directory artifacts/test-results
    }
}
finally {
    Pop-Location
}
