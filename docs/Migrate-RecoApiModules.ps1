<#
.SYNOPSIS
    Retargets the RecoApi MODULES (subscribers/filters) from an old .NET TFM (default net6.0) to a
    new one (default net10.0) against an ALREADY-MIGRATED host. Modules-only companion to
    Migrate-RecoApi.ps1.

.DESCRIPTION
    In this plugin architecture the host loads every module with Assembly.LoadFile into ONE process,
    so host and modules must agree on the versions of every shared assembly (EF Core,
    Microsoft.Extensions.*, Newtonsoft.Json, ...) -- the "same-process rule". Modules therefore
    upgrade in LOCKSTEP with the host: this script performs the module side, assuming the host repo
    beside it is already on the new TFM.

    Per discovered module .csproj it performs:
      1. TargetFramework bump              net6.0 -> net10.0
      2. HintPath normalisation            bin\<cfg>\net6.0\ -> bin\$(Configuration)\$(TargetFramework)\
      3. Package version alignment         *** derived from the HOST's own csproj files ***
                                           (single source of truth for the same-process rule;
                                            embedded defaults + optional -ConfigFile fill the gaps)
      4. AutoMapper inventory (Part B)     CSV worklist of every Profile/CreateMap/ForMember --
                                           the Mapperly conversion is bespoke and NOT automated.
      5. Build gate                        host DLL presence verified, then each module built;
                                           failures are collected, not fatal (resilient at scale).

    It is DISCOVERY-BASED (works for 4 modules or 400 -- use -ModuleFilter to batch), IDEMPOTENT
    (re-runs report "already migrated"), and DRY-RUN BY DEFAULT (nothing written without -Apply).

.PARAMETER ModulesRepo
    Path to the modules repository (the tree containing Subscribers\ / Filters\).

.PARAMETER HostRepo
    Path to the ALREADY-MIGRATED host repository. Used to (a) verify the host is on the target TFM,
    (b) derive the package version map from its csproj files, and (c) verify the host DLLs modules
    reference actually exist before the build gate.

.PARAMETER ModuleFilter
    Wildcard applied to module project names (e.g. 'RECO.*' or '*Subscriber*'). Default '*' = all.
    With hundreds of subscribers, migrate and build in batches.

.PARAMETER Apply
    Write changes. Without it: dry-run report only.

.PARAMETER FromTfm / ToTfm / Configuration / BranchName / ConfigFile / SkipGit / NoBuild / Rollback
    As in Migrate-RecoApi.ps1. -ConfigFile JSON overrides win over host-derived versions.

.PARAMETER BuildHostFirst
    Build the host solution before the module build gate (modules reference host DLLs by HintPath).

.PARAMETER SkipHostCheck
    Skip the host-TFM preflight (use only if you know what you are doing).

.EXAMPLE
    .\Migrate-RecoApiModules.ps1 -ModulesRepo C:\src\RecoApiModules -HostRepo C:\src\RecoApi
    # dry run over every module; shows derived version map and everything it WOULD change.

