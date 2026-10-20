"""Only drives the explicitly isolated QA game through the workspace request protocol."""
from pathlib import Path
import json,time,uuid,sys
ROOT=Path(__file__).resolve().parents[1]
CONTROL=ROOT/'artifacts/audit-0.3/control'
REPORT=ROOT/('artifacts/audit-0.3/spawn-audit-'+(sys.argv[1] if len(sys.argv)>1 else 'latest')+'.json')
LOG=Path(r'C:\Program Files (x86)\Steam\steamapps\common\Hollow Knight Silksong\BepInEx\LogOutput.log')
def request(command,timeout=35):
    id='matrix-'+uuid.uuid4().hex
    file=CONTROL/(id+'.request.json')
    temp=CONTROL/(id+'.tmp');temp.write_text(json.dumps(command,ensure_ascii=False),encoding='utf-8');temp.rename(file)
    end=time.monotonic()+timeout
    while time.monotonic()<end:
        out=CONTROL/(id+'.result.json')
        if out.exists():return json.loads(out.read_text(encoding='utf-8'))
        time.sleep(.05)
    raise RuntimeError('No actual game receipt: '+id)
def qa(op,**extra):
    value=request({'Kind':'qa','Operation':op,**extra})['Result']
    if not value['Success']:raise RuntimeError(value['Message'])
    return json.loads(value['Message'])
def act(id,args=None,duration=2):
    return request({'Kind':'action','Request':{'ActionId':id,'Arguments':args or {},'DurationSeconds':duration,'AllowPersistent':True,'DisplayText':'隔离怪物验收','Triggerer':'测试观众小明','EffectText':id}})['Result']
state=qa('state')
if not state.get('Isolated') or not state.get('CanAct'):raise RuntimeError('Need isolated and controllable QA save; original save is never changed by this runner.')
rows=[]
for monster in json.loads((ROOT/'src/LiveInteract.Contracts/monsters.json').read_text(encoding='utf-8')):
    try:
        qa('prepare',Unlock=True)
        act('health.invincible',duration=8)
        offset=LOG.stat().st_size
        before=qa('state')
        receipt=act(monster['Id'],{'count':'2','offsetX':'4','offsetY':'1'},8)
        time.sleep(.15); immediate=qa('state')
        time.sleep(.7); after=qa('state')
        enemies=[e for e in after.get('Enemies',[]) if e['Name']==monster['ObjectName']+' [LiveInteract]']
        with LOG.open('rb') as file:file.seek(offset);text=file.read().decode('utf-8',errors='replace')
        errors=[line for line in text.splitlines() if '[Error' in line or 'Exception:' in line or 'FSM Error' in line]
        row={'Id':monster['Id'],'Name':monster['Name'],'Scene':after['Scene'],'Arguments':{'count':2},'Receipt':receipt,'SpawnedMatching':len(enemies),'Alive':all(e['Hp']>0 for e in enemies),'UnityErrors':errors[:12],'Immediate':immediate,'After':after,'Passed':receipt['Success'] and len(enemies)==2 and not errors}
        rows.append(row)
        REPORT.write_text(json.dumps({'Complete':False,'Count':len(rows),'Results':rows},ensure_ascii=False,indent=2),encoding='utf-8')
        print(monster['Id'],'PASS' if row['Passed'] else 'FAIL',len(enemies),'errors',len(errors),flush=True)
        if after.get('Hero',{}).get('Dead'):break
    except Exception as exception:
        rows.append({'Id':monster['Id'],'Name':monster['Name'],'Passed':False,'Error':str(exception)})
        REPORT.write_text(json.dumps({'Complete':False,'Count':len(rows),'Results':rows},ensure_ascii=False,indent=2),encoding='utf-8')
        print(monster['Id'],'ERROR',exception,flush=True)
        break
qa('reset')
REPORT.write_text(json.dumps({'Complete':len(rows)==27,'Count':len(rows),'Passed':sum(bool(row.get('Passed')) for row in rows),'Results':rows},ensure_ascii=False,indent=2),encoding='utf-8')
