# PSX XMB Manager tests

Two suites cover the WSL2 NBD storage backend. Neither needs a PSX, an NBD server or Windows.

## Backend unit tests (VB.NET, xUnit)

`PSXXMBManager.Tests` links the backend sources (`Storage/*.vb` except `LocalWindowsBackend.vb`, plus
`Classes/Structs.vb`) and the two embedded WSL scripts into a .NET 8 test assembly. A fake `wsl.exe`
(`FakeProcessRunner.vb`) answers every helper call, so the tests also run on Linux and in CI.

```
dotnet test Tests/PSXXMBManager.Tests
```

Needs the .NET 8 SDK. The project is not part of `PSX XMB Manager.sln`, so building the application in
Visual Studio does not require it.

Covered: `wsl --list` parsing (UTF-16, localized output), distro selection, `/etc/os-release`, `hdl_toc`
line parsing, IPv4/port/partition/mount-id/distro validation, UNC path building, exact size matching,
Windows argument quoting, the connection state machine, command classification and timeouts, the helper
JSON envelope and error codes, and the backend itself (probe results, connect/disconnect, stale state,
busy and mounted-partition refusals, cancellation and `MountRemoved`).

## Helper unit tests (Python, unittest)

```
python3 -m unittest discover -s Tests/helper -v
```

Needs Python 3.8 or later on Linux. Each test points `HOME` at a temporary folder, and stand-in tool
scripts on `PATH` let well-formed requests reach the connection checks.

Covered: input validation, mount-id safety, `/proc/self/mountinfo` unescaping, `modify_header` header
staging for case-sensitive folders, and the command-line contract (`--version`, unknown verbs, bad JSON,
disallowed `hdl_dump` verbs, other devices, line-break injection into `pfsshell`, status while
disconnected or with stale state).

## Not covered here

The helper's connect, mount, backup and restore paths need FUSE, nbdfuse and an NBD server. They were
exercised end to end against `nbdkit`-served PS2 HDD images in a Linux container, not by these suites.
Real PSX hardware and the Windows side of WSL (`\\wsl.localhost` visibility, `wsl.exe` behaviour) still
need a manual check on Windows.
