<#
.SYNOPSIS
  Publishes Billing Software and builds the Windows installer (needs Inno Setup 6).

.EXAMPLE
  .\installer\build-installer.ps1
  .\installer\build-installer.ps1 -Version 1.1.0 -SignThumbprint <certificate thumbprint>

.NOTES
  Not yet run: Inno Setup was not installed where this was written. Run it once on a build PC and test the installer on a clean machine.
  Signing needs a code-signing certificate (the customer's SmartScreen warning goes away only with a signed installer from an established publisher).
#>
param(
    [string]$Version = "1.0.0",
    [string]$SignThumbprint = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "1/3 Publishing the program (self-contained, win-x64)..."
if (Test-Path "artifacts\publish\BillingSoftware") { Remove-Item "artifacts\publish\BillingSoftware" -Recurse -Force }
dotnet publish src\BillingSoftware.Desktop -c Release -r win-x64 --self-contained true -o artifacts\publish\BillingSoftware -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "2/3 Checking the published program starts and passes its own checks..."
$result = Join-Path $env:TEMP "billing-selftest-$([guid]::NewGuid().ToString('N')).txt"
& "artifacts\publish\BillingSoftware\BillingSoftware.exe" --selftest $result
if ($LASTEXITCODE -ne 0) { Get-Content $result | Select-String "FAIL" | Write-Host; throw "The self-test failed. Not building an installer." }

Write-Host "3/3 Building the installer..."
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 was not found. Install it from https://jrsoftware.org/isinfo.php and run this again." }

& $iscc "/DAppVersion=$Version" "installer\BillingSoftware.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }

$setup = "artifacts\installer\BillingSoftware-Setup-$Version.exe"
if ($SignThumbprint) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "x64" } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw "signtool.exe was not found (install the Windows SDK)." }
    & $signtool.FullName sign /sha1 $SignThumbprint /fd sha256 /tr $TimestampUrl /td sha256 $setup
    if ($LASTEXITCODE -ne 0) { throw "Signing failed" }
    Write-Host "Signed $setup"
} else {
    Write-Host "Built $setup (NOT signed: Windows may warn customers who run it)."
}
