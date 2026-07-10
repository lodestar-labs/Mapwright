<#
.SYNOPSIS
    Retargets a RecoApi host + its module repositories from an old .NET TFM (default net6.0)
    to a new one (default net10.0), resiliently and idempotently. Part A of the migration.

.DESCRIPTION
    This script performs the MECHANICAL, safely-automatable part of the .NET 10 upgrade:

      1. TargetFramework bumps            net6.0 -> net10.0 in every discovered *.csproj
      2. HintPath normalisation           bin\Debug\net6.0\  -> bin\$(Configuration)\$(TargetFramework)\
      3. NuGet package version alignment  a version map (host + all modules use IDENTICAL versions)
      4. appsettings hardening            + TrustServerCertificate=True on SQL connection strings
                                          absolute Modules paths -> environment-independent relative
      5. Two known code edits             Microsoft.OpenApi.Models -> Microsoft.OpenApi (Startup)
                                          ModuleProviderLoader relative->absolute path resolution
      6. Detect-only for the risky bits   Serilog.Sinks.Email 4.x rewrite is FLAGGED, never auto-written
      7. AutoMapper inventory (Part B)    lists every AutoMapper Profile / CreateMap / ForMember so a
                                          human can convert to Mapperly. NOT auto-converted.

    It is DISCOVERY-BASED: it does not assume any particular module count or folder names, so a target
    repo with a different number of modules (or an extra auth layer) is handled the same way. It is
    IDEMPOTENT: re-running on an already-migrated tree reports "already done" and changes nothing.

    DRY-RUN IS THE DEFAULT. Nothing is written unless you pass -Apply.

.PARAMETER HostRepo
    Path to the RecoApi host repository (the one containing RECO.API.sln).

.PARAMETER ModulesRepo
    Path to the RecoApiModules repository (Filters/ + Subscribers/). Optional: if the modules live
    inside the host repo, pass the same path or omit it.

.PARAMETER Apply
    Actually write changes. Without this switch the script runs in DRY-RUN mode and only reports.

.PARAMETER FromTfm / ToTfm
    Source and target framework monikers. Defaults: net6.0 -> net10.0.

.PARAMETER Configuration
    Build configuration used for the build gate. Default: Debug.

.PARAMETER BranchName
    Git branch to create/checkout before applying. Default: net10-upgrade. Use -SkipGit to disable.

.PARAMETER ConfigFile
    Optional path to a JSON file overriding the embedded package version map, e.g.
        { "Serilog": "4.3.1", "Swashbuckle.AspNetCore": "10.2.3" }

.PARAMETER SkipGit
    Do not touch git (no branch, no clean-tree check). File-level .recobak backups are still made.

.PARAMETER NoBuild
    Skip the post-migration build gate.

.PARAMETER Rollback
    Restore every *.recobak backup produced by the most recent -Apply run, then exit.

.EXAMPLE
    .\Migrate-RecoApi.ps1 -HostRepo C:\src\RecoApi -ModulesRepo C:\src\RecoApiModules
    # dry run: shows everything it WOULD do, writes nothing.

.EXAMPLE
    .\Migrate-RecoApi.ps1 -HostRepo C:\src\RecoApi -ModulesRepo C:\src\RecoApiModules -Apply
    # creates branch net10-upgrade, backs up, migrates, builds, prints a summary.

.EXAMPLE
    .\Migrate-RecoApi.ps1 -HostRepo C:\src\RecoApi -ModulesRepo C:\src\RecoApiModules -Rollback
    # undoes the last -Apply run from the .recobak files.

.NOTES
    Windows PowerShell 5.1+ compatible. Requires the .NET 10 SDK on PATH for the build gate.
    This script covers PART A only. PART B (AutoMapper -> Mapperly) is bespoke per subscriber and
    must be done by hand using the inventory this script produces. See the HTML guide.
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $HostRepo,
    [string] $ModulesRepo,
    [switch] $Apply,
    [string] $FromTfm = 'net6.0',
    [string] $ToTfm = 'net10.0',
    [string] $Configuration = 'Debug',
    [string] $BranchName = 'net10-upgrade',
    [string] $ConfigFile,
    [switch] $SkipGit,
    [switch] $NoBuild,
    [switch] $Rollback
)

