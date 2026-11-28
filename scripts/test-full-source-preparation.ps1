[CmdletBinding()]
param(
    [ValidateSet('Focused', 'AllLauncher')][string]$Scope = 'Focused',
    [string]$FrozenReleasePackRun,
    [switch]$IncludeRecipeTests,
    [string]$TestFilter,
    [string]$RecipeTestFilter,
    [switch]$IsolatedBuild,
    [string]$FullScaleArchiveReceipt,
    [string]$FullScaleMeasurements,
    [string]$FullScaleProbeConfig,
    [string]$ParentFeedbackPath,
    [string]$CrossVolumeFixtureRoot,
    [switch]$NativeLinux,
    [switch]$AcquireNativeSdk,
    [string]$NativeSdkReceipt,
    [switch]$PublishNativeCli,
    [string]$EvidenceRunRoot,
    [switch]$SourceInputsOnly,
    [string]$SourceInputRoot,
    [switch]$NoCompile
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
# One local source graph supplies both read-only inspection and the normal pre/post fence.
function Get-FullSourceInputSnapshot {
    param([Parameter(Mandatory)][string]$Root)
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($rootPath -eq [IO.Path]::GetPathRoot($rootPath).TrimEnd([IO.Path]::DirectorySeparatorChar)) { throw 'Use an exact local Tools source tree.' }
    $rootPrefix = $rootPath + [IO.Path]::DirectorySeparatorChar
    $files = [Collections.Generic.SortedDictionary[string,string]]::new([StringComparer]::Ordinal)
    $projects = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $roots = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $rootFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $declarations = [Collections.Generic.List[object]]::new()
    $generated = [Collections.Generic.List[object]]::new()
    $excluded = @('bin', 'obj', '.git', '.vs', '.idea', '__pycache__', '.cache', '.pytest_cache', '.mypy_cache', '.nuget', '.dotnet', 'TestResults', 'node_modules')
    function Assert-InputPath([string]$Path) {
        $full = [IO.Path]::GetFullPath($Path)
        if ($full -ne $rootPath -and -not $full.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Source input escaped the Tools root: $full"
        }
        $cursor = $full
        while ($cursor) {
            if (([IO.File]::Exists($cursor) -or [IO.Directory]::Exists($cursor)) -and
                ([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Linked source input or ancestor: $cursor"
            }
            $cursor = [IO.Path]::GetDirectoryName($cursor)
        }
        return $full
    }
    function Add-InputFile([string]$Path) {
        $full = Assert-InputPath $Path
        if (-not [IO.File]::Exists($full)) { throw "Missing declared build input: $full" }
        $name = [IO.Path]::GetRelativePath($rootPath, $full).Replace('\', '/')
        if (-not $files.ContainsKey($name)) { $files.Add($name, (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()) }
        return $name
    }
    function Add-InputTree([string]$Directory) {
        $pendingDirectories = [Collections.Generic.Stack[string]]::new()
        $pendingDirectories.Push((Assert-InputPath $Directory))
        while ($pendingDirectories.Count) {
            $directory = $pendingDirectories.Pop()
            foreach ($entry in [IO.DirectoryInfo]::new($directory).GetFileSystemInfos()) {
                if ($entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::Directory)) {
                    if ($entry.Name -notin $excluded) { $pendingDirectories.Push((Assert-InputPath $entry.FullName)) }
                } elseif ($entry.Name -ne '.git' -and $entry.Extension -notin @('.pyc', '.pyo')) {
                    [void](Add-InputFile $entry.FullName)
                }
            }
        }
    }
    [void](Assert-InputPath $rootPath)
    $pending = [Collections.Generic.Stack[string]]::new()
    foreach ($seed in @('tests/Ksp2Redux.Tools.Launcher.Tests/Ksp2Redux.Tools.Launcher.Tests.csproj',
                        'tests/Ksp2Redux.Tools.BundleConversion.Tests/Ksp2Redux.Tools.BundleConversion.Tests.csproj')) {
        $pending.Push((Join-Path $rootPath $seed))
    }
    while ($pending.Count) {
        $project = Assert-InputPath $pending.Pop()
        if (-not $projects.Add($project)) { continue }
        [void](Add-InputFile $project)
        [xml]$xml = [IO.File]::ReadAllText($project)
        $directory = [IO.Path]::GetDirectoryName($project)
        [void]$roots.Add($directory)
        foreach ($reference in $xml.SelectNodes('//ProjectReference')) {
            if ($reference.Include -match '[*?$@;]') { throw "Unresolved project reference: $($reference.Include)" }
            $pending.Push((Assert-InputPath (Join-Path $directory $reference.Include)))
        }
    }
    # Directory files include inherited props/targets, analyzer configs and shared linked sources.
    foreach ($directory in @($roots) + @((Join-Path $rootPath 'scripts'))) {
        Add-InputTree $directory
        $ancestor = $directory
        while ($ancestor.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or $ancestor -eq $rootPath) {
            foreach ($file in [IO.DirectoryInfo]::new($ancestor).GetFiles()) {
                if ($file.Name -ne '.git' -and $file.Extension -notin @('.pyc', '.pyo')) {
                    [void]$rootFiles.Add((Add-InputFile $file.FullName))
                }
            }
            if ($ancestor -eq $rootPath) { break }
            $ancestor = [IO.Path]::GetDirectoryName($ancestor)
        }
    }
    # Follow literal local imports and verify file items in the actual project/build controls.
    $controlQueue = [Collections.Generic.Queue[string]]::new()
    $seenControls = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $files.Keys) {
        if ([IO.Path]::GetExtension($name) -in @('.csproj', '.props', '.targets')) { $controlQueue.Enqueue((Join-Path $rootPath $name)) }
    }
    while ($controlQueue.Count) {
        $project = Assert-InputPath $controlQueue.Dequeue()
        if (-not $seenControls.Add($project)) { continue }
        [xml]$xml = [IO.File]::ReadAllText($project)
        $directory = [IO.Path]::GetDirectoryName($project)
        foreach ($reference in $xml.SelectNodes('//ProjectReference')) {
            $target = Assert-InputPath (Join-Path $directory $reference.Include)
            if (-not $projects.Contains($target)) { throw "Review the changed imported project reference: $target" }
        }
        foreach ($node in $xml.SelectNodes('//Compile[@Include or @Update] | //EmbeddedResource[@Include] | //AvaloniaResource[@Include] | //AdditionalFiles[@Include] | //Content[@Include] | //None[@Include] | //Analyzer[@Include] | //EditorConfigFiles[@Include] | //GlobalAnalyzerConfigFiles[@Include] | //Import[@Project] | //ApplicationManifest | //ApplicationIcon | //Copy[@SourceFiles]')) {
            $expression = if ($node.LocalName -eq 'Import') { [string]$node.Project } elseif ($node.LocalName -in @('ApplicationManifest', 'ApplicationIcon')) {
                [string]$node.InnerText
            } elseif ($node.LocalName -eq 'Copy') { [string]$node.SourceFiles } elseif ($node.Include) { [string]$node.Include } else { [string]$node.Update }
            foreach ($part in $expression.Split(';', [StringSplitOptions]::RemoveEmptyEntries)) {
                # This is the actual project-generated MockGame resource, whose source project is in the graph.
                if ($part -eq '..\Ksp2Redux.Tools.Launcher.Tests.MockGame\bin\$(Configuration)\net10.0\MockGame.dll' -and
                    $node.LocalName -eq 'EmbeddedResource' -and
                    $projects.Contains([IO.Path]::GetFullPath((Join-Path $rootPath 'tests/Ksp2Redux.Tools.Launcher.Tests.MockGame/Ksp2Redux.Tools.Launcher.Tests.MockGame.csproj')))) {
                    $generated.Add(@{Project=[IO.Path]::GetRelativePath($rootPath, $project).Replace('\','/'); Expression=$part; Producer='tests/Ksp2Redux.Tools.Launcher.Tests.MockGame/Ksp2Redux.Tools.Launcher.Tests.MockGame.csproj'})
                    continue
                }
                if ($part -match '[$@]') { throw "Unresolved source input expression in ${project}: $part" }
                $pattern = [IO.Path]::GetRelativePath($rootPath, (Assert-InputPath (Join-Path $directory $part))).Replace('\','/')
                if ($part -match '[*?]') {
                    $prefix = $part.Substring(0, $part.IndexOfAny([char[]]'*?'))
                    $globRoot = Assert-InputPath ([IO.Path]::GetDirectoryName((Join-Path $directory ($prefix + '__glob__'))))
                    Add-InputTree $globRoot
                    $matchedFiles = @($files.Keys | Where-Object { $_ -like $pattern })
                    if (-not $matchedFiles.Count) { throw "Declared input glob has no fenced source: $pattern" }
                } else {
                    $matchedFiles = @((Add-InputFile (Join-Path $directory $part)))
                }
                $declarations.Add(@{Project=[IO.Path]::GetRelativePath($rootPath,$project).Replace('\','/'); Item=$node.LocalName; Expression=$part; Files=$matchedFiles})
                if ($node.LocalName -eq 'Import') {
                    foreach ($name in $matchedFiles) { $controlQueue.Enqueue((Join-Path $rootPath $name)) }
                }
            }
        }
    }
    $editorConfig = Join-Path $rootPath '.editorconfig'
    if ([IO.File]::ReadAllText($editorConfig) -notmatch '(?im)^\s*root\s*=\s*true\s*$') {
        throw 'Review inherited analyzer configuration when the Tools root editorconfig boundary changes.'
    }
    return [pscustomobject]@{Contract='omnipatching.tools-build-source-inputs.v2'; Pins=$files;
        Projects=@($projects | ForEach-Object { [IO.Path]::GetRelativePath($rootPath,$_).Replace('\','/') } | Sort-Object);
        RootAndAncestorFiles=@($rootFiles | Sort-Object); Declarations=@($declarations.ToArray()); GeneratedResources=@($generated.ToArray()); ExcludedDirectories=$excluded}
}

if ($SourceInputsOnly) {
    $inputRoot = if ($SourceInputRoot) { $SourceInputRoot } else { Join-Path $PSScriptRoot '..' }
    Get-FullSourceInputSnapshot -Root $inputRoot | ConvertTo-Json -Depth 12 -Compress
    exit 0
}
if ($SourceInputRoot) { throw 'SourceInputRoot is available only for read-only source inspection.' }
$toolsRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$checkoutRoot = [IO.Path]::GetFullPath((Join-Path $toolsRoot '..'))
$evidenceParent = Join-Path $checkoutRoot 'Library/Omnipatching/launcher-preparation'
$runRoot = Join-Path $evidenceParent ('tests-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N'))
if ($EvidenceRunRoot) {
    $runRoot = [IO.Path]::GetFullPath($EvidenceRunRoot)
    if ([IO.Path]::GetDirectoryName($runRoot) -ne [IO.Path]::GetFullPath($evidenceParent) -or
        [IO.Path]::GetFileName($runRoot) -notmatch '^tests-current-fi31-[a-f0-9]{32}$' -or
        (Test-Path -LiteralPath $runRoot)) { throw 'Use a fresh explicit tests-current-fi31 evidence directory.' }
}
$started = [DateTime]::UtcNow
if ($FrozenReleasePackRun) {
    $FrozenReleasePackRun = [IO.Path]::GetFullPath($FrozenReleasePackRun)
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $checkoutRoot 'Library/Omnipatching')) + [IO.Path]::DirectorySeparatorChar
    if (-not $FrozenReleasePackRun.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath (Join-Path $FrozenReleasePackRun 'result.json'))) {
        throw 'Frozen input must identify a retained release-pack run below this checkout Library/Omnipatching.'
    }
}
[void](New-Item -ItemType Directory -Path $runRoot)
$priorProbe = $env:KSP2REDUX_PREPARATION_PROBE
$priorScaleProbe = $env:KSP2REDUX_FULL_SCALE_TRANSACTION_PROBE
$priorCrossVolume = $env:KSP2REDUX_CROSS_VOLUME_FIXTURE_ROOT
$results = [Collections.Generic.List[object]]::new()
$failure = $null
$exitCode = 0
$sourcePins = @{}
$changedInputs = @()
try {
    $feedbackPath = if ($ParentFeedbackPath) { [IO.Path]::GetFullPath($ParentFeedbackPath) } elseif ($FullScaleArchiveReceipt -or $FullScaleProbeConfig) {
        Join-Path $checkoutRoot '../.agents/omnipatching-work/launcher-fullscale-continuation.feedback.md'
    } else { Join-Path $checkoutRoot '../.agents/omnipatching-work/launcher-delivery-host-closure.feedback.md' }
    $feedbackHash = if (Test-Path -LiteralPath $feedbackPath) { (Get-FileHash -LiteralPath $feedbackPath).Hash } else { $null }
    if ($CrossVolumeFixtureRoot) {
        $CrossVolumeFixtureRoot = [IO.Path]::GetFullPath($CrossVolumeFixtureRoot)
        if ((Split-Path -Leaf $CrossVolumeFixtureRoot) -notmatch '^redux-publication-test-[a-f0-9]{32}$' -or
            (Test-Path -LiteralPath $CrossVolumeFixtureRoot) -or
            [IO.Path]::GetPathRoot($CrossVolumeFixtureRoot) -eq [IO.Path]::GetPathRoot($checkoutRoot)) {
            throw 'Cross-volume verification requires a fresh redux-publication-test-<32 hex digits> root on another volume.'
        }
    }
    $env:KSP2REDUX_CROSS_VOLUME_FIXTURE_ROOT = $CrossVolumeFixtureRoot
    if ($FullScaleArchiveReceipt) {
        if ($FullScaleProbeConfig -or -not $FullScaleMeasurements) { throw 'New full-scale preparation requires both pinned input receipts and no resume config.' }
        $FullScaleProbeConfig = Join-Path $runRoot 'fullscale-probe.json'
        [ordered]@{
            ArchiveReceiptPath = [IO.Path]::GetFullPath($FullScaleArchiveReceipt)
            ArchiveReceiptSha256 = (Get-FileHash -LiteralPath $FullScaleArchiveReceipt).Hash
            MeasurementsPath = [IO.Path]::GetFullPath($FullScaleMeasurements)
            MeasurementsSha256 = (Get-FileHash -LiteralPath $FullScaleMeasurements).Hash
            OutputRoot = (Join-Path $evidenceParent ('fullscale-' + [Guid]::NewGuid().ToString('N')))
            ParentFeedbackPath = [IO.Path]::GetFullPath($feedbackPath); ParentFeedbackSha256 = $feedbackHash
        } | ConvertTo-Json | Set-Content -LiteralPath $FullScaleProbeConfig -Encoding utf8NoBOM
    }
    if ($FullScaleProbeConfig) { $FullScaleProbeConfig = [IO.Path]::GetFullPath($FullScaleProbeConfig) }
    $env:KSP2REDUX_FULL_SCALE_TRANSACTION_PROBE = $FullScaleProbeConfig
    [ordered]@{ ProcessId = $PID; StartedUtc = $started.ToString('o'); ParentFeedbackSha256 = $feedbackHash; FullScaleProbeConfig = $FullScaleProbeConfig } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'owner.json') -Encoding utf8
    $env:KSP2REDUX_PREPARATION_PROBE = $FrozenReleasePackRun
    Push-Location -LiteralPath $toolsRoot
    try {
        $sourceSnapshot = Get-FullSourceInputSnapshot -Root $toolsRoot
        $sourcePins = $sourceSnapshot.Pins
        $sourcePins | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'source-inputs.json') -Encoding utf8
        $jobs = @(@{
            Name = 'launcher'
            Project = 'tests/Ksp2Redux.Tools.Launcher.Tests/Ksp2Redux.Tools.Launcher.Tests.csproj'
            Filter = if ($TestFilter) { $TestFilter } elseif ($NativeLinux) { 'FullyQualifiedName~NativeLinux_' } elseif ($Scope -eq 'Focused') {
                'FullyQualifiedName~FullSourcePreparationServiceTest|FullyQualifiedName~FullSourceFeedTransportTest|FullyQualifiedName~ReleaseManifestValidatorTest'
            } else { $null }
        })
        if ($IncludeRecipeTests) {
            $jobs += @{
                Name = 'recipes'
                Project = 'tests/Ksp2Redux.Tools.BundleConversion.Tests/Ksp2Redux.Tools.BundleConversion.Tests.csproj'
                Filter = if ($RecipeTestFilter) { $RecipeTestFilter } else { 'FullyQualifiedName~ReleaseRecipePipelineTest' }
            }
        }
        foreach ($job in $jobs) {
            $arguments = @('test', $job.Project, '--no-restore', '--logger', "trx;LogFileName=$($job.Name).trx",
                '--results-directory', $runRoot, '--verbosity', 'minimal')
            if ($IsolatedBuild) { $arguments += @('--disable-build-servers', '-maxcpucount:1', '-nodeReuse:false', '-p:UseSharedCompilation=false', '-p:UsedAvaloniaProducts=', '-p:NuGetAudit=false', '-p:ImportDirectoryBuildTargets=false') }
            if ($NoCompile) { $arguments += '--no-build' }
            if ($job.Filter) { $arguments += @('--filter', $job.Filter) }
            $logPath = Join-Path $runRoot "$($job.Name).log"
            if ($NativeLinux) {
                if ($NoCompile -or $IncludeRecipeTests -or $CrossVolumeFixtureRoot) { throw 'Native Linux uses a fresh focused source fixture without Windows cross-volume or recipe options.' }
                $linuxTools = (& wsl.exe --distribution Ubuntu --exec wslpath -a $toolsRoot).Trim()
                $linuxEvidence = (& wsl.exe --distribution Ubuntu --exec wslpath -a $runRoot).Trim()
                $windowsCache = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'
                $linuxCache = (& wsl.exe --distribution Ubuntu --exec wslpath -a $windowsCache).Trim()
                $arguments = @('--distribution', 'Ubuntu', '--exec', 'python3', "$linuxTools/scripts/test-native-platform.py",
                    '--source', $linuxTools, '--evidence', $linuxEvidence, '--cache', $linuxCache, '--filter', $job.Filter)
                if ($AcquireNativeSdk) { $arguments += '--acquire-sdk' }
                if ($NativeSdkReceipt) { $arguments += @('--sdk-receipt', $NativeSdkReceipt) }
                if ($PublishNativeCli) { $arguments += '--publish-cli' }
                & wsl.exe @arguments 2>&1 | Out-File -LiteralPath $logPath -Encoding utf8
            } else {
                & dotnet @arguments 2>&1 | Out-File -LiteralPath $logPath -Encoding utf8
            }
            $code = $LASTEXITCODE
            $trxPath = Join-Path $runRoot "$($job.Name).trx"
            $counts = $null
            $failedCases = @()
            if (Test-Path -LiteralPath $trxPath) {
                [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
                $counters = $trx.TestRun.ResultSummary.Counters
                $counts = [ordered]@{
                    Total = [int]$counters.total
                    Executed = [int]$counters.executed
                    Passed = [int]$counters.passed
                    Failed = [int]$counters.failed
                    NotExecuted = [int]$counters.notExecuted
                }
                $failedCases = @($trx.SelectNodes('//*[local-name()="UnitTestResult"]') |
                    Where-Object { $_.outcome -eq 'Failed' } | ForEach-Object {
                        [ordered]@{ Name = $_.testName; Message = [string]$_.Output.ErrorInfo.Message }
                    })
            }
            $results.Add([ordered]@{ Name = $job.Name; Command = @(if ($NativeLinux) { 'wsl.exe' } else { 'dotnet' }) + $arguments; ExitCode = $code; Counts = $counts; FailedCases = $failedCases; Log = $logPath; Trx = $trxPath })
            if ($code -ne 0) { $exitCode = 1 }
        }
        $postSourcePins = (Get-FullSourceInputSnapshot -Root $toolsRoot).Pins
        $postSourcePins | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'source-inputs-after.json') -Encoding utf8
        $changedInputs = @(@($sourcePins.Keys) + @($postSourcePins.Keys) | Sort-Object -Unique | Where-Object {
            -not $sourcePins.ContainsKey($_) -or -not $postSourcePins.ContainsKey($_) -or $postSourcePins[$_] -ne $sourcePins[$_]
        })
        if ($changedInputs.Count -ne 0) { $exitCode = 1 }
    } finally { Pop-Location }
} catch {
    $failure = $_.Exception.Message
    $exitCode = 1
} finally {
    $env:KSP2REDUX_PREPARATION_PROBE = $priorProbe
    $env:KSP2REDUX_FULL_SCALE_TRANSACTION_PROBE = $priorScaleProbe
    $env:KSP2REDUX_CROSS_VOLUME_FIXTURE_ROOT = $priorCrossVolume
    $native = @(Get-ChildItem -LiteralPath $evidenceParent -Directory -Filter 'native-*' | ForEach-Object {
        $report = Join-Path $_.FullName 'result.json'
        if ((Test-Path -LiteralPath $report) -and (Get-Item -LiteralPath $report).LastWriteTimeUtc -ge $started) { $report }
    })
    $summary = [ordered]@{
        RunRoot = $runRoot; Scope = $Scope; StartedUtc = $started.ToString('o')
        FrozenReleasePackRun = $FrozenReleasePackRun; ExplicitFixtureAuthority = [bool]($FrozenReleasePackRun -or $FullScaleProbeConfig)
        ProductionAuthenticationProven = $false; UnityCompilationOrGameplayVerified = $false
        Results = @($results.ToArray()); NativeReports = $native; Failure = $failure; ExitCode = $exitCode
        ArchiveAdoptionReports = @(
            $adoptionRoot = Join-Path $checkoutRoot 'Library/Omnipatching/launcher-backup-adoption'
            if (Test-Path -LiteralPath $adoptionRoot) {
                Get-ChildItem -LiteralPath $adoptionRoot -Directory -Filter 'native-*' | ForEach-Object {
                    $report = Join-Path $_.FullName 'archive-adoption-result.json'
                    if ((Test-Path -LiteralPath $report) -and (Get-Item -LiteralPath $report).LastWriteTimeUtc -ge $started) { $report }
                }
            }
        )
        SourceInputContract = 'omnipatching.tools-build-source-inputs.v2'
        SourceInputPins = (Join-Path $runRoot 'source-inputs.json'); ChangedSourceInputs = $changedInputs
        SourceInputPinsSha256 = if (Test-Path -LiteralPath (Join-Path $runRoot 'source-inputs.json')) { (Get-FileHash -LiteralPath (Join-Path $runRoot 'source-inputs.json')).Hash } else { $null }
        PostSourceInputPins = (Join-Path $runRoot 'source-inputs-after.json')
        PostSourceInputPinsSha256 = if (Test-Path -LiteralPath (Join-Path $runRoot 'source-inputs-after.json')) { (Get-FileHash -LiteralPath (Join-Path $runRoot 'source-inputs-after.json')).Hash } else { $null }
        FullScaleProbeConfig = $FullScaleProbeConfig; ParentFeedbackSha256 = $feedbackHash; ParentFeedbackPath = $feedbackPath
        CrossVolumeFixtureRoot = $CrossVolumeFixtureRoot
        NativeLinux = [bool]$NativeLinux
        NativePlatformReport = if (Test-Path -LiteralPath (Join-Path $runRoot 'native-platform.json')) { Join-Path $runRoot 'native-platform.json' } else { $null }
        DeliveryReports = @(Get-ChildItem -LiteralPath $evidenceParent -Directory -Filter 'delivery-*' | ForEach-Object {
            $report = Join-Path $_.FullName 'result.json'
            if ((Test-Path -LiteralPath $report) -and (Get-Item -LiteralPath $report).LastWriteTimeUtc -ge $started) { $report }
        })
        OwnedTlsChildren = @(Get-ChildItem -LiteralPath $evidenceParent -Directory -Filter 'tls-*' | ForEach-Object {
            $ownerPath = Join-Path $_.FullName 'owner.json'
            if ((Test-Path -LiteralPath $ownerPath) -and (Get-Item -LiteralPath $ownerPath).LastWriteTimeUtc -ge $started) {
                $child = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
                $exitPath = Join-Path $_.FullName 'exit.json'
                [ordered]@{
                    Owner = $ownerPath; ProcessId = $child.ProcessId; StartedUtc = $child.StartedUtc; Executable = $child.Executable
                    ExitRecorded = (Test-Path -LiteralPath $exitPath)
                    ExitCode = if (Test-Path -LiteralPath $exitPath) { (Get-Content -LiteralPath $exitPath -Raw | ConvertFrom-Json).ExitCode } else { $null }
                }
            }
        })
        FullScaleReports = if ($FullScaleProbeConfig -and (Test-Path -LiteralPath $FullScaleProbeConfig)) {
            $scale = Get-Content -LiteralPath $FullScaleProbeConfig -Raw | ConvertFrom-Json
            $report = Join-Path $scale.OutputRoot 'result.json'
            if (Test-Path -LiteralPath $report) { @($report) } else { @() }
        } else { @() }
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runRoot 'summary.json') -Encoding utf8
    $summary | ConvertTo-Json -Depth 8
}
exit $exitCode
