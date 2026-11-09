# Skunkworks

Skunkworks is a minimal rendering host built on the Unity (Mono) runtime.
It gives you a blank canvas: a window, Unity's renderer and captured input, and nothing else.

## Built-ins

A rendering surface and input capture. Skunkworks runs no world, simulation,
shell or scripting interface of its own. It is the host for an external, native
computation: it collects input for that computation and presents what the
computation resolves. That computation is a separate project.

## Demo and building

Skunkworks is a demo and portfolio piece. The download on the website,
`skunkworks v<version>-demo`, is a demo of the host, not a distribution of it.
To build on Skunkworks, clone this repository, open `Unity/` in
Unity 6000.5.6f1 under your own Unity license and build the Windows player.
Skunkworks needs nothing else: Unity and the .NET CLR.

Versions up to 0.6.1 were a reflective engine with a terminal and a
named-pipe protocol; that source is in this repository's history.

## License

Copyright (C) 2026 Jinkai. Skunkworks is licensed under the GNU General Public
License, version 2 only (`GPL-2.0-only`); see [LICENSE](LICENSE). It does not
cover Unity: the editor, engine, player runtime and Unity packages stay under
Unity's terms; see [NOTICE](NOTICE). A release ships both under `Licenses/`.
