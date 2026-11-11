# Publica InstallerKeeper: compilación autocontenida y, opcionalmente, release de GitHub.
#
# Uso (desde la raíz del repositorio):
#   .\scripts\Publish-InstallerKeeper.ps1                # publicación local en artifacts\
#   .\scripts\Publish-InstallerKeeper.ps1 -Release       # además crea tag y release (pide confirmación)
#
# El release solo se crea para versiones depuradas: el script exige escribir la versión exacta para confirmarlo.

[CmdletBinding()]
param(
    [switch]$Release,
    # Con -SinceVersion, las notas incluyen todas las secciones del CHANGELOG posteriores a esa versión (hasta la actual).
    [string]$SinceVersion = '',
    [string]$Repo = 'elkekito/InstallerKeeper'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root 'src\InstallerKeeper\InstallerKeeper.csproj'
$changelog = Join-Path $root 'CHANGELOG.md'

[xml]$project = Get-Content -LiteralPath $csproj -Raw
$version = $project.Project.PropertyGroup | ForEach-Object { $_.InformationalVersion } | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No se encontró <InformationalVersion> en el .csproj.' }
$tag = "v$version"
$publishDir = Join-Path $root "artifacts\publish\$version\win-x64"

Write-Host "Versión del proyecto: $version"

Write-Host 'Publicando ejecutable autocontenido...'
dotnet publish $csproj --configuration Release -p:PublishProfile=WinX64 "-p:PublishDir=$publishDir"
if ($LASTEXITCODE -ne 0) { throw 'La publicación ha fallado.' }

$exe = Join-Path $publishDir 'InstallerKeeper.exe'
$exeVersion = (Get-Item $exe).VersionInfo.ProductVersion
$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
Write-Host "Ejecutable: $exe"
Write-Host "Versión incrustada: $exeVersion"
Write-Host "SHA-256: $hash"

if (-not $Release) {
    Write-Host 'Publicación local lista. No se ha creado ningún release (usa -Release cuando la versión esté depurada).'
    return
}

# Solo se publican las secciones del CHANGELOG que correspondan: la de esta versión, o todas las
# posteriores a -SinceVersion (versiones sin release intermedias) hasta la actual.
$text = (Get-Content -LiteralPath $changelog -Raw -Encoding UTF8) -replace "`r`n", "`n"
$sections = [regex]::Matches($text, '(?ms)^## \[(\d+\.\d+\.\d+)\].*?(?=^---\s*$|^## \[|\z)') |
    Where-Object { ([version]$_.Groups[1].Value) -le ([version]$version) -and
        ((-not $SinceVersion) -and $_.Groups[1].Value -eq $version -or
         ($SinceVersion -and ([version]$_.Groups[1].Value) -gt ([version]$SinceVersion))) }
if (-not ($sections | Where-Object { $_.Groups[1].Value -eq $version })) { throw "No hay sección [$version] en CHANGELOG.md." }
$notes = (($sections | ForEach-Object { $_.Value.Trim() }) -join "`n`n---`n`n") + "`n"

$confirmation = Read-Host "Confirma que la versión $version está depurada y probada escribiéndola de nuevo"
if ($confirmation -ne $version) { throw 'Confirmación no válida; no se crea el release.' }

$notesFile = Join-Path $env:TEMP "ik-release-notes-$version.md"
[IO.File]::WriteAllText($notesFile, $notes, (New-Object Text.UTF8Encoding($false)))
try {
    $commit = git -C $root rev-parse HEAD
    gh release create $tag $exe --repo $Repo --target $commit --title "InstallerKeeper $tag" --notes-file $notesFile --latest
    if ($LASTEXITCODE -ne 0) { throw 'gh release create ha fallado.' }
    # Solo el último release conserva el ejecutable; los anteriores se quedan sin binario.
    gh release list --repo $Repo --limit 100 --json tagName --jq '.[].tagName' |
        Where-Object { $_ -ne $tag } |
        ForEach-Object { gh release delete-asset $_ 'InstallerKeeper.exe' --repo $Repo --yes }
} finally {
    Remove-Item -LiteralPath $notesFile -ErrorAction SilentlyContinue
}

$remote = gh release view $tag --repo $Repo --json tagName,isDraft,assets --jq '{tagName, isDraft, assets: [.assets[] | {name, digest}]}'
Write-Host "Release comprobado en GitHub: $remote"
