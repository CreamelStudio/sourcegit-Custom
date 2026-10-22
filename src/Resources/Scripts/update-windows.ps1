$ErrorActionPreference = 'Stop'

function Install-SourceGitUpdate([string] $SourceDirectory, [string] $TargetDirectory, [scriptblock] $AfterInstall) {
    $target = [System.IO.Path]::GetFullPath($TargetDirectory)
    if ((Get-Item -LiteralPath $target).Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        throw 'The application directory must not be a symbolic link.'
    }

    $files = @(Get-ChildItem -LiteralPath $SourceDirectory -File | Sort-Object Name)
    if (-not ($files.Name -contains 'SourceGit.exe')) {
        throw 'SourceGit.exe is missing from the update.'
    }

    $backup = Join-Path $target ('.sourcegit-update-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $backup | Out-Null
    $changed = [System.Collections.Generic.List[object]]::new()
    $keepBackup = $false
    try {
        foreach ($file in $files) {
            $destination = Join-Path $target $file.Name
            if ($file.Name -eq 'data' -or ($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
                throw 'The update contains an unsupported application file.'
            }
            if ((Test-Path -LiteralPath $destination) -and ((Get-Item -LiteralPath $destination).Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
                throw 'An application file is a symbolic link.'
            }

            $replacement = Join-Path $backup ($file.Name + '.new')
            $original = Join-Path $backup ($file.Name + '.old')
            Copy-Item -LiteralPath $file.FullName -Destination $replacement
            $existed = Test-Path -LiteralPath $destination -PathType Leaf
            if ($existed) {
                [System.IO.File]::Replace($replacement, $destination, $original)
            } else {
                [System.IO.File]::Move($replacement, $destination)
            }
            $changed.Add([pscustomobject]@{ Destination = $destination; Original = $original; Existed = $existed })
        }
        if ($null -ne $AfterInstall) {
            & $AfterInstall
        }
    } catch {
        $failure = $_
        for ($index = $changed.Count - 1; $index -ge 0; $index--) {
            $file = $changed[$index]
            try {
                if ($file.Existed) {
                    [System.IO.File]::Replace($file.Original, $file.Destination, ($file.Original + '.failed'))
                } else {
                    Remove-Item -LiteralPath $file.Destination -Force
                }
            } catch {
                $keepBackup = $true
            }
        }
        if ($keepBackup) {
            throw "Update failed. Some files could not be restored. Backup: $backup. $failure"
        }
        throw $failure
    } finally {
        if (-not $keepBackup) {
            Get-ChildItem -LiteralPath $backup -File | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue }
            Remove-Item -LiteralPath $backup -ErrorAction SilentlyContinue
        }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    $updateRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
    $target = [System.IO.Path]::GetFullPath($env:SOURCEGIT_UPDATE_TARGET)
    $executable = Join-Path $target 'SourceGit.exe'
    try {
        $application = Get-Process -Id ([int] $env:SOURCEGIT_UPDATE_PID) -ErrorAction SilentlyContinue
        if ($null -ne $application) {
            $application | Wait-Process -Timeout 120
        }
        $otherInstances = @(Get-Process -Name SourceGit -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable })
        if ($otherInstances.Count -gt 0) {
            throw 'Close other SourceGit windows before installing the update.'
        }

        Install-SourceGitUpdate (Join-Path $updateRoot 'payload\SourceGit') $target {
            Start-Process -FilePath $executable -WorkingDirectory $target
        }

        # Delete only this updater's known, flat temporary files.
        try {
            Get-ChildItem -LiteralPath (Join-Path $updateRoot 'payload\SourceGit') -File | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
            Remove-Item -LiteralPath (Join-Path $updateRoot 'payload\SourceGit')
            Remove-Item -LiteralPath (Join-Path $updateRoot 'payload')
            Remove-Item -LiteralPath (Join-Path $updateRoot 'update.zip')
            Remove-Item -LiteralPath $PSCommandPath
            Remove-Item -LiteralPath $updateRoot
        } catch {
            # Leftover temporary files do not invalidate an installed update.
        }
    } catch {
        $message = "SourceGit update failed: $($_.Exception.Message)`nDetails: $updateRoot\update-error.log"
        Set-Content -LiteralPath (Join-Path $updateRoot 'update-error.log') -Value ($_ | Out-String) -Encoding UTF8
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show($message, 'SourceGit Update') | Out-Null
        if (Test-Path -LiteralPath $executable -PathType Leaf) {
            Start-Process -FilePath $executable -WorkingDirectory $target
        }
    }
}
