#!/usr/bin/env python3
"""Turns each design-canvas artboard in source/*.dc.html into a plain HTML page in html/, so a browser can render it.

An artboard is a Design Component: its markup sits inside <x-dc>, its page basics inside <helmet>, and its tweaks in the
data-props of its script. This keeps the markup and the helmet, links source/corvid.css, and fills the one hole the
artboards use, {{theme}}, with the theme the artboard declares as its default (light, or dark for Board and Audit).
"""
import json
import pathlib
import re

here = pathlib.Path(__file__).resolve().parent
source, out = here / "source", here / "html"
out.mkdir(exist_ok=True)
(out / "corvid.css").write_text((source / "corvid.css").read_text())

for artboard in sorted(source.glob("*.dc.html")):
    text = artboard.read_text()
    body = text.split("<x-dc>", 1)[1].split("</x-dc>", 1)[0]
    helmet = re.search(r"<helmet>(.*?)</helmet>", body, re.S).group(1)
    body = re.sub(r"<helmet>.*?</helmet>", "", body, flags=re.S)
    props = json.loads(re.search(r"data-props='(.*?)'", text, re.S).group(1))
    body = body.replace("{{theme}}", props.get("theme", {}).get("default", "light"))
    title = re.search(r"<title>(.*?)</title>", text).group(1)
    page = (f'<!doctype html><html lang="en"><head><meta charset="utf-8"><title>{title}</title>'
            f'<link rel="stylesheet" href="corvid.css">{helmet}</head><body style="margin:0">{body}</body></html>')
    (out / artboard.name.replace(".dc.html", ".html")).write_text(page)
    print(artboard.name)
