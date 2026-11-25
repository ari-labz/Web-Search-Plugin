#!/usr/bin/env python3
"""Packs this plugin into websearch.ari: manifest.json, icon.png and the .cs files. Usage: pack.py <version>"""
import json, sys, zipfile, glob, os

version = sys.argv[1] if len(sys.argv) > 1 else sys.exit("usage: pack.py <version>")
here = os.path.dirname(os.path.abspath(__file__))
manifest = {
    "id": "websearch",
    "name": "Web Search",
    "description": "Lets Ari search the web through SearXNG. Runs SearXNG for you in Docker, or uses one you already have.",
    "author": "first-party",
    "version": version,
}
out = os.path.join(here, "websearch.ari")
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    z.writestr("manifest.json", json.dumps(manifest, indent=2))
    z.write(os.path.join(here, "icon.png"), "icon.png")
    for cs in sorted(glob.glob(os.path.join(here, "*.cs"))):
        z.write(cs, os.path.basename(cs))
print(out)
