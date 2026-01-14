@echo off
echo ========================================
echo  Agent.NET NuGet Package Publisher
echo ========================================
echo.

:: Clean previous builds
echo Cleaning previous builds...
dotnet clean -c Release --verbosity quiet
dotnet clean -c Release --verbosity quiet --project ..\AgentNet.Durable\AgentNet.Durable.fsproj

:: Build and pack AgentNet
echo.
echo Building and packing AgentNet...
dotnet pack -c Release

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ERROR: AgentNet build failed!
    exit /b %ERRORLEVEL%
)

:: Build and pack AgentNet.Durable
echo.
echo Building and packing AgentNet.Durable...
dotnet pack -c Release --project ..\AgentNet.Durable\AgentNet.Durable.fsproj

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ERROR: AgentNet.Durable build failed!
    exit /b %ERRORLEVEL%
)

:: Show the output
echo.
echo ========================================
echo  Packages created successfully!
echo ========================================
echo.
echo AgentNet package:
dir /b bin\Release\*.nupkg
echo.
echo AgentNet.Durable package:
dir /b ..\AgentNet.Durable\bin\Release\*.nupkg
echo.
echo To publish to NuGet.org:
echo   dotnet nuget push bin\Release\AgentNet.*.nupkg --api-key YOUR_API_KEY --source https://api.nuget.org/v3/index.json
echo   dotnet nuget push ..\AgentNet.Durable\bin\Release\AgentNet.Durable.*.nupkg --api-key YOUR_API_KEY --source https://api.nuget.org/v3/index.json
echo.
