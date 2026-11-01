@echo off
rem Runs the read-only schema export (see dump_schema.ps1) and keeps the window open.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0dump_schema.ps1"
pause
