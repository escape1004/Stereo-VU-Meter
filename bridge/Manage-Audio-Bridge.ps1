param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$installDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Escape\XeneonStereoVU'
$bridge = Join-Path $installDir 'AudioBridge.exe'
$startup = [Environment]::GetFolderPath('Startup')
$shortcutPath = Join-Path $startup 'XENEON EDGE Stereo VU Audio Bridge.lnk'

function Stop-InstalledBridge {
    Get-Process -Name AudioBridge -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $bridge } |
        ForEach-Object {
            if (-not $_.HasExited) {
                Stop-Process -InputObject $_ -ErrorAction Stop
                if (-not $_.WaitForExit(5000)) { throw 'The installed bridge did not exit.' }
            }
        }
}

try {
    if ($Uninstall) {
        if (Test-Path -LiteralPath $shortcutPath) {
            Remove-Item -LiteralPath $shortcutPath
        }
        Stop-InstalledBridge
        if (Test-Path -LiteralPath $bridge) { Remove-Item -LiteralPath $bridge }
        Write-Host 'Audio bridge and automatic startup removed.'
        exit 0
    }

    $compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $compiler)) {
        $compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
    }
    if (-not (Test-Path -LiteralPath $compiler)) {
        throw 'The Windows .NET Framework C# compiler was not found.'
    }

    $null = New-Item -ItemType Directory -Path $installDir -Force
    $null = New-Item -ItemType Directory -Path $startup -Force
    $stage = Join-Path $installDir ('AudioBridge-' + [Guid]::NewGuid().ToString('N') + '.exe')
    $source = Join-Path $PSScriptRoot 'AudioBridge.cs'
    $backup = $stage + '.bak'
    $installed = $false
    $started = $false
    try {
        & $compiler /nologo /optimize+ /target:winexe "/out:$stage" $source
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $stage)) {
            throw 'Audio bridge compilation failed.'
        }

        Stop-InstalledBridge
        if (Test-Path -LiteralPath $bridge) {
            [IO.File]::Replace($stage, $bridge, $backup)
        } else {
            [IO.File]::Move($stage, $bridge)
        }
        $installed = $true

        $process = Start-Process -FilePath $bridge -WorkingDirectory $installDir -PassThru
        $started = $true
        if ($process.WaitForExit(1000)) {
            throw 'The bridge exited. Close any old bridge console or app using port 18742, then retry.'
        }

        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = $bridge
        $shortcut.WorkingDirectory = $installDir
        $shortcut.Description = 'Starts the Stereo VU Meter bridge in standby at Windows sign-in.'
        $shortcut.Save()
    } catch {
        if ($started) { Stop-InstalledBridge }
        if ($installed) {
            if (Test-Path -LiteralPath $backup) {
                [IO.File]::Copy($backup, $bridge, $true)
                Remove-Item -LiteralPath $backup
                $null = Start-Process -FilePath $bridge -WorkingDirectory $installDir
            } else {
                Remove-Item -LiteralPath $bridge
            }
        }
        throw
    } finally {
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage }
    }
    if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup }

    Write-Host 'Audio bridge installed and running in standby.'
    Write-Host 'It will start automatically when this Windows user signs in.'
    Write-Host 'Import Stereo-VU-Meter.icuewidget in iCUE and tap START.'
    exit 0
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