.EXAMPLE
    .\Migrate-RecoApiModules.ps1 -ModulesRepo C:\src\RecoApiModules -HostRepo C:\src\RecoApi `
        -ModuleFilter 'RDBES.*' -Apply -BuildHostFirst
    # migrate + build just the RDBES batch.

.NOTES
    Windows PowerShell 5.1+ compatible. Requires the target .NET SDK for the build gate.
    Covers PART A only; PART B (AutoMapper -> Mapperly per subscriber) is a human task driven by the
    inventory CSV -- see the modules upgrade guide.
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $ModulesRepo,
    [Parameter(Mandatory = $true)] [string] $HostRepo,
    [string] $ModuleFilter = '*',
    [switch] $Apply,
    [string] $FromTfm = 'net6.0',
    [string] $ToTfm = 'net10.0',
    [string] $Configuration = 'Debug',
    [string] $BranchName = 'net10-upgrade',
    [string] $ConfigFile,
    [switch] $SkipGit,
    [switch] $NoBuild,
    [switch] $BuildHostFirst,
    [switch] $SkipHostCheck,
    [switch] $Rollback
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# Embedded fallback versions -- used only for packages the host does not reference itself.
# Wildcards (trailing *) align families; host-derived exact versions always win over these.
$script:DefaultMap = [ordered]@{
    'Microsoft.Extensions.*'         = '10.0.9'
    'Microsoft.EntityFrameworkCore*' = '10.0.9'
    'Newtonsoft.Json'                = '13.0.4'
    'Riok.Mapperly'                  = '4.3.1'
}

# ---- Run state ------------------------------------------------------------------------------------
$script:Stamp       = (Get-Date).ToString('yyyyMMdd-HHmmss')
$script:LogFile     = Join-Path $ModulesRepo ("migrate-modules-{0}.log" -f $script:Stamp)
$script:Backups     = New-Object System.Collections.Generic.List[string]
$script:Warnings    = New-Object System.Collections.Generic.List[string]
$script:Errors      = New-Object System.Collections.Generic.List[string]
$script:ChangeCount = 0
$script:ManualTodo  = New-Object System.Collections.Generic.List[string]
$script:HostMap     = @{}   # package id -> version, derived from host csprojs

# ---- Logging --------------------------------------------------------------------------------------
function Write-Log {
    param([string] $Message, [ValidateSet('INFO','STEP','OK','WARN','ERROR','DRY','TODO')] [string] $Level = 'INFO')
    $color = @{ INFO='Gray'; STEP='Cyan'; OK='Green'; WARN='Yellow'; ERROR='Red'; DRY='DarkGray'; TODO='Magenta' }[$Level]
    $line = "[{0}] {1,-5} {2}" -f (Get-Date -Format 'HH:mm:ss'), $Level, $Message
    Write-Host $line -ForegroundColor $color
    try { Add-Content -LiteralPath $script:LogFile -Value $line -Encoding UTF8 } catch { }
    if ($Level -eq 'WARN')  { $script:Warnings.Add($Message)   | Out-Null }
    if ($Level -eq 'ERROR') { $script:Errors.Add($Message)     | Out-Null }
    if ($Level -eq 'TODO')  { $script:ManualTodo.Add($Message) | Out-Null }
}
function Write-Banner { param([string] $Text) Write-Host ''; Write-Host ("===  {0}  ===" -f $Text) -ForegroundColor White }

# ---- Native command wrapper -----------------------------------------------------------------------
# Under $ErrorActionPreference='Stop', Windows PowerShell 5.1 turns any REDIRECTED stderr line from a
# native exe (git/dotnet -- git checkout even reports SUCCESS on stderr) into a terminating error.
# All native calls that redirect or merge stderr must go through this wrapper.
function Invoke-Native {
    param([scriptblock] $Command)
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Command } finally { $ErrorActionPreference = $old }
}

# ---- Encoding-preserving file IO ------------------------------------------------------------------
function Read-TextFile {
    param([string] $Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    [pscustomobject]@{ Text = [System.IO.File]::ReadAllText($Path); HasBom = $hasBom }
}
function Save-TextFile {
    param([string] $Path, [string] $Text, [bool] $HasBom)
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($HasBom)))
}
function Backup-Once {
    param([string] $Path)
    $bak = "$Path.recobak"
    if (-not (Test-Path -LiteralPath $bak)) {
        Copy-Item -LiteralPath $Path -Destination $bak -Force
        $script:Backups.Add($Path) | Out-Null
    }
}
function Set-FileContent {
    param([string] $Path, [string] $NewText, [bool] $HasBom, [string] $What)
    if ($script:Apply) {
        Backup-Once -Path $Path
        Save-TextFile -Path $Path -Text $NewText -HasBom $HasBom
        Write-Log ("CHANGED  {0}  ({1})" -f (Split-Path $Path -Leaf), $What) 'OK'
    }
    else { Write-Log ("WOULD    {0}  ({1})" -f $Path, $What) 'DRY' }
    $script:ChangeCount++
}

# ---- File discovery (NOTE: -Include is unreliable with -LiteralPath+-Recurse in PS 5.1 --
#      filter by extension explicitly) ---------------------------------------------------------------
function Get-SourceFiles {
    param([string] $Root, [string[]] $Extensions)
    Get-ChildItem -LiteralPath $Root -Recurse -File |
        Where-Object { $Extensions -contains $_.Extension -and $_.FullName -notmatch '\\(bin|obj)\\' }
}
function Get-ModuleProjects {
    @(Get-SourceFiles -Root $ModulesRepo -Extensions '.csproj' | Where-Object { $_.BaseName -like $ModuleFilter })
}

# ---- Host version map -----------------------------------------------------------------------------
# The host's csproj files are the single source of truth for shared-package versions: whatever the
# host resolves in-process is what every module MUST also resolve (same-process rule).
function Build-HostVersionMap {
    Write-Banner "Deriving package version map from the host"
    $pkgPattern = '<PackageReference\s+Include="(?<id>[^"]+)"\s+Version="(?<ver>[^"]+)"'
    foreach ($proj in (Get-SourceFiles -Root $HostRepo -Extensions '.csproj')) {
        $text = (Read-TextFile -Path $proj.FullName).Text
        foreach ($m in [regex]::Matches($text, $pkgPattern)) {
            $id = $m.Groups['id'].Value; $ver = $m.Groups['ver'].Value
            if ($script:HostMap.ContainsKey($id) -and $script:HostMap[$id] -ne $ver) {
                Write-Log ("Host itself references {0} at BOTH {1} and {2} -- using {2}; align the host!" -f $id, $script:HostMap[$id], $ver) 'WARN'
            }
            $script:HostMap[$id] = $ver
        }
    }
    Write-Log ("Derived {0} package version(s) from host csproj files." -f $script:HostMap.Count) 'OK'
    # -ConfigFile overrides beat everything
    if ($ConfigFile) {
        if (Test-Path -LiteralPath $ConfigFile) {
            $json = Get-Content -LiteralPath $ConfigFile -Raw | ConvertFrom-Json
            foreach ($p in $json.PSObject.Properties) { $script:HostMap[$p.Name] = $p.Value }
            Write-Log ("Applied {0} override(s) from {1}" -f @($json.PSObject.Properties).Count, $ConfigFile) 'INFO'
        }
        else { Write-Log "ConfigFile not found: $ConfigFile" 'WARN' }
    }
}
function Resolve-TargetVersion {
    param([string] $PackageId)
    if ($script:HostMap.ContainsKey($PackageId)) { return $script:HostMap[$PackageId] }   # host truth
    if ($script:DefaultMap.Contains($PackageId)) { return $script:DefaultMap[$PackageId] }
    foreach ($k in $script:DefaultMap.Keys) {
        if ($k.EndsWith('*') -and $PackageId.StartsWith($k.TrimEnd('*'))) { return $script:DefaultMap[$k] }
    }
    return $null
}

# ---- Preflight ------------------------------------------------------------------------------------
function Test-Preflight {
    Write-Banner "Preflight"
    $ok = $true

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { Write-Log "dotnet CLI not found on PATH." 'ERROR'; $ok = $false }
    else {
        $sdks = Invoke-Native { & dotnet --list-sdks 2>$null }
        $major = ($ToTfm -replace '[^\d.]', '').Split('.')[0]
        if (-not ($sdks | Select-String -SimpleMatch "$major.")) { Write-Log ("No .NET {0} SDK found -- the build gate will fail." -f $major) 'WARN' }
        else { Write-Log ("Found .NET {0} SDK." -f $major) 'OK' }
    }

    foreach ($p in @($ModulesRepo, $HostRepo)) {
        if (-not (Test-Path -LiteralPath $p)) { Write-Log "Path not found: $p" 'ERROR'; $ok = $false }
    }
    if (-not $ok) { return $false }

    # The lockstep guard: modules built against a stale net6 host are the WORST failure mode in a
    # plugin system (they load, then explode at runtime). Refuse to run against an unmigrated host.
    if (-not $SkipHostCheck) {
        $staleHost = @(Get-SourceFiles -Root $HostRepo -Extensions '.csproj' |
            Where-Object { (Read-TextFile -Path $_.FullName).Text -match ("<TargetFrameworks?>[^<]*" + [regex]::Escape($FromTfm)) })
        if ($staleHost.Count -gt 0) {
            Write-Log ("HOST IS NOT MIGRATED: {0} host project(s) still target {1} (e.g. {2}). Migrate/pull the host first, or pass -SkipHostCheck." -f $staleHost.Count, $FromTfm, $staleHost[0].Name) 'ERROR'
            return $false
        }
        Write-Log ("Host check passed: no host project targets {0}." -f $FromTfm) 'OK'
    }

    # git: branch + clean-tree on the MODULES repo only (the host is read-only input here)
    if (-not $SkipGit) {
        Push-Location $ModulesRepo
        try {
            $isGit = Invoke-Native { & git rev-parse --is-inside-work-tree 2>$null }
            if ($LASTEXITCODE -ne 0 -or $isGit -ne 'true') { Write-Log "ModulesRepo is not a git repo -- skipping git handling." 'WARN' }
            else {
                $dirty = Invoke-Native { & git status --porcelain 2>$null } |
                    Where-Object { $_ -notmatch '(migrate-modules-.*\.log|automapper-inventory-.*\.csv|\.recobak)\s*$' }
                if ($dirty) { Write-Log "Working tree not clean in $ModulesRepo. Commit/stash first, or pass -SkipGit." 'ERROR'; $ok = $false }
                elseif ($script:Apply) {
                    $exists = Invoke-Native { & git branch --list $BranchName 2>$null }
                    if ($exists) { Invoke-Native { & git checkout $BranchName 2>&1 } | Out-Null; Write-Log "Checked out existing branch $BranchName" 'INFO' }
                    else { Invoke-Native { & git checkout -b $BranchName 2>&1 } | Out-Null; Write-Log "Created branch $BranchName" 'OK' }
                    if ($LASTEXITCODE -ne 0) { Write-Log "git checkout failed (exit $LASTEXITCODE)." 'ERROR'; $ok = $false }
                }
            }
        } finally { Pop-Location }
    }
    return $ok
}

# ---- Per-module transform -------------------------------------------------------------------------
function Convert-ModuleCsproj {
    param([System.IO.FileInfo] $File)
    try {
        $r = Read-TextFile -Path $File.FullName
        $text = $r.Text; $orig = $text
        $notes = New-Object System.Collections.Generic.List[string]

        # (1) TFM
        $tfmPattern = "(?<open><TargetFrameworks?>)(?<val>[^<]*)(?<close></TargetFrameworks?>)"
        $text = [regex]::Replace($text, $tfmPattern, {
            param($m)
            $val = $m.Groups['val'].Value
            if ($val -match [regex]::Escape($FromTfm)) {
                $notes.Add("TFM $FromTfm->$ToTfm") | Out-Null
                return $m.Groups['open'].Value + ($val -replace [regex]::Escape($FromTfm), $ToTfm) + $m.Groups['close'].Value
            }
            return $m.Value
        })

        # (2) HintPaths: hard-coded bin\<cfg>\<old-tfm>\ -> framework-agnostic tokens.
        #     Unrelated framework folders (e.g. a pinned netcoreapp2.2 utility DLL) are left alone.
        $hintPattern = "bin\\[^\\]+\\" + [regex]::Escape($FromTfm) + "\\"
        if ($text -match $hintPattern) {
            $text = [regex]::Replace($text, $hintPattern, 'bin\$(Configuration)\$(TargetFramework)\')
            $notes.Add("HintPath -> framework-agnostic") | Out-Null
        }

        # (3) Package versions from the host-derived map
        $pkgPattern = '(?<pre><PackageReference\s+Include="(?<id>[^"]+)"\s+Version=")(?<ver>[^"]*)(?<post>")'
        $text = [regex]::Replace($text, $pkgPattern, {
            param($m)
            $id = $m.Groups['id'].Value; $cur = $m.Groups['ver'].Value
            $target = Resolve-TargetVersion $id
            if ($target -and $target -ne $cur) {
                $notes.Add(("{0} {1}->{2}" -f $id, $cur, $target)) | Out-Null
                return $m.Groups['pre'].Value + $target + $m.Groups['post'].Value
            }
            if (-not $target -and $cur -match '^[0-6]\.') {
                # A pre-net10-era version we have no mapping for: surface it, don't guess.
                Write-Log ("{0}: no target version known for {1} {2} -- verify manually (add to -ConfigFile)." -f $File.BaseName, $id, $cur) 'TODO'
            }
            return $m.Value
        })

        if ($text -ne $orig) { Set-FileContent -Path $File.FullName -NewText $text -HasBom $r.HasBom -What ($notes -join '; ') }
        else { Write-Log ("ok       {0}  (already migrated)" -f $File.Name) 'INFO' }
    }
    catch { Write-Log ("FAILED {0}: {1}" -f $File.FullName, $_.Exception.Message) 'ERROR' }
}

# ---- Host DLL presence check (before the build gate) ----------------------------------------------
function Test-HostDlls {
    Write-Banner "Verifying host DLLs referenced by module HintPaths"
    $missing = 0
    foreach ($proj in (Get-ModuleProjects)) {
        $text = (Read-TextFile -Path $proj.FullName).Text
        foreach ($m in [regex]::Matches($text, '<HintPath>(?<p>[^<]+)</HintPath>')) {
            $raw = $m.Groups['p'].Value
            $resolved = $raw.Replace('$(Configuration)', $Configuration).Replace('$(TargetFramework)', $ToTfm)
            $full = Join-Path $proj.DirectoryName $resolved
            if (-not (Test-Path -LiteralPath $full)) {
                Write-Log ("{0}: missing referenced DLL {1}" -f $proj.BaseName, $resolved) 'WARN'
                $missing++
            }
        }
    }
    if ($missing -gt 0) {
        Write-Log ("{0} referenced DLL(s) not found -- build the HOST first (or pass -BuildHostFirst). Module builds will fail with CS0246 'RECO' otherwise." -f $missing) 'WARN'
        return $false
    }
    Write-Log "All module-referenced host DLLs are present." 'OK'
    return $true
}

# ---- AutoMapper inventory (Part B worklist) --------------------------------------------------------
function Invoke-AutoMapperInventory {
    Write-Banner "AutoMapper inventory (Part B -- manual Mapperly conversion worklist)"
    $hits = New-Object System.Collections.Generic.List[object]
    foreach ($f in (Get-SourceFiles -Root $ModulesRepo -Extensions '.cs', '.csproj')) {
        $lines = @(Get-Content -LiteralPath $f.FullName)
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match 'AutoMapper|CreateMap|ForMember|:\s*Profile\b|IMapper\b') {
                $hits.Add([pscustomobject]@{ File = $f.FullName; Line = ($i + 1); Text = $lines[$i].Trim() }) | Out-Null
            }
        }
    }
    if ($hits.Count -eq 0) { Write-Log "No AutoMapper usage found -- modules are already on Mapperly (or never used it)." 'OK'; return }
    $out = Join-Path $ModulesRepo ("automapper-inventory-{0}.csv" -f $script:Stamp)
    $hits | Export-Csv -LiteralPath $out -NoTypeInformation -Encoding UTF8
    Write-Log ("Found {0} AutoMapper reference(s). Worklist: {1}" -f $hits.Count, $out) 'WARN'
    Write-Log "Part B is NOT automated: convert each module's maps to Mapperly (in-place Copy methods + registry). See the modules guide." 'TODO'
}

# ---- Build gate ------------------------------------------------------------------------------------
function Invoke-BuildGate {
    if ($NoBuild) { Write-Log "Build gate skipped (-NoBuild)." 'INFO'; return $true }
    if (-not $script:Apply) { Write-Log "Build gate skipped in dry-run." 'DRY'; return $true }
    Write-Banner "Build gate"

    if ($BuildHostFirst) {
        $hostSln = @(Get-SourceFiles -Root $HostRepo -Extensions '.sln') | Select-Object -First 1
        if ($hostSln) {
            Write-Log ("Building host solution first: {0}" -f $hostSln.Name) 'STEP'
            $log = Invoke-Native { & dotnet build $hostSln.FullName -c $Configuration --nologo 2>&1 }
            $errs = $log | Select-String -Pattern ': error' | Where-Object { $_ -notmatch 'MSB302[0-9]' }
            if ($LASTEXITCODE -ne 0 -or $errs) {
                Write-Log "HOST build failed -- module builds cannot succeed. Fix the host first." 'ERROR'
                $errs | Select-Object -First 10 | ForEach-Object { Write-Log ("    {0}" -f $_.ToString().Trim()) 'ERROR' }
                return $false
            }
            Write-Log "Host build OK." 'OK'
        }
        else { Write-Log "No host .sln found under $HostRepo." 'WARN' }
    }

    [void](Test-HostDlls)

    $okCount = 0; $failCount = 0
    foreach ($proj in (Get-ModuleProjects)) {
        # Prefer the module's own .sln when present (restores exactly what the team uses).
        $slnCandidate = Join-Path $proj.DirectoryName ($proj.BaseName + '.sln')
        $target = if (Test-Path -LiteralPath $slnCandidate) { $slnCandidate } else { $proj.FullName }
        Write-Log ("Building module: {0}" -f $proj.BaseName) 'STEP'
        $log = Invoke-Native { & dotnet build $target -c $Configuration --nologo 2>&1 }
        $errs = $log | Select-String -Pattern ': error' | Where-Object { $_ -notmatch 'MSB302[0-9]' }
        if ($LASTEXITCODE -ne 0 -or $errs) {
            $failCount++
            Write-Log ("Module build FAILED: {0}" -f $proj.BaseName) 'ERROR'
            $errs | Select-Object -First 6 | ForEach-Object { Write-Log ("    {0}" -f $_.ToString().Trim()) 'ERROR' }
            # keep going -- with hundreds of modules one failure must not hide the state of the rest
        }
        else { $okCount++; Write-Log ("Module build OK: {0}" -f $proj.BaseName) 'OK' }
    }
    Write-Log ("Build gate result: {0} OK, {1} failed." -f $okCount, $failCount) $(if ($failCount) { 'WARN' } else { 'OK' })
    return ($failCount -eq 0)
}

# ---- Rollback -------------------------------------------------------------------------------------
function Invoke-Rollback {
    Write-Banner "Rollback"
    $baks = @(Get-ChildItem -LiteralPath $ModulesRepo -Recurse -File | Where-Object { $_.Extension -eq '.recobak' -or $_.Name -like '*.recobak' })
    if (-not $baks) { Write-Log "No .recobak files found -- nothing to roll back." 'WARN'; return }
    foreach ($b in $baks) {
        $target = $b.FullName -replace '\.recobak$', ''
        Copy-Item -LiteralPath $b.FullName -Destination $target -Force
        Remove-Item -LiteralPath $b.FullName -Force
        Write-Log ("Restored {0}" -f $target) 'OK'
    }
    Write-Log "Rollback complete." 'INFO'
}

# ---- Summary --------------------------------------------------------------------------------------
function Write-Summary {
    param([bool] $BuildGreen)
    Write-Banner "Summary"
    Write-Host ("  Mode           : {0}" -f $(if ($script:Apply) { 'APPLY (files written)' } else { 'DRY-RUN (no changes written)' }))
    Write-Host ("  Module filter  : {0}" -f $ModuleFilter)
    Write-Host ("  Files changed  : {0}" -f $script:ChangeCount)
    Write-Host ("  Backups made   : {0}" -f $script:Backups.Count)
    Write-Host ("  Warnings       : {0}" -f $script:Warnings.Count) -ForegroundColor $(if ($script:Warnings.Count) { 'Yellow' } else { 'Gray' })
    Write-Host ("  Errors         : {0}" -f $script:Errors.Count)   -ForegroundColor $(if ($script:Errors.Count) { 'Red' } else { 'Gray' })
    if (-not $NoBuild -and $script:Apply) {
        Write-Host ("  Build gate     : {0}" -f $(if ($BuildGreen) { 'GREEN' } else { 'RED -- see errors above' })) -ForegroundColor $(if ($BuildGreen) { 'Green' } else { 'Red' })
    }
    if ($script:ManualTodo.Count) {
        Write-Host ''
        Write-Host "  MANUAL follow-up required (not automated):" -ForegroundColor Magenta
        $script:ManualTodo | Select-Object -Unique | ForEach-Object { Write-Host ("    - {0}" -f $_) -ForegroundColor Magenta }
    }
    Write-Host ''
    Write-Host ("  Log: {0}" -f $script:LogFile) -ForegroundColor DarkGray
    if (-not $script:Apply) { Write-Host ''; Write-Host "  This was a DRY RUN. Re-run with -Apply to write the changes." -ForegroundColor Cyan }
}

# ===================================================================================================
#  MAIN
# ===================================================================================================
try {
    $script:Apply = $Apply.IsPresent
    $mode = if ($Rollback) { 'ROLLBACK' } elseif ($script:Apply) { 'APPLY' } else { 'DRY-RUN' }
    Write-Banner ("RecoApi MODULES migration  {0} -> {1}   [{2}]" -f $FromTfm, $ToTfm, $mode)
    Write-Log ("Modules : {0}" -f $ModulesRepo) 'INFO'
    Write-Log ("Host    : {0}" -f $HostRepo) 'INFO'

    if ($Rollback) { Invoke-Rollback; return }

    if (-not (Test-Preflight)) { Write-Log "Preflight failed. Aborting before any change." 'ERROR'; exit 2 }
    Build-HostVersionMap

    Write-Banner "Retargeting module csproj files"
    # @() at the CALL SITE: PowerShell unrolls arrays returned from functions, so a single-match
    # -ModuleFilter would otherwise yield a scalar FileInfo with no .Count (StrictMode error).
    $projs = @(Get-ModuleProjects)
    Write-Log ("Discovered {0} module project(s) matching '{1}'." -f $projs.Count, $ModuleFilter) 'INFO'
    if ($projs.Count -eq 0) { Write-Log "Nothing matched -- check -ModulesRepo / -ModuleFilter." 'WARN' }
    foreach ($p in $projs) { Convert-ModuleCsproj -File $p }

    Invoke-AutoMapperInventory
    $green = Invoke-BuildGate

    Write-Summary -BuildGreen $green
    if ($script:Errors.Count -gt 0 -or ($script:Apply -and -not $NoBuild -and -not $green)) { exit 1 }
    exit 0
}
catch {
    Write-Log ("UNHANDLED: {0}" -f $_.Exception.Message) 'ERROR'
    Write-Log ($_.ScriptStackTrace) 'ERROR'
    Write-Host ''
    Write-Host "Aborted. If you ran with -Apply, restore with:  .\Migrate-RecoApiModules.ps1 -ModulesRepo '$ModulesRepo' -HostRepo '$HostRepo' -Rollback" -ForegroundColor Yellow
    exit 3
}
