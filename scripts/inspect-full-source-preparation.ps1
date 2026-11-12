[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunRoot,
    [string]$ToolsRoot,
    [switch]$ProgressOnly
)

$ErrorActionPreference = 'Stop'
$toolsRoot = if ($ToolsRoot) { [IO.Path]::GetFullPath($ToolsRoot) } else { [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')) }
$checkoutRoot = [IO.Path]::GetFullPath((Join-Path $toolsRoot '..'))
$evidenceRoot = Join-Path $checkoutRoot 'Library/Omnipatching'
$allowedRoot = [IO.Path]::GetFullPath($evidenceRoot) + [IO.Path]::DirectorySeparatorChar

function Resolve-EvidencePath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Inspection inputs must stay below this checkout Library/Omnipatching.'
    }
    return $resolved
}

function Test-ContractFiles([string]$Root, [object[]]$Files) {
    $rootPath = Resolve-EvidencePath $Root
    $mismatches = @()
    foreach ($file in $Files) {
        $path = Resolve-EvidencePath (Join-Path $rootPath $file.Path)
        if (-not $path.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'A contract file leaves its declared evidence tree.'
        }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-Item -LiteralPath $path).Length -ne $file.Length -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.Sha256) {
            $mismatches += $file.Path
        }
    }
    return [ordered]@{ Root = $rootPath; CheckedFiles = $Files.Count; Mismatches = $mismatches }
}


function Read-FullScaleObservation([string]$ConfigPath) {
    $configFile = Resolve-EvidencePath $ConfigPath
    $config = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
    $root = Resolve-EvidencePath $config.OutputRoot
    $progress = $null
    $progressPath = Join-Path $root 'progress.json'
    if (Test-Path -LiteralPath $progressPath) {
        try { $progress = Get-Content -LiteralPath $progressPath -Raw | ConvertFrom-Json }
        catch { $progress = [ordered]@{ Observation = 'Progress was unreadable during this sample. No operation outcome inferred.' } }
    }
    $phases = @(Get-ChildItem -LiteralPath $root -File -Filter '*.complete.json' | Sort-Object LastWriteTimeUtc | ForEach-Object {
        $row = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
        [ordered]@{
            Phase = $row.Phase; Outcome = $row.Outcome
            CurrentFiles = $row.CurrentFiles; TargetFiles = $row.TargetFiles
            CurrentBytes = $row.CurrentBytes; TargetBytes = $row.TargetBytes
            CopiedTargetBytes = $row.CopiedTargetBytes; CopiedUndoBytes = $row.CopiedUndoBytes
            AvoidedTargetAndUndoBytes = $row.AvoidedTargetAndUndoBytes
            RecoveredExistingIntent = $row.RecoveredExistingIntent; ElapsedMilliseconds = $row.ElapsedMilliseconds
        }
    })
    $final = $null
    $reportPath = Join-Path $root 'result.json'
    if (Test-Path -LiteralPath $reportPath) {
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        $final = [ordered]@{
            Report = $reportPath; DeclaredNativeSuccess = $report.Success
            FullRebuiltGame = $report.FullRebuiltGame; AuthenticatedDelivery = $report.AuthenticatedDelivery
            NativeHost = $report.NativeHost; PosixHostVerified = $report.PosixHostVerified
            StockFiles = $report.StockFiles; StockBytes = $report.StockBytes; StockManifestSha256 = $report.StockManifestSha256
            OriginalArchiveSha256 = $report.OriginalArchiveSha256; ImmutableInstalledArchiveSha256 = $report.ImmutableInstalledArchiveSha256
            IndependentRestoredStockHashesVerifiedByNativeTest = $report.IndependentRestoredStockHashesVerified
            UnknownUserDataPreservedByNativeTest = $report.UnknownUserDataPreserved
            ElapsedMilliseconds = $report.ElapsedMilliseconds
            ReusedCompletedPhaseRecords = $report.ReusedCompletedPhaseRecords
            RecordedPhaseElapsedMilliseconds = $report.RecordedPhaseElapsedMilliseconds
            ResourceMeasurementsCoverCurrentAttemptOnly = $report.ResourceMeasurementsCoverCurrentAttemptOnly
            CumulativeManagedAllocatedBytes = $report.CumulativeManagedAllocatedBytes
            ProcessLifetimePeakWorkingSetBytes = $report.ProcessLifetimePeakWorkingSetBytes
            PagingAllowed = $report.PagingAllowed; FreeDiskBytes = $report.FreeDiskBytes
        }
    }
    return [ordered]@{
        OutputRoot = $root; Config = $configFile; ConfigSha256 = (Get-FileHash -LiteralPath $configFile).Hash
        ProgressObservation = $progress; CompletedPhaseRecords = $phases; FinalNativeReport = $final
        InspectionRehashesFullStock = $false
    }
}

