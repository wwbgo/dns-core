@echo off
REM DNS Core Server - Docker Build (快捷方式)
REM 此脚本调用 docker-build.bat

call "%~dp0docker-build.bat" -t latest -r docker.flexem.com/flexem -p %*

pause
