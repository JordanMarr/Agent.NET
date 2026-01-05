@echo off
echo ========================================
echo  Agent.NET NuGet Package Publisher
echo ========================================
echo.

:: Clean previous builds
echo Cleaning previous builds...
dotnet clean -c Release --verbosity quiet

:: Build and pack
echo.
echo Building and packing...
dotnet pack -c Release

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ERROR: Build failed!
    exit /b %ERRORLEVEL%
)

:: Show the output
echo.
echo ========================================
echo  Package created successfully!
echo ========================================
echo.
echo Package location:
dir /b bin\Release\*.nupkg
echo.
echo Full path: %CD%\bin\Release\
echo.
echo To publish to NuGet.org:
echo   dotnet nuget push bin\Release\AgentNet.*.nupkg --api-key YOUR_API_KEY --source https://api.nuget.org/v3/index.json
echo.
