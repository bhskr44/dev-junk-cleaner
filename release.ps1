# Builds the app and publishes a GitHub release with the exe, a zip and SHA-256 checksums.
#   powershell -ExecutionPolicy Bypass -File release.ps1            # version from src\DevJunkCleaner.cs
#   powershell -ExecutionPolicy Bypass -File release.ps1 -Draft     # release as a draft to check first
# Needs: git, GitHub CLI (gh auth login), a clean, pushed working tree.
param([switch]$Draft)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = [regex]::Match((Get-Content src\DevJunkCleaner.cs -Raw), 'AssemblyVersion\("(\d+\.\d+\.\d+)').Groups[1].Value
if (-not $version) { throw 'AssemblyVersion not found in src\DevJunkCleaner.cs' }
$tag = "v$version"
if (git status --porcelain) { throw 'Commit your changes first (git status is not clean).' }
if (git tag --list $tag) { throw "Tag $tag already exists. Bump AssemblyVersion in src\DevJunkCleaner.cs." }

cmd /c "`"$PSScriptRootbuild.cmd`""
if ($LASTEXITCODE) { throw 'Build failed.' }

$zip = "dist\DevJunkCleaner-$version-win.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path dist\DevJunkCleaner.exe, README.md, LICENSE -DestinationPath $zip
$sums = 'dist\SHA256SUMS.txt'
Get-ChildItem dist\DevJunkCleaner.exe, $zip | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
} | Set-Content $sums -Encoding ascii

git tag -a $tag -m "Dev Junk Cleaner $version"
git push origin $tag
$notes = "Download **DevJunkCleaner.exe** and run it - no install needed (Windows 10/11, .NET Framework 4.8 is built in).`n`n" +
         "Windows may show *Windows protected your PC* because the exe is not code-signed yet: click **More info > Run anyway**.`n`n" +
         "Checksums are in SHA256SUMS.txt."
$ghArgs = @('release', 'create', $tag, 'dist\DevJunkCleaner.exe', $zip, $sums, '--title', "Dev Junk Cleaner $version", '--notes', $notes)
if ($Draft) { $ghArgs += '--draft' }
gh @ghArgs
Write-Host "Released $tag" -ForegroundColor Green
