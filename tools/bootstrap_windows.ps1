$ErrorActionPreference = 'Stop'
# Force UTF-8 end-to-end so Russian compiler messages are readable on Windows PowerShell 5.1.
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[Console]::InputEncoding = $Utf8NoBom
[Console]::OutputEncoding = $Utf8NoBom
$OutputEncoding = $Utf8NoBom
Set-StrictMode -Version Latest

$Root = Split-Path -Parent $PSScriptRoot
$VersionFile = Join-Path $Root 'VERSION.txt'
if (-not (Test-Path -LiteralPath $VersionFile -PathType Leaf)) { throw "VERSION.txt is missing: $VersionFile" }
$AppVersion = [System.IO.File]::ReadAllText($VersionFile).Trim()
if ($AppVersion -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') { throw "VERSION.txt contains an invalid version: '$AppVersion'" }
$OutputRoot = Join-Path $Root 'BUILD_OUTPUT'
$ToolsRoot = Join-Path $Root '.tools'
$DotnetDir = Join-Path $ToolsRoot 'dotnet'
$DotnetExe = Join-Path $DotnetDir 'dotnet.exe'
$InstallScript = Join-Path $ToolsRoot 'dotnet-install.ps1'
$SetupLog = Join-Path $OutputRoot 'setup.log'
$BuildLog = Join-Path $OutputRoot 'build.log'
$LastError = Join-Path $OutputRoot 'LAST_ERROR.txt'
$PublishDir = Join-Path $OutputRoot ("PhotoArchiveManager_${AppVersion}_win-x64")
$ZipPath = Join-Path $OutputRoot ("PhotoArchiveManager_${AppVersion}_win-x64.zip")
$Project = Join-Path $Root 'src\PhotoArchiveManager\PhotoArchiveManager.csproj'
if (-not (Test-Path -LiteralPath $Project -PathType Leaf)) { throw "Project file is missing: $Project" }
[xml]$ProjectXml = [System.IO.File]::ReadAllText($Project)
$ProjectVersionNode = $ProjectXml.SelectSingleNode('/Project/PropertyGroup/Version')
if ($null -eq $ProjectVersionNode) { throw 'PhotoArchiveManager.csproj does not define <Version>.' }
$ProjectVersion = [string]$ProjectVersionNode.InnerText
if (-not [string]::Equals($ProjectVersion.Trim(), $AppVersion, [System.StringComparison]::Ordinal)) {
    throw "Version mismatch: VERSION.txt=$AppVersion but PhotoArchiveManager.csproj=$ProjectVersion. Refusing to build a mislabeled release."
}
$AssetsDir = Join-Path $Root 'src\PhotoArchiveManager\Assets'
$SFaceModel = Join-Path $AssetsDir 'face_recognition_sface_2021dec.onnx'
$SFaceSha256 = '0ba9fbfa01b5270c96627c4ef784da859931e02f04419c829e83484087c34e79'
$YuNetModel = Join-Path $AssetsDir 'face_detection_yunet_2023mar.onnx'
$YuNetSha256 = '8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4'
$PortableDataBackupPrefix = '_PAM_PORTABLE_DATA_BACKUP'
$PortableDataBackupMarkerName = '_PAM_BACKUP_COMPLETE.json'
$PortableDataBackupMarkerVersion = 2
$PortableDataRestoreSource = $null
$TargetDataDir = Join-Path $PublishDir 'Data'

New-Item -ItemType Directory -Force -Path $OutputRoot, $ToolsRoot | Out-Null
Remove-Item $LastError -Force -ErrorAction SilentlyContinue
"Build started: $(Get-Date -Format o)" | Set-Content -Encoding UTF8 $SetupLog
"Build started: $(Get-Date -Format o)" | Set-Content -Encoding UTF8 $BuildLog
"Version: $AppVersion" | Add-Content -Encoding UTF8 $SetupLog
"Version: $AppVersion" | Add-Content -Encoding UTF8 $BuildLog

function Write-Setup([string]$Message) {
    $Message | Tee-Object -FilePath $SetupLog -Append
}

function Fail([string]$Message, [System.Exception]$Exception = $null) {
    $text = $Message
    if ($Exception) { $text += "`r`n`r`n" + $Exception.ToString() }
    $text | Set-Content -Encoding UTF8 $LastError
    Write-Host "`nERROR: $Message" -ForegroundColor Red
    exit 1
}

function Get-PortableDataState([string]$DataPath) {
    $dbPath = Join-Path $DataPath 'archive.db'
    if (-not (Test-Path -LiteralPath $dbPath -PathType Leaf)) {
        throw "Portable Data does not contain archive.db: $DataPath"
    }

    $dbInfo = Get-Item -LiteralPath $dbPath
    if ($dbInfo.Length -le 0) { throw "archive.db is empty: $dbPath" }

    $walPath = $dbPath + '-wal'
    $walPresent = Test-Path -LiteralPath $walPath -PathType Leaf
    $stamp = $dbInfo.LastWriteTimeUtc
    $walLength = 0L
    $walSha256 = ''
    if ($walPresent) {
        $walInfo = Get-Item -LiteralPath $walPath
        $walLength = [long]$walInfo.Length
        $walSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $walPath).Hash.ToLowerInvariant()
        if ($walInfo.LastWriteTimeUtc -gt $stamp) { $stamp = $walInfo.LastWriteTimeUtc }
    }

    return [PSCustomObject]@{
        ArchiveDbLength = [long]$dbInfo.Length
        ArchiveDbSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $dbPath).Hash.ToLowerInvariant()
        WalPresent = [bool]$walPresent
        WalLength = $walLength
        WalSha256 = $walSha256
        Stamp = $stamp
    }
}

