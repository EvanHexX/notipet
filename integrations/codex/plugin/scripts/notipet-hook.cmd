@echo off
rem notipet's Codex plugin hooks run this (hooks\hooks.json), with the hook
rem payload on stdin. It finds notipet.exe and passes everything through.
rem
rem Why a script and not the exe in hooks.json: Codex trusts a hook by the hash
rem of its definition, so every edit to hooks.json makes the user review and
rem trust the hooks again. Where notipet lives can change here instead.
rem
rem Order: NOTIPET_CLI (a dev build), the installed copy (its path stays the
rem same across updates), then PATH. Not found: exit 0 without a word - a
rem missing notifier must never change what Codex does, and hook stdout can be
rem read by Codex.
setlocal
set "NOTIPET_EXE="
if defined NOTIPET_CLI if exist "%NOTIPET_CLI%" set "NOTIPET_EXE=%NOTIPET_CLI%"
if not defined NOTIPET_EXE if exist "%LOCALAPPDATA%\NotipetApp\current\notipet.exe" set "NOTIPET_EXE=%LOCALAPPDATA%\NotipetApp\current\notipet.exe"
if not defined NOTIPET_EXE for %%I in (notipet.exe) do set "NOTIPET_EXE=%%~$PATH:I"
if not defined NOTIPET_EXE exit /b 0
"%NOTIPET_EXE%" %*
exit /b 0
