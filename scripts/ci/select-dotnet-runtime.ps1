#Requires -Version 7.0
<#
.SYNOPSIS
    Removes setup-dotnet's extra LTS runtime from a fresh, owned CI SDK tree.
.DESCRIPTION
    setup-dotnet v6 installs the latest LTS CoreCLR before the requested SDK.
    Keep only the selected SDK/shared-framework line so roll-forward tests cannot
    use that unrelated runtime. Native host/fxr files are deliberately untouched.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('8.0', '9.0', '10.0')]
    [string]$ExpectedDotnet,
    [string]$InstallRoot = $env:DOTNET_INSTALL_DIR,
    [string]$RunnerTemp = $env:RUNNER_TEMP
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-PhysicalDirectory {
    param([string]$Path)
    $item = Get-Item -LiteralPath $Path -Force
    if (-not $item.PSIsContainer -or
        ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The isolated SDK tree must contain physical directories, never links or reparse points.'
    }
    return $item
}

if ([string]::IsNullOrWhiteSpace($InstallRoot) -or [string]::IsNullOrWhiteSpace($RunnerTemp)) {
    throw 'DOTNET_INSTALL_DIR and RUNNER_TEMP must identify the fresh CI installation.'
}
$root = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($InstallRoot))
$temp = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($RunnerTemp))
$comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$null = Assert-PhysicalDirectory $temp
$null = Assert-PhysicalDirectory $root
$ancestor = [System.IO.DirectoryInfo]::new($root)
while ($null -ne $ancestor) {
    $null = Assert-PhysicalDirectory $ancestor.FullName
    $ancestor = $ancestor.Parent
}
$parent = [System.IO.Directory]::GetParent($root)
if ($null -eq $parent -or -not $parent.FullName.Equals($temp, $comparison) -or
    [System.IO.Path]::GetFileName($root) -notin @('dotnet-compatibility', 'vouchfx-release-dotnet', 'vouchfx-suite-dotnet')) {
    throw 'Refusing an SDK installation outside the known, directly owned RUNNER_TEMP directories.'
}

$shared = Join-Path $root 'shared'
$sdk = Join-Path $root 'sdk'
$null = Assert-PhysicalDirectory $shared
$null = Assert-PhysicalDirectory $sdk
$containers = @($sdk)
foreach ($framework in Get-ChildItem -LiteralPath $shared -Force) {
    $null = Assert-PhysicalDirectory $framework.FullName
    if ($framework.Name -notin @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App', 'Microsoft.WindowsDesktop.App')) {
        throw 'Unexpected shared-framework directory in the isolated SDK tree.'
    }
    $containers += $framework.FullName
}
$required = @('sdk', 'Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')
$selected = @{}
$removals = [System.Collections.Generic.List[string]]::new()

# Validate the entire plan before removing anything. In particular, a junction
# in a later framework must not allow an earlier runtime to be partly removed.
foreach ($container in $containers) {
    $containerName = [System.IO.Path]::GetFileName($container)
    foreach ($version in Get-ChildItem -LiteralPath $container -Force) {
        $null = Assert-PhysicalDirectory $version.FullName
        if ($version.Name -notmatch '^\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?$') {
            throw 'Unexpected SDK/runtime version directory in the isolated tree.'
        }
        $target = [System.IO.Path]::GetFullPath($version.FullName)
        if (-not [System.IO.Directory]::GetParent($target).FullName.Equals($container, $comparison) -or
            -not $target.StartsWith($root + [System.IO.Path]::DirectorySeparatorChar, $comparison)) {
            throw 'Refusing an SDK/runtime path outside the isolated installation.'
        }
        if ($version.Name.StartsWith($ExpectedDotnet + '.', [StringComparison]::Ordinal)) {
            $selected[$containerName] = $true
        } else {
            foreach ($descendant in Get-ChildItem -LiteralPath $target -Recurse -Force) {
                if (($descendant.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw 'Refusing recursive removal of an SDK/runtime containing a link or reparse point.'
                }
            }
            $removals.Add($target)
        }
    }
}
foreach ($name in $required) {
    if (-not $selected.ContainsKey($name)) {
        throw "The selected .NET $ExpectedDotnet SDK and both shared frameworks must be installed before isolation."
    }
}

foreach ($target in $removals) {
    # Recheck immediately before the native filesystem mutation; no shell-built
    # commands or recursive traversal of reparse points are permitted.
    $null = Assert-PhysicalDirectory $target
    Remove-Item -LiteralPath $target -Recurse -Force
    Write-Host "Removed unselected CI SDK/runtime: $([System.IO.Path]::GetRelativePath($root, $target))"
}
Write-Host "Isolated SDK/shared frameworks retain only .NET $ExpectedDotnet."
