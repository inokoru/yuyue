#!/usr/bin/env python3
"""English for the Apple catalog: lists the keys without it, or fills them in.

  l10n-apple.py            prints the keys in apple/Yuyue/Localizable.xcstrings that have no
                           English yet, as JSON to fill in
  l10n-apple.py FILE.json  writes the English in FILE.json (key -> English) into the catalog

Run `xcodebuild -exportLocalizations …` (or build in Xcode) first, so new keys are in the catalog.
"""
import glob, json, os, re, sys

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
path = os.path.join(root, 'apple/Yuyue/Localizable.xcstrings')
cat = json.load(open(path, encoding='utf-8'))
strings = cat['strings']
han = re.compile(r'[\u3000-\u303f\u3400-\u9fff\uff00-\uffef]')
swift_lit = re.compile(r'(?:String\(localized:\s*|Text\(|Button\(|Label\()"((?:[^"\\]|\\.)*)"')

if len(sys.argv) < 2:
    # Keys with Chinese in them that the code still uses (Xcode marks the rest stale),
    # plus static Swift UI strings not yet exported into Localizable.xcstrings.
    missing = {k: '' for k, v in strings.items()
               if han.search(k) and v.get('extractionState') != 'stale'
               and v.get('shouldTranslate', True) and 'en' not in v.get('localizations', {})}
    for f in sorted(glob.glob(os.path.join(root, 'apple/Yuyue/**/*.swift'), recursive=True)):
        if f.endswith(('DebugSeed.swift', 'DebugShots.swift')):
            continue
        for m in swift_lit.finditer(open(f, encoding='utf-8').read()):
            raw = m.group(1)
            if '\\(' in raw:
                continue
            k = re.sub(r'\\u\{([0-9A-Fa-f]+)\}', lambda u: chr(int(u.group(1), 16)), raw)
            k = k.replace('\\n', '\n').replace('\\r', '\r').replace('\\t', '\t').replace('\\"', '"').replace('\\\\', '\\')
            if han.search(k):
                v = strings.get(k, {})
                if v.get('shouldTranslate', True) and 'en' not in v.get('localizations', {}):
                    missing.setdefault(k, '')
    print(json.dumps(dict(sorted(missing.items())), ensure_ascii=False, indent=1))
    sys.exit()
for key, en in json.load(open(sys.argv[1], encoding='utf-8')).items():
    if key not in strings:
        strings[key] = {}
    strings[key].setdefault('localizations', {})['en'] = {'stringUnit': {'state': 'translated', 'value': en}}
with open(path, 'w', encoding='utf-8') as f:
    json.dump(cat, f, ensure_ascii=False, indent=2, separators=(',', ' : '))
    f.write('\n')
