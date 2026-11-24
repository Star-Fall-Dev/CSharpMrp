@echo off
dotnet publish "%~dp0CSharpMrp.Desktop.csproj" --configuration Release --runtime win-x64 --self-contained true ^
    -p:SatelliteResourceLanguages=zh-Hans -p:DebugType=None -p:BaseOutputPath=obj/publish-build/ ^
    --output "%~dp0bin\Release\net10.0-windows\win-x64\publish"
exit /b %errorlevel%
