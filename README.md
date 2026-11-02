# DISM Studio

A native Windows desktop GUI for servicing Windows installation images with the built-in DISM executable. Written in C# / WPF, without third-party NuGet dependencies.

## Features

- Read image editions from WIM and ESD files.
- Inject folders of extracted INF drivers recursively into multiple selected WIM editions.
- Preview the exact commands before applying changes.
- Process editions sequentially: mount, add drivers, save and unmount.
- Stop after the current edition has finished saving.
- Export one ESD/WIM edition to a new WIM; the exported edition becomes index 1.
- Split a WIM into SWM files for installation media (3,800 MB target per part).
- Inspect existing DISM mounts and explicitly remount, save, or discard them.
- Stream commands, stdout, stderr, exit codes, errors, and status to the GUI and a timestamped log file.

## Run

Requires Windows 10/11, the **.NET 10 Desktop Runtime**, and administrator rights for DISM operations. Building from source requires the **.NET 10 SDK** instead of just the runtime. Use a Windows host and DISM version compatible with the image being serviced.

Double-click `Launch.cmd`, or run from the repository root:

```powershell
dotnet run --project src/DismGui --configuration Release
```

The app opens without elevation, with a **Restart as administrator** button. Restarting starts a new session; choose your inputs again afterward.

Build a distributable folder:

```powershell
dotnet publish src/DismGui -c Release --self-contained false -o artifacts/DismStudio
```

Run `artifacts/DismStudio/DismGui.exe`. Distribute the entire folder; the recipient needs the .NET 10 Desktop Runtime. The GitHub Actions workflow builds this same folder as a downloadable artifact.

## Add drivers to install media

1. Extract/copy your Windows ISO or USB contents to a writable folder. Keep an untouched backup.
2. Extract your vendor's drivers into folders containing `.inf`, `.sys`, `.cat`, and related files. DISM cannot directly install vendor EXE/MSI packages.
3. Select `sources\install.wim`, then **Read editions**. Select the edition(s) you will install using Ctrl/Shift. If your media has `install.esd`, select it and export the required edition to a new WIM on **Image tools** first, then select that WIM and read its editions.
4. Add your driver folders and choose an empty working folder on a local NTFS volume with enough free space for the expanded image. The default is `%LOCALAPPDATA%\DismStudio\Mount`.
5. **Preview commands**, then **Add drivers and save**. Confirm the selected indexes and source file.
6. Put the serviced `install.wim` into the installation media's `sources` folder. When replacing `install.esd`, remove the old ESD from that copy of the media so it contains the intended installation image.
7. If Windows Setup also needs storage or network drivers, repeat for the appropriate edition(s) of `sources\boot.wim`. Servicing install.wim does not update boot.wim.

For FAT32 media, split the serviced WIM into an empty folder and copy **all** `install*.swm` parts to `sources` in place of `install.wim`. A single very large file inside the image can make a part larger than the requested split size; check the resulting sizes before copying to FAT32.

This version does not create a bootable ISO, format a USB drive, install drivers into the running OS, extract driver installers, or automatically edit every edition. Export currently supports one edition per new WIM.

## Logs and recovery

Session logs are UTF-8 files under:

```text
%LOCALAPPDATA%\DismStudio\Logs\session-<date>-<time>-<id>.log
```

Use **Open logs** to open the directory. Every line has a timestamp with time-zone offset and a level. Output is flushed immediately, and files remain readable during servicing. The on-screen log is bounded; the file retains the complete session output. Logs include local file paths and DISM diagnostic output; review before sharing. Logs are not automatically deleted.

The app refuses to start if its session log cannot be created. If log writing fails during DISM, it drains the process output and waits for the current command to finish, then stops before issuing another command. DISM also maintains its own system log at `%WINDIR%\Logs\DISM\dism.log`.

If injection or saving fails, the app stops. It does **not** automatically commit the failed edition or discard unsaved work. Already saved editions remain changed. Open **Mounted images**, refresh, and inspect the image path/index. Use **Remount** for a recoverable disconnected mount; choose **Save and unmount** to retain pending work or **Discard and unmount** to remove it. These operations can affect mounts created by other tools and require confirmation.

The close button is blocked while a command is running. **Stop after edition** does not kill DISM: it finishes the current edition, saves it, and stops before mounting the next one. If the machine or process exits unexpectedly, use Mounted images on your next launch. Avoid servicing the same image concurrently in another tool.

## Development checks

```powershell
dotnet build src/DismGui --configuration Release
dotnet run --project tests/DismGui.Checks --configuration Release
```

The checks cover command arguments, edition/mount parsing, source/mount validation, sequential servicing, stopping, failure handling, and flushed concurrent log writes. They use temporary fixture files and a fake DISM runner; they do not execute DISM or modify real Windows images. End-to-end image servicing should be validated separately using disposable installation media.

## Versioning

Version: **0.1.0**. Source is tracked by Git; binaries, logs, drivers, and Windows media are excluded. The GitHub Actions workflow builds, runs checks, and publishes an app artifact for every push and pull request.

## Microsoft references

- [Mount and modify a Windows image](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/mount-and-modify-a-windows-image-using-dism)
- [DISM driver servicing options](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dism-driver-servicing-command-line-options-s14)
- [DISM image management options](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dism-image-management-command-line-options-s14)
