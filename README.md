# ARI Web Search plugin

Lets Ari search the web through [SearXNG](https://github.com/searxng/searxng). By default it runs SearXNG for you in Docker; or point it at a SearXNG you already run.

A plugin for [ARI](https://github.com/ari-labz/A.R.I). Install it from ARI's Plugins page (Browse), or download `websearch.ari` from the latest release here and drop it onto ARI. Needs ARI 0.12.0 or later, where web search left ARI's core. Reading web pages (`fetch_page`, Ari's browser) stays built into ARI.

## What it adds
- **`search_web`**: searches the web and returns numbered results (title, address, snippet). Results that share no word with the query are dropped, since search engines pad an empty answer with unrelated pages. When engines are rate limited, the result says so.

## Settings
| Setting | What it does |
|---|---|
| Use my own SearXNG | Off (default): the plugin runs SearXNG in Docker as container `ari-searxng` on `127.0.0.1:8085`, and starts Docker Desktop if it isn't running. On: it uses the address below. |
| My SearXNG address | Where searches go with "Use my own SearXNG" on, e.g. `http://192.168.1.50:8080`. Its JSON output must be on (`search: formats: [html, json]` in its `settings.yml`). |

Running SearXNG for you needs Docker installed (Docker Desktop on a Mac or Windows). The plugin writes SearXNG's settings once into its own folder (`Plugins/websearch/searxng/`), reuses an existing `ari-searxng` container, and stops it when you switch the plugin off.

## Build the package
```
python3 pack.py 1.0.0
```
writes `websearch.ari` (a zip of `manifest.json`, `icon.png` and the `.cs` files). ARI compiles the source itself when it installs it.

## Release
Attach `websearch.ari` to a GitHub release tagged `v<version>`, then add the version to the catalogue in [A.R.I-Plugins](https://github.com/ari-labz/A.R.I-Plugins).
