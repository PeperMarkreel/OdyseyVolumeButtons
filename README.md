# Odyssey+ Volume Buttons

Use the physical volume buttons on a Samsung Odyssey+ to control Windows audio.

If your headset works in SteamVR but pressing its volume buttons does nothing, this small Windows tray app restores those controls. It reads the buttons directly from the headset and raises or lowers the volume of the default Windows multimedia output. Hold a button to keep adjusting the volume.

## Compatibility

- The app targets the USB interface of the **Samsung Odyssey+**.
- Tested on **64-bit Windows 11, version 25H2**.
- **SteamVR** is optional for volume control. Your VR setup still uses its own headset driver. [Oasis Driver for Windows Mixed Reality](https://store.steampowered.com/app/3824490/Oasis_Driver_for_Windows_Mixed_Reality/)

## Run the app

1. [Download the Windows ZIP](https://github.com/PeperMarkreel/OdyseyVolumeButtons/releases/latest) and extract `OdysseyVolume.exe`, or [build it from source](#build-from-source).
2. Put the executable in a folder where you plan to keep it, then double-click it.
3. Connect the headset and try its volume buttons. Right-click the speaker icon in the Windows notification area to see the connection or volume status. If the icon is hidden, look under the notification area's up arrow.

The app runs as your Windows user and works without an installer or administrator rights.

## Start automatically

Both options are off by default.

| Tray menu | What happens |
| --- | --- |
| **Start at Windows sign-in** | Runs at Windows sign-in and stays available until you close it. |
| **Start with SteamVR → Enable** | Lets SteamVR start the app. That instance exits when SteamVR shuts down. |
| **Start with SteamVR → Disable** | Removes the SteamVR autostart registration. |
| **Exit** | Closes the running app. |

For volume controls only during VR sessions, enable SteamVR autostart and leave Windows sign-in unchecked. If the app is already running, a second launch keeps the existing instance. An instance opened manually or at Windows sign-in stays open when SteamVR closes.

If you move the executable, turn each enabled autostart option off and on from its new location to update the saved path. To uninstall, turn off both autostart options, choose **Exit**, then delete the executable.

## Build from source

Install the .NET 10 SDK for Windows. [Download the .NET SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

Run the build script from the repository root in PowerShell.

```powershell
.\app\build.ps1
```

The script builds `outputs\OdysseyVolume.exe`. Close the app before rebuilding into the same location. The SDK is required for the build. The executable runs on Windows using .NET Framework.

## Troubleshooting

- **The tray says “Waiting for Odyssey+”.** Reconnect the headset's USB plug and check whether the tray status changes. The app retries automatically.
- **A button changes the wrong device's volume.** Choose the intended device in Windows sound settings. The app looks up the default multimedia output on each press.
- **SteamVR autostart fails.** Check that SteamVR is installed through Steam, then enable the option again from the tray.

If you're still stuck, [open an issue](https://github.com/PeperMarkreel/OdyseyVolumeButtons/issues/new) with your Windows version, headset model, and the message shown in the tray. For a SteamVR issue, also include your SteamVR version and how the app was started.
