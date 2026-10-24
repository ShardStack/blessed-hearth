<#
Build-Release.ps1

Makes a release of Blessed Hearth that's safe to hand to anyone.

What it does:
  1. Reads the version number out of src\Core\Settings.cs (AppInfo.Version).
  2. Makes a folder named after it right here, like "2.0.0". If that one's already there it
     uses "2.0.0 (2)", then "2.0.0 (3)" and so on, so an older build never gets overwritten.
  3. Copies a clean copy of the app into <that folder>\Source. Only the app's own files get
     copied (the source code and the interface), so nothing else sitting in this
     folder can sneak in. Your settings and API key were never in here anyway, they live in
     %APPDATA%\BlessedHearth.
  4. Scans that copy for API keys, email addresses and your Windows user folder, and stops if it
     finds any.
  5. Builds "Blessed Hearth.exe" from the clean copy. It's one small file with the interface
     packed inside it. It needs nothing installed except Windows 10 or 11, which already have
     .NET Framework 4.8 and Edge's WebView2 engine.
     The app's own CurseForge API key gets built into the exe (scrambled, not as plain text),
     so people using it never have to get a key of their own. The key is never put in Source
     or anywhere else in this folder, so it can't end up on GitHub.

The app's CurseForge key:
  The first time you build, this asks you to paste the key CurseForge gave you (it doesn't show
  on screen). It's saved encrypted for your Windows account in
  %LOCALAPPDATA%\BlessedHearth\build-tools\curseforge-app-key.dat and reused after that.
  If the key is in the BLESSED_HEARTH_API_KEY environment variable, that's used instead.

Building needs two free tools from Microsoft, fetched once from nuget.org (Microsoft's own package
site) into %LOCALAPPDATA%\BlessedHearth\build-tools and reused after that, about 20 MB:
  - the C# compiler (Microsoft.Net.Compilers.Toolset). Windows has an older one built in, but
    it only understands C# from 2012.
  - the WebView2 SDK (Microsoft.Web.WebView2), the two small DLLs that let a window host Edge's
    engine, plus the native loader for each kind of processor.
Every file is checked for Microsoft's digital signature before it's used.

Run it from this folder:
    powershell -ExecutionPolicy Bypass -File .\Build-Release.ps1

Options:
    -SkipExe     only make the clean source copy, skip the exe (quick)
    -NewKey      forget the saved CurseForge key and ask for it again (if CurseForge gives you a new one)
    -NoKey       build without the app's key, so the exe asks each person for their own key like 2.0 did
