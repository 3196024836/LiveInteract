"""Isolated native companion lifecycle/limit/combat proof; no real platform traffic or player input."""
from pathlib import Path
import json,time,uuid
ROOT=Path(__file__).resolve().parents[1];C=ROOT/'artifacts/audit-0.3/control';OUT=ROOT/'artifacts/default-v2';LOG=Path(r'C:\Program Files (x86)\Steam\steamapps\common\Hollow Knight Silksong\BepInEx\LogOutput.log')
def req(c):
    id='ally-'+uuid.uuid4().hex;f=C/(id+'.request.json');t=f.with_suffix('.tmp');t.write_text(json.dumps(c,ensure_ascii=False),encoding='utf8');t.rename(f);until=time.monotonic()+40
    while time.monotonic()<until:
        p=C/(id+'.result.json')
        try:
            if p.exists():return json.loads(p.read_text(encoding='utf8'))['Result']
        except (PermissionError,json.JSONDecodeError):pass
        time.sleep(.04)
    raise TimeoutError(id)
def qa(op,**kw):
    r=req({'Kind':'qa','Operation':op,**kw})
    if not r['Success']:raise RuntimeError(r['Message'])
    return json.loads(r['Message'])
def act(id,args=None,name='礼物观众小明'):
    return req({'Kind':'action','Request':{'ActionId':id,'Arguments':args or {},'AllowPersistent':True,'Triggerer':name}})
def allies(s):return [a for a in s.get('OwnedCompanions',[]) if a['Active']]
def prepare():qa('prepare',Unlock=False);qa('restore-position');time.sleep(.1)
if not qa('state')['Isolated']:raise RuntimeError('Isolated save required')
rows=[];offset=LOG.stat().st_size
prepare();before=qa('state');r=act('ally.clockwork',{'count':'2','value':'8','lifetime':'5'});time.sleep(.7);during=qa('state');time.sleep(5);after=qa('state')
rows.append({'Case':'lifetime-and-attribution','Receipt':r,'During':during,'After':after,'Passed':r['Success'] and len(allies(during))==2 and not allies(after) and sum(x['Name']=='礼物观众小明' and x['Friendly'] for x in during['SummonLabels'])==2 and during['Tools']==before['Tools'] and after['Player']['health']==before['Player']['health']})
prepare();r=act('ally.clockwork',{'count':'6','value':'8','lifetime':'5'});time.sleep(.3);cap=qa('state');held=act('ally.clockwork',{'count':'1','value':'8','lifetime':'5'});time.sleep(5.1);retry=act('ally.clockwork',{'count':'1','value':'8','lifetime':'5'});retryState=qa('state')
rows.append({'Case':'cap-waits-without-consuming-unit','CountAtCap':len(allies(cap)),'Held':held,'Retry':retry,'CountAfterRetry':len(allies(retryState)),'Passed':r['Success'] and len(allies(cap))==6 and held['Retryable'] and held['ErrorCode']=='ALLY_LIMIT' and retry['Success'] and len(allies(retryState))==1})
prepare();spawn=act('spawn.bone-goomba',{'count':'1','offsetX':'4','offsetY':'1'},'整活观众小红');target=next(e for e in qa('state')['Enemies'] if '[LiveInteract]' in e['Name']);act('enemy.health',{'enemy':target['Name']+'#'+str(target['Id']),'value':'200'});qa('fixture-safe-enemy');before=qa('state');r=act('ally.clockwork',{'count':'1','value':'8','lifetime':'20'});samples=[]
for n in range(20):
    time.sleep(.35);s=qa('state');samples.append({'EnemyHp':next((e['Hp'] for e in s['Enemies'] if e['Id']==target['Id']),0),'HeroHp':s['Player']['health'],'CanInput':s['Hero']['CanInput'],'SkillState':s['Hero']['SkillState'],'Allies':allies(s)})
last=samples[-1];capture=req({'Kind':'qa','Operation':'capture'})
rows.append({'Case':'actual-unassisted-combat-no-player-control','Receipt':r,'Before':before,'Samples':samples,'CaptureId':capture['RequestId'],'Passed':r['Success'] and last['EnemyHp']<200 and all(x['HeroHp']==before['Player']['health'] and x['CanInput'] and x['SkillState']=='Idle' for x in samples) and any(a['LastTargetDamaged']==target['Name'] for x in samples for a in x['Allies']) and all(a['DamageHeroEnabled']==0 and not a['HasToolSource'] for x in samples for a in x['Allies'])})
with LOG.open('rb') as f:f.seek(offset);errors=[l for l in f.read().decode('utf8',errors='replace').splitlines() if '[Error' in l or 'Exception:' in l]
report={'ActualGame':True,'IsolatedSave':True,'RealDouyinTraffic':False,'PlayerInputsIssued':False,'Count':len(rows),'UnityErrors':errors,'Results':rows,'Passed':all(r['Passed'] for r in rows) and not errors}
(OUT/'companion-audit-final.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf8');print(json.dumps({'Passed':report['Passed'],'Cases':[{ 'Case':r['Case'],'Passed':r['Passed']} for r in rows],'UnityErrors':errors},ensure_ascii=True,indent=2))
if not report['Passed']:raise SystemExit(1)
