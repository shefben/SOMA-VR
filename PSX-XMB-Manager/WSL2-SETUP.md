# PSX XMB Manager: WSL2 setup guide

PSX XMB Manager talks to your PSX over the network through **WSL2** (Linux inside Windows). This guide installs everything
it needs, step by step. Commands go into **PowerShell** unless a step says **Ubuntu**.

You only do this once. It takes about 15 minutes, most of it waiting for downloads.

---

## What you need

- Windows 10 (version 2004 or newer) or Windows 11.
- Virtualization turned on in your PC's BIOS/UEFI (most PCs have it on already; step 1 tells you if it is off).
- Your PSX with **Open PS2 Loader (OPL)** and its **NBD server**, on the same network as your PC.
- The PSX XMB Manager program folder (the one that contains `PSX XMB Manager.exe`, the `Tools` folder and the `WSL` folder).

---

## Step 1: Install WSL2 and Ubuntu

1. Right-click the Start button and choose **Terminal (Admin)** or **Windows PowerShell (Admin)**.
2. Run:

   ```powershell
   wsl --install -d Ubuntu-24.04
   ```

3. **Restart Windows** if it asks you to.
4. After the restart, an **Ubuntu** window opens by itself (if it does not, open **Ubuntu 24.04** from the Start menu).
   It asks for a **username** and **password**. Pick anything you like and remember the password; Linux asks for it
   when you install things.

> Already had WSL installed before? Run these instead, then continue with Step 2:
>
> ```powershell
> wsl --update
> wsl --set-default-version 2
> wsl --install -d Ubuntu-24.04
> ```

## Step 2: Check that Ubuntu runs as WSL **2**

In PowerShell (it does not need to be Admin any more):

```powershell
wsl -l -v
```

You should see a line like this. The number under **VERSION** must be **2**:

```
  NAME            STATE           VERSION
* Ubuntu-24.04    Running         2
```

If it says **1**, convert it:

```powershell
wsl --set-version Ubuntu-24.04 2
```

## Step 3: Install the Linux side

Pick **one** of these. Both install exactly the same things.

### Option A: from the app (easiest)

1. Start **PSX XMB Manager**.
2. Set **Connection Method** to **WSL2 NBD (Recommended)**.
3. Set **WSL2 Distribution** to **Ubuntu-24.04**.
4. Click **Install / Repair WSL Backend** and answer **Yes**.
5. Wait until it says the setup finished (several minutes). The **Linux PS2 Tools** row then turns green.

### Option B: one command

1. In File Explorer, open your PSX XMB Manager folder, click the address bar, type `powershell` and press Enter.
   A PowerShell window opens **in that folder**.
2. Run:

   ```powershell
   wsl -d Ubuntu-24.04 -u root -- bash ./WSL/install-wsl-backend.sh
   ```

3. Wait for the last line to say:

   ```
   PSX_XMB_BOOTSTRAP_OK
   ```

What gets installed (same for A and B):

| What | Why |
|---|---|
| `libnbd-bin` (nbdfuse, nbdinfo) | Opens the PSX's NBD server as a disk inside Linux |
| `fuse3`, `libfuse-dev` | Lets Linux and Windows open the disk and game partitions |
| `hdl_dump` (built from ps2homebrew/hdl-dump, commit `32c296c`) | Lists, installs and edits games |
| `pfsshell`, `pfsfuse` (built from ps2homebrew/pfsshell, commit `8c92467`) | Reads and writes PS2 partitions |
| `meson` 1.3.2 (run from its source in `/opt/psx-xmb-manager-src`) | Builds pfsshell; Ubuntu 22.04's own meson cannot |
| `psx-xmb-helper` | The small program PSX XMB Manager talks to inside Linux |
| `user_allow_other` in `/etc/fuse.conf` | Lets Windows Explorer open a mounted game partition |

## Step 4: Check that the tools work

Run these in PowerShell:

```powershell
wsl -d Ubuntu-24.04 -- psx-xmb-helper --version
```

It should print `1`.

