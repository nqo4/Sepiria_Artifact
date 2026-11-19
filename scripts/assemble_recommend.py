"""recommend_template.html + data.json + placement_db.json + engine.js → Plugin/companion/recommend.html (생성물, 커밋 안 함)."""
from pathlib import Path
ROOT = Path(__file__).resolve().parent.parent
COMPANION = ROOT / "Plugin" / "companion"


def read(name):
    return (COMPANION / name).read_text(encoding="utf-8")


def json_in_script(text):
    # <script type="application/json"> 안에서 "</script>" 가 나오면 태그가 끝나버리므로 "</" 를 "<\/" 로(JSON 의미는 같음)
    return text.replace("</", "<\/")


template = read("recommend_template.html")
out = template.replace("__SEPHIRIA_DATA_JSON__", json_in_script(read("data.json")))
out = out.replace("__SEPHIRIA_PLACEMENT_JSON__", json_in_script(read("placement_db.json")))
out = out.replace("__SEPHIRIA_ENGINE_JS__", read("engine.js"))
assert "__SEPHIRIA_" not in out, "채워지지 않은 자리표시자가 있습니다"

(COMPANION / "recommend.html").write_text(out, encoding="utf-8")
print(f"wrote recommend.html, {len(out)} chars")
