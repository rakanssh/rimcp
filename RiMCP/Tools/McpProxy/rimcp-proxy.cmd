@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
set "EXE=%SCRIPT_DIR%win-x64\RiMCP.McpProxy.exe"

if not exist "%EXE%" (
  echo Rim-MCP MCP proxy executable is missing: "%EXE%" 1>&2
  echo Build it with scripts\publish-mcp-proxy.sh before packaging the mod. 1>&2
  exit /b 1
)

"%EXE%" %*
exit /b %ERRORLEVEL%
