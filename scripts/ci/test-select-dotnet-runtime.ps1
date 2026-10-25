#Requires -Version 7.0
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$selector = Join-Path $PSScriptRoot 'select-dotnet-runtime.ps1'
$testTemp = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()))
$scratch = Join-Path $testTemp ('vouchfx-dotnet-selector-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $scratch
$links = [System.Collections.Generic.List[string]]::new()
$passed = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}
function Add-Version {
    param([string]$Root, [string]$Container, [string]$Version)
    $path = Join-Path (Join-Path $Root $Container) $Version
    $null = New-Item -ItemType Directory -Force -Path $path
    [System.IO.File]::WriteAllText((Join-Path $path 'sentinel.txt'), 'retain exact bytes')
    return $path
}
function New-Fixture {
    param([string]$Name, [string]$Line = '8.0', [string]$RootName = 'dotnet-compatibility', [switch]$OmitCore)
    $temp = Join-Path (Join-Path $scratch $Name) 'runner-temp'
    $root = Join-Path $temp $RootName
    $null = Add-Version $root 'sdk' "$Line.426"
    if (-not $OmitCore) { $null = Add-Version $root 'shared/Microsoft.NETCore.App' "$Line.32" }
    $null = Add-Version $root 'shared/Microsoft.AspNetCore.App' "$Line.32"
    $null = Add-Version $root 'host/fxr' '10.0.13'
    return [pscustomobject]@{ Root = $root; Temp = $temp; Line = $Line }
}
function Invoke-Selector {
    param($Fixture)
    & $selector -ExpectedDotnet $Fixture.Line -InstallRoot $Fixture.Root -RunnerTemp $Fixture.Temp
}
function Assert-Refused {
    param($Fixture)
    $refused = $false
    try { Invoke-Selector $Fixture } catch { $refused = $true }
    Assert-True $refused 'Unsafe or incomplete installation was not refused.'
}
function Add-TestLink {
    param([string]$Path, [string]$Target)
    $kind = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
    $null = New-Item -ItemType $kind -Path $Path -Target $Target
    $links.Add($Path)
}

try {
    foreach ($line in @('8.0', '9.0', '10.0')) {
        $fixture = New-Fixture "valid-$line" $line
        $extra = if ($line -eq '10.0') { '11.0.1' } else { '10.0.13' }
        $removed = @()
        foreach ($container in @('sdk', 'shared/Microsoft.NETCore.App', 'shared/Microsoft.AspNetCore.App')) {
            $removed += Add-Version $fixture.Root $container $extra
        }
        Invoke-Selector $fixture
        foreach ($path in $removed) { Assert-True (-not (Test-Path -LiteralPath $path)) 'Unselected SDK/runtime was retained.' }
        Assert-True (([System.IO.File]::ReadAllText((Join-Path $fixture.Root "shared/Microsoft.NETCore.App/$line.32/sentinel.txt"))) -eq 'retain exact bytes') 'Selected runtime payload changed.'
        Assert-True (Test-Path -LiteralPath (Join-Path $fixture.Root 'host/fxr/10.0.13/sentinel.txt')) 'Native host/fxr payload must remain untouched.'
        $passed++
    }
    $fixture = New-Fixture 'outside-root'
    $fixture.Temp = Join-Path $scratch 'different-runner-temp'
    $null = New-Item -ItemType Directory -Path $fixture.Temp
    Assert-Refused $fixture
    Assert-True (Test-Path -LiteralPath (Join-Path $fixture.Root 'sdk/8.0.426/sentinel.txt')) 'Outside installation changed.'
    $passed++

    $fixture = New-Fixture 'unknown-root' '8.0' 'unrecognised'
    Assert-Refused $fixture
    $passed++

    $fixture = New-Fixture 'missing-selected' -OmitCore
    $foreign = Add-Version $fixture.Root 'shared/Microsoft.NETCore.App' '10.0.13'
    Assert-Refused $fixture
    Assert-True (Test-Path -LiteralPath $foreign) 'Incomplete setup must not trigger partial deletion.'
    $passed++

    $fixture = New-Fixture 'invalid-directory'
    $foreign = Add-Version $fixture.Root 'sdk' '10.0.401'
    $null = Add-Version $fixture.Root 'shared/Microsoft.NETCore.App' 'not-a-version'
    Assert-Refused $fixture
    Assert-True (Test-Path -LiteralPath $foreign) 'An invalid later entry caused partial deletion.'
    $passed++

    $outside = Join-Path $scratch 'outside-link-target'
    $null = New-Item -ItemType Directory -Path $outside
    [System.IO.File]::WriteAllText((Join-Path $outside 'sentinel.txt'), 'never delete link target')

    $fixture = New-Fixture 'linked-version'
    $foreign = Add-Version $fixture.Root 'sdk' '10.0.401'
    Add-TestLink (Join-Path $fixture.Root 'shared/Microsoft.AspNetCore.App/10.0.13') $outside
    Assert-Refused $fixture
    Assert-True (Test-Path -LiteralPath $foreign) 'A later linked runtime caused partial deletion.'
    Assert-True (Test-Path -LiteralPath (Join-Path $outside 'sentinel.txt')) 'External link target was modified.'
    $passed++

    $fixture = New-Fixture 'nested-link'
    $foreign = Add-Version $fixture.Root 'shared/Microsoft.NETCore.App' '10.0.13'
    Add-TestLink (Join-Path $foreign 'nested-link') $outside
    Assert-Refused $fixture
    Assert-True (Test-Path -LiteralPath $foreign) 'A runtime containing a nested link was deleted.'
    $passed++

    $fixture = New-Fixture 'linked-ancestor'
    $link = Join-Path $scratch 'ancestor-link'
    Add-TestLink $link (Split-Path -Parent $fixture.Temp)
    $fixture.Temp = Join-Path $link 'runner-temp'
    $fixture.Root = Join-Path $fixture.Temp 'dotnet-compatibility'
    Assert-Refused $fixture
    $passed++

    $fixture = New-Fixture 'invalid-selector'
    $fixture.Line = '11.0'
    Assert-Refused $fixture
    $passed++

    $workflowPath = Join-Path $PSScriptRoot '../../.github/workflows/vouchfx-run.yml'
    if (Test-Path -LiteralPath $workflowPath) {
        $workflow = [System.IO.File]::ReadAllText($workflowPath)
        $match = [regex]::Match($workflow, '(?ms)^            # BEGIN SHARED RUNTIME SELECTOR\r?\n(?<body>.*?)^            # END SHARED RUNTIME SELECTOR\r?$')
        Assert-True $match.Success 'The reusable workflow must embed its legacy-compatible runtime guard.'
        $lines = $match.Groups['body'].Value -split '\r?\n'
        $actual = (($lines | ForEach-Object { if ($_.StartsWith('            ')) { $_.Substring(12) } else { $_ } }) -join [char]10).TrimEnd()
        $expected = [System.IO.File]::ReadAllText($selector).Replace([string][char]13, '').TrimEnd()
        Assert-True ([StringComparer]::Ordinal.Equals($actual, $expected)) 'The reusable workflow guard differs from the safety-tested script.'
        $passed++
    }
    Write-Host "$passed runtime-isolation regression cases passed."
}
finally {
    # Remove only our link nodes before recursively cleaning the physical test tree.
    foreach ($link in $links) {
        if (Test-Path -LiteralPath $link) { [System.IO.Directory]::Delete($link) }
    }
    $resolved = [System.IO.Path]::GetFullPath($scratch)
    if (-not $resolved.StartsWith($testTemp + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing test cleanup outside the explicit temporary test tree.'
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
