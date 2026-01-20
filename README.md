# SysTrayOBS
WinForms SysTray App for Windows 11 to control OBS via websocket

# Requirements

DotNet 10 runtime: 

```
winget install --id="Microsoft.DotNet.Runtime.Preview"
```

# Setup
- Clone appsettings.json file into appsettings.prod.json and enter values for Uri and Password (Password is ignored if no auth is configured in OBS)
- To enable web socket communication with OBS, go to OBS -> Tools -> WebSocket Server Settings and click Enable WebSocket Server
