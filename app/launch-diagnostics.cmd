@echo off
rem ===========================================================
rem  DSH Guardian - diagnostic collector launcher
rem
rem  Two hard rules for this file, both learned the hard way:
rem   1. Pure ASCII. cmd.exe decodes batch files with the OEM code
rem      page, so UTF-8 Chinese here becomes garbage and corrupts
rem      the command line.
rem   2. CRLF line endings. With bare LF, cmd.exe can read several
rem      lines as one and split words in half.
rem
rem  The ASCII script name is used on purpose: a Chinese name would
rem  have to be written into this file as non-ASCII bytes. The
rem  desktop shortcut is Chinese, so the user never sees this name.
rem
rem  chcp 65001 puts the console in UTF-8 so the Chinese output
rem  produced by the PowerShell script displays correctly.
rem ===========================================================
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0collect-diagnostics.ps1"
if errorlevel 1 pause
