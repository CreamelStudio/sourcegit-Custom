$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../../src/Resources/Scripts/update-windows.ps1"

function Assert-UpdateCheck([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

$root = Join-Path ([System.IO.Path]::GetTempPath()) ("SourceGit update test ' [literal] " + [guid]::NewGuid().ToString('N'))
$source = Join-Path $root 'source'
$target = Join-Path $root 'target'
New-Item -ItemType Directory -Path $source, $target | Out-Null
try {
    New-Item -ItemType Directory -Path (Join-Path $target 'data') | Out-Null
    Set-Content -LiteralPath (Join-Path $target 'data/settings.json') -Value 'user settings'
    Set-Content -LiteralPath (Join-Path $target 'unrelated.txt') -Value 'user file'
    Set-Content -LiteralPath (Join-Path $target 'SourceGit.exe') -Value 'old executable'
    Set-Content -LiteralPath (Join-Path $source 'SourceGit.exe') -Value 'new executable'
    Set-Content -LiteralPath (Join-Path $source 'library.dll') -Value 'new library'

    Install-SourceGitUpdate $source $target
    Assert-UpdateCheck ((Get-Content -LiteralPath (Join-Path $target 'SourceGit.exe')) -eq 'new executable') 'The executable was not updated.'
    Assert-UpdateCheck ((Get-Content -LiteralPath (Join-Path $target 'data/settings.json')) -eq 'user settings') 'Portable settings changed.'
    Assert-UpdateCheck ((Get-Content -LiteralPath (Join-Path $target 'unrelated.txt')) -eq 'user file') 'An unrelated file changed.'

    Set-Content -LiteralPath (Join-Path $source 'a-new.dll') -Value 'new file'
    Set-Content -LiteralPath (Join-Path $source 'library.dll') -Value 'replacement library'
    Set-Content -LiteralPath (Join-Path $source 'SourceGit.exe') -Value 'another executable'
    $lock = [System.IO.File]::Open((Join-Path $target 'SourceGit.exe'), 'Open', 'Read', 'Read')
    $failed = $false
    try {
        Install-SourceGitUpdate $source $target
    } catch {
        $failed = $true
    } finally {
        $lock.Dispose()
    }
    Assert-UpdateCheck $failed 'A locked executable must abort installation.'
    Assert-UpdateCheck ((Get-Content -LiteralPath (Join-Path $target 'SourceGit.exe')) -eq 'new executable') 'The executable changed after a failed update.'
    Assert-UpdateCheck ((Get-Content -LiteralPath (Join-Path $target 'library.dll')) -eq 'new library') 'The previous library was not restored.'
    Assert-UpdateCheck (-not (Test-Path -LiteralPath (Join-Path $target 'a-new.dll'))) 'A new file was not removed during rollback.'

    $failed = $false
    try {
        Install-SourceGitUpdate $source $target { throw 'Restart failed' }
    } catch {
        $failed = $true
    }
    Assert-UpdateCheck $failed 'A restart failure must abort installation.'
    Assert-UpdateCheck ((Get-Content -LiteralPath (Join-Path $target 'SourceGit.exe')) -eq 'new executable') 'A restart failure did not restore the executable.'
    Assert-UpdateCheck ((Get-Content -LiteralPath (Join-Path $target 'library.dll')) -eq 'new library') 'A restart failure did not restore the library.'
    Assert-UpdateCheck (@(Get-ChildItem -LiteralPath $target -Directory -Filter '.sourcegit-update-*').Count -eq 0) 'Temporary backup files remain.'
    Write-Output 'Windows update checks passed: replacement, portable data preservation, locked-file and restart rollback, and literal paths.'
} finally {
    $resolvedRoot = [System.IO.Path]::GetFullPath($root)
    if (-not $resolvedRoot.StartsWith([System.IO.Path]::GetTempPath(), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to clean a directory outside the test temp folder.'
    }
    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}