Now start the NBD server on the PSX (in **Open PS2 Loader**, open the menu and choose **Start NBD Server**; OPL shows
the PSX's IP address). Then run this, with your PSX's IP address instead of `192.168.1.50`:

```powershell
wsl -d Ubuntu-24.04 -- nbdinfo nbd://192.168.1.50:10809
```

It should print details of the HDD, including a line like `export-size: 500107862016 (465G)`. If it says
`Connection refused` or nothing happens for a while, see **If something goes wrong** below.

## Step 5: Connect in PSX XMB Manager

1. Start **PSX XMB Manager**.
2. **Connection Method**: **WSL2 NBD (Recommended)**.
3. **WSL2 Distribution**: **Ubuntu-24.04**.
4. **Enter the IP address of your PSX**: the IP that OPL shows. **NBD Port**: `10809`.
5. Click **Connect**. **Connection Status** turns to connected.

That's it. Partitions, the game libraries, installing projects and the utilities now go through WSL2.
When you open a game partition, Windows Explorer shows it at a path like
`\\wsl.localhost\Ubuntu-24.04\home\<your Linux username>\.local\share\psx-xmb-manager\pfs\...`.

---

## If something goes wrong

| Problem | What to do |
|---|---|
| `wsl --install` says virtualization is off | Turn on **Intel VT-x** / **AMD-V (SVM)** in your BIOS/UEFI, start Windows, run Step 1 again. |
| The app lists no distribution | Run `wsl -l -v`. Ubuntu must be there with VERSION **2** (Step 2). Install it under the **same Windows account** you run PSX XMB Manager with. |
| `nbdinfo` says `Connection refused` | The NBD server is not running in OPL, or the IP is wrong. Start it again in OPL and use the IP it shows. |
| `nbdinfo` times out | The PC cannot reach the PSX. Check both are on the same network. A VPN or firewall on Windows also blocks WSL2. Turn the VPN off, or try **mirrored networking** (below). |
| `/dev/fuse` is missing | Run `wsl --update`, then `wsl --shutdown`, then try again. |
| **Linux PS2 Tools** shows something missing | Click **Install / Repair WSL Backend** (Step 3). It is safe to run again. |
| The app says the helper is outdated | Same: click **Install / Repair WSL Backend**. This happens after updating PSX XMB Manager. |
| Something got stuck after a crash | Click **Recover Connection** in the app. If that does not help, run `wsl --shutdown` and connect again. |
| Option B fails with `$'\r': command not found` | `install-wsl-backend.sh` was saved with Windows line endings (for example by opening and saving it in Notepad). Use Option A instead, or get a fresh copy of the `WSL` folder. |

**Mirrored networking** (only if the PSX cannot be reached and a VPN/firewall is not the reason): create the file
`C:\Users\<you>\.wslconfig` with this content, then run `wsl --shutdown`:

```ini
[wsl2]
networkingMode=mirrored
```

**Logs**: every operation is written to `%LOCALAPPDATA%\PSX XMB Manager\Logs`. Inside Ubuntu, the helper's own log is
`~/.local/state/psx-xmb-manager/helper.log`.

**Don't** run `wsl --shutdown` while PSX XMB Manager is connected or copying something: it stops Linux immediately.

---

## Doing Step 3 completely by hand

Only needed if you want to see or run every command yourself. Open **Ubuntu** from the Start menu and paste these
blocks one at a time.

1. Packages:

   ```bash
   sudo apt-get update
   sudo apt-get install -y ca-certificates git build-essential pkg-config meson ninja-build python3 libnbd-bin fuse3 libfuse-dev
   command -v fusermount || sudo ln -sf "$(command -v fusermount3)" /usr/local/bin/fusermount
   sudo sed -i -E 's/^[[:space:]]*#[[:space:]]*user_allow_other.*/user_allow_other/' /etc/fuse.conf
   grep -q '^user_allow_other' /etc/fuse.conf || echo user_allow_other | sudo tee -a /etc/fuse.conf
   ```

2. hdl_dump. `DEBUG=yes` is required: the release build refuses to open the NBD disk file.

   ```bash
   sudo mkdir -p /opt/psx-xmb-manager-src && sudo chown "$USER" /opt/psx-xmb-manager-src
   cd /opt/psx-xmb-manager-src
   git clone https://github.com/ps2homebrew/hdl-dump.git
   cd hdl-dump
   git checkout 32c296c69cf9c263fcbe035004aa28c345b3b279
   make RELEASE=no DEBUG=yes
   sudo install -m 0755 hdl_dump /usr/local/bin/hdl_dump
   ```

3. pfsshell and pfsfuse. They are built with meson 1.3.2 run from its source: the meson that Ubuntu 22.04
   installs (0.61) stops with `Sandbox violation` on pfsshell.

   ```bash
   cd /opt/psx-xmb-manager-src
   git clone --depth 1 --branch 1.3.2 https://github.com/mesonbuild/meson.git meson-1.3.2
   git clone --recursive https://github.com/ps2homebrew/pfsshell.git
   cd pfsshell
   git checkout 8c92467b3d715c3698f1f8ce63a8a07e214d6c73
   git submodule update --init --recursive
   python3 ../meson-1.3.2/meson.py setup build -Denable_pfsfuse=true -Denable_pfs2tar=true
   python3 ../meson-1.3.2/meson.py compile -C build
   sudo install -m 0755 build/pfsshell build/pfsfuse /usr/local/bin/
   ```

4. The helper. Change the path to where your PSX XMB Manager folder is (`C:\` is `/mnt/c/` in Ubuntu; keep the quotes):

   ```bash
   sudo mkdir -p /usr/local/lib/psx-xmb-manager
   sudo install -m 0755 "/mnt/c/PSX XMB Manager/WSL/psx-xmb-helper.py" /usr/local/lib/psx-xmb-manager/psx-xmb-helper.py
   sudo ln -sf /usr/local/lib/psx-xmb-manager/psx-xmb-helper.py /usr/local/bin/psx-xmb-helper
   psx-xmb-helper --version
   ```

   The last command must print `1`. Then continue with Step 4.
