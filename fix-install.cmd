@echo off
rem Runs outside the Claude MSIX container when started via explorer, so
rem %LOCALAPPDATA% resolves to the real profile and not the package cache.
taskkill /IM CaretLangIndicator.exe /F >nul 2>&1
timeout /t 2 /nobreak >nul
copy /Y "%~dp0CaretLangIndicator.exe" "%LOCALAPPDATA%\CaretLangIndicator\CaretLangIndicator.exe" >nul
for %%F in ("%LOCALAPPDATA%\CaretLangIndicator\CaretLangIndicator.exe") do echo %%~zF > "%~dp0installed-size.txt"
start "" "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\Caret Language Indicator.lnk"
