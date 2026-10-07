# PKRtool
PKRtool is a tool to unpack/repack **.PKR files** from **Walt Disney World Quest: Magical Racing Tour** & **Tony Hawk Pro Skater 2**.<br />

## Requirements
**[.NET 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)** (or higher) - Supported platforms: **Windows, macOS, and Linux**.

## Usage
- `unpack`: Unpack a PKR file
- `repack`: Repack a folder to a PKR file
- `-compress`: Compress with zlib when repacking to a PKR
- `-nocompress`: Skip zlib compression when repacking to a PKR

## Notes
- PKRtool also supports **drag-n-drop** & automatically selects the right mode depending on the input file/folder.<br />When repacking automatically, zlib compression is applied (override with `-nocompress`)
- Repacked archives work in WDWR, THPS2 is not extensively tested.

> [!IMPORTANT]
> The Linux/macOS binaries must be made executable:
> ```bash
> chmod +x <path-to-PKRtool>
> ```
> The macOS binary is not signed, so you may need to remove the quarantine attribute:
> ```bash
> sudo xattr -rd com.apple.quarantine <path-to-PKRtool>
> ```