function Test-SamePortableDataState($Left, $Right) {
    return (
        $Left.ArchiveDbLength -eq $Right.ArchiveDbLength -and
        [string]::Equals($Left.ArchiveDbSha256, $Right.ArchiveDbSha256, [System.StringComparison]::OrdinalIgnoreCase) -and
        $Left.WalPresent -eq $Right.WalPresent -and
        $Left.WalLength -eq $Right.WalLength -and
        [string]::Equals($Left.WalSha256, $Right.WalSha256, [System.StringComparison]::OrdinalIgnoreCase)
    )
}


function Get-QuarantineState([string]$DataPath) {
    $quarantinePath = Join-Path $DataPath 'Quarantine'
    if (-not (Test-Path -LiteralPath $quarantinePath -PathType Container)) {
        return [PSCustomObject]@{
            FileCount = 0L
            TotalBytes = 0L
            ManifestSha256 = ''
        }
    }

    $quarantineRootItem = Get-Item -LiteralPath $quarantinePath -Force -ErrorAction Stop
    if (($quarantineRootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Data\Quarantine itself is a junction/symlink and cannot be safety-backed automatically: $quarantinePath"
    }

    # Quarantine may contain the only remaining copy of a user's original photo. Build-time
    # backup verification therefore hashes every regular file there before a recovery source
    # may be deleted. Generated Cache/Models/Runtime/Logs are intentionally excluded.
    # Reject junctions/symlinks rather than accidentally traversing or backing up data outside
    # PAM's quarantine boundary. Normal PAM quarantine folders are never reparse points.
    $reparseItems = @(Get-ChildItem -LiteralPath $quarantinePath -Recurse -Force -ErrorAction Stop |
        Where-Object { ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 })
    if ($reparseItems.Count -gt 0) {
        throw "Data\Quarantine contains a junction/symlink and cannot be safety-backed automatically: $($reparseItems[0].FullName)"
    }
    $entries = New-Object System.Collections.Generic.List[string]
    $totalBytes = 0L
    $files = @(Get-ChildItem -LiteralPath $quarantinePath -File -Recurse -Force -ErrorAction Stop |
        Sort-Object FullName)
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($quarantinePath.Length).TrimStart([char[]]@('\','/'))
        $relative = $relative.Replace('\','/')
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
        $totalBytes += [long]$file.Length
        $entries.Add(($relative + '|' + [long]$file.Length + '|' + $hash))
    }

    $manifestText = [string]::Join("`n", $entries.ToArray())
    $bytes = $Utf8NoBom.GetBytes($manifestText)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $manifestHash = if ($bytes.Length -eq 0) { '' } else { ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-','').ToLowerInvariant() }
    } finally {
        $sha.Dispose()
    }

    return [PSCustomObject]@{
        FileCount = [long]$files.Count
        TotalBytes = $totalBytes
        ManifestSha256 = $manifestHash
    }
}

function Get-PortableSafetyState([string]$DataPath) {
    $catalog = Get-PortableDataState $DataPath
    $quarantine = Get-QuarantineState $DataPath
    return [PSCustomObject]@{
        ArchiveDbLength = $catalog.ArchiveDbLength
        ArchiveDbSha256 = $catalog.ArchiveDbSha256
        WalPresent = $catalog.WalPresent
        WalLength = $catalog.WalLength
        WalSha256 = $catalog.WalSha256
        Stamp = $catalog.Stamp
        QuarantineFileCount = $quarantine.FileCount
        QuarantineTotalBytes = $quarantine.TotalBytes
        QuarantineManifestSha256 = $quarantine.ManifestSha256
    }
}

function Test-SamePortableSafetyState($Left, $Right) {
    return (
        (Test-SamePortableDataState $Left $Right) -and
        $Left.QuarantineFileCount -eq $Right.QuarantineFileCount -and
        $Left.QuarantineTotalBytes -eq $Right.QuarantineTotalBytes -and
        [string]::Equals($Left.QuarantineManifestSha256, $Right.QuarantineManifestSha256, [System.StringComparison]::OrdinalIgnoreCase)
    )
}

function Test-PortableDataBackup([string]$BackupPath) {
    try {
        $markerPath = Join-Path $BackupPath $PortableDataBackupMarkerName
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) { return $false }
        $marker = [System.IO.File]::ReadAllText($markerPath) | ConvertFrom-Json
        $required = @('FormatVersion','ArchiveDbLength','ArchiveDbSha256','WalPresent','WalLength','WalSha256','QuarantineFileCount','QuarantineTotalBytes','QuarantineManifestSha256')
        foreach ($name in $required) {
            if (-not ($marker.PSObject.Properties.Name -contains $name)) { return $false }
        }
        if ([int]$marker.FormatVersion -ne $PortableDataBackupMarkerVersion) { return $false }

        $state = Get-PortableSafetyState $BackupPath
        if ($state.ArchiveDbLength -ne [long]$marker.ArchiveDbLength) { return $false }
        if (-not [string]::Equals($state.ArchiveDbSha256, [string]$marker.ArchiveDbSha256, [System.StringComparison]::OrdinalIgnoreCase)) { return $false }
        if ($state.WalPresent -ne [bool]$marker.WalPresent) { return $false }
        if ($state.WalLength -ne [long]$marker.WalLength) { return $false }
        if (-not [string]::Equals($state.WalSha256, [string]$marker.WalSha256, [System.StringComparison]::OrdinalIgnoreCase)) { return $false }
        if ($state.QuarantineFileCount -ne [long]$marker.QuarantineFileCount) { return $false }
        if ($state.QuarantineTotalBytes -ne [long]$marker.QuarantineTotalBytes) { return $false }
        if (-not [string]::Equals($state.QuarantineManifestSha256, [string]$marker.QuarantineManifestSha256, [System.StringComparison]::OrdinalIgnoreCase)) { return $false }
        return $true
    } catch {
        return $false
    }
}

function Write-PortableDataBackupMarker([string]$BackupPath) {
    $state = Get-PortableSafetyState $BackupPath
    $marker = [ordered]@{
        FormatVersion = $PortableDataBackupMarkerVersion
        CreatedUtc = (Get-Date).ToUniversalTime().ToString('o')
        ArchiveDbLength = $state.ArchiveDbLength
        ArchiveDbSha256 = $state.ArchiveDbSha256
        WalPresent = $state.WalPresent
        WalLength = $state.WalLength
        WalSha256 = $state.WalSha256
        QuarantineFileCount = $state.QuarantineFileCount
        QuarantineTotalBytes = $state.QuarantineTotalBytes
        QuarantineManifestSha256 = $state.QuarantineManifestSha256
    }
    $markerPath = Join-Path $BackupPath $PortableDataBackupMarkerName
    $tempMarker = $markerPath + '.tmp-' + [Guid]::NewGuid().ToString('N')
    try {
        [System.IO.File]::WriteAllText($tempMarker, ($marker | ConvertTo-Json -Depth 3), $Utf8NoBom)
        Move-Item -LiteralPath $tempMarker -Destination $markerPath -Force
    } finally {
        Remove-Item -LiteralPath $tempMarker -Force -ErrorAction SilentlyContinue
    }
    if (-not (Test-PortableDataBackup $BackupPath)) {
        throw 'Safety-backup completion marker could not be verified.'
    }
}

function Download-VerifiedFileWithFallback(
    [string]$Destination,
    [string[]]$Urls,
    [string]$Description,
    [string]$ExpectedSha256,
    [long]$MinimumSize = 1000
) {
    $expected = $ExpectedSha256.ToLowerInvariant()

    if (Test-Path $Destination) {
        try {
            $existingHash = (Get-FileHash -Algorithm SHA256 -Path $Destination).Hash.ToLowerInvariant()
            $existingSize = (Get-Item $Destination).Length
            if ($existingSize -ge $MinimumSize -and $existingHash -eq $expected) {
                Write-Setup "$Description already exists and SHA-256 is valid: $existingHash"
                return
            }
            Write-Setup "$Description exists but is invalid (size=$existingSize, SHA-256=$existingHash). Re-downloading."
        } catch {
            Write-Setup "$Description exists but could not be verified. Re-downloading."
        }
        Remove-Item $Destination -Force -ErrorAction SilentlyContinue
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    $last = $null
    $attempt = 0

    foreach ($url in $Urls) {
        $attempt++
        $temp = "$Destination.download.$attempt"
        Remove-Item $temp -Force -ErrorAction SilentlyContinue
        try {
            Write-Setup "Downloading $Description (source $attempt/$($Urls.Count))..."
            Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $temp

            if (-not (Test-Path $temp)) {
                throw "Download completed without creating a file."
            }

            $size = (Get-Item $temp).Length
            if ($size -lt $MinimumSize) {
                throw "Downloaded file is unexpectedly small ($size bytes)."
            }

            $actual = (Get-FileHash -Algorithm SHA256 -Path $temp).Hash.ToLowerInvariant()
            if ($actual -ne $expected) {
                Write-Setup "Rejected $Description from source ${attempt}: checksum mismatch (got $actual). Trying next source."
                Remove-Item $temp -Force -ErrorAction SilentlyContinue
                continue
            }

            Move-Item $temp $Destination -Force
            Write-Setup "$Description SHA-256 verified: $actual"
            return
        } catch {
            $last = $_.Exception
            Write-Setup "Source $attempt failed for ${Description}: $($_.Exception.Message)"
            Remove-Item $temp -Force -ErrorAction SilentlyContinue
        }
    }

    Remove-Item $Destination -Force -ErrorAction SilentlyContinue
    Fail "Could not download a verified copy of $Description. All official sources failed or returned unexpected content. Expected SHA-256: $expected" $last
}

try {
    if (-not [Environment]::Is64BitOperatingSystem) {
        Fail 'This build script currently supports only 64-bit Windows.'
    }

    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    # A user may unpack a newer PAM source release over an older project directory.
    # SDK-style projects compile every *.cs/XAML by default, so source files removed in older
    # releases would otherwise remain on disk and could still participate in compilation.
    # Remove only exact obsolete source files; never touch Data or user photos.
    $ObsoleteSources = @(
        'src\PhotoArchiveManager\Services\SemanticAnalyzer.cs',
        'src\PhotoArchiveManager\Services\SemanticModelService.cs',
        'src\PhotoArchiveManager\Services\ClipTokenizer.cs',
        'src\PhotoArchiveManager\Services\RussianPhotoQueryTranslator.cs',
        'src\PhotoArchiveManager\Models\SemanticEmbeddingItem.cs',
        'src\PhotoArchiveManager\Models\SemanticIndexCandidate.cs',
        'src\PhotoArchiveManager\Models\SemanticProgress.cs',
        'src\PhotoArchiveManager\Models\SemanticSearchPlan.cs',
        'src\PhotoArchiveManager\Models\SemanticSearchResult.cs',
        'src\PhotoArchiveManager\DuplicateWizardWindow.xaml',
        'src\PhotoArchiveManager\DuplicateWizardWindow.xaml.cs',
        'src\PhotoArchiveManager\Models\DuplicateCleanupRecommendation.cs'
    )
    $RemovedLegacySource = $false
    foreach ($relativePath in $ObsoleteSources) {
        $legacyPath = Join-Path $Root $relativePath
        if (Test-Path $legacyPath) {
            Remove-Item $legacyPath -Force
            Write-Setup "Removed obsolete source left from an older PAM release: $relativePath"
            $RemovedLegacySource = $true
        }
    }
    if ($RemovedLegacySource) {
        Write-Setup 'Obsolete source files from an older PAM release were removed. Current Data/catalogue was not touched.'
    }

    # Prevent stale generated WPF/C# artifacts from a previous source version from leaking
    # into this build. These folders contain compiler output only.
    $ProjectDir = Split-Path -Parent $Project
    foreach ($generatedDirName in @('bin', 'obj')) {
        $generatedDir = Join-Path $ProjectDir $generatedDirName
        if (Test-Path $generatedDir) {
            Write-Setup "Cleaning stale compiler output: $generatedDirName"
            Remove-Item $generatedDir -Recurse -Force
        }
    }

    if (-not (Test-Path $DotnetExe)) {
        Write-Setup '[1/7] Local .NET 8 SDK is not installed. Downloading official bootstrap script...'
        if (-not (Test-Path $InstallScript)) {
            Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $InstallScript
        }

        Write-Setup "Installing .NET 8 SDK to $DotnetDir (no admin rights required)..."
        New-Item -ItemType Directory -Force -Path $DotnetDir | Out-Null
        & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $InstallScript -Channel '8.0' -Quality 'GA' -Architecture 'x64' -InstallDir $DotnetDir -NoPath 2>&1 |
            Tee-Object -FilePath $SetupLog -Append
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $DotnetExe)) {
            Fail "dotnet-install failed with exit code $LASTEXITCODE."
        }
    } else {
        Write-Setup '[1/7] Local .NET SDK already exists. Verifying .NET 8 is present.'
    }

    $SdkLines = @(& $DotnetExe --list-sdks 2>&1)
    if ($LASTEXITCODE -ne 0) { Fail 'dotnet --list-sdks failed for the local SDK.' }
    $HasNet8Sdk = @($SdkLines | Where-Object { $_ -match '^8\.\d+\.\d+' }).Count -gt 0
    if (-not $HasNet8Sdk) {
        Write-Setup 'The local dotnet host exists but no .NET 8 SDK is installed. Installing .NET 8 side-by-side...'
        if (-not (Test-Path $InstallScript)) {
            Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $InstallScript
        }
        & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $InstallScript -Channel '8.0' -Quality 'GA' -Architecture 'x64' -InstallDir $DotnetDir -NoPath 2>&1 |
            Tee-Object -FilePath $SetupLog -Append
        if ($LASTEXITCODE -ne 0) { Fail "Installing the missing .NET 8 SDK failed with exit code $LASTEXITCODE." }
        $SdkLines = @(& $DotnetExe --list-sdks 2>&1)
        if ($LASTEXITCODE -ne 0 -or @($SdkLines | Where-Object { $_ -match '^8\.\d+\.\d+' }).Count -eq 0) {
            Fail 'The installer finished, but a .NET 8 SDK is still not available.'
        }
    }

    $env:DOTNET_ROOT = $DotnetDir
    $env:PATH = "$DotnetDir;$env:PATH"
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:NUGET_XMLDOC_MODE = 'skip'

    Write-Setup '[2/7] .NET information:'
    & $DotnetExe --info 2>&1 | Tee-Object -FilePath $SetupLog -Append
    if ($LASTEXITCODE -ne 0) { Fail 'dotnet --info failed.' }

    Write-Setup '[3/7] Preparing local YuNet/SFace AI assets...'
    Download-VerifiedFileWithFallback $SFaceModel @(
        'https://github.com/opencv/opencv_zoo/raw/main/models/face_recognition_sface/face_recognition_sface_2021dec.onnx',
        'https://huggingface.co/opencv/face_recognition_sface/resolve/main/face_recognition_sface_2021dec.onnx?download=true'
    ) 'OpenCV SFace recognition model (about 39 MB)' $SFaceSha256 38000000

    Download-VerifiedFileWithFallback $YuNetModel @(
        'https://github.com/opencv/opencv_zoo/raw/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx',
        'https://huggingface.co/opencv/face_detection_yunet/resolve/main/face_detection_yunet_2023mar.onnx?download=true'
    ) 'OpenCV YuNet face detector (about 230 KB)' $YuNetSha256 200000

    Write-Setup '[4/7] Restoring NuGet packages...'
    & $DotnetExe restore $Project -r win-x64 --nologo 2>&1 | Tee-Object -FilePath $BuildLog -Append
    if ($LASTEXITCODE -ne 0) { Fail "NuGet restore failed with exit code $LASTEXITCODE." }

    # Do not trust a successful managed restore alone. The face engine requires the
    # win-x64 native OpenCvSharp binding, which must be present in the runtime asset graph
    # before a portable single-file publish can bundle it.
    $ProjectDir = Split-Path -Parent $Project
    $AssetsJson = Join-Path $ProjectDir 'obj\project.assets.json'
    if (-not (Test-Path $AssetsJson)) { Fail 'NuGet restore did not create obj\project.assets.json.' }
    $AssetsText = [System.IO.File]::ReadAllText($AssetsJson)
    foreach ($RequiredNativeAsset in @(
        'runtimes/win-x64/native/OpenCvSharpExtern.dll',
        'runtimes/win-x64/native/opencv_videoio_ffmpeg4130_64.dll'
    )) {
        if ($AssetsText.IndexOf($RequiredNativeAsset, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            Fail "Required OpenCvSharp native runtime asset was not resolved: $RequiredNativeAsset"
        }
    }
    Write-Setup 'OpenCvSharp win-x64 native runtime assets are present in the NuGet graph.'

    Write-Setup '[5/7] Compiling Release...'
    & $DotnetExe build $Project -c Release --no-restore --nologo 2>&1 | Tee-Object -FilePath $BuildLog -Append
    if ($LASTEXITCODE -ne 0) { Fail "Build failed with exit code $LASTEXITCODE." }

    Write-Setup '[6/7] Publishing self-contained Windows x64 build...'

    # Preserve the portable catalogue across rebuilds. Never destroy or roll back a user's archive.db.
    # A previous failed build can leave a safety backup behind, so choose the newest SQLite state
    # across the current publish, older publish folders and recovery backups instead of blindly
    # trusting one fixed backup folder. archive.db-wal participates in the freshness timestamp.
    $RunningPam = @(Get-Process -Name 'PhotoArchiveManager' -ErrorAction SilentlyContinue)
    if ($RunningPam.Count -gt 0) {
        Fail 'Photo Archive Manager is currently running. Close it before rebuilding so Data\archive.db and its WAL can be copied consistently.'
    }
    if (Test-Path -LiteralPath $TargetDataDir -PathType Container) {
        $targetDataItem = Get-Item -LiteralPath $TargetDataDir -Force -ErrorAction Stop
        if (($targetDataItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            Fail 'BUILD_OUTPUT portable Data is a junction/symlink. Build refuses to remove or replace a reparse-point Data directory automatically.'
        }
    }

    $DataCandidates = @()
    $CandidatePaths = @()
    $LegacyRecoveryBackup = $null
    if (Test-Path (Join-Path $TargetDataDir 'archive.db')) {
        $CandidatePaths += [PSCustomObject]@{ Path = $TargetDataDir; Kind = 'current build'; Priority = 3 }
    }
    Get-ChildItem -Path $OutputRoot -Directory -Filter 'PhotoArchiveManager_*_win-x64' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -ne $PublishDir -and (Test-Path (Join-Path $_.FullName 'Data\archive.db')) } |
        ForEach-Object { $CandidatePaths += [PSCustomObject]@{ Path = (Join-Path $_.FullName 'Data'); Kind = 'previous build'; Priority = 2 } }

    foreach ($backupDir in @(Get-ChildItem -Path $OutputRoot -Directory -Filter ($PortableDataBackupPrefix + '*') -ErrorAction SilentlyContinue)) {
        if (Test-PortableDataBackup $backupDir.FullName) {
            $CandidatePaths += [PSCustomObject]@{ Path = $backupDir.FullName; Kind = 'recovery backup'; Priority = 1 }
        } elseif ($backupDir.Name -eq $PortableDataBackupPrefix -and (Test-Path (Join-Path $backupDir.FullName 'archive.db'))) {
            # PAM <=1.9.0 used one fixed recovery directory without a completion marker. Keep it as
            # a last-resort source only when no current/previous/verified backup survives.
            $LegacyRecoveryBackup = $backupDir.FullName
            Write-Setup "Found legacy unmarked safety backup (last-resort only): $LegacyRecoveryBackup"
        } else {
            Write-Setup "Ignoring incomplete/unverified recovery folder: $($backupDir.FullName)"
        }
    }

    foreach ($candidate in $CandidatePaths) {
        try {
            $state = Get-PortableDataState $candidate.Path
            $DataCandidates += [PSCustomObject]@{
                Path = $candidate.Path
                Kind = $candidate.Kind
                Priority = $candidate.Priority
                Stamp = $state.Stamp
            }
        } catch {
            Write-Setup "Ignoring unreadable Data candidate $($candidate.Path): $($_.Exception.Message)"
        }
    }

    $SelectedData = $DataCandidates |
        Sort-Object @{ Expression = 'Stamp'; Descending = $true }, @{ Expression = 'Priority'; Descending = $true } |
        Select-Object -First 1

    if (-not $SelectedData -and $LegacyRecoveryBackup) {
        try {
            $legacyState = Get-PortableDataState $LegacyRecoveryBackup
            $SelectedData = [PSCustomObject]@{
                Path = $LegacyRecoveryBackup
                Kind = 'legacy recovery backup'
                Priority = 0
                Stamp = $legacyState.Stamp
            }
            Write-Setup 'WARNING: no marked/current Data survived; using the legacy 1.9.0 safety backup as the only remaining recovery source.'
        } catch {
            Write-Setup "Legacy recovery backup is unreadable and will not be used: $($_.Exception.Message)"
        }
    }

    if ($SelectedData -and (Test-Path -LiteralPath (Join-Path $TargetDataDir 'archive.db') -PathType Leaf)) {
        $selectedFullPath = [System.IO.Path]::GetFullPath($SelectedData.Path).TrimEnd('\')
        $currentFullPath = [System.IO.Path]::GetFullPath($TargetDataDir).TrimEnd('\')
        if (-not [string]::Equals($selectedFullPath, $currentFullPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            # The newest candidate may be a recovery/previous build. The current publish can still
            # contain an older but valid catalogue (and, critically, quarantined originals). Do not
            # destroy that alternate state merely because another archive.db has a newer timestamp.
            # Move the whole current Data tree outside PublishDir before PublishDir is removed.
            $fallbackName = $PortableDataBackupPrefix + '_fallback_' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '_' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
            $currentFallback = Join-Path $OutputRoot $fallbackName
            Write-Setup "Preserving alternate current Data before switching to a newer candidate: $currentFallback"
            try {
                $currentStateBefore = Get-PortableSafetyState $TargetDataDir
                Move-Item -LiteralPath $TargetDataDir -Destination $currentFallback
                $fallbackState = Get-PortableSafetyState $currentFallback
                if (-not (Test-SamePortableSafetyState $currentStateBefore $fallbackState)) {
                    throw 'Moved alternate Data does not match its original archive.db/WAL/Quarantine state.'
                }
                Write-PortableDataBackupMarker $currentFallback
                Write-Setup 'Alternate current Data was preserved as a verified recovery fallback. It is intentionally kept after a successful build.'
            } catch {
                # If the move happened but validation/marker creation failed, put the original Data
                # tree back whenever possible. Never continue into PublishDir deletion in this state.
                try {
                    if (-not (Test-Path -LiteralPath $TargetDataDir) -and (Test-Path -LiteralPath $currentFallback)) {
                        Remove-Item -LiteralPath (Join-Path $currentFallback $PortableDataBackupMarkerName) -Force -ErrorAction SilentlyContinue
                        Move-Item -LiteralPath $currentFallback -Destination $TargetDataDir
                    }
                } catch {
                    Write-Setup "WARNING: automatic rollback of alternate Data failed. Recovery path: $currentFallback"
                }
                Fail "Could not safely preserve the current portable Data before selecting $($SelectedData.Path)." $_.Exception
            }
        }
    }

    if ($SelectedData) {
        Write-Setup "Newest portable Data candidate: $($SelectedData.Path) [$($SelectedData.Kind), $($SelectedData.Stamp.ToString('o'))]"
        if ($SelectedData.Kind -eq 'recovery backup') {
            if (-not (Test-PortableDataBackup $SelectedData.Path)) {
                Fail "Selected recovery backup failed its completion/hash marker: $($SelectedData.Path)"
            }
            # It is already outside any publish directory that this build deletes. Reuse it directly.
            $PortableDataRestoreSource = $SelectedData.Path
            Write-Setup "Recovering Data from verified safety backup: $PortableDataRestoreSource"
        } elseif ($SelectedData.Kind -eq 'legacy recovery backup') {
            $PortableDataRestoreSource = $SelectedData.Path
            Write-Setup "Recovering Data from legacy safety backup: $PortableDataRestoreSource"
        } else {
            $backupName = $PortableDataBackupPrefix + '_' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '_' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
            $PortableDataBackup = Join-Path $OutputRoot $backupName
            Write-Setup "Preserving portable Data from: $($SelectedData.Path)"
            try {
                $sourceStateBefore = Get-PortableSafetyState $SelectedData.Path
                New-Item -ItemType Directory -Path $PortableDataBackup | Out-Null
                Get-ChildItem -LiteralPath $SelectedData.Path -Force | Copy-Item -Destination $PortableDataBackup -Recurse -Force
                $sourceStateAfter = Get-PortableSafetyState $SelectedData.Path
                $backupState = Get-PortableSafetyState $PortableDataBackup

                if (-not (Test-SamePortableSafetyState $sourceStateBefore $sourceStateAfter)) {
                    throw 'Source archive.db/WAL or Quarantine changed while its safety copy was being made.'
                }
                if (-not (Test-SamePortableSafetyState $sourceStateAfter $backupState)) {
                    throw 'archive.db/WAL or Quarantine SHA-256 state differs after the safety copy.'
                }

                Write-PortableDataBackupMarker $PortableDataBackup
                $PortableDataRestoreSource = $PortableDataBackup
                Write-Setup "Portable Data safety copy + completion marker verified: $PortableDataRestoreSource"
            } catch {
                Fail 'Could not create and verify a safety copy of portable Data. Existing Data was not intentionally deleted.' $_.Exception
            }
        }
    } else {
        Write-Setup 'No existing portable Data/archive.db was found; this will be a clean publish.'
    }

    if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null

    & $DotnetExe publish $Project -c Release -r win-x64 --self-contained true --no-restore --nologo `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false `
        -p:DebugType=None -p:DebugSymbols=false -p:PublishReadyToRun=false `
        -o $PublishDir 2>&1 | Tee-Object -FilePath $BuildLog -Append
    if ($LASTEXITCODE -ne 0) { Fail "Publish failed with exit code $LASTEXITCODE." }

    $ExePath = Join-Path $PublishDir 'PhotoArchiveManager.exe'
    if (-not (Test-Path $ExePath)) { Fail 'Single-file publish did not produce PhotoArchiveManager.exe.' }

    # The distributable build must contain one program file. Data is restored only afterwards
    # and is user state, not a runtime dependency.
    $UnexpectedFiles = Get-ChildItem -Path $PublishDir -File | Where-Object { $_.Name -ne 'PhotoArchiveManager.exe' }
    if ($UnexpectedFiles) {
        $names = ($UnexpectedFiles | ForEach-Object { $_.Name }) -join ', '
        Fail "Single-file publish unexpectedly produced extra files: $names"
    }

    Write-Setup '[7/7] Testing the FINAL portable single EXE (SQLite + OpenCV + embedded face models)...'
    $SelfTestReport = Join-Path $OutputRoot 'portable_self_test.txt'
    Remove-Item $SelfTestReport -Force -ErrorAction SilentlyContinue
    $PreviousSelfTestReport = $env:PAM_PORTABLE_SELFTEST_REPORT
    try {
        $env:PAM_PORTABLE_SELFTEST_REPORT = $SelfTestReport
        $SelfTestProcess = Start-Process -FilePath $ExePath -ArgumentList '--portable-self-test' -Wait -PassThru
    } catch {
        Fail 'Could not start the final PhotoArchiveManager.exe for its portable self-test.' $_.Exception
    } finally {
        if ($null -eq $PreviousSelfTestReport) {
            Remove-Item Env:PAM_PORTABLE_SELFTEST_REPORT -ErrorAction SilentlyContinue
        } else {
            $env:PAM_PORTABLE_SELFTEST_REPORT = $PreviousSelfTestReport
        }
    }

    if ($SelfTestProcess.ExitCode -ne 0) {
        $details = if (Test-Path $SelfTestReport) { [System.IO.File]::ReadAllText($SelfTestReport) } else { '<self-test report was not created>' }
        $details | Add-Content -Encoding UTF8 $BuildLog
        Fail "Final portable EXE self-test failed with exit code $($SelfTestProcess.ExitCode). See BUILD_OUTPUT\portable_self_test.txt and build.log."
    }
    if (-not (Test-Path $SelfTestReport)) { Fail 'Final portable EXE did not create its self-test report.' }
    $SelfTestText = [System.IO.File]::ReadAllText($SelfTestReport)
    $SelfTestText | Add-Content -Encoding UTF8 $BuildLog
    if ($SelfTestText.IndexOf('RESULT=OK', [System.StringComparison]::Ordinal) -lt 0) {
        Fail 'Final portable EXE self-test did not report RESULT=OK. See BUILD_OUTPUT\portable_self_test.txt.'
    }
    $ExpectedVersionLine = 'Version=' + $AppVersion
    $SelfTestLines = $SelfTestText -split "`r?`n"
    if (-not ($SelfTestLines -contains $ExpectedVersionLine)) {
        Fail "Final portable EXE reports a different version. Expected '$ExpectedVersionLine'. See BUILD_OUTPUT\portable_self_test.txt."
    }
    Write-Setup 'Portable self-test OK: SQLite schema + embedded OpenCvSharpExtern + YuNet/SFace load from the final EXE.'

    # The self-test intentionally exercises the same Data\Runtime extraction path as a clean PC.
    # Remove that generated test state again so a fresh BUILD_OUTPUT publish remains one EXE.
    # Any real portable Data was backed up before publish and is restored below.
    if (Test-Path $TargetDataDir) { Remove-Item $TargetDataDir -Recurse -Force }

    # Create a distributable ZIP containing only the EXE, before restoring private Data.
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    Compress-Archive -Path $ExePath -DestinationPath $ZipPath -CompressionLevel Optimal

    if ($PortableDataRestoreSource -and (Test-Path (Join-Path $PortableDataRestoreSource 'archive.db'))) {
        Write-Setup "Restoring preserved portable Data into the ready build from: $PortableDataRestoreSource"
        try {
            $markerPath = Join-Path $PortableDataRestoreSource $PortableDataBackupMarkerName
            if (Test-Path -LiteralPath $markerPath) {
                if (-not (Test-PortableDataBackup $PortableDataRestoreSource)) {
                    throw 'Recovery backup no longer matches its completion/hash marker.'
                }
            }
            $restoreState = Get-PortableSafetyState $PortableDataRestoreSource

            if (Test-Path $TargetDataDir) { Remove-Item $TargetDataDir -Recurse -Force }
            New-Item -ItemType Directory -Path $TargetDataDir | Out-Null
            Get-ChildItem -LiteralPath $PortableDataRestoreSource -Force |
                Where-Object { $_.Name -ne $PortableDataBackupMarkerName } |
                Copy-Item -Destination $TargetDataDir -Recurse -Force

            $targetState = Get-PortableSafetyState $TargetDataDir
            if (-not (Test-SamePortableSafetyState $restoreState $targetState)) {
                throw 'Restored archive.db/WAL/Quarantine did not pass SHA-256 verification.'
            }

            # Only remove the backup that was actually restored, and only after the target copy verifies.
            if ((Split-Path -Leaf $PortableDataRestoreSource).StartsWith($PortableDataBackupPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                Remove-Item $PortableDataRestoreSource -Recurse -Force
            }
        } catch {
            Fail "Build succeeded, but restoring Data failed. The safety source remains at: $PortableDataRestoreSource" $_.Exception
        }
    }

    Write-Setup "SUCCESS. Single EXE: $ExePath"
    Write-Setup "ZIP: $ZipPath"
    exit 0
}
catch {
    Fail 'Unexpected build/setup failure.' $_.Exception
}
