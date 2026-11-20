# Halerith DriveMapper

Map network drives on Windows devices using policies delivered through Microsoft Intune.

A Windows service applies mappings in each signed-in user's session. It responds to sign-in, policy and network changes, keeping drives available without logon scripts or scheduled tasks.

## Features

- Configure drive letters, share paths, display names and reconnection settings.
- Apply policy changes automatically and retry unavailable connections.
- Install, update and uninstall through a setup window or silent commands.

## Installation

Download and extract the ZIP from [Releases](https://github.com/halerith/DriveMapper/releases/latest). Keep all three executables together and open `Install.exe`, or run elevated:

| Action | Command |
| --- | --- |
| Install or update | `Install.exe -quiet -install` |
| Uninstall | `Install.exe -quiet -uninstall` |
| Uninstall and clear settings/logging data | `Install.exe -quiet -uninstall -force` |

The default location is `C:\Program Files\DriveMapper`. The same install command upgrades existing installations. Uninstalling leaves drive mappings unchanged.

## Intune deployment

Package these files as a Win32 app:

- `Install.exe`
- `DriveMapper-Service.exe`
- `DriveMapper-Agent.exe`

Use **System** install behavior and the commands above. Configure **3010** as a soft reboot code.

For detection, use the **64-bit** registry key `HKEY_LOCAL_MACHINE\SOFTWARE\DriveMapper`, value **Version**, with a version comparison **greater than or equal to the release being installed**.

## Drive configuration

1. Import [DriveMapper_v2.admx](ADMX/DriveMapper_v2.admx) and [DriveMapper_v2.adml](ADMX/en-US/DriveMapper_v2.adml) into Intune.
2. Create an imported administrative templates profile and enable the required drive-letter policies.
3. Enter each UNC path, optional display name and reconnect setting, then assign the profile to users.

Users need network access and permission to the shares. Allow approximately 60 seconds for policy changes. The ZIP includes the templates in its `ADMX` folder; existing v1 profiles must be recreated using v2.

Disabling or withdrawing a policy removes its previously managed mapping. Changing or removing a drive can force a disconnect, including open files. Avoid overlapping v1/v2 policies for the same drive letter.

## Troubleshooting

Check **Halerith DriveMapper** in Services. Mapping events appear in **Event Viewer → Windows Logs → Application**, source **DriveMapper**.

Installer log: `%ProgramData%\DriveMapper\Logs\install.log`.

## Building

On Windows with a .NET 10 SDK (10.0.300 or newer):

```powershell
dotnet build DriveMapper.sln -c Release
```

Publish each project for `win-x64` to produce its self-contained executable.

## License

Maintained and substantially rewritten by Halerith. Originally based on [DriveMapper by icds250](https://github.com/icds250/DriveMapper). Licensed under [GNU GPLv3](LICENSE.txt).
