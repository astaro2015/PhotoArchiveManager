$ErrorActionPreference = 'Stop'
# Force UTF-8 end-to-end so Russian compiler messages are readable on Windows PowerShell 5.1.
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[Console]::InputEncoding = $Utf8NoBom
[Console]::OutputEncoding = $Utf8NoBom
$OutputEncoding = $Utf8NoBom
Set-StrictMode -Version Latest

$Root = Split-Path -Parent $PSScriptRoot
$OutputRoot = Join-Path $Root 'BUILD_OUTPUT'
$ToolsRoot = Join-Path $Root '.tools'
$DotnetDir = Join-Path $ToolsRoot 'dotnet'
$DotnetExe = Join-Path $DotnetDir 'dotnet.exe'
$InstallScript = Join-Path $ToolsRoot 'dotnet-install.ps1'
$SetupLog = Join-Path $OutputRoot 'setup.log'
$BuildLog = Join-Path $OutputRoot 'build.log'
$LastError = Join-Path $OutputRoot 'LAST_ERROR.txt'
$PublishDir = Join-Path $OutputRoot 'PhotoArchiveManager_1.7.1_win-x64'
$ZipPath = Join-Path $OutputRoot 'PhotoArchiveManager_1.7.1_win-x64.zip'
$Project = Join-Path $Root 'src\PhotoArchiveManager\PhotoArchiveManager.csproj'
$AssetsDir = Join-Path $Root 'src\PhotoArchiveManager\Assets'
$FaceCascade = Join-Path $AssetsDir 'haarcascade_frontalface_default.xml'
$EyeCascade = Join-Path $AssetsDir 'haarcascade_eye_tree_eyeglasses.xml'
$SFaceModel = Join-Path $AssetsDir 'face_recognition_sface_2021dec.onnx'
$SFaceSha256 = '0ba9fbfa01b5270c96627c4ef784da859931e02f04419c829e83484087c34e79'
$YuNetModel = Join-Path $AssetsDir 'face_detection_yunet_2023mar.onnx'
$YuNetSha256 = '8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4'
$PortableDataBackup = Join-Path $OutputRoot '_PAM_PORTABLE_DATA_BACKUP'
$TargetDataDir = Join-Path $PublishDir 'Data'

New-Item -ItemType Directory -Force -Path $OutputRoot, $ToolsRoot | Out-Null
Remove-Item $LastError -Force -ErrorAction SilentlyContinue
"Build started: $(Get-Date -Format o)" | Set-Content -Encoding UTF8 $SetupLog
"Build started: $(Get-Date -Format o)" | Set-Content -Encoding UTF8 $BuildLog

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

