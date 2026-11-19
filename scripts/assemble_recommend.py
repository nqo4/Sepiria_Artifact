from pathlib import Path
ROOT = Path(__file__).resolve().parent.parent
COMPANION = ROOT / "Plugin" / "companion"

template = open(f"{COMPANION}/recommend_template.html", encoding="utf-8").read()
data_json = open(f"{COMPANION}/data.json", encoding="utf-8").read()
placement_json = open(f"{COMPANION}/placement_db.json", encoding="utf-8").read()
engine_js = open(f"{COMPANION}/engine.js", encoding="utf-8").read()

out = template.replace("__SEPHIRIA_DATA_JSON__", data_json)
out = out.replace("__SEPHIRIA_PLACEMENT_JSON__", placement_json)
out = out.replace("__SEPHIRIA_ENGINE_JS__", engine_js)

with open(f"{COMPANION}/recommend.html", "w", encoding="utf-8") as f:
    f.write(out)

print(f"wrote recommend.html, {len(out)} chars")