# Fail fast on unhandled errors; we convert expected failures into logged, recoverable events.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# ---------------------------------------------------------------------------------------------------
#  Package version map: host AND every module must resolve shared assemblies to the SAME version,
#  because the plugin loader uses Assembly.LoadFile across a process boundary (the "same-process rule").
#  Wildcards (keys ending in *) let unknown Microsoft.Extensions.* / EF packages in other modules be
#  aligned automatically -- important for target repos whose module set differs from the reference.
# ---------------------------------------------------------------------------------------------------
$script:VersionMap = [ordered]@{
    'Microsoft.Extensions.*'          = '10.0.9'
    'Microsoft.EntityFrameworkCore*'  = '10.0.9'
    'Newtonsoft.Json'                 = '13.0.4'
    'Serilog'                         = '4.3.1'
    'Serilog.AspNetCore'              = '10.0.0'
    'Serilog.Expressions'             = '5.0.0'
    'Serilog.Extensions.Hosting'      = '10.0.0'
    'Serilog.Extensions.Logging'      = '10.0.0'
    'Serilog.Settings.Configuration'  = '10.0.1'
    'Serilog.Sinks.Console'           = '6.1.1'
    'Serilog.Sinks.Email'             = '4.2.1'
    'Serilog.Sinks.File'              = '7.0.0'
    'Serilog.Sinks.MSSqlServer'       = '10.0.0'
    'Swashbuckle.AspNetCore'          = '10.2.3'
    'NUnit'                           = '4.6.1'
    'NUnit3TestAdapter'               = '6.2.0'
    'Microsoft.NET.Test.Sdk'          = '18.7.0'
    'Moq'                             = '4.20.72'
    'Riok.Mapperly'                   = '4.3.1'
}

# ---- Run state ------------------------------------------------------------------------------------
$script:Stamp       = (Get-Date).ToString('yyyyMMdd-HHmmss')
$script:LogFile     = Join-Path $HostRepo ("migrate-recoapi-{0}.log" -f $script:Stamp)
$script:Backups     = New-Object System.Collections.Generic.List[string]
$script:Warnings    = New-Object System.Collections.Generic.List[string]
$script:Errors      = New-Object System.Collections.Generic.List[string]
$script:ChangeCount = 0
$script:ManualTodo  = New-Object System.Collections.Generic.List[string]

# ---- Logging --------------------------------------------------------------------------------------
function Write-Log {
    param([string] $Message, [ValidateSet('INFO', 'STEP', 'OK', 'WARN', 'ERROR', 'DRY', 'TODO')] [string] $Level = 'INFO')
    $color = @{ INFO = 'Gray'; STEP = 'Cyan'; OK = 'Green'; WARN = 'Yellow'; ERROR = 'Red'; DRY = 'DarkGray'; TODO = 'Magenta' }[$Level]
    $line = "[{0}] {1,-5} {2}" -f (Get-Date -Format 'HH:mm:ss'), $Level, $Message
    Write-Host $line -ForegroundColor $color
    try { Add-Content -LiteralPath $script:LogFile -Value $line -Encoding UTF8 } catch { }
    if ($Level -eq 'WARN')  { $script:Warnings.Add($Message) | Out-Null }
    if ($Level -eq 'ERROR') { $script:Errors.Add($Message)   | Out-Null }
    if ($Level -eq 'TODO')  { $script:ManualTodo.Add($Message) | Out-Null }
}

function Write-Banner { param([string] $Text)
    Write-Host ''
    Write-Host ("===  {0}  ===" -f $Text) -ForegroundColor White
}

