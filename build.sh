#!/bin/bash
# Test build with Mono on Linux. The real release is built on Windows by Build-Release.ps1.
# WEBVIEW2_LIB points at a folder with Microsoft.Web.WebView2.Core.dll and .WinForms.dll
# (from the Microsoft.Web.WebView2 NuGet package, lib/net462).
cd "$(dirname "$0")"
W=${WEBVIEW2_LIB:-lib}
mkdir -p build
[ -e build/ui ] || ln -s ../ui build/ui
mcs -langversion:6 -nologo -target:winexe -out:build/BlessedHearth.exe -r:System.Security.dll -r:System.IO.Compression.dll \
  -r:System.IO.Compression.FileSystem.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.Core.dll \
  -r:"$W/Microsoft.Web.WebView2.Core.dll" -r:"$W/Microsoft.Web.WebView2.WinForms.dll" -recurse:'src/*.cs'
