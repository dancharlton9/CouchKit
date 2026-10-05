@echo off
rem See build.ps1. Builds bin\CouchKit.exe and the Playnite add-on bin\CouchKit-<version>.pext.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
