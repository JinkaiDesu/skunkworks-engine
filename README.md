# Skunkworks

Skunkworks is a reflective game engine based in the Unity (Mono) runtime. 
It gives you a blank rendering, physics, and gameobject context with clr reflectivity via AOT compilation.

## Built-ins

A rendering surface with reflection, input, a permanent terminal and shell
supervision. Any client that speaks its protocol can drive it, and its terminal
runs whichever shell its configuration names. The Windows distribution ships
[hirari](https://github.com/JinkaiDesu/hirari-shell).

## Terminal

Skunkworks ships no shell. `SKUNK_TERMINAL_CONFIG` names a terminal
configuration, an XML file that selects the executable, its arguments, its
environment and PATH; without one the terminal stays stopped and says so.
`{root}` is the installation and `{user}` the player's user folder:

```xml
<?xml version="1.0" encoding="utf-8"?>
<terminal executable="%SystemRoot%\System32\cmd.exe" arguments="">
  <environment name="EXAMPLE" value="{user}/example" />
  <path value="{root}/Tools" />
</terminal>
```

## Protocol

A running player serves `skunkworks.wire/1` on the named pipe
`\\.\pipe\skunkworks.unity.<process id>`, and gives its terminal that name as
`SKUNK_UNITY_PIPE`. Any client may speak it: the schema is
`Unity/Packages/com.skunkworks.protocol/schema.json`. A connection opens with
`Hello`, then finds roots and types, gets, sets, calls and creates, alone or in
batches whose steps use earlier results. Handles end with their connection, so
keep a lasting name with the `reflection` root's `RegisterRoot` and `find` it
later.

## Demo and building

Skunkworks is a demo and portfolio piece. The download on the website,
`skunkworks v<version>-demo`, is a demo of the engine, not a distribution of it.
To make your own games with Skunkworks, clone this repository, open `Unity/` in
Unity 6000.5.6f1 under your own Unity license and build the Windows player.
Skunkworks needs nothing else: Unity and the .NET CLR.

## License

Copyright (C) 2026 Jinkai. Skunkworks is licensed under the GNU General Public
License, version 2 only (`GPL-2.0-only`); see [LICENSE](LICENSE). It does not
cover Unity: the editor, engine, player runtime and Unity packages stay under
Unity's terms. [NOTICE](NOTICE) lists the third-party files that keep their own
licenses; a release ships their notices under `Licenses/`.
