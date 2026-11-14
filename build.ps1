<#
  Compile Ma Passerelle.
    build.cmd [-Run|-Msix]  -> même chose depuis l'invite de commandes (cmd)
    .\build.ps1            -> version autonome (dossier dist\MaPasserelle, aucun prérequis), puis la lance avec -Run
    .\build.ps1 -Msix      -> paquet MSIX non signé (dist\msix) pour le Microsoft Store
  Les deux exécutables voyagent ensemble : MaPasserelle.App.exe (l'application) et mapasserelle.exe
  (la ligne de commande, alias « mapasserelle » dans la version paquet).
#>
param([switch]$Run, [switch]$Msix, [ValidateSet('x64', 'ARM64')][string]$Platform = 'x64')
$ErrorActionPreference = 'Stop'
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'

$root = $PSScriptRoot
$project = Join-Path $root 'src\MaPasserelle.App\MaPasserelle.App.csproj'
$rid = "win-$($Platform.ToLower())"
$common = @('-c', 'Release', "-p:Platform=$Platform", '-r', $rid, '-nologo', '-v', 'q', '-clp:NoSummary')

# Version unique : <Version> de Directory.Build.props, reportée dans le manifeste MSIX.
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$manifest = Join-Path $root 'src\MaPasserelle.App\Package.appxmanifest'
$text = [IO.File]::ReadAllText($manifest)
$synced = [regex]::Replace($text, '(<Identity [^>]*Version=")[^"]+(")', "`${1}$version.0`${2}")
if ($synced -ne $text) { [IO.File]::WriteAllText($manifest, $synced, (New-Object Text.UTF8Encoding $true)) }
"Ma Passerelle $version"

if ($Msix) {
    $dist = Join-Path $root 'dist\msix'
    Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
    # -m:1 : Engine est compilé deux fois (pour l'application et pour la ligne de commande) ; en parallèle,
    # les deux écrivent le même fichier et la compilation échoue (« fichier utilisé par un autre processus »).
    & $dotnet publish $project @common '-m:1' '-p:WindowsPackageType=MSIX' '-p:GenerateAppxPackageOnBuild=true' `
        '-p:AppxPackageSigningEnabled=false' "-p:AppxPackageDir=$dist/" '-p:UapAppxPackageBuildMode=StoreUpload' '-p:AppxBundle=Never'
    if ($LASTEXITCODE -ne 0) { throw 'Échec de la création du paquet MSIX' }
    # Le paquet à téléverser (les dépendances du runtime sont fournies par le Store).
    Get-ChildItem $dist -Recurse -Include *.msix, *.msixupload | Where-Object { $_.DirectoryName -notlike '*Dependencies*' } |
        ForEach-Object { "Paquet : $($_.FullName)" }
    return
}

$dist = Join-Path $root 'dist\MaPasserelle'
Get-Process MaPasserelle.App -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dist\*" } | ForEach-Object { $_.Kill(); $_.WaitForExit(4000) | Out-Null }
"Compilation en cours (1 à 2 minutes, sans affichage)…"
& $dotnet publish $project @common '-o' $dist
if ($LASTEXITCODE -ne 0) { throw 'Échec de la compilation' }
"Application : $dist\MaPasserelle.App.exe"
if ($Run) { Start-Process (Join-Path $dist 'MaPasserelle.App.exe'); 'Lancée.' } else { 'Pour la lancer : build.cmd -Run' }
