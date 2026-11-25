# Set Working Directory
Split-Path $MyInvocation.MyCommand.Path | Push-Location
[Environment]::CurrentDirectory = $PWD

Remove-Item "$env:RELOADEDIIMODS/jjkccpc.essentials/*" -Force -Recurse
dotnet publish "./jjkccpc.essentials.csproj" -c Release -o "$env:RELOADEDIIMODS/jjkccpc.essentials" /p:OutputPath="./bin/Release" /p:ReloadedILLink="true"

# Restore Working Directory
Pop-Location