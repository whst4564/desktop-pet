param([string]$CodexHome)
$ErrorActionPreference='Stop'
if([string]::IsNullOrWhiteSpace($CodexHome)) {
    $CodexHome=$env:CODEX_HOME
    if([string]::IsNullOrWhiteSpace($CodexHome)) {$CodexHome=Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex'}
}
$packageRoot=[IO.Path]::GetFullPath($PSScriptRoot)
$spritesheet=Join-Path $packageRoot 'spritesheet.webp'
$manifest=Join-Path $packageRoot 'pet.json'
if(!(Test-Path -LiteralPath $spritesheet) -or !(Test-Path -LiteralPath $manifest)) {throw 'Missing validated pet package files'}
$metadata=Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
if($metadata.id -ne 'baili' -or $metadata.spriteVersionNumber -ne 2 -or $metadata.spritesheetPath -ne 'spritesheet.webp') {throw 'Unexpected pet manifest'}
$petsRoot=[IO.Path]::GetFullPath((Join-Path $CodexHome 'pets'))
$petTarget=[IO.Path]::GetFullPath((Join-Path $petsRoot 'baili'))
if([IO.Path]::GetDirectoryName($petTarget) -ne $petsRoot){throw 'Unexpected install path'}
New-Item -ItemType Directory -Path $petTarget -Force | Out-Null
foreach($petFile in @('spritesheet.webp','pet.json')) {
    $petDestination=Join-Path $petTarget $petFile
    if(Test-Path -LiteralPath $petDestination) {
        if((Get-FileHash -LiteralPath $petDestination).Hash -ne (Get-FileHash -LiteralPath (Join-Path $packageRoot $petFile)).Hash) {
            Copy-Item -LiteralPath $petDestination -Destination ($petDestination+'.before-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
        }
    }
    Copy-Item -LiteralPath (Join-Path $packageRoot $petFile) -Destination $petDestination
}
Write-Output ('Installed pet package: '+$petTarget)
Write-Output 'Open Codex Settings > Pets, Refresh, and select the new pet.'
