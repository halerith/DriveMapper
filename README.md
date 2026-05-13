# DriveMapper (Intune / Registry-Driven Edition)

DriveMapper is a lightweight Windows utility for **policy-driven network drive mapping** on Entra ID–joined or hybrid devices, designed for **Microsoft Intune deployments**.

This fork replaces JSON / AD group–based configuration with **registry values delivered via Intune ADMX**, enabling modern, cloud-native drive mapping without logon scripts or Group Policy Preferences.

---

## Architecture Overview

- **DriveMapper.exe**
  - Runs in the context of the **logged-in user**
  - Reads drive mapping configuration from registry policy keys
  - Maps, remaps, or removes drives as required
  - Logs all actions and errors to the Windows Application Event Log

- **Install.exe**
  - Copies files to a target directory
  - Creates scheduled tasks (logon + network change)
  - Creates the Event Log source used by DriveMapper
  - Registers DriveMapper in Windows Apps & Features / Add or Remove Programs
  - Fully uninstallable
  - Designed for Intune Win32 deployment

---

## Configuration Model (Intune / ADMX)

Drive mappings are controlled via **ADMX-backed registry policy** applied to **users** (not machines).

### Registry Locations

For each drive letter (`A`–`Z`), DriveMapper reads:

#### User policy
```
HKCU\Software\Policies\DriveMapper\Drives\<LETTER>\
```


### Supported Values

| Value Name | Type | Description |
|-----------|------|-------------|
| `Enabled` | DWORD (0/1) | Enables or disables the mapping |
| `Path` | REG_SZ | UNC path (e.g. `\\server\\share`) |
| `Name` | REG_SZ | Optional Explorer display name |
| `Reconnect` | DWORD (0/1) | Persistent mapping |

---

## Runtime Behavior

### Per-User Execution

DriveMapper runs via **Scheduled Tasks registered for `BUILTIN\Users` with group logon**, meaning:

- The task is created once, system-wide
- At runtime, Windows executes it as the **currently logged-in user**
- Each user receives **their own drive mappings**
- Users do not affect each other, even on shared machines

---

### Idempotent Mapping Logic

For each drive letter:

- If the drive is already mapped to the correct path → **skip**
- If policy is enabled and the drive is mapped to a different path → **remap to the policy path**
- If `Enabled=0` → **remove mapping if it still matches the previously applied path**
- If policy no longer exists → **remove mapping if it still matches the previously applied path**

Configured policy is authoritative for that drive letter. If policy enables a letter,
DriveMapper will enforce the configured path even if the current mapping was created
manually or by another tool.

For cleanup scenarios, DriveMapper only removes drives it previously created, tracked
per user under:

```
HKCU\Software\DriveMapper\State\AppliedDrives
```

This prevents accidental removal of user-managed drives when policy is disabled or
removed for a drive letter. If a user or another tool changes a DriveMapper-applied
letter before cleanup, DriveMapper deletes only its saved state and leaves the current
mapping alone.

If `Name` is configured, DriveMapper writes the Explorer display name for the mapped
UNC path. If `Name` is later cleared in policy, DriveMapper clears the custom display
name.

---

## Logging

All actions and errors are written to the **Windows Application Event Log**:

- **Log:** Application
- **Source:** `DriveMapper`

Logged events include:
- Drive mapped
- Drive removed
- Mapping skipped (already correct)
- Configuration errors
- Win32 API failures

The Event Log source is created by the installer during install (admin required).

---

## Installer (`Install.exe`)

`Install.exe` is a standalone .NET 8 installer utility designed for Intune Win32 deployment.

### Features

- Copies files to a target directory (e.g. `Program Files`)
- Registers scheduled tasks:
  - On user logon
  - On network profile change (VPN / Wi-Fi / Ethernet)
- Runs DriveMapper **as the logged-in user**
- Creates the `DriveMapper` Event Log source
- Writes DriveMapper version info to HKLM for Intune detection
- Registers an uninstall entry in Windows Apps & Features / Add or Remove Programs
- Runs a GUI when launched without arguments
- Runs silently when launched with command-line arguments
- Add/Remove Programs launches the GUI; quiet uninstall remains available for management tools
- Fully uninstallable

---

## Installer Configuration (`install.config.json`)

The installer includes a default `install.config.json` as an embedded asset, so no
loose JSON file is required for normal installs. If an `install.config.json` file
exists next to `Install.exe`, that adjacent file is used as an override. During
install and repair, the effective config is written into the target install
directory. During upgrade, the package config is reconciled with the currently
installed config: existing values are kept, missing current keys are added, and
keys no longer defined by the package config are removed.

Example:

```json
{
  "TargetDirectory": "C:\\Program Files\\DriveMapper",
  "ExeName": "DriveMapper.exe",
  "ShortcutName": "",
  "ScheduledTasks": [
    {
      "TaskName": "DriveMapper",
      "Arguments": "",
      "CreateLogonTask": true,
      "CreateNetworkTask": true,
      "CreateBootTask": false
    }
  ]
}
```

### Field Descriptions

| Field | Description |
|------|-------------|
| `TargetDirectory` | Destination directory for binaries |
| `ExeName` | Executable launched by scheduled tasks |
| `ShortcutName` | Optional Start Menu shortcut |
| `ScheduledTasks` | One or more task definitions |

Version detection uses the file version of `DriveMapper.exe`, not a config value.

---

## Installation & Uninstallation

Run from an **elevated prompt**:

Launching `Install.exe` without arguments opens the installer GUI. If elevation is
required, the installer prompts for UAC before performing the selected action.

### Install
```powershell
Install.exe install
```

### Upgrade
```powershell
Install.exe upgrade
```

### Repair
```powershell
Install.exe repair
```

### Uninstall
```powershell
Install.exe uninstall
```

When launched with arguments, the installer runs silently and returns an exit code.
This is the recommended mode for Intune. The installer uses its embedded config by
default. An explicit config path is supported only when you need an override, for
example: `Install.exe install.config.json install`.

---

## Intune Deployment (Win32 App)

1. Publish binaries as **self-contained single-file executables**
   - Recommended: run `.\build.ps1`
2. Package:
   - `Install.exe`
   - `DriveMapper.exe`
   - Optional `install.config.json` only if you need to override the embedded default
3. Create `.intunewin` package
4. Configure:
   - **Install command:**  
     `Install.exe install`
   - **Uninstall command:**  
     `Install.exe uninstall`
   - **Detection rule:**  
     HKLM `SOFTWARE\<ExeNameWithoutExtension>\Version`

Assignments should typically target **users**, not devices.

---

## Troubleshooting

- Check registry policy:
  ```powershell
  Get-ChildItem HKCU:\Software\Policies\DriveMapper\Drives
  ```
- Check Event Viewer:
  - Windows Logs → Application
  - Source: **DriveMapper**
- If drives don’t map immediately, wait for:
  - Network change
  - Next logon

---

## License

GPL.  
Original project by `icds250`.  
This fork extends functionality for Intune / Entra ID environments.
