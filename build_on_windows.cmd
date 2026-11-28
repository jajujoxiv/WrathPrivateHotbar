@echo off
setlocal EnableExtensions
pushd "%~dp0" || exit /b 1
where dotnet >nul 2>&1
if errorlevel 1 (
  echo Install the .NET 10 SDK before building this project.
  goto fail
)
dotnet restore "Plugin\WrathPrivateHotbar.csproj" --locked-mode -p:Platform=x64
if errorlevel 1 goto fail
dotnet build "Plugin\WrathPrivateHotbar.csproj" --no-restore -c Release -p:Platform=x64
if errorlevel 1 goto fail
if not exist "DevPlugin" mkdir "DevPlugin"
if errorlevel 1 goto fail
for %%E in (dll json deps.json) do (
  copy /Y "Plugin\bin\x64\Release\WrathPrivateHotbar.%%E" "DevPlugin\WrathPrivateHotbar.%%E" >nul
  if errorlevel 1 goto fail
)
echo Built "%~dp0DevPlugin\WrathPrivateHotbar.dll".
popd
exit /b 0

:fail
echo Build or copy failed. See the message above.
popd
exit /b 1
