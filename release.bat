@echo off
setlocal EnableExtensions EnableDelayedExpansion
rem ===========================================================================
rem  Camera Sweep - release packager
rem
rem  1. Reads the version from src\CameraSweepPlugin.cs (Version = "x.y.z").
rem  2. Builds the DLL with "dotnet build" if the .NET SDK is installed; otherwise
rem     packages the DLL already installed in HS2 (the one you tested).
rem  3. Zips it with libwebp and a readme into releases\HS2CameraSweep-vX.Y.Z.zip,
rem     laid out so it extracts straight into the HS2 folder.
rem  4. Optional: commits and pushes the zip (needs git on PATH) and creates a
rem     GitHub release with the zip attached (needs the GitHub CLI, "gh").
rem
rem  Usage:  release.bat            (asks before committing / releasing)
rem          release.bat nobuild    (skip the build, use the installed DLL)
rem ===========================================================================

cd /d "%~dp0"
set "HS2DIR=X:\HS2 RX15"
set "INSTALLED=%HS2DIR%\BepInEx\plugins\HS2CameraSweep\HS2CameraSweep.dll"

rem ---- version ---------------------------------------------------------------
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "$m = Select-String -Path 'src\CameraSweepPlugin.cs' -Pattern 'const string Version\s*=\s*.([0-9.]+).' | Select-Object -First 1; if ($m) { $m.Matches[0].Groups[1].Value }"`) do set "VER=%%v"
if not defined VER (
  echo Could not read the version from src\CameraSweepPlugin.cs
  goto :fail
)
echo.
echo  Camera Sweep v%VER%
echo  ------------------------------

rem ---- DLL -------------------------------------------------------------------
set "DLL="
if /i "%~1"=="nobuild" goto :use_installed
where dotnet >nul 2>nul
if errorlevel 1 (
  echo  .NET SDK not found - using the DLL installed in HS2.
  goto :use_installed
)
echo  Building Release...
dotnet build HS2CameraSweep.csproj -c Release -nologo -v:minimal -p:Version=%VER% -p:HS2Dir=
if errorlevel 1 (
  echo.
  echo  Build failed - see above. Run "release.bat nobuild" to package the installed DLL instead.
  goto :fail
)
set "DLL=bin\Release\HS2CameraSweep.dll"
goto :have_dll

:use_installed
if not exist "%INSTALLED%" (
  echo  No DLL at "%INSTALLED%".
  goto :fail
)
set "DLL=%INSTALLED%"

:have_dll
for %%f in ("%DLL%") do echo  DLL: %%~ff  ^(%%~tf^)
for %%f in (native\libwebp_cs.dll native\LICENSE-libwebp.txt native\PATENTS-libwebp.txt packaging\README.txt) do (
  if not exist "%%f" (
    echo  Missing %%f
    goto :fail
  )
)

rem ---- stage + zip -----------------------------------------------------------
set "STAGE=%TEMP%\HS2CameraSweep-release-%RANDOM%"
set "PLUG=%STAGE%\BepInEx\plugins\HS2CameraSweep"
mkdir "%PLUG%" || goto :fail
copy /y "%DLL%" "%PLUG%\HS2CameraSweep.dll" >nul || goto :fail
copy /y native\libwebp_cs.dll "%PLUG%\" >nul || goto :fail
copy /y native\LICENSE-libwebp.txt "%PLUG%\" >nul || goto :fail
copy /y native\PATENTS-libwebp.txt "%PLUG%\" >nul || goto :fail
powershell -NoProfile -Command "(Get-Content -Raw 'packaging\README.txt').Replace('{VERSION}','%VER%') | Set-Content -NoNewline -Encoding UTF8 '%STAGE%\HS2CameraSweep-README.txt'" || goto :fail
if exist CHANGELOG.md copy /y CHANGELOG.md "%STAGE%\HS2CameraSweep-CHANGELOG.txt" >nul

if not exist releases mkdir releases
set "ZIP=releases\HS2CameraSweep-v%VER%.zip"
if exist "%ZIP%" del /q "%ZIP%"
powershell -NoProfile -Command "Compress-Archive -Path '%STAGE%\*' -DestinationPath '%ZIP%' -CompressionLevel Optimal" || goto :fail
rmdir /s /q "%STAGE%"
for %%f in ("%ZIP%") do echo  Zip: %%~ff  ^(%%~zf bytes^)
echo.

rem ---- commit + push (optional) ----------------------------------------------
where git >nul 2>nul
if errorlevel 1 (
  echo  git isn't on PATH: commit and push the zip with GitHub Desktop.
  goto :release
)
choice /c YN /n /m " Commit and push %ZIP% now? [Y/N] "
if errorlevel 2 goto :release
git add -- "%ZIP%" || goto :fail
git commit -m "Release v%VER%" -- "%ZIP%" || goto :fail
git push || goto :fail

:release
rem ---- GitHub release (optional) ---------------------------------------------
where gh >nul 2>nul
if errorlevel 1 (
  echo  GitHub CLI ^(gh^) not installed: to publish, open the repo's Releases page,
  echo  draft a new release tagged v%VER% and attach %ZIP%.
  goto :done
)
choice /c YN /n /m " Create GitHub release v%VER% with the zip attached? [Y/N] "
if errorlevel 2 goto :done
set "NOTES=%TEMP%\cs-notes-%RANDOM%.md"
powershell -NoProfile -Command "$t = Get-Content -Raw 'CHANGELOG.md'; $m = [regex]::Match($t, '(?ms)^## v?%VER:.=\.%\b.*?(?=^## |\z)'); if ($m.Success) { $m.Value } else { 'Camera Sweep v%VER%' }" > "%NOTES%"
gh release create "v%VER%" "%ZIP%" --title "Camera Sweep v%VER%" --notes-file "%NOTES%" --target main
del /q "%NOTES%" 2>nul

:done
echo.
echo  Done.
endlocal
exit /b 0

:fail
echo.
echo  Release stopped.
endlocal
exit /b 1
