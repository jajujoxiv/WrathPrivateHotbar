@echo off
setlocal EnableExtensions DisableDelayedExpansion
chcp 65001 >nul
set "WPH_DEV_DLL=%~dp0DevPlugin\WrathPrivateHotbar.dll"
if not exist "%WPH_DEV_DLL%" goto missing
if not exist "%~dp0DevPlugin\WrathPrivateHotbar.json" goto missing
echo.
echo Dalamudに登録するDLLは次の場所にあります。
setlocal EnableDelayedExpansion
echo(!WPH_DEV_DLL!
endlocal
echo.
powershell.exe -NoLogo -NoProfile -NonInteractive -STA -Command "try { Set-Clipboard -Value $env:WPH_DEV_DLL -ErrorAction Stop } catch { exit 1 }"
if errorlevel 1 (
  echo コピーできませんでした。上のパスを選択してコピーしてください。
) else (
  echo DLLのフルパスをクリップボードにコピーしました。
)
echo /xlsettings の Experimental で Dev Plugin Locations に貼り付けます。
echo ＋を押し、追加した行の有効チェックを入れて保存してください。
echo /xlplugins の Dev Tools から Installed Dev Plugins を開きます。
echo.
pause
exit /b 0

:missing
echo.
echo DevPluginフォルダー内の WrathPrivateHotbar.dll または同名のJSONが見つかりません。
echo WPH210.zip を「すべて展開」してから、展開先でこのファイルを開いてください。
echo GitHub用ZIPからビルドする場合は、先に build_on_windows.cmd を実行してください。
pause
exit /b 1
