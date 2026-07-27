# Playfront

A console-style shell for Windows handhelds, built for the ASUS ROG Xbox Ally.

Playfront replaces `explorer.exe` as the Windows shell. Instead of a desktop you get a
full-screen, gamepad-driven interface designed for a small screen and a controller —
close to what an Xbox gives you, on hardware that has to stay on Windows.

Windows stays underneath and is one button away. That is the point: kernel-level
anti-cheat, GPU drivers and the games themselves need real Windows, so Playfront replaces
the shell rather than the operating system. Not loading the Windows desktop also frees
roughly 2 GB for the game.

> **Early version.** Building from source is currently the only way to run it. Do not set
> Playfront as your shell on a machine you depend on.

## What is in here

| | |
|---|---|
| `src/Playfront.App` | The shell itself. Avalonia UI: home, library, store, settings, video background, gamepad navigation. |
| `src/Playfront.Helper` | A Windows service running as SYSTEM. The interface has no administrator rights; anything privileged goes through this service over a named pipe, with a fixed allow-list of verbs. |
| `build/` | Publishing and installation scripts. |

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows x64.

```powershell
git clone https://github.com/AdriBuho/playfront
cd playfront
dotnet build Playfront.slnx
dotnet run --project src/Playfront.App
```

To produce self-contained folders that run on a machine without .NET installed:

```powershell
powershell -ExecutionPolicy Bypass -File build\Publish-Playfront.ps1 -Clean
```

The helper service is optional for development; install it with
`build\Install-Helper.ps1` from an administrator terminal (`-Uninstall` removes it
cleanly).

Game backgrounds and video are not in this repository — they are 416 MB and ship with
releases instead. The application starts and works without them; artwork simply appears
blank.

## Hardware

Developed and measured on the **ROG Xbox Ally / Ally X**. It should start and work on any
Windows x64 machine — ASUS-specific features (TDP control, fan curves, sensors) detect
their absence, disable themselves and say why rather than failing.

## Licence

**Source-available, not open source.** The code is here so that anyone who runs Playfront
can see exactly what it does — it replaces your Windows shell and installs a service with
system privileges, and you should not have to take that on trust.

You may **read the source** and **run the released app** for personal, non-commercial use.
You may not copy it, modify it, redistribute it, or publish your own version. Full terms in
[`LICENSE.md`](LICENSE.md) — and if you want to do something it does not allow, open an
issue and ask.

Playfront ships artwork and fonts that belong to Microsoft and to individual game
publishers — all of it is listed in [`THIRD-PARTY.md`](THIRD-PARTY.md), along with how to
ask for something to be removed.
