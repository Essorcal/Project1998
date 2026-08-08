<#
.SYNOPSIS
  Assemble a complete, ready-to-copy deployment tree for a Linux host.

.DESCRIPTION
  Produces a self-contained directory you can rsync/scp to /opt/nexus in one shot. Deliberately does NOT
  use git: data/ is a submodule whose remote is a local Windows path, so `git clone --recursive` on the
  host would fetch nothing and leave you with an empty world. Copying the assembled tree sidesteps that
  entirely.

  Layout produced (matching deploy/README.md):

    <Out>/
      Server/     <- EMPTY marker dir. Both processes locate the data directory by walking UP from the
      Shared/     <- binary looking for a .sln, or for a dir containing both Server/ and Shared/. Without
                     these two the walk fails and the content registry loads NOTHING (the server still
                     starts and accepts logins — it just has no maps, mobs or NPCs).
      login/      <- published LoginServer
      game/       <- published game Server
      data/       <- game-data + maps + SObj.tbl + gm_accounts.txt

  Runtime state is NOT copied: nexus.db (and its -wal/-shm sidecars) and the logs stay behind, so staging
  can never overwrite a live production database with your dev one. The host creates a fresh DB on first
  run; to migrate real accounts, copy nexus.db across separately with `sqlite3 ... ".backup"`.

  NOTE: the staged binaries CANNOT be run on Windows to smoke-test them. `-r linux-x64` ships only that
  RID's native SQLite (libe_sqlite3.so, no Windows .dll), so `dotnet game/Server.dll` here dies in
  SqliteConnection's static constructor. That is the bundle being correctly Linux-targeted, not a fault.
  Content loading happens BEFORE that point, so the startup lines it does print (terrain / spawns / npcs
  counts) are still a valid check that the marker directories resolved the data dir. Anything past
  "character store:" has to be tested on the host.

.PARAMETER Out
  Where to build the tree. Defaults to a sibling of the repo so it never lands inside git.

.PARAMETER MapsFrom
  Source of the .map terrain files. Defaults to the 4.95 client install. These are what make server-side
  collision and mob spawn placement work; without them nothing crashes, players and monsters just walk
  through walls.

.EXAMPLE
  .\deploy\stage-bundle.ps1
  Then: rsync -av <Out>/ user@host:/opt/nexus/
#>
[CmdletBinding()]
param(
    [string]$Out      = "$PSScriptRoot\..\..\nexus-deploy",
    [string]$MapsFrom = "C:\Program Files (x86)\Nexon\NextAeon\Maps",
    [string]$Dotnet   = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path "$PSScriptRoot\..").Path
if (-not (Test-Path $Dotnet)) { $Dotnet = "dotnet" }   # fall back to a dotnet on PATH

# Resolve $Out to an absolute path even though it may not exist yet.
$Out = [System.IO.Path]::GetFullPath($Out)
Write-Host "repo   : $repo"
Write-Host "output : $Out"
Write-Host ""

if (Test-Path $Out) {
    Write-Host "Removing previous staging tree ..."
    Remove-Item -Recurse -Force $Out
}
New-Item -ItemType Directory -Force -Path $Out | Out-Null

# ---- 1. publish both processes -------------------------------------------------------------------
# --self-contained false: the host needs the .NET 8 runtime installed (apt install dotnet-runtime-8.0).
# Switch to `--self-contained true` if you'd rather not install a runtime there.
foreach ($p in @(
    @{ Proj = "LoginServer\LoginServer.csproj"; Dir = "login" },
    @{ Proj = "Server\Server.csproj";           Dir = "game"  }
)) {
    Write-Host "Publishing $($p.Proj) -> $($p.Dir) ..."
    & $Dotnet publish (Join-Path $repo $p.Proj) -c Release -r linux-x64 --self-contained false `
        -o (Join-Path $Out $p.Dir) -v:q --nologo
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $($p.Proj)" }
}

# ---- 2. repo-root marker directories -------------------------------------------------------------
New-Item -ItemType Directory -Force -Path (Join-Path $Out "Server") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $Out "Shared") | Out-Null
# Git does not track empty directories, and some archive tools drop them, either of which would silently
# break the data-directory walk. A placeholder file guarantees they survive the trip.
foreach ($m in @("Server", "Shared")) {
    Set-Content -Encoding utf8 -Path (Join-Path $Out "$m\.keep") -Value @"
Intentionally empty. Both server processes find the data directory by walking up from their own binary
looking for a .sln file, or for a directory containing BOTH Server/ and Shared/. These two markers are
what make that walk stop here. Delete them and the world loads empty. See deploy/README.md.
"@
}

# ---- 3. content ----------------------------------------------------------------------------------
$dataOut = Join-Path $Out "data"
New-Item -ItemType Directory -Force -Path $dataOut | Out-Null

Write-Host "Copying data/game-data ..."
Copy-Item -Recurse -Force (Join-Path $repo "data\game-data") $dataOut
# Editor/backup droppings would otherwise ride along and confuse a later diff.
Get-ChildItem -Path (Join-Path $dataOut "game-data") -Include *.bak, *.bak2, *.pre-* -Recurse -File |
    Remove-Item -Force

foreach ($f in @("SObj.tbl", "monster_mapping.json", "gm_accounts.txt")) {
    $src = Join-Path $repo "data\$f"
    if (Test-Path $src) { Copy-Item -Force $src $dataOut }
}

# ---- 4. terrain ----------------------------------------------------------------------------------
$mapsOut = Join-Path $dataOut "maps"
New-Item -ItemType Directory -Force -Path $mapsOut | Out-Null
Copy-Item -Force (Join-Path $repo "data\maps\*.map") $mapsOut -ErrorAction SilentlyContinue

if (Test-Path $MapsFrom) {
    Write-Host "Copying terrain from $MapsFrom ..."
    Copy-Item -Force "$MapsFrom\*.map" $mapsOut
} else {
    Write-Warning "Terrain source not found: $MapsFrom"
    Write-Warning "Pass -MapsFrom <path to the client's Maps directory>, or the host will have no collision."
}

$mapCount = (Get-ChildItem $mapsOut -Filter *.map -File).Count

# ---- 5. unit files -------------------------------------------------------------------------------
Copy-Item -Force (Join-Path $repo "deploy\nexus-login.service") $Out
Copy-Item -Force (Join-Path $repo "deploy\nexus-game.service")  $Out
Copy-Item -Force (Join-Path $repo "deploy\README.md")           $Out

# ---- summary -------------------------------------------------------------------------------------
$size = "{0:N1} MB" -f ((Get-ChildItem $Out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
$gm   = @(Get-Content (Join-Path $dataOut "gm_accounts.txt") -ErrorAction SilentlyContinue |
          Where-Object { $_.Trim() -and -not $_.TrimStart().StartsWith("#") }).Count

Write-Host ""
Write-Host "=== staged: $Out  ($size) ==="
Write-Host "  terrain      : $mapCount .map file(s)"
Write-Host "  GM accounts  : $gm"
Write-Host ""
if ($mapCount -lt 100) { Write-Warning "Only $mapCount map(s) staged - collision will be broken on every other map." }
if ($gm -eq 0)         { Write-Warning "No GM accounts in data/gm_accounts.txt - '!' commands will be unavailable to everyone." }
Write-Host "Next:"
Write-Host "  1. Edit NEXUS_GAME_HOST in BOTH nexus-login.service and nexus-game.service (public IP)."
Write-Host "  2. rsync -av `"$Out/`" user@host:/opt/nexus/"
Write-Host "  3. On the host: see README.md sections 3 onward."
