import json
d=json.load(open('_live2.json',encoding='utf-8'))
plan=d.get('Plan') or {}
steps=plan.get('Steps') or []
print('=== ALL STEP 18 VARIANTS ===')
for s in steps:
    if s.get('Id')==18:
        print('CWD:', s.get('WorkingDirectory'))
        print('CMD:', s.get('Command'))
        print('---')
logs=d.get('Logs') or []
print('=== LOG context around step 18 errors ===')
idx=[i for i,l in enumerate(logs) if 'Step 18' in (l.get('Message') or '') or 'Registry names may contain' in (l.get('Message') or '') or "containerapp 'ui-angular' does not exist" in (l.get('Message') or '')]
seen=set()
for i in idx:
    for j in range(max(0,i-3), min(len(logs), i+3)):
        if j in seen: continue
        seen.add(j)
        print(logs[j].get('TimestampUtc','')[11:19], (logs[j].get('Level') or ''), (logs[j].get('Message') or '')[:300])
