#requires -Version 7.0
<#
.SYNOPSIS
    Собирает zip-дистрибутив СчитайКа для Synology / Xpenology (Container Manager).

.DESCRIPTION
    Результат — архив, который распаковывается в папку docker НАС и сразу готов
    к созданию проекта в Container Manager:

        /volume1/docker/schitayka/
        ├── docker-compose.yml   (context: ./app — внешние пути не нужны)
        ├── .env                 (сгенерированный JWT_KEY)
        ├── app/                 (исходники + Dockerfile)
        ├── scripts/             (update / backup / restore / init-env)
        └── README.md, VERSION, MANIFEST.txt

.PARAMETER OutputDir
    Куда положить архив. По умолчанию <репозиторий>/dist.

.PARAMETER Version
    Версия дистрибутива. По умолчанию — содержимое файла VERSION.

.PARAMETER RootName
    Имя папки внутри архива (станет именем проекта Container Manager).
    По умолчанию schitayka.

.PARAMETER NoEnv
    Не создавать .env с секретом (для публичных релизов: пользователь запустит
    scripts/init-env.sh сам).

.PARAMETER Image
    Собрать вариант без сборки на NAS: docker-compose.yml будет ссылаться на
    готовый образ (docker-compose.image.yml). Нужен опубликованный GHCR-образ.

.PARAMETER NoApp
    Не собирать артефакт приложения schitayka-<версия>-app.zip.
    Он нужен кнопке «Обновить сейчас» (автоматическое обновление).

.PARAMETER KeepStage
    Не удалять временную папку сборки (для отладки).

.EXAMPLE
    pwsh -File deploy/xpenology/release/build-release.ps1

.EXAMPLE
    pwsh -File deploy/xpenology/release/build-release.ps1 -NoEnv -OutputDir C:\temp