# ---- Encoding-preserving file IO ------------------------------------------------------------------
# Reads text and remembers whether the file had a UTF-8 BOM, so we write it back exactly the same way.
function Read-TextFile {
    param([string] $Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $text = [System.IO.File]::ReadAllText($Path)  # StreamReader strips the BOM if present
    return [pscustomobject]@{ Text = $text; HasBom = $hasBom }
}

function Save-TextFile {
    param([string] $Path, [string] $Text, [bool] $HasBom)
    $enc = New-Object System.Text.UTF8Encoding($HasBom)
    [System.IO.File]::WriteAllText($Path, $Text, $enc)
}

# Back up a file once per run (before its first edit) so -Rollback can restore it.
function Backup-Once {
    param([string] $Path)
    $bak = "$Path.recobak"
    if (-not (Test-Path -LiteralPath $bak)) {
        Copy-Item -LiteralPath $Path -Destination $bak -Force
        $script:Backups.Add($Path) | Out-Null
    }
}

# Central write gate: honours dry-run, backs up, preserves encoding, counts changes.
function Set-FileContent {
    param([string] $Path, [string] $NewText, [bool] $HasBom, [string] $What)
    if ($script:Apply) {
        Backup-Once -Path $Path
        Save-TextFile -Path $Path -Text $NewText -HasBom $HasBom
        Write-Log ("CHANGED  {0}  ({1})" -f (Resolve-Path -LiteralPath $Path -Relative), $What) 'OK'
    }
    else {
        Write-Log ("WOULD    {0}  ({1})" -f $Path, $What) 'DRY'
    }
    $script:ChangeCount++
}

# ---- Native command wrapper ------------------------------------------------------------------------
# Under $ErrorActionPreference='Stop', Windows PowerShell 5.1 turns any REDIRECTED stderr line from a
# native exe (git, dotnet) into a terminating NativeCommandError — e.g. `git rev-parse 2>$null` in a
# non-repo folder would crash the script instead of returning empty. Relax EAP just for these calls;
# $LASTEXITCODE is global and still reflects the command's exit code afterwards.
function Invoke-Native {
    param([scriptblock] $Command)
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Command } finally { $ErrorActionPreference = $old }
}

# ---- Preflight ------------------------------------------------------------------------------------
function Test-Preflight {
    Write-Banner "Preflight"
    $ok = $true

    # dotnet + the target SDK
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { Write-Log "dotnet CLI not found on PATH. Install the .NET 10 SDK." 'ERROR'; $ok = $false }
    else {
        $sdks = Invoke-Native { & dotnet --list-sdks 2>$null }
        $tfmNum = ($ToTfm -replace '[^\d.]', '')   # net10.0 -> 10.0
        $major = $tfmNum.Split('.')[0]
        if (-not ($sdks | Select-String -SimpleMatch "$major.")) {
            Write-Log ("No .NET {0} SDK detected (dotnet --list-sdks). The build gate will fail." -f $major) 'WARN'
        }
        else { Write-Log ("Found .NET {0} SDK." -f $major) 'OK' }
    }

    # repos
    if (-not (Test-Path -LiteralPath $HostRepo)) { Write-Log "HostRepo not found: $HostRepo" 'ERROR'; $ok = $false }
    if ($ModulesRepo -and -not (Test-Path -LiteralPath $ModulesRepo)) { Write-Log "ModulesRepo not found: $ModulesRepo" 'ERROR'; $ok = $false }

    if (-not $ok) { return $false }

    # git branch + clean-tree guard (skippable)
    if (-not $SkipGit) {
        foreach ($repo in (Get-Repos)) {
            Push-Location $repo
            try {
                $isGit = Invoke-Native { & git rev-parse --is-inside-work-tree 2>$null }
                if ($LASTEXITCODE -ne 0 -or $isGit -ne 'true') { Write-Log "Not a git repo (skipping git for it): $repo" 'WARN'; continue }
                # Ignore this script's own artifacts so they don't count as a "dirty" tree.
                $dirty = Invoke-Native { & git status --porcelain 2>$null } |
                    Where-Object { $_ -notmatch '(migrate-recoapi-.*\.log|automapper-inventory-.*\.csv|\.recobak)\s*$' }
                if ($dirty) {
                    Write-Log "Working tree not clean in $repo. Commit/stash first, or pass -SkipGit." 'ERROR'
                    $ok = $false
                }
                elseif ($script:Apply) {
                    # git checkout reports progress on STDERR ("Switched to a new branch...") — merge it
                    # via Invoke-Native so it can't terminate the script in redirected/CI hosts.
                    $exists = Invoke-Native { & git branch --list $BranchName 2>$null }
                    if ($exists) { Invoke-Native { & git checkout $BranchName 2>&1 } | Out-Null; Write-Log "Checked out existing branch $BranchName in $repo" 'INFO' }
                    else { Invoke-Native { & git checkout -b $BranchName 2>&1 } | Out-Null; Write-Log "Created branch $BranchName in $repo" 'OK' }
                    if ($LASTEXITCODE -ne 0) { Write-Log "git checkout failed in $repo (exit $LASTEXITCODE)." 'ERROR'; $ok = $false }
                }
            } finally { Pop-Location }
        }
    }
    return $ok
}

