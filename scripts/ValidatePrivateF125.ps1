[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateLength(1, 80)]
    [string] $GameBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

try {
    $modulePath = Join-Path $PSScriptRoot 'PrivateF125Validation.psm1'
    Import-Module -Force -Name $modulePath
    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    $result = Invoke-ApexLabPrivateF125Validation `
        -GameBuild $GameBuild `
        -RepositoryRoot $repositoryRoot
    $result | ConvertTo-Json -Compress
    Write-Output 'ApexLab private F1 25 validation passed.'
    exit 0
}
catch {
    $data = $_.Exception.Data
    if ($null -ne $data `
        -and $data.Contains('ApexLabStage') `
        -and $data.Contains('ApexLabExitCode') `
        -and $data.Contains('ApexLabCorrection')) {
        Write-Output ("stage={0}" -f $data['ApexLabStage'])
        Write-Output ("exitCode={0}" -f $data['ApexLabExitCode'])
        Write-Output ("correction={0}" -f $data['ApexLabCorrection'])
        exit [int]$data['ApexLabExitCode']
    }

    Write-Output 'unexpectedFailure'
    exit 49
}