#>
[CmdletBinding()]
param(
    [string]$OutputDir,
    [string]$Version,
    [string]$RootName = 'schitayka',
    [switch]$NoEnv,
    [switch]$Image,
    [switch]$NoApp,
    [switch]$KeepStage
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ReleaseDir = $PSScriptRoot
$RepoRoot = (Resolve-Path (Join-Path $ReleaseDir '..\..\..')).Path

if (-not $OutputDir) { $OutputDir = Join-Path $RepoRoot 'dist' }
if (-not $Version) {
    $Version = (Get-Content -LiteralPath (Join-Path $RepoRoot 'VERSION') -Raw).Trim()
}
if (-not $Version) { throw 'Не удалось определить версию (файл VERSION пуст).' }

$StageRoot = Join-Path $OutputDir '.stage'
$StageApp = Join-Path $StageRoot $RootName
$ZipPath = Join-Path $OutputDir ("schitayka-{0}-docker{1}.zip" -f $Version, $(if ($Image) { '-image' } else { '' }))

$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Write-TextFile {
    param([string]$Path, [string]$Text)
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    # Всегда LF: файлы уходят на Linux-НАС.
    $normalized = ($Text -replace "`r`n", "`n" -replace "`r", "`n")
    [System.IO.File]::WriteAllText($Path, $normalized, $Utf8NoBom)
}

function Copy-Tree {
    param(
        [string]$Source,
        [string]$Destination,
        [string[]]$ExcludePaths = @(),
        [string[]]$ExcludeFiles = @()
    )
    if (-not (Test-Path -LiteralPath $Source)) { throw "Нет папки: $Source" }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $roboArgs = @($Source, $Destination, '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
    if ($ExcludePaths.Count -gt 0) {
        $roboArgs += '/XD'
        $roboArgs += $ExcludePaths
    }
    if ($ExcludeFiles.Count -gt 0) {
        $roboArgs += '/XF'
        $roboArgs += $ExcludeFiles
    }
    & robocopy @roboArgs | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy завершился с кодом $LASTEXITCODE ($Source)" }
}

function Convert-ToLf {
    param([string]$Path)
    $text = [System.IO.File]::ReadAllText($Path)
    $text = ($text -replace "`r`n", "`n" -replace "`r", "`n")
    [System.IO.File]::WriteAllText($Path, $text, $Utf8NoBom)
}

function New-JwtKey {
    $bytes = New-Object byte[] 48
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    [Convert]::ToBase64String($bytes)
}

function Get-Commit {
    try {
        $sha = & git -C $RepoRoot rev-parse HEAD 2>$null
        if ($LASTEXITCODE -eq 0 -and $sha) { return $sha.Trim() }
    } catch { }
    return 'unknown'
}

Write-Host "== СчитайКа: сборка дистрибутива ==" -ForegroundColor Cyan
Write-Host "Версия:   $Version"
Write-Host "Репозито: $RepoRoot"
Write-Host "Вывод:    $OutputDir"
Write-Host "Режим:    $(if ($Image) { 'готовый образ (без сборки)' } else { 'сборка из ./app' })"

# --- 1. Чистая площадка ---------------------------------------------------
if (Test-Path -LiteralPath $StageRoot) { Remove-Item -LiteralPath $StageRoot -Recurse -Force }
New-Item -ItemType Directory -Path $StageApp -Force | Out-Null

# --- 2. Исходники в app/ (контекст сборки) --------------------------------
Write-Host "`n[1/6] Копирую backend..." -ForegroundColor Yellow
Copy-Tree -Source (Join-Path $RepoRoot 'backend') -Destination (Join-Path $StageApp 'app\backend') `
    -ExcludePaths @(
        (Join-Path $RepoRoot 'backend\bin'),
        (Join-Path $RepoRoot 'backend\obj'),
        (Join-Path $RepoRoot 'backend\wwwroot')
    ) `
    -ExcludeFiles @('app.db', 'app.db-shm', 'app.db-wal')

Write-Host "[2/6] Копирую frontend..." -ForegroundColor Yellow
Copy-Tree -Source (Join-Path $RepoRoot 'frontend') -Destination (Join-Path $StageApp 'app\frontend') `
    -ExcludePaths @(
        (Join-Path $RepoRoot 'frontend\node_modules'),
        (Join-Path $RepoRoot 'frontend\dist')
    )

Write-Host "[3/6] Dockerfile, VERSION, .dockerignore..." -ForegroundColor Yellow
Copy-Item -LiteralPath (Join-Path $RepoRoot 'deploy\xpenology\Dockerfile') -Destination (Join-Path $StageApp 'app\Dockerfile')
Copy-Item -LiteralPath (Join-Path $RepoRoot 'VERSION') -Destination (Join-Path $StageApp 'app\VERSION')
Copy-Item -LiteralPath (Join-Path $RepoRoot 'VERSION') -Destination (Join-Path $StageApp 'VERSION')
Copy-Item -LiteralPath (Join-Path $RepoRoot '.dockerignore') -Destination (Join-Path $StageApp 'app\.dockerignore')

# Мусор от robocopy/IDE, который не должен попасть в архив.
Get-ChildItem -LiteralPath (Join-Path $StageApp 'app') -Recurse -Force -File |
    Where-Object { $_.Name -match '\.(user|suo)$' -or $_.Name -eq 'tsconfig.tsbuildinfo' } |
    Remove-Item -Force

# --- 3. Compose, .env, скрипты, документация ------------------------------
Write-Host "[4/6] Compose, .env, скрипты..." -ForegroundColor Yellow
$composeSource = if ($Image) {
    Join-Path $ReleaseDir 'docker-compose.image.yml'
} else {
    Join-Path $ReleaseDir 'docker-compose.yml'
}
Copy-Item -LiteralPath $composeSource -Destination (Join-Path $StageApp 'docker-compose.yml')
Copy-Item -LiteralPath (Join-Path $ReleaseDir 'docker-compose.image.yml') -Destination (Join-Path $StageApp 'docker-compose.image.yml')
Copy-Item -LiteralPath (Join-Path $ReleaseDir '.env.example') -Destination (Join-Path $StageApp '.env.example')
Copy-Item -LiteralPath (Join-Path $ReleaseDir 'README.md') -Destination (Join-Path $StageApp 'README.md')

New-Item -ItemType Directory -Path (Join-Path $StageApp 'scripts') -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $ReleaseDir 'scripts') -Filter '*.sh' -File | ForEach-Object {
    $target = Join-Path (Join-Path $StageApp 'scripts') $_.Name
    Copy-Item -LiteralPath $_.FullName -Destination $target
    Convert-ToLf -Path $target
}

# --- 4. .env с секретом ---------------------------------------------------
$commit = Get-Commit
if ($NoEnv) {
    Write-Host "[5/6] .env не создаю (-NoEnv): пользователь запустит scripts/init-env.sh" -ForegroundColor Yellow
} else {
    Write-Host "[5/6] Генерирую .env и JWT_KEY..." -ForegroundColor Yellow
    $envText = @"
# СчитайКа — настройки проекта Container Manager.
# Создан упаковщиком $(Get-Date -Format 'yyyy-MM-dd HH:mm'). Содержит секрет: не публикуйте.

APP_PORT=5023
JWT_KEY=$(New-JwtKey)
TZ=Europe/Moscow
SEED_DEMO=false
UPDATE_REPO_URL=https://github.com/NikolaYudin/schitayka-finance
GITHUB_TOKEN=
GIT_COMMIT=$commit
"@
    Write-TextFile -Path (Join-Path $StageApp '.env') -Text $envText
}

# --- 5. MANIFEST ----------------------------------------------------------
Write-Host "[6/6] MANIFEST и упаковка..." -ForegroundColor Yellow
$appFiles = Get-ChildItem -LiteralPath (Join-Path $StageApp 'app') -Recurse -File
$manifest = @"
СчитайКа — дистрибутив для Synology / Xpenology (Container Manager)
Версия:        $Version
Коммит:        $commit
Собрано:       $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')
Режим:         $(if ($Image) { 'готовый образ (docker-compose.image.yml)' } else { 'сборка из ./app' })
Файлов в app/: $($appFiles.Count)
Размер app/:   $([Math]::Round(($appFiles | Measure-Object Length -Sum).Sum / 1MB, 2)) МБ

Состав:
  docker-compose.yml       проект Container Manager (context ./app)
  .env                     настройки (содержит JWT_KEY) $(if ($NoEnv) { '— ОТСУТСТВУЕТ (-NoEnv)' } else { '' })
  app/                     исходники + Dockerfile
  scripts/                 update.sh, backup.sh, restore.sh, init-env.sh
  README.md                инструкция по установке

Проверка целостности: распакуйте и выполните  sh scripts/update.sh
"@
Write-TextFile -Path (Join-Path $StageApp 'MANIFEST.txt') -Text $manifest

# --- 6. Zip ---------------------------------------------------------------
if (-not (Test-Path -LiteralPath $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}
if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
Compress-Archive -LiteralPath $StageApp -DestinationPath $ZipPath -CompressionLevel Optimal

# Проверка содержимого архива.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$required = @(
    "$RootName/docker-compose.yml",
    "$RootName/app/Dockerfile",
    "$RootName/app/VERSION",
    "$RootName/app/backend/SchitayKa.Api.csproj",
    "$RootName/app/backend/Data/AppDbContext.cs",
    "$RootName/app/backend/Data/AppDbContextFactory.cs",
    "$RootName/app/frontend/package.json",
    "$RootName/app/frontend/package-lock.json",
    "$RootName/scripts/update.sh",
    "$RootName/README.md"
)
if (-not $NoEnv) { $required += "$RootName/.env" }

$zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
try {
    $entries = $zip.Entries | ForEach-Object { $_.FullName }
    $missing = $required | Where-Object { $entries -notcontains $_ }
    if ($missing) {
        throw "В архиве отсутствуют обязательные файлы:`n  $($missing -join "`n  ")"
    }
    $entryCount = $entries.Count
} finally {
    $zip.Dispose()
}

