import json, urllib.request
url = "http://dl.liteloader.com/versions/versions.json"
raw = urllib.request.urlopen(url, timeout=30).read()
print("bytes:", len(raw), "head:", raw[:60])
d = json.loads(raw.decode("utf-8"))
print("top keys:", list(d.keys())[:8])
v = d["versions"]
print("versions count:", len(v))
print("mc keys:", list(v.keys()))
e = v.get("1.12.2")
print("1.12.2 keys:", list(e.keys()) if e else None)
if e:
    for k, val in e.items():
        print("  ", k, "->", type(val).__name__, (list(val.keys())[:6] if isinstance(val, dict) else val))
    snap = e.get("snapshots") or {}
    print("snapshots keys:", list(snap.keys()))
    ll = snap.get("com.mumfrey:liteloader")
    print("liteloader:", json.dumps(ll, ensure_ascii=False)[:600])
# 有没有条目类型不对
bad = []
for mc, entry in v.items():
    for k in ("snapshots", "artefacts", "repo"):
        if k in entry and not isinstance(entry[k], dict):
            bad.append((mc, k, type(entry[k]).__name__))
print("type deviations:", bad[:10])