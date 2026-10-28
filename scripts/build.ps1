param([string]$Destination)
$ErrorActionPreference='Stop'
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if([string]::IsNullOrWhiteSpace($Destination)){$Destination=Join-Path $repoRoot 'dist'}
$buildDestination=[IO.Path]::GetFullPath($Destination)
$framework=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compiler=Join-Path $framework 'csc.exe'
if(!(Test-Path -LiteralPath $compiler)){throw '.NET Framework 4.x 64-bit compiler was not found.'}
New-Item -ItemType Directory -Path $buildDestination -Force | Out-Null
$sourceNames=@('CompanionApp.cs','DataBridge.cs','AnimationController.cs','MotionInterpolation.cs','MotionPreviewExporter.cs','NativeControlsBridge.cs','CarryMotion.cs','PetViewport.cs','ConversationBridge.cs','CodexConnection.cs','ConversationWindow.cs')
$sourcePaths=$sourceNames | ForEach-Object {Join-Path (Join-Path $repoRoot 'src') $_}
$references=@('WPF/PresentationCore.dll','WPF/PresentationFramework.dll','WPF/WindowsBase.dll','System.Xaml.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll') | ForEach-Object {'/reference:'+(Join-Path $framework $_)}
& $compiler /nologo /langversion:5 /target:library ('/out:'+(Join-Path $buildDestination 'BailiCompanion.dll')) @references @sourcePaths
if($LASTEXITCODE -ne 0){throw 'Library compilation failed.'}
& $compiler /nologo /langversion:5 /target:winexe ('/out:'+(Join-Path $buildDestination 'BailiCompanion.exe')) @references @sourcePaths
if($LASTEXITCODE -ne 0){throw 'Executable compilation failed.'}
$assetDestination=Join-Path $buildDestination 'assets'
New-Item -ItemType Directory -Path $assetDestination -Force | Out-Null
foreach($asset in @('spritesheet.png','thinking-calm.png','pet.ico')){Copy-Item -LiteralPath (Join-Path $repoRoot ('assets/enhanced/'+$asset)) -Destination (Join-Path $assetDestination $asset) -Force}
$settings=Join-Path $buildDestination 'settings.json'
if(!(Test-Path -LiteralPath $settings)){Copy-Item -LiteralPath (Join-Path $repoRoot 'config/settings.example.json') -Destination $settings}
Write-Output ('Built: '+(Join-Path $buildDestination 'BailiCompanion.exe'))
Write-Output 'Existing settings are preserved. This script does not launch the program or change security settings.'