$RunRoot = Resolve-EvidencePath $RunRoot
if ($ProgressOnly) {
    $owner = Get-Content -LiteralPath (Join-Path $RunRoot 'owner.json') -Raw | ConvertFrom-Json
    Read-FullScaleObservation $owner.FullScaleProbeConfig | ConvertTo-Json -Depth 10
    exit 0
}
$summary = Get-Content -LiteralPath (Join-Path $RunRoot 'summary.json') -Raw | ConvertFrom-Json
$pins = Get-Content -LiteralPath (Resolve-EvidencePath $summary.SourceInputPins) -Raw | ConvertFrom-Json
$sourceChanges = @($pins.PSObject.Properties | Where-Object {
    $path = [IO.Path]::GetFullPath((Join-Path $toolsRoot $_.Name))
    if (-not $path.StartsWith($toolsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A source pin leaves the tools checkout.'
    }
    -not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $_.Value
} | Select-Object -ExpandProperty Name)
$tests = @($summary.Results | ForEach-Object {
    $trxPath = Resolve-EvidencePath $_.Trx
    $counters = $null
    $failedCases = @()
    if (Test-Path -LiteralPath $trxPath) {
        [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
        $node = $trx.SelectSingleNode('//*[local-name()="Counters"]')
        $counters = [ordered]@{}
        foreach ($name in @('total', 'executed', 'passed', 'failed', 'notExecuted', 'notRunnable')) {
            $counters[$name] = [int]$node.GetAttribute($name)
        }
        $failedCases = @($trx.SelectNodes('//*[local-name()="UnitTestResult"]') |
            Where-Object { $_.outcome -eq 'Failed' } | ForEach-Object {
                [ordered]@{ Name = $_.testName; Message = [string]$_.Output.ErrorInfo.Message }
            })
    }
    $outcomes = [ordered]@{}
    if ($counters) {
        $trx.SelectNodes('//*[local-name()="UnitTestResult"]') | Group-Object outcome | ForEach-Object {
            $outcomes[$_.Name] = $_.Count
        }
    }
    [ordered]@{ Name = $_.Name; ExitCode = $_.ExitCode; ActualTrxCounters = $counters; ActualResultOutcomes = $outcomes; FailedCases = $failedCases }
})
$native = @($summary.NativeReports | ForEach-Object {
    $reportPath = Resolve-EvidencePath $_
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    $inputs = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $reportPath) 'inputs.json') -Raw | ConvertFrom-Json
    $frozen = Resolve-EvidencePath $inputs.FrozenInput
    $artifact = Resolve-EvidencePath (Join-Path $frozen 'artifact')
    $manifestPath = Join-Path $artifact 'release.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $packFiles = @()
    for ($index = 0; $index -lt $manifest.Packs.Count; $index++) {
        $packFiles += [pscustomobject]@{ Path = ('pack-{0:D6}.bin' -f $index); Length = $manifest.Packs[$index].Length; Sha256 = $manifest.Packs[$index].Sha256 }
    }
    $archive = Resolve-EvidencePath (Join-Path $inputs.Installation 'uninstall.zip')
    [ordered]@{
        Report = $reportPath; FixtureAuthority = $report.FixtureAuthority
        ProductionAuthenticationProven = $false; CompleteGameBaselineProven = $false
        FrozenBaseline = Test-ContractFiles (Join-Path $frozen 'frozen-baseline') $report.ExactRestoredFiles
        FrozenTarget = Test-ContractFiles (Join-Path $frozen 'frozen-target') $report.ExactTargetFiles
        PreparedTarget = Test-ContractFiles $report.PreparedSourceRoot $report.ExactTargetFiles
        UpdatedTarget = Test-ContractFiles $report.UpdatedSourceRoot $report.ExactTargetFiles
        RestoredInstallation = Test-ContractFiles $inputs.Installation $report.ExactRestoredFiles
        Packs = Test-ContractFiles $artifact $packFiles
        ArtifactManifestUnchanged = (Get-FileHash -LiteralPath $manifestPath).Hash -eq $inputs.ArtifactSha256
        ArchiveMatchesReceipt = (Get-FileHash -LiteralPath $archive).Hash -eq $report.ImmutableStockArchiveSha256
        ImmutableStockArchiveSha256 = $report.ImmutableStockArchiveSha256
        TargetBytes = $report.TargetBytes; StockBytes = $report.StockBytes
        StoredPackBytes = $report.StoredPackBytes; ArtifactManifestBytes = $report.Size
        ElapsedMilliseconds = $report.ElapsedMilliseconds
        CumulativeManagedAllocatedBytes = $report.CumulativeManagedAllocatedBytes
        ProcessLifetimePeakWorkingSetBytes = $report.ProcessLifetimePeakWorkingSetBytes
        MeasurementIncludesStandaloneRunnerAndAssertions = $report.MeasurementIncludesStandaloneRunnerAndAssertions
    }
})
$scale = if ($summary.FullScaleProbeConfig) { Read-FullScaleObservation $summary.FullScaleProbeConfig } else { $null }
$delivery = @($summary.DeliveryReports | ForEach-Object {
    $reportPath = Resolve-EvidencePath $_
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    [ordered]@{
        Report = $reportPath; ReportSha256 = (Get-FileHash -LiteralPath $reportPath).Hash
        DeclaredSuccess = $report.Success; RealTlsLoopback = $report.RealTlsLoopback
        NativeWindowsFilesystem = $report.NativeWindowsFilesystem
        ExternalPublisherAuthentication = $report.ExternalPublisherAuthentication
        FullRebuiltGame = $report.FullRebuiltGame; NativeLinuxMacOrWine = $report.NativeLinuxMacOrWine
        GuiVisualVerification = $report.GuiVisualVerification
        RestoredInstallation = Test-ContractFiles $report.Installation $report.ExactRestoredFiles
        ComponentTarget = Test-ContractFiles $report.TargetRoot $report.ExactTargetFiles
        ComponentStock = Test-ContractFiles $report.StockRoot $report.ExactRestoredFiles
        ArchiveMatchesReceipt = (Get-FileHash -LiteralPath (Resolve-EvidencePath (Join-Path $report.Installation 'uninstall.zip'))).Hash -eq $report.ArchiveSha256
        StockBytes = $report.StockBytes; TargetBytes = $report.TargetBytes
        ArtifactSha256 = $report.ArtifactSha256; StoredPackBytes = $report.StoredPackBytes
        WireRequests = @($report.Requests).Count
        AuthenticatedRequests = @($report.Requests | Where-Object Authorized).Count
        CredentialsObservedOutsideApi = @($report.Requests | Where-Object { $_.Authorized -and $_.Host -ne 'api.github.com' }).Count
        ElapsedMilliseconds = $report.ElapsedMilliseconds
        CumulativeManagedAllocatedBytes = $report.CumulativeManagedAllocatedBytes
        ProcessLifetimePeakWorkingSetBytes = $report.ProcessLifetimePeakWorkingSetBytes
    }
})
$pathAdmission = @()
if ($summary.CrossVolumeFixtureRoot) {
    $pathAdmission = @(Get-ChildItem -LiteralPath (Join-Path $evidenceRoot 'launcher-preparation') -Directory -Filter 'root-admission-*' | ForEach-Object {
        $reportPath = Resolve-EvidencePath (Join-Path $_.FullName 'result.json')
        if (Test-Path -LiteralPath $reportPath) {
            $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
            if ($report.SecondRoot -eq $summary.CrossVolumeFixtureRoot -and $report.FirstRoot -eq $_.FullName) {
                [ordered]@{
                    Report = $reportPath; ReportSha256 = (Get-FileHash -LiteralPath $reportPath).Hash
                    DeclaredNativeSuccess = $report.Success; NativeWindows = $report.NativeWindows; FullGame = $report.FullGame
                    FirstRoot = $report.FirstRoot; SecondRoot = $report.SecondRoot; Failures = $report.Failures
                    NativeNamespaceObservations = @($report.NativeIdentities | Where-Object { $_.Flags -in @(0, 1) })
                    GuardResults = $report.GuardResults; CrossVolumeGeneration = $report.CrossVolumeGeneration
                    ElapsedMilliseconds = $report.ElapsedMilliseconds
                    CumulativeManagedAllocatedBytes = $report.CumulativeManagedAllocatedBytes
                    ProcessLifetimePeakWorkingSetBytes = $report.ProcessLifetimePeakWorkingSetBytes
                    InspectionRehashesNativeFixtureFiles = $false
                }
            }
        }
    })
}
$inspection = [ordered]@{
    RunRoot = $RunRoot; InspectedUtc = [DateTime]::UtcNow.ToString('o')
    PinnedSourceFiles = @($pins.PSObject.Properties).Count; ChangedSinceReceipt = $sourceChanges
    Tests = $tests; Native = $native; FullScale = $scale; Delivery = $delivery
    OwnedTlsChildren = $summary.OwnedTlsChildren
    PathAdmission = $pathAdmission
    ChangedCompiledInputs = @($sourceChanges | Where-Object { $_ -match '\.(cs|csproj|props|json)$' })
    InspectionToolSha256 = (Get-FileHash -LiteralPath $PSCommandPath).Hash
    NativePlatform = if ($summary.NativePlatformReport) {
        $platformPath = Resolve-EvidencePath $summary.NativePlatformReport
        $platform = Get-Content -LiteralPath $platformPath -Raw | ConvertFrom-Json
        [ordered]@{
            Report = $platformPath; ReportSha256 = (Get-FileHash -LiteralPath $platformPath).Hash
            LinuxRoot = $platform.LinuxRoot; RuntimeQualification = $platform.RuntimeQualification
            Uname = $platform.Uname; NativeMount = $platform.NativeMount
            SourcePins = @($platform.SourcePins).Count; ChangedSourceInputs = $platform.ChangedSourceInputs
            DependencyPins = @($platform.DependencyPins).Count; NoRemotePackageSources = $platform.NoRemotePackageSources
            LinuxAbiHeaders = $platform.LinuxAbiHeaders
            NativeFixtureReports = $platform.NativeFixtureReports
            ElapsedSeconds = $platform.ElapsedSeconds; ChildProcessPeakRssKiB = $platform.ChildProcessPeakRssKiB
            ChildUserCpuSeconds = $platform.ChildUserCpuSeconds; ChildSystemCpuSeconds = $platform.ChildSystemCpuSeconds
            ResourceScope = $platform.ResourceScope; PagingAllowed = $platform.PagingAllowed
            InspectionRehashesNativeLinuxFiles = $false
        }
    } else { $null }
}
$output = Join-Path (Join-Path $evidenceRoot 'launcher-preparation') ('inspection-' + [Guid]::NewGuid().ToString('N') + '.json')
$inspection['InspectionReceipt'] = $output
$inspection | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $output -Encoding utf8
$inspection | ConvertTo-Json -Depth 10