function Get-Repos {
    $r = @($HostRepo)
    if ($ModulesRepo -and ((Resolve-Path $ModulesRepo).Path -ne (Resolve-Path $HostRepo).Path)) { $r += $ModulesRepo }
    return $r
}

function Get-AllCsproj {
    Get-Repos | ForEach-Object {
        Get-ChildItem -LiteralPath $_ -Recurse -Filter *.csproj -File |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
    }
}

# ---- Load version map overrides -------------------------------------------------------------------
function Import-ConfigOverrides {
    if (-not $ConfigFile) { return }
    if (-not (Test-Path -LiteralPath $ConfigFile)) { Write-Log "ConfigFile not found: $ConfigFile" 'WARN'; return }
    $json = Get-Content -LiteralPath $ConfigFile -Raw | ConvertFrom-Json
    foreach ($p in $json.PSObject.Properties) { $script:VersionMap[$p.Name] = $p.Value }
    Write-Log ("Applied {0} version override(s) from {1}" -f $json.PSObject.Properties.Count, $ConfigFile) 'INFO'
}

function Resolve-TargetVersion {
    param([string] $PackageId)
    if ($script:VersionMap.Contains($PackageId)) { return $script:VersionMap[$PackageId] }
    foreach ($k in $script:VersionMap.Keys) {
        if ($k.EndsWith('*')) {
            $prefix = $k.TrimEnd('*')
            if ($PackageId.StartsWith($prefix)) { return $script:VersionMap[$k] }
        }
    }
    return $null
}

