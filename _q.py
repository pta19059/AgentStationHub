import json,sys
d=json.load(sys.stdin)
print(f"Status: {d['status']}")
tail = d.get('logTail',[])
print(f"LogTail entries: {len(tail)}")
for l in tail[-30:]:
    print(l.get('level',''), l.get('message','')[:220])
logs=d.get('Logs') or []
print('=== LOG context around step 18 errors ===')
idx=[i for i,l in enumerate(logs) if 'Step 18' in (l.get('Message') or '') or 'Registry names may contain' in (l.get('Message') or '') or "containerapp 'ui-angular' does not exist" in (l.get('Message') or '')]
seen=set()
for i in idx:
    for j in range(max(0,i-3), min(len(logs), i+3)):
        if j in seen: continue
        seen.add(j)
        print(logs[j].get('TimestampUtc','')[11:19], (logs[j].get('Level') or ''), (logs[j].get('Message') or '')[:300])
