# Starts a release: tags the current commit and pushes the tag. GitHub Actions
# (.github/workflows/release.yml) then builds the exe from source, signs it
# through SignPath and publishes the GitHub release.
#   powershell -ExecutionPolicy Bypass -File release.ps1
#   powershell -ExecutionPolicy Bypass -File release.ps1 -Local   # build and publish from this PC (unsigned)
# Needs: git, GitHub CLI (gh auth login), a clean, pushed working tree.
param([switch]$Local)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = [regex]::Match((Get-Content src\DevJunkCleaner.cs -Raw), 'AssemblyVersion\("(\d+\.\d+\.\d+)').Groups[1].Value
if (-not $version) { throw 'AssemblyVersion not found in src\DevJunkCleaner.cs' }
$tag = "v$version"
if (git status --porcelain) { throw 'Commit your changes first (git status is not clean).' }
if (git tag --list $tag) { throw "Tag $tag already exists. Bump the version in src\DevJunkCleaner.cs." }
git fetch -q origin
if ((git rev-parse HEAD) -ne (git rev-parse '@{u}')) { throw 'Push your commits first (git push).' }

if (-not $Local) {
    git tag -a $tag -m "Dev Junk Cleaner $version"
    git push origin $tag
    Write-Host "Tagged $tag. GitHub is building and signing it:" -ForegroundColor Green
    gh run list --workflow release.yml --limit 1
    return
}

& (Join-Path $PSScriptRoot 'build.cmd')
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
         "This build is not code-signed, so Windows may show *Windows protected your PC*: click **More info > Run anyway**.`n`n" +
         "Checksums are in SHA256SUMS.txt."
gh release create $tag dist\DevJunkCleaner.exe $zip $sums --title "Dev Junk Cleaner $tag" --notes $notes
Write-Host "Released $tag" -ForegroundColor Green