# ---- 1-3. csproj transforms -----------------------------------------------------------------------
function Convert-Csproj {
    param([System.IO.FileInfo] $File)
    $rel = $File.FullName
    try {
        $r = Read-TextFile -Path $File.FullName
        $text = $r.Text
        $orig = $text
        $notes = New-Object System.Collections.Generic.List[string]

        # (1) TargetFramework(s) bump -- handles <TargetFramework> and <TargetFrameworks>
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

        # (2) HintPath normalisation: any hard-coded bin\<cfg>\<tfm>\ -> bin\$(Configuration)\$(TargetFramework)\
        #     Leave already-tokenised paths and unrelated framework folders (e.g. netcoreapp2.2) untouched.
        $hintPattern = "bin\\[^\\]+\\" + [regex]::Escape($FromTfm) + "\\"
        if ($text -match $hintPattern) {
            $text = [regex]::Replace($text, $hintPattern, 'bin\$(Configuration)\$(TargetFramework)\')
            $notes.Add("HintPath -> framework-agnostic") | Out-Null
        }

        # (3) Package versions from the map
        $pkgPattern = '(?<pre><PackageReference\s+Include="(?<id>[^"]+)"\s+Version=")(?<ver>[^"]*)(?<post>")'
        $text = [regex]::Replace($text, $pkgPattern, {
            param($m)
            $id = $m.Groups['id'].Value
            $cur = $m.Groups['ver'].Value
            $target = Resolve-TargetVersion $id
            if ($target -and $target -ne $cur) {
                $notes.Add(("{0} {1}->{2}" -f $id, $cur, $target)) | Out-Null
                return $m.Groups['pre'].Value + $target + $m.Groups['post'].Value
            }
            return $m.Value
        })

        if ($text -ne $orig) {
            Set-FileContent -Path $File.FullName -NewText $text -HasBom $r.HasBom -What ($notes -join '; ')
        }
        else {
            Write-Log ("ok       {0}  (already migrated)" -f $File.Name) 'INFO'
        }
    }
    catch {
        Write-Log ("FAILED to process {0}: {1}" -f $rel, $_.Exception.Message) 'ERROR'
    }
}

# ---- 4. appsettings transforms --------------------------------------------------------------------
function Convert-AppSettings {
    Write-Banner "appsettings hardening"
    $files = Get-Repos | ForEach-Object {
        Get-ChildItem -LiteralPath $_ -Recurse -Filter 'appsettings*.json' -File |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
    }
    foreach ($f in $files) {
        try {
            $r = Read-TextFile -Path $f.FullName
            $text = $r.Text; $orig = $text
            $notes = New-Object System.Collections.Generic.List[string]

            # (a) TrustServerCertificate on any SQL Server connection string that lacks encrypt settings.
            #     Generic: matches any JSON string value that looks like a connection string. This also
            #     covers connection strings an auth layer might add.
            $csPattern = '(?<q>")(?<cs>[^"]*(?:Data Source=|Server=)[^"]*)(?<qe>")'
            $text = [regex]::Replace($text, $csPattern, {
                param($m)
                $cs = $m.Groups['cs'].Value
                if ($cs -match 'TrustServerCertificate' -or $cs -match 'Encrypt\s*=') { return $m.Value }
                $sep = if ($cs.TrimEnd().EndsWith(';')) { '' } else { ';' }
                $notes.Add("connstring +TrustServerCertificate") | Out-Null
                return '"' + $cs + $sep + 'TrustServerCertificate=True;"'
            })

            # (b) Absolute Modules paths -> environment-independent relative (from "Modules\" onward).
            $absPattern = '(?<q>")(?:[A-Za-z]:\\\\|[A-Za-z]:\\)[^"]*?(?<rel>Modules\\{1,2}[^"]*)(?<qe>")'
            $text = [regex]::Replace($text, $absPattern, {
                param($m)
                $relPath = $m.Groups['rel'].Value
                $notes.Add("modules path -> relative ($relPath)") | Out-Null
                return '"' + $relPath + '"'
            })

            if ($text -ne $orig) {
                Set-FileContent -Path $f.FullName -NewText $text -HasBom $r.HasBom -What ($notes -join '; ')
            }
            else { Write-Log ("ok       {0}  (no change)" -f $f.Name) 'INFO' }
        }
        catch { Write-Log ("FAILED appsettings {0}: {1}" -f $f.FullName, $_.Exception.Message) 'ERROR' }
    }
}

# ---- 5. Known code edits + 6. detect-only ---------------------------------------------------------
function Convert-CodeFiles {
    Write-Banner "Code edits (known) + risk detection"
    $csFiles = Get-Repos | ForEach-Object {
        Get-ChildItem -LiteralPath $_ -Recurse -Filter '*.cs' -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
    }
    foreach ($f in $csFiles) {
        try {
            $r = Read-TextFile -Path $f.FullName
            $text = $r.Text; $orig = $text; $notes = New-Object System.Collections.Generic.List[string]

            # (5a) Microsoft.OpenApi.Models -> Microsoft.OpenApi (Swashbuckle 10 / OpenApi 2.x flattening)
            if ($text -match 'using\s+Microsoft\.OpenApi\.Models\s*;') {
                $text = [regex]::Replace($text, 'using\s+Microsoft\.OpenApi\.Models\s*;', 'using Microsoft.OpenApi;')
                $notes.Add("OpenApi.Models->OpenApi") | Out-Null
            }

            # (5b) ModuleProviderLoader: relative path must be resolved to absolute for Assembly.LoadFile.
            if ($f.Name -eq 'ModuleProviderLoader.cs' -and $text -match '_pathToModulesFolder\s*=\s*pathToModulesFolder\s*;') {
                $repl = "_pathToModulesFolder = Path.GetFullPath(pathToModulesFolder, AppContext.BaseDirectory);"
                $text = [regex]::Replace($text, '_pathToModulesFolder\s*=\s*pathToModulesFolder\s*;', $repl)
                $notes.Add("loader path -> absolute") | Out-Null
            }

            if ($text -ne $orig) {
                Set-FileContent -Path $f.FullName -NewText $text -HasBom $r.HasBom -What ($notes -join '; ')
            }

            # (6) DETECT-ONLY: Serilog.Sinks.Email 4.x custom sink needs a manual rewrite (EmailConnectionInfo removed).
            #     A file that already uses EmailSinkOptions/MailKit is the REWRITTEN version — don't re-flag it
            #     (its doc comments may still mention EmailConnectionInfo).
            $alreadyRewritten = ($orig -match 'EmailSinkOptions' -or $orig -match 'MailKit')
            if (-not $alreadyRewritten -and
                ($orig -match 'EmailConnectionInfo' -or ($orig -match 'EmailSink' -and $orig -match 'System\.Net\.Mail'))) {
                Write-Log ("Manual rewrite needed (Serilog.Sinks.Email 4.x -> MailKit/EmailSinkOptions): {0}" -f $f.FullName) 'TODO'
            }
        }
        catch { Write-Log ("FAILED code file {0}: {1}" -f $f.FullName, $_.Exception.Message) 'ERROR' }
    }
}

# ---- 7. AutoMapper inventory (Part B worklist) ----------------------------------------------------
function Invoke-AutoMapperInventory {
    Write-Banner "AutoMapper inventory (Part B -- manual conversion worklist)"
    $hits = New-Object System.Collections.Generic.List[object]
    # NOTE: -Include is unreliable with -LiteralPath + -Recurse in Windows PowerShell 5.1 (it can be
    # silently ignored, scanning every file) — filter by extension explicitly instead.
    $csFiles = Get-Repos | ForEach-Object {
        Get-ChildItem -LiteralPath $_ -Recurse -File |
            Where-Object { ($_.Extension -eq '.cs' -or $_.Extension -eq '.csproj') -and $_.FullName -notmatch '\\(bin|obj)\\' }
    }
    foreach ($f in $csFiles) {
        $lines = @(Get-Content -LiteralPath $f.FullName)
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $ln = $lines[$i]
            if ($ln -match 'AutoMapper|CreateMap|ForMember|:\s*Profile\b|IMapper\b') {
                $hits.Add([pscustomobject]@{ File = $f.FullName; Line = ($i + 1); Text = $ln.Trim() }) | Out-Null
            }
        }
    }
    if ($hits.Count -eq 0) {
        Write-Log "No AutoMapper usage found -- either already on Mapperly, or this repo never used it." 'OK'
        return
    }
    $projects = @($hits | Group-Object { ($_.File -split '\\src\\|\\Subscribers\\|\\Filters\\')[-1].Split('\')[0] })
    Write-Log ("Found {0} AutoMapper reference(s) across {1} area(s). These need manual Mapperly conversion." -f $hits.Count, $projects.Count) 'WARN'
    $out = Join-Path $HostRepo ("automapper-inventory-{0}.csv" -f $script:Stamp)
    $hits | Export-Csv -LiteralPath $out -NoTypeInformation -Encoding UTF8
    Write-Log ("Worklist written: {0}" -f $out) 'INFO'
    Write-Log "Part B is NOT automated: each Profile/CreateMap/ForMember(Ignore) becomes a Mapperly [Mapper] partial with [MapperIgnoreTarget]. See the guide, section 'Part B'." 'TODO'
}

# ---- 8. Build gate --------------------------------------------------------------------------------
function Invoke-BuildGate {
    if ($NoBuild) { Write-Log "Build gate skipped (-NoBuild)." 'INFO'; return $true }
    if (-not $script:Apply) { Write-Log "Build gate skipped in dry-run (nothing was written)." 'DRY'; return $true }
    Write-Banner "Build gate"

    $allGreen = $true
    # Host solution(s) first -- modules reference host DLLs via HintPath.
    $hostSlns = Get-ChildItem -LiteralPath $HostRepo -Recurse -Filter *.sln -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
    foreach ($sln in $hostSlns) {
        Write-Log ("Building host solution: {0}" -f $sln.Name) 'STEP'
        $log = Invoke-Native { & dotnet build $sln.FullName -c $Configuration --nologo 2>&1 }
        $errs = $log | Select-String -Pattern ': error' | Where-Object { $_ -notmatch 'MSB302[0-9]' }
        if ($LASTEXITCODE -ne 0 -or $errs) {
            $allGreen = $false
            Write-Log ("Host build FAILED: {0}" -f $sln.Name) 'ERROR'
            $errs | Select-Object -First 15 | ForEach-Object { Write-Log ("    {0}" -f $_.ToString().Trim()) 'ERROR' }
        }
        else { Write-Log ("Host build OK: {0}" -f $sln.Name) 'OK' }
    }

    # Then each module (its own .sln, or fall back to csproj). Modules deploy into the host on build.
    if ($ModulesRepo) {
        $modSlns = Get-ChildItem -LiteralPath $ModulesRepo -Recurse -Filter *.sln -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
        $buildTargets = if ($modSlns) { $modSlns } else { Get-ChildItem -LiteralPath $ModulesRepo -Recurse -Filter *.csproj -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } }
        foreach ($t in $buildTargets) {
            Write-Log ("Building module: {0}" -f $t.Name) 'STEP'
            $log = Invoke-Native { & dotnet build $t.FullName -c $Configuration --nologo 2>&1 }
            $errs = $log | Select-String -Pattern ': error' | Where-Object { $_ -notmatch 'MSB302[0-9]' }
            if ($LASTEXITCODE -ne 0 -or $errs) {
                $allGreen = $false
                Write-Log ("Module build FAILED: {0}" -f $t.Name) 'ERROR'
                $errs | Select-Object -First 10 | ForEach-Object { Write-Log ("    {0}" -f $_.ToString().Trim()) 'ERROR' }
            }
            else { Write-Log ("Module build OK: {0}" -f $t.Name) 'OK' }
        }
    }
    return $allGreen
}

# ---- Rollback -------------------------------------------------------------------------------------
function Invoke-Rollback {
    Write-Banner "Rollback"
    $baks = Get-Repos | ForEach-Object { Get-ChildItem -LiteralPath $_ -Recurse -Filter '*.recobak' -File }
    if (-not $baks) { Write-Log "No .recobak files found -- nothing to roll back." 'WARN'; return }
    foreach ($b in $baks) {
        $target = $b.FullName -replace '\.recobak$', ''
        Copy-Item -LiteralPath $b.FullName -Destination $target -Force
        Remove-Item -LiteralPath $b.FullName -Force
        Write-Log ("Restored {0}" -f $target) 'OK'
    }
    Write-Log "Rollback complete. (Git branch, if created, was left in place -- delete it manually if desired.)" 'INFO'
}

# ---- Summary --------------------------------------------------------------------------------------
function Write-Summary {
    param([bool] $BuildGreen)
    Write-Banner "Summary"
    Write-Host ("  Mode           : {0}" -f $(if ($script:Apply) { 'APPLY (files written)' } else { 'DRY-RUN (no changes written)' }))
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
    if (-not $script:Apply) {
        Write-Host ''
        Write-Host "  This was a DRY RUN. Re-run with -Apply to write the changes." -ForegroundColor Cyan
    }
}

# ===================================================================================================
#  MAIN
# ===================================================================================================
try {
    $script:Apply = $Apply.IsPresent
    $mode = if ($Rollback) { 'ROLLBACK' } elseif ($script:Apply) { 'APPLY' } else { 'DRY-RUN' }
    Write-Banner ("RecoApi .NET migration  {0} -> {1}   [{2}]" -f $FromTfm, $ToTfm, $mode)
    Write-Log ("Host    : {0}" -f $HostRepo) 'INFO'
    Write-Log ("Modules : {0}" -f $(if ($ModulesRepo) { $ModulesRepo } else { '(none / in host)' })) 'INFO'

    if ($Rollback) { Invoke-Rollback; return }

    Import-ConfigOverrides
    if (-not (Test-Preflight)) { Write-Log "Preflight failed. Aborting before any change." 'ERROR'; exit 2 }

    Write-Banner "Retargeting csproj files"
    $projs = @(Get-AllCsproj)
    Write-Log ("Discovered {0} project(s)." -f $projs.Count) 'INFO'
    foreach ($p in $projs) { Convert-Csproj -File $p }

    Convert-AppSettings
    Convert-CodeFiles
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
    Write-Host "Aborted. If you ran with -Apply, restore with:  .\Migrate-RecoApi.ps1 -HostRepo '$HostRepo' -ModulesRepo '$ModulesRepo' -Rollback" -ForegroundColor Yellow
    exit 3
}
