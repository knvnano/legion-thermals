[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.3.0',
    [string]$Compiler = "${env:ProgramFiles(x86)}\NSIS\makensis.exe"
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$payload = Join-Path $PSScriptRoot 'payload'
$dist = Join-Path $projectRoot 'dist'
if (!(Test-Path -LiteralPath $Compiler)) { throw "NSIS 3 compiler not found: $Compiler" }
if ((Split-Path -Parent ([IO.Path]::GetFullPath($payload))) -ne $PSScriptRoot) { throw 'Unexpected payload path' }
if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload -Recurse -Force }
New-Item -ItemType Directory -Force $payload,$dist | Out-Null
& dotnet publish (Join-Path $projectRoot 'LegionThermals.csproj') -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version" -o $payload --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# Licenses from the exact packages named by the published runtime dependency graph.
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
$licenses = Join-Path $payload 'Licenses'
New-Item -ItemType Directory -Force $licenses | Out-Null
$deps = Get-Content (Join-Path $payload 'LegionThermals.deps.json') -Raw | ConvertFrom-Json
$index = [Collections.Generic.List[string]]::new()
$index.Add("Legion Thermals $Version - third-party components and license notices")
$index.Add('The application EULA does not replace or restrict the licenses below.')
$index.Add('See the Licenses subdirectory for original texts and native-library notices.')
$index.Add('')
$mit = @'
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
'@
foreach ($library in $deps.libraries.PSObject.Properties) {
    $parts = $library.Name.Split('/')
    $id = $parts[0] -replace '^runtimepack\.', ''
    $packageVersion = $parts[1]
    if ($id -eq 'LegionThermals') { continue }
    $package = Join-Path (Join-Path $nugetRoot $id.ToLowerInvariant()) $packageVersion.ToLowerInvariant()
    $nuspecPath = Join-Path $package ($id.ToLowerInvariant()+'.nuspec')
    if (!(Test-Path -LiteralPath $nuspecPath)) { throw "Missing license metadata: $($library.Name)" }
    [xml]$nuspec = Get-Content -LiteralPath $nuspecPath -Raw
    $metadata = $nuspec.package.metadata
    $license = $metadata.license
    $target = Join-Path $licenses "$id-$packageVersion"
    New-Item -ItemType Directory -Force $target | Out-Null
    $texts = @(Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -match '^(LICENSE|NOTICE|THIRD-PARTY|COPYING)' })
    foreach ($file in $texts) { Copy-Item -LiteralPath $file.FullName -Destination $target }
    if ($license.type -eq 'file') {
        $licenseFile = Join-Path $package $license.InnerText
        if (!(Test-Path -LiteralPath $licenseFile)) { throw "Missing package license: $licenseFile" }
        Copy-Item -LiteralPath $licenseFile -Destination $target -Force
    } elseif ($license.type -eq 'expression' -and $license.InnerText -eq 'MIT') {
        $copyright = [string]$metadata.copyright
        if (!$copyright) { $copyright = 'Copyright: ' + [string]$metadata.authors }
        ($copyright+"`r`n`r`n"+$mit) | Set-Content (Join-Path $target 'MIT.txt') -Encoding UTF8
    } elseif ($texts.Count -eq 0) { throw "License needs review: $id $($license.InnerText)" }
    $index.Add("$id $packageVersion | $($license.InnerText) | $($metadata.projectUrl)")
}
$nsisLicense = Join-Path (Split-Path -Parent $Compiler) 'COPYING'
Copy-Item -LiteralPath $nsisLicense -Destination (Join-Path $licenses 'NSIS-COPYING.txt')
$index.Add('NSIS installer engine | zlib/libpng license; see NSIS-COPYING.txt (also documents the compiler license).')
$index | Set-Content (Join-Path $payload 'THIRD-PARTY-NOTICES.txt') -Encoding UTF8
Copy-Item (Join-Path $PSScriptRoot 'EULA.ru.txt') (Join-Path $payload 'EULA.ru.txt')
Copy-Item (Join-Path $PSScriptRoot 'INSTALL.ru.txt') (Join-Path $payload 'INSTALL.ru.txt')

Copy-Item (Join-Path $PSScriptRoot 'INSTALL.en.txt') (Join-Path $payload 'INSTALL.en.txt')
Copy-Item (Join-Path $PSScriptRoot 'EULA.en.txt') (Join-Path $payload 'EULA.en.txt')
Copy-Item (Join-Path $projectRoot 'LICENSE') (Join-Path $payload 'LICENSE')

# ASCII RTF with Unicode escapes for the bilingual MIT license page.
$agreement = (Get-Content (Join-Path $PSScriptRoot 'EULA.en.txt') -Raw) + "`r`n`r`n" + (Get-Content (Join-Path $PSScriptRoot 'EULA.ru.txt') -Raw)
$rtf = [Text.StringBuilder]::new('{\rtf1\ansi\ansicpg1251\deff0{\fonttbl{\f0 Segoe UI;}}\f0\fs20 ')
foreach ($character in $agreement.ToCharArray()) {
    $number = [int]$character
    switch ($number) {
        13 { }
        10 { [void]$rtf.Append('\par ' + "`r`n") }
        92 { [void]$rtf.Append('\\') }
        123 { [void]$rtf.Append('\{') }
        125 { [void]$rtf.Append('\}') }
        default {
            if ($number -gt 127) { if ($number -gt 32767) { $number -= 65536 }; [void]$rtf.Append('\u'+$number+'?') }
            else { [void]$rtf.Append($character) }
        }
    }
}
[void]$rtf.Append('}')
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'LICENSE.generated.rtf'),$rtf.ToString(),[Text.Encoding]::ASCII)

# Delete only owned files; never recursively remove the installation directory.
$uninstall = [Collections.Generic.List[string]]::new()
foreach ($file in Get-ChildItem -LiteralPath $payload -File -Recurse) {
    $relative = $file.FullName.Substring($payload.Length+1).Replace('$','$$')
    $uninstall.Add('Delete /REBOOTOK "$INSTDIR\'+$relative+'"')
}
foreach ($directory in Get-ChildItem -LiteralPath $payload -Directory -Recurse | Sort-Object { $_.FullName.Length } -Descending) {
    $relative = $directory.FullName.Substring($payload.Length+1).Replace('$','$$')
    $uninstall.Add('RMDir "$INSTDIR\'+$relative+'"')
}
$uninstall | Set-Content (Join-Path $PSScriptRoot 'uninstall-files.nsh') -Encoding UTF8
& $Compiler /INPUTCHARSET UTF8 "/DAPP_VERSION=$Version" (Join-Path $PSScriptRoot 'LegionThermals.nsi')
if ($LASTEXITCODE -ne 0) { throw 'NSIS compilation failed' }
$setup = Join-Path $dist "LegionThermals-Setup-$Version-win-x64.exe"
$hash = Get-FileHash -LiteralPath $setup -Algorithm SHA256
($hash.Hash+'  '+[IO.Path]::GetFileName($setup)) | Set-Content ($setup+'.sha256') -Encoding ASCII
Write-Output "Installer: $setup"
Write-Output "SHA256: $($hash.Hash)"