function Download-FileWithFallback([string]$Destination, [string[]]$Urls, [string]$Description) {
    if (Test-Path $Destination) { return }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    $last = $null
    foreach ($url in $Urls) {
        try {
            Write-Setup "Downloading $Description..."
            Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $Destination
            if ((Test-Path $Destination) -and (Get-Item $Destination).Length -gt 1000) { return }
            throw "Downloaded file is unexpectedly small."
        } catch {
            $last = $_.Exception
            Remove-Item $Destination -Force -ErrorAction SilentlyContinue
        }
    }
    Fail "Could not download $Description. Internet access is required for the first build." $last
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
    # SDK-style projects compile every *.cs by default, so source files removed in 1.6.x
    # would otherwise remain on disk and could still participate in compilation.
    # Remove only the exact obsolete semantic/CLIP files; never touch Data or user photos.
    $LegacySemanticSources = @(
        'src\PhotoArchiveManager\Services\SemanticAnalyzer.cs',
        'src\PhotoArchiveManager\Services\SemanticModelService.cs',
        'src\PhotoArchiveManager\Services\ClipTokenizer.cs',
        'src\PhotoArchiveManager\Services\RussianPhotoQueryTranslator.cs',
        'src\PhotoArchiveManager\Models\SemanticEmbeddingItem.cs',
        'src\PhotoArchiveManager\Models\SemanticIndexCandidate.cs',
        'src\PhotoArchiveManager\Models\SemanticProgress.cs',
        'src\PhotoArchiveManager\Models\SemanticSearchPlan.cs',
        'src\PhotoArchiveManager\Models\SemanticSearchResult.cs'
    )
    $RemovedLegacySource = $false
    foreach ($relativePath in $LegacySemanticSources) {
        $legacyPath = Join-Path $Root $relativePath
        if (Test-Path $legacyPath) {
            Remove-Item $legacyPath -Force
            Write-Setup "Removed obsolete source left from an older PAM release: $relativePath"
            $RemovedLegacySource = $true
        }
    }
    if ($RemovedLegacySource) {
        Write-Setup 'Old semantic-search source files were removed. Current Data/catalogue was not touched.'
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
        Write-Setup '[1/6] Local .NET 8 SDK is not installed. Downloading official bootstrap script...'
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
        Write-Setup '[1/6] Local .NET SDK already exists. Reusing it.'
    }

    $env:DOTNET_ROOT = $DotnetDir
    $env:PATH = "$DotnetDir;$env:PATH"
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:NUGET_XMLDOC_MODE = 'skip'

    Write-Setup '[2/6] .NET information:'
    & $DotnetExe --info 2>&1 | Tee-Object -FilePath $SetupLog -Append
    if ($LASTEXITCODE -ne 0) { Fail 'dotnet --info failed.' }

    Write-Setup '[3/6] Preparing local face AI assets...'
    Download-FileWithFallback $FaceCascade @(
        'https://raw.githubusercontent.com/opencv/opencv/4.13.0/data/haarcascades/haarcascade_frontalface_default.xml',
        'https://cdn.jsdelivr.net/gh/opencv/opencv@4.13.0/data/haarcascades/haarcascade_frontalface_default.xml'
    ) 'OpenCV frontal-face cascade'
    Download-FileWithFallback $EyeCascade @(
        'https://raw.githubusercontent.com/opencv/opencv/4.13.0/data/haarcascades/haarcascade_eye_tree_eyeglasses.xml',
        'https://cdn.jsdelivr.net/gh/opencv/opencv@4.13.0/data/haarcascades/haarcascade_eye_tree_eyeglasses.xml'
    ) 'OpenCV eye cascade'
    Download-VerifiedFileWithFallback $SFaceModel @(
        'https://github.com/opencv/opencv_zoo/raw/main/models/face_recognition_sface/face_recognition_sface_2021dec.onnx',
        'https://huggingface.co/opencv/face_recognition_sface/resolve/main/face_recognition_sface_2021dec.onnx?download=true'
    ) 'OpenCV SFace recognition model (about 39 MB)' $SFaceSha256 38000000

    Download-VerifiedFileWithFallback $YuNetModel @(
        'https://github.com/opencv/opencv_zoo/raw/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx',
        'https://huggingface.co/opencv/face_detection_yunet/resolve/main/face_detection_yunet_2023mar.onnx?download=true'
    ) 'OpenCV YuNet face detector (about 230 KB)' $YuNetSha256 200000

    Write-Setup '[4/6] Restoring NuGet packages...'
    & $DotnetExe restore $Project -r win-x64 --nologo 2>&1 | Tee-Object -FilePath $BuildLog -Append
    if ($LASTEXITCODE -ne 0) { Fail "NuGet restore failed with exit code $LASTEXITCODE." }

    Write-Setup '[5/6] Compiling Release...'
    & $DotnetExe build $Project -c Release --no-restore --nologo 2>&1 | Tee-Object -FilePath $BuildLog -Append
    if ($LASTEXITCODE -ne 0) { Fail "Build failed with exit code $LASTEXITCODE." }

    Write-Setup '[6/6] Publishing self-contained Windows x64 build...'

    # Preserve the portable catalogue across rebuilds. Never destroy a user's archive.db.
    if (-not (Test-Path $PortableDataBackup)) {
        $DataSource = $null
        if (Test-Path (Join-Path $TargetDataDir 'archive.db')) {
            $DataSource = $TargetDataDir
        } else {
            $PreviousBuild = Get-ChildItem -Path $OutputRoot -Directory -Filter 'PhotoArchiveManager_*_win-x64' -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -ne $PublishDir -and (Test-Path (Join-Path $_.FullName 'Data\archive.db')) } |
                Sort-Object LastWriteTime -Descending |
                Select-Object -First 1
            if ($PreviousBuild) { $DataSource = Join-Path $PreviousBuild.FullName 'Data' }
        }

        if ($DataSource) {
            Write-Setup "Preserving portable Data from: $DataSource"
            try {
                Copy-Item $DataSource $PortableDataBackup -Recurse -Force
            } catch {
                Fail 'Could not preserve portable Data. Close Photo Archive Manager and run the build again. Existing data was NOT intentionally deleted.' $_.Exception
            }
        }
    } else {
        Write-Setup "Found an existing safety backup: $PortableDataBackup"
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

    # Create a distributable ZIP containing only the EXE, before restoring private Data.
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    Compress-Archive -Path $ExePath -DestinationPath $ZipPath -CompressionLevel Optimal

    if (Test-Path $PortableDataBackup) {
        Write-Setup 'Restoring preserved portable Data into the ready build...'
        try {
            if (Test-Path $TargetDataDir) { Remove-Item $TargetDataDir -Recurse -Force }
            Copy-Item $PortableDataBackup $TargetDataDir -Recurse -Force
            Remove-Item $PortableDataBackup -Recurse -Force
        } catch {
            Fail "Build succeeded, but restoring Data failed. Your safety copy remains at: $PortableDataBackup" $_.Exception
        }
    }

    Write-Setup "SUCCESS. Single EXE: $ExePath"
    Write-Setup "ZIP: $ZipPath"
    exit 0
}
catch {
    Fail 'Unexpected build/setup failure.' $_.Exception
}
