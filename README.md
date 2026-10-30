# Stellar.MultiTool

A StellarResonance plugin with small quality-of-life tools, all in one hub window:

- **Mute Instruments**: silence other players' instruments (Blacklist or Whitelist). Their animation and nameplate stay as they are.
- **Auto-Accept Invites**: accept party and social invites from the players you list, and optionally every party join request. Players can be added by UID or with the **Auto-Accept** button on their name card.
- **Language Unlock**: show every text and voice language this game client ships in the game's Settings › Language.

![The MultiTool hub](docs/hub.png)

## Build

The plugin builds against the published SDK from NuGet.org, so you don't need a framework checkout or a game install:

```bash
dotnet build -c Release
```

Requires Stellar framework 2.28.0 or newer. For a local deploy, copy `Local.props.example` to `Local.props` and set `GameInstallDir`.

## Release

Published through the [plugin registry](https://github.com/StellarProtocol/StellarResonancePlugins). The manifest pins this repo and a commit, and CI builds it in an isolated container.

## License

AGPL-3.0-or-later.