#>
[CmdletBinding()]
param(
    [switch]$SkipExe,
    [switch]$NewKey,
    [switch]$NoKey
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$Root = $PSScriptRoot
$AppName = 'Blessed Hearth'
$CompilerPackage = @{ Id = 'Microsoft.Net.Compilers.Toolset'; Version = '4.8.0' }
$WebViewPackage = @{ Id = 'Microsoft.Web.WebView2'; Version = '1.0.3856.49' }

# What goes into a release, as patterns under this folder. Anything that doesn't match stays behind.
$ReleasePatterns = @(
    'Build-Release.ps1', 'README.md', 'LICENSE', '.gitignore', 'build.sh', '.github\FUNDING.yml',
    'src\*.cs', 'src\Core\*.cs', 'src\App\*.cs', 'src\Window\*.cs',
    'ui\index.html', 'ui\icons\*.svg', 'ui\icons\*.ico', 'ui\fonts\*.woff2', 'ui\fonts\OFL.txt'
)

# File types that are plain text, so they can be read and searched
$TextTypes = @('.cs', '.sh', '.md', '.txt', '.html', '.htm', '.css', '.js', '.json', '.bat', '.cmd',
               '.ps1', '.svg', '.xml', '.ini', '.cfg', '.toml', '.log', '.yml', '.yaml')

function Say([string]$Text) { Write-Host $Text }
function Step([string]$Text) { Write-Host ''; Write-Host "== $Text" -ForegroundColor Cyan }
$script:Out = $null
$script:KeyFile = $null
function Remove-KeyFile {
    if ($script:KeyFile -and (Test-Path -LiteralPath $script:KeyFile)) { Remove-Item -LiteralPath $script:KeyFile -Force }
    $script:KeyFile = $null
}
function Fail([string]$Text) {
    Remove-KeyFile
    Write-Host ''
    Write-Host "STOPPED: $Text" -ForegroundColor Red
    # don't leave a half made release lying around, especially one that failed the privacy check
    if ($script:Out -and (Test-Path -LiteralPath $script:Out)) {
        Remove-Item -LiteralPath $script:Out -Recurse -Force
        Write-Host "Removed the unfinished release folder."
    }
    exit 1
}

# Your own API key, read from your settings so the scan knows exactly what to look for. On
# Windows the app stores it encrypted for your account, so this asks Windows to decrypt it.
function Get-SavedApiKey {
    if (-not $env:APPDATA) { return $null }
    # (NotSoCursedForge is where it lived before the app was renamed, in case it hasn't moved yet)
    $cfg = Join-Path $env:APPDATA 'BlessedHearth\config.json'
    if (-not (Test-Path -LiteralPath $cfg)) { $cfg = Join-Path $env:APPDATA 'NotSoCursedForge\config.json' }
    if (-not (Test-Path -LiteralPath $cfg)) { return $null }
    try {
        $data = Get-Content -LiteralPath $cfg -Raw | ConvertFrom-Json
        $names = @($data.PSObject.Properties.Name)
        if ($names -contains 'api_key_protected' -and $data.api_key_protected) {
            Add-Type -AssemblyName System.Security
            $bytes = [Convert]::FromBase64String($data.api_key_protected)
            $scope = [System.Security.Cryptography.DataProtectionScope]::CurrentUser
            $plain = [System.Security.Cryptography.ProtectedData]::Unprotect($bytes, $null, $scope)
            return [Text.Encoding]::UTF8.GetString($plain)
        }
        if ($names -contains 'api_key' -and $data.api_key) { return [string]$data.api_key }
    } catch {
        Say "(Couldn't read your saved key to look for it. The general key check still runs.)"
    }
    return $null
}

# The app's own CurseForge key (the one CurseForge issued to Blessed Hearth), kept encrypted for
# your Windows account so only you, logged in as you, can read it back.
$KeyShape = '^\$2[aby]\$\d{2}\$[./A-Za-z0-9]{53}$'
$KeyEntropy = [Text.Encoding]::UTF8.GetBytes('Blessed Hearth build key')
function Get-AppKeyPath {
    if (-not $env:LOCALAPPDATA) { return $null }
    return (Join-Path $env:LOCALAPPDATA 'BlessedHearth\build-tools\curseforge-app-key.dat')
}
function Read-SavedAppKey {
    $path = Get-AppKeyPath
    if (-not $path -or -not (Test-Path -LiteralPath $path)) { return $null }
    try {
        Add-Type -AssemblyName System.Security
        $bytes = [Convert]::FromBase64String((Get-Content -LiteralPath $path -Raw).Trim())
        $scope = [System.Security.Cryptography.DataProtectionScope]::CurrentUser
        $plain = [System.Security.Cryptography.ProtectedData]::Unprotect($bytes, $KeyEntropy, $scope)
        return [Text.Encoding]::UTF8.GetString($plain)
    } catch {
        Say "(The saved CurseForge key couldn't be read, so you'll be asked for it again.)"
        return $null
    }
}
function Save-AppKey([string]$Key) {
    $path = Get-AppKeyPath
    if (-not $path) { return }
    Add-Type -AssemblyName System.Security
    New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force | Out-Null
    $scope = [System.Security.Cryptography.DataProtectionScope]::CurrentUser
    $locked = [System.Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($Key), $KeyEntropy, $scope)
    Set-Content -LiteralPath $path -Value ([Convert]::ToBase64String($locked)) -Encoding Ascii
}
# Takes out spaces and line breaks that sneak in when copying
function Clean-Key([string]$Key) { if (-not $Key) { return '' }; return ($Key -replace '\s', '') }
function Get-AppKey {
    $fromEnv = Clean-Key $env:BLESSED_HEARTH_API_KEY
    if ($fromEnv) {
        if ($fromEnv -notmatch $KeyShape) { Say "(Heads up: BLESSED_HEARTH_API_KEY isn't shaped like the usual CurseForge key, using it anyway.)" }
        Say 'Using the key from BLESSED_HEARTH_API_KEY.'
        return $fromEnv
    }
    if ($NewKey) {
        $path = Get-AppKeyPath
        if ($path -and (Test-Path -LiteralPath $path)) { Remove-Item -LiteralPath $path -Force }
        Say 'Forgot the saved key.'
    } else {
        $saved = Clean-Key (Read-SavedAppKey)
        if ($saved) {
            Say 'Using your saved CurseForge key.'
            return $saved
        }
    }
    Say 'Paste the API key CurseForge gave Blessed Hearth (from console.curseforge.com).'
    Say 'It will not show on screen while you paste. Press Enter when done.'
    for ($try = 1; $try -le 3; $try++) {
        $secure = Read-Host -Prompt 'Key' -AsSecureString
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try { $typed = Clean-Key ([Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
        $use = $typed -match $KeyShape
        if (-not $use -and $typed.Length -ge 20) {
            # CurseForge keys are usually 60 characters starting with $2a$10$, but just in case theirs is different
            Say "That isn't shaped like the usual CurseForge key ($($typed.Length) characters, they're normally 60 and start with `$2a`$10`$)."
            $use = (Read-Host -Prompt 'Use it anyway? Type yes or no') -match '^\s*y'
        }
        if ($use) {
            Save-AppKey $typed
            Say 'Got it. Saved encrypted for your Windows account, so you will not be asked again.'
            return $typed
        }
        Say 'Try again.'
    }
    Fail 'No usable CurseForge key, so nothing was built. Run this again, or use -NoKey to build without one.'
}

# Writes the extra code file that hands the key to the app (see src\Core\BuiltInKey.cs). The key
# is mixed with random bytes so it isn't sitting in the exe as plain text. The file goes in your
# temp folder, never in Source, and gets deleted as soon as the exe is built.
function New-KeyFile([string]$Key) {
    $plain = [Text.Encoding]::ASCII.GetBytes($Key)
    $pad = New-Object byte[] $plain.Length
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($pad)
    $mixed = for ($i = 0; $i -lt $plain.Length; $i++) { '0x{0:X2}' -f ($plain[$i] -bxor $pad[$i]) }
    $padText = ($pad | ForEach-Object { '0x{0:X2}' -f $_ }) -join ','
    $code = @"
namespace BlessedHearth
{
    public static partial class BuiltInKey
    {
        static partial void Load(ref string key)
        {
            byte[] a = { $($mixed -join ',') };
            byte[] b = { $padText };
            var c = new char[a.Length];
            for (int i = 0; i < a.Length; i++) c[i] = (char)(a[i] ^ b[i]);
            key = new string(c);
        }
    }
}
"@
    $path = Join-Path ([IO.Path]::GetTempPath()) ("BlessedHearth-key-" + [Guid]::NewGuid().ToString('N') + '.cs')
    $script:KeyFile = $path
    Set-Content -LiteralPath $path -Value $code -Encoding Ascii
    return $path
}

# Runs a program and shows what it prints. Build tools print their progress on the error stream,
# and Windows PowerShell can treat that as a failure, so this just passes it all through and
# hands back the program's exit code.
function Run([string]$Exe, [string[]]$ArgList) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Exe @ArgList 2>&1 | ForEach-Object { Write-Host $_ }
        return $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $old
    }
}

# Looks through every text file in a folder for anything that shouldn't be in a release.
# $Mine is what to look for by name, like @{ 'your API key' = '...' }. It never prints the
# values themselves. Returns a list of what it found, empty means all clear.
function Find-PersonalInfo([string]$Folder, [hashtable]$Mine) {
    $keyShape = '\$2[aby]\$\d{2}\$[./A-Za-z0-9]{53}'
    $emailShape = '[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}'
    $found = @()
    foreach ($f in Get-ChildItem -LiteralPath $Folder -Recurse -File) {
        if ($TextTypes -notcontains $f.Extension.ToLower()) { continue }
        $text = [IO.File]::ReadAllText($f.FullName)
        $rel = $f.FullName.Substring($Folder.Length).TrimStart('\', '/')
        if ($text -match $keyShape) { $found += "$rel has something shaped like a CurseForge API key" }
        $mail = [regex]::Match($text, $emailShape)
        if ($mail.Success) { $found += "$rel has an email address ($($mail.Value))" }
        foreach ($item in $Mine.GetEnumerator()) {
            if ($item.Value -and $text.IndexOf([string]$item.Value, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $found += "$rel contains $($item.Key)"
            }
        }
    }
    return ,$found
}

# Fetches one NuGet package (it's just a zip) and unpacks it, once. Returns the folder.
function Get-Package([hashtable]$Pkg, [string]$Into) {
    $id = $Pkg.Id.ToLower()
    $dir = Join-Path $Into "$($Pkg.Id).$($Pkg.Version)"
    if (Test-Path -LiteralPath (Join-Path $dir '.done')) { return $dir }
    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $url = "https://api.nuget.org/v3-flatcontainer/$id/$($Pkg.Version)/$id.$($Pkg.Version).nupkg"
    $zip = Join-Path $Into "$id.$($Pkg.Version).zip"
    Say "Downloading $($Pkg.Id) $($Pkg.Version)"
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    } catch {
        Fail "Couldn't download $($Pkg.Id) from nuget.org. Check your internet connection and run this again."
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $dir)
    Remove-Item -LiteralPath $zip -Force
    Set-Content -LiteralPath (Join-Path $dir '.done') -Value $Pkg.Version
    return $dir
}

# Only use a program or DLL that Microsoft signed and that hasn't been changed since
function Assert-Microsoft([string]$File) {
    $sig = Get-AuthenticodeSignature -LiteralPath $File
    if ($sig.Status -ne 'Valid' -or -not $sig.SignerCertificate -or $sig.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        Fail "$File isn't signed by Microsoft (signature: $($sig.Status)), so it wasn't used. Delete the build-tools folder and try again."
    }
}

# 1. The version
Step 'Reading the version'
$settings = Join-Path $Root 'src\Core\Settings.cs'
if (-not (Test-Path -LiteralPath $settings)) { Fail "Can't find src\Core\Settings.cs. Run this from the app's folder." }
$match = Select-String -LiteralPath $settings -Pattern 'public const string Version = "([^"]+)"' | Select-Object -First 1
if (-not $match) { Fail "Couldn't find the version in src\Core\Settings.cs." }
$Version = $match.Matches[0].Groups[1].Value
if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "The version is '$Version', which doesn't look like 1.2.3." }
Say "Version $Version"

# 2. The release folder
Step 'Making the release folder'
$OutName = $Version
$n = 2
while (Test-Path -LiteralPath (Join-Path $Root $OutName)) {
    $OutName = "$Version ($n)"
    $n++
}
$Out = Join-Path $Root $OutName
New-Item -ItemType Directory -Path $Out | Out-Null
$script:Out = $Out
Say "Created $Out"

# 3. The clean copy
Step 'Copying a clean copy of the app'
$Source = Join-Path $Out 'Source'
$count = 0
foreach ($pattern in $ReleasePatterns) {
    $items = @(Get-ChildItem -Path (Join-Path $Root $pattern) -File -Force -ErrorAction SilentlyContinue)
    if ($items.Count -eq 0 -and -not $pattern.Contains('*')) { Fail "$pattern is missing, and a release needs it." }
    foreach ($f in $items) {
        $rel = $f.FullName.Substring($Root.Length).TrimStart('\')
        $to = Join-Path $Source $rel
        New-Item -ItemType Directory -Path (Split-Path $to -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $f.FullName -Destination $to
        $count++
    }
}
Say "$count files copied to Source"

# 4. Make sure nothing personal came along
Step 'Checking for anything personal'
$mine = @{}
$key = Get-SavedApiKey
if ($key) { $mine['your API key'] = $key.Trim() }
# the app's own key too, if it's been saved already (otherwise it's asked for just below)
$savedAppKey = Clean-Key (Read-SavedAppKey)
if ($savedAppKey) { $mine["the app's CurseForge key"] = $savedAppKey }
if ($env:USERPROFILE) { $mine['your user folder'] = $env:USERPROFILE }
if ($env:USERNAME) {
    $mine['your user folder name'] = "Users\$($env:USERNAME)"
    $mine['your user folder name (forward slashes)'] = "Users/$($env:USERNAME)"
}
$leaks = Find-PersonalInfo -Folder $Source -Mine $mine
if ($leaks.Count -gt 0) {
    $leaks | ForEach-Object { Say "  $_" }
    Fail "The clean copy has personal info in it, so nothing was built. Fix those files and run this again."
}
Say 'All clear: no API keys, email addresses or user folders.'

if ($SkipExe) {
    Step 'Done (skipped the exe)'
    Say "Clean copy: $Source"
    exit 0
}

# 5. The exe
$AppKey = $null
if ($NoKey) {
    Step 'Skipping the CurseForge key (-NoKey)'
    Say 'This exe will ask each person for their own key in Settings.'
} else {
    Step 'Getting the CurseForge key ready'
    $AppKey = Get-AppKey
    # one more look now that the key's known for sure, in case a new one was just pasted
    $leaks = Find-PersonalInfo -Folder $Source -Mine @{ "the app's CurseForge key" = $AppKey }
    if ($leaks.Count -gt 0) {
        $leaks | ForEach-Object { Say "  $_" }
        Fail "The clean copy has the app's key in it, so nothing was built. Take it out of those files and run this again."
    }
}

Step 'Getting the build tools ready'
if (-not $env:LOCALAPPDATA) { Fail "Can't find %LOCALAPPDATA% for the build tools." }
$Tools = Join-Path $env:LOCALAPPDATA 'BlessedHearth\build-tools'
New-Item -ItemType Directory -Path $Tools -Force | Out-Null
$compilerDir = Get-Package -Pkg $CompilerPackage -Into $Tools
$webViewDir = Get-Package -Pkg $WebViewPackage -Into $Tools
$csc = Join-Path $compilerDir 'tasks\net472\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { Fail "The C# compiler package doesn't have tasks\net472\csc.exe in it." }
Assert-Microsoft $csc
# the .NET part of the SDK, for .NET Framework (the folder's called net462, or net45 in older SDKs)
$wvLib = $null
foreach ($cand in @('lib\net462', 'lib\net45')) {
    if (Test-Path -LiteralPath (Join-Path $webViewDir "$cand\Microsoft.Web.WebView2.WinForms.dll")) { $wvLib = Join-Path $webViewDir $cand; break }
}
if (-not $wvLib) { Fail "The WebView2 package doesn't have its .NET Framework DLLs where expected." }
$wvCore = Join-Path $wvLib 'Microsoft.Web.WebView2.Core.dll'
$wvForms = Join-Path $wvLib 'Microsoft.Web.WebView2.WinForms.dll'
$loaders = @{}
foreach ($arch in @('win-x64', 'win-x86', 'win-arm64')) {
    $loader = Join-Path $webViewDir "runtimes\$arch\native\WebView2Loader.dll"
    if (-not (Test-Path -LiteralPath $loader)) { Fail "The WebView2 package is missing $arch\WebView2Loader.dll." }
    $loaders[$arch] = $loader
}
foreach ($f in @($wvCore, $wvForms) + @($loaders.Values)) { Assert-Microsoft $f }
Say 'Build tools ready, all signed by Microsoft.'

Step 'Building the exe'
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $fw 'mscorlib.dll'))) { $fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
if (-not (Test-Path -LiteralPath (Join-Path $fw 'System.Windows.Forms.dll'))) { Fail ".NET Framework 4.8 isn't on this PC (it comes with Windows 10 and 11)." }
$exe = Join-Path $Out "$AppName.exe"
$cscArgs = @('/nologo', '/noconfig', '/target:winexe', '/optimize+', '/platform:anycpu', '/langversion:6', "/out:$exe",
             ('/win32icon:' + (Join-Path $Source 'ui\icons\hearth.ico')))
foreach ($ref in @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
                   'System.Security.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll')) {
    $cscArgs += "/reference:$(Join-Path $fw $ref)"
}
$cscArgs += "/reference:$wvCore"
$cscArgs += "/reference:$wvForms"
# Everything the app needs at runtime rides inside the exe as a resource, named by where it goes
$ui = Join-Path $Source 'ui'
foreach ($f in Get-ChildItem -LiteralPath $ui -Recurse -File) {
    $name = 'ui/' + $f.FullName.Substring($ui.Length + 1).Replace('\', '/')
    $cscArgs += "/resource:$($f.FullName),$name"
}
$cscArgs += "/resource:$wvCore,lib/Microsoft.Web.WebView2.Core.dll"
$cscArgs += "/resource:$wvForms,lib/Microsoft.Web.WebView2.WinForms.dll"
foreach ($arch in $loaders.Keys) { $cscArgs += "/resource:$($loaders[$arch]),lib/$arch/WebView2Loader.dll" }
$cscArgs += "/recurse:$(Join-Path $Source 'src\*.cs')"
if ($AppKey) { $cscArgs += (New-KeyFile $AppKey) }
try {
    $built = (Run $csc $cscArgs) -eq 0
} finally {
    Remove-KeyFile   # the key file only ever exists for the few seconds the compiler needs it
}
if (-not $built -or -not (Test-Path -LiteralPath $exe)) { Fail "The build failed, see the messages above." }
if ($AppKey) {
    # Double check the key really is scrambled in there and not sitting as readable text
    $raw = [IO.File]::ReadAllBytes($exe)
    $asText = [Text.Encoding]::GetEncoding(28591).GetString($raw)
    if ($asText.Contains($AppKey) -or $asText.Contains([Text.Encoding]::GetEncoding(28591).GetString([Text.Encoding]::Unicode.GetBytes($AppKey)))) {
        Remove-Item -LiteralPath $exe -Force
        Fail 'The key showed up as plain text inside the exe, so the exe was deleted. This should never happen, so something is off with the build.'
    }
    Say "The app's CurseForge key is built in (scrambled, not readable as text)."
}
$mb = [Math]::Round((Get-Item -LiteralPath $exe).Length / 1MB, 1)
Say "Made $exe ($mb MB)"

Step 'Done'
Say "Release folder: $Out"
Say "  $AppName.exe   the whole app in one file, ready to share"
Say "  Source         the clean copy of the app's files"