if (-not $KeepStage) { Remove-Item -LiteralPath $StageRoot -Recurse -Force }

$sizeMb = [Math]::Round((Get-Item -LiteralPath $ZipPath).Length / 1MB, 2)
$sha = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash

# --- 7. Артефакт приложения для кнопки «Обновить сейчас» -------------------
# schitayka-<версия>-app.zip — собранное приложение (dll + wwwroot).
# Его скачивает SelfUpdateService при автообновлении.
$appZipPath = $null
if (-not $NoApp) {
    Write-Host "`n[7/7] Собираю артефакт приложения для автообновления..." -ForegroundColor Yellow
    $publishDir = Join-Path $OutputDir '.app-publish'
    Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue

    Push-Location (Join-Path $RepoRoot 'frontend')
    try {
        if (-not (Test-Path 'node_modules')) { & npm ci --no-audit --no-fund }
        & npm run build
        if ($LASTEXITCODE -ne 0) { throw 'Сборка фронтенда не удалась.' }
    } finally { Pop-Location }

    & dotnet publish (Join-Path $RepoRoot 'backend\SchitayKa.Api.csproj') `
        -c Release -o $publishDir --nologo -v q /p:UseAppHost=false /p:SourceRevisionId=$commit
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish не удался.' }

    $appZipPath = Join-Path $OutputDir ("schitayka-{0}-app.zip" -f $Version)
    if (Test-Path -LiteralPath $appZipPath) { Remove-Item -LiteralPath $appZipPath -Force }
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $appZipPath -CompressionLevel Optimal

    $appSha = (Get-FileHash -LiteralPath $appZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-TextFile -Path "$appZipPath.sha256" -Text "$appSha  $(Split-Path -Leaf $appZipPath)`n"

    Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  артефакт: $appZipPath ($([Math]::Round((Get-Item $appZipPath).Length/1MB,2)) МБ)"
}

Write-Host "`n== Готово ==" -ForegroundColor Green
Write-Host "Архив:   $ZipPath"
Write-Host "Размер:  $sizeMb МБ, файлов: $entryCount"
Write-Host "SHA256:  $sha"
if ($appZipPath) {
    Write-Host "Артефакт обновления: $appZipPath"
    Write-Host "SHA256:  $appSha"
}
Write-Host "`nДальше: загрузите архив на НАС в папку docker и распакуйте (см. README.md внутри архива)." -ForegroundColor Cyan
