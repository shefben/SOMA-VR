# PSX XMB Manager
A tool that helps to install PS1 & PS2 games and PS2 homebrew on the internal HDD of the PSX DVR (DESR).</br>
The installed game or homebrew will show up on the XMB where you can start it like on the PS3.</br>

<img width="648" alt="psxv4" src="https://github.com/user-attachments/assets/2897075e-d4ea-428b-91b0-713623e88736" />

## Features
- Install PS2 homebrew and games on the internal PSX HDD
- Install PS1 games on the internal PSX HDD
- Backup manager for PS2 games on PC & installed games on the PSX HDD (Game Library)
  - A right click on a (local) game will give you the possibility to quickly create a PS2 game project for the PSX.
- Backup manager for PS1 games on PC (Game Library)
  - A right click on a game will give you the possibility to quickly create a PS1 game project for the PSX.
- Mount PS2 games from PSX HDD and modify/update the partition header
  - Change game properties / Update OPL-Launcher
- HDD Partition Manager (Create partition, Remove partition (destructive), Change partition visibility, Mount/Unmount partition)
- PS2 Game Partition Manager (Dump partition header, Change game title, flags, DMA)
- XMB Files Explorer (XMB Tools)
  - Open a _system or xosd folder to load, view and edit its content
  - Text Editor for .xml & .dic files with syntax highlighting
  - Translate .dic & .xml files automatically in most languages
- Utilities
  - HDD Utilities
    - Create & Restore full raw HDD backups
    - Install POPStarter on the connected PSX HDD
  - Converters
    - Convert CUE backups to POPS (VCD) format
    - Convert BIN/CUE backups to ISO format (PS1 & PS2)
  - Extractors
    - Extract STAR files using stargazer
    - Extract PAK files using PAKerUtility
  - Decryptors
    - Decrypt KELF files using kelftool
    - Decrypt REL files
  - Decompressors/Unpackers
    - Decompress a decrypted xosdmain file
    - Unpack a PS2/PSX BIOS file

## Notes
- Requires FMCB installed on your memory card and Open PS2 Loader (to load PS2 games)
  - Installation Guide: [https://www.youtube.com/watch?v=9SU594F0pYc](https://www.youtube.com/watch?v=9SU594F0pYc)
  - Modified Open PS2 Loader: [https://github.com/SvenGDK/Open-PS2-Loader](https://github.com/SvenGDK/Open-PS2-Loader)
  - Do not forget to place/replace OPNPS2LD.ELF in the OPL+ partition
- Requires POPStarter installed for PS1 games
  - Installation Guide: [https://bitbucket.org/ShaolinAssassin/popstarter-documentation-stuff/wiki/quickstart-hdd](https://bitbucket.org/ShaolinAssassin/popstarter-documentation-stuff/wiki/quickstart-hdd)
- It is not recommended to abort an installation within the first 3%, this could corrupt your HDD. The same goes for the last percentages of the installation.
- Only connect your PSX's HDD locally if you know how to and never initialize it on Windows !

## WSL2 NBD Backend Requirements
The PSX HDD is reached over the network through WSL2. The Windows NBD driver (Ceph for Windows / WNBD) is **no longer required** and is not used anymore.

How it works: the Open PS2 Loader NBD server on the PSX is opened inside WSL2 by `nbdfuse`, which exposes the HDD as a raw file. The Linux builds of `hdl_dump`, `pfsshell` and `pfsfuse` work on that file. Mounted game partitions are opened by Windows through `\\wsl.localhost\<distro>\...`, so no drive letter is needed.

Prerequisites:
1. Windows 10 or 11 with WSL2 (`wsl --install` in an administrator terminal, then restart)
2. An Ubuntu or Debian distro running as WSL **2** (automatic setup supports Ubuntu and Debian)
3. The OPL NBD server running on the PSX (default port **10809**)
4. A network connection between the PC and the PSX

Setup is done inside the application: choose `WSL2 NBD` as connection method, select the distro and click **Install / Repair WSL Backend** (also in the menu as **WSL2 Setup**). It installs `libnbd-bin` (nbdfuse, nbdinfo), `fuse3`, `libfuse-dev`, `python3` and the build tools, builds the pinned `hdl_dump` and `pfsshell`/`pfsfuse` and installs the PSX XMB Manager helper. It asks before running and never runs on its own at startup.

- Dokan is **not** required for the WSL2 network connection.
- Dokan is still used when a PSX HDD is connected **locally** to the PC (`Local HDD` connection method) and a partition is mounted.

### Troubleshooting
| Problem | What to do |
|-----|-----|
| WSL is not installed | Run `wsl --install` in an administrator terminal, restart Windows, then start PSX XMB Manager again. |
| The distro is WSL1 | WSL1 distros are greyed out. Convert it with `wsl --set-version <distro> 2` or install a new Ubuntu distro. |
| The OPL NBD server is not running | Start the NBD server in Open PS2 Loader on the PSX and check the IP address shown there. Connect reports `NBD_SERVER_UNREACHABLE` until it answers. |
| Firewall or network cannot reach the PSX | The PC and the PSX must be on the same network and TCP port 10809 must not be blocked. WSL2 uses the Windows network, so a VPN or firewall rule on Windows also affects it. |
| `/dev/fuse` is unavailable | Update WSL (`wsl --update`), then restart it with `wsl --shutdown`. nbdfuse and pfsfuse cannot work without `/dev/fuse`. |
| Linux tools are missing or the helper is outdated | Click **Install / Repair WSL Backend**. The status rows under the connect button show which tool is missing. |
| A stale mount remains after a crash | Click **Recover Connection**. It unmounts the old PFS partitions first and then the NBD connection inside WSL. |

Logs of every backend operation and of the setup are written to `%LOCALAPPDATA%\PSX XMB Manager\Logs`.

## Create & Install Projects
- [https://github.com/SvenGDK/PSX-XMB-Manager/wiki/Manage-Projects](https://github.com/SvenGDK/PSX-XMB-Manager/wiki/Manage-Projects)

## Used tools from other developers
| Tool | Developer |
|-----|-----|
| dd | [http://www.chrysocome.net/dd](http://www.chrysocome.net/dd) |
| hdl_dump | [https://github.com/ps2homebrew/hdl-dump](https://github.com/ps2homebrew/hdl-dump) |
| nbdfuse & nbdinfo (libnbd, inside WSL2) | [https://gitlab.com/nbdkit/libnbd](https://gitlab.com/nbdkit/libnbd) |
| kelftool | [https://github.com/xfwcfw/kelftool](https://github.com/xfwcfw/kelftool) |
| PAKerUtility | [El_isra](https://github.com/israpps/PAKerUtility) |
| pfsshell & pfsfuse | [https://github.com/ps2homebrew/pfsshell](https://github.com/ps2homebrew/pfsshell) |
| SCEDoormat_NoME | krHACKen |
| stargazer | [Brawl345](https://github.com/Brawl345/stargazer) |

## Other used code
| Tool | Gist |
|-----|-----|
| PSX_rel.cpp | [https://gist.github.com/balika011/220dd4147ddc2a32efbaedfb8ebcd387#file-psx_rel-cpp](https://gist.github.com/balika011/220dd4147ddc2a32efbaedfb8ebcd387#file-psx_rel-cpp) |
| xosdmain_decomp.cpp | [https://gist.github.com/balika011/336b348b43fe1d10140513d02dba3442#file-xosdmain_decomp-cpp](https://gist.github.com/balika011/336b348b43fe1d10140513d02dba3442#file-xosdmain_decomp-cpp) |
