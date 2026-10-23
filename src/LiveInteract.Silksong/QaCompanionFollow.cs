#if LIVEINTERACT_QA
using System.Linq;
using HutongGames.PlayMaker.Actions;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LiveInteract.Silksong;
internal sealed partial class QaHarness
{
    /// <summary>只读记录原作朝向约定与实际渲染变换，配合左右行走截图判断倒走。</summary>
    private JArray FacingProfiles() => new(UnityEngine.Object.FindObjectsByType<OwnedSummon>(FindObjectsSortMode.None).Select(actor =>
    {
        var crawler = actor.GetComponent<Crawler>();
        var walker = actor.GetComponent<Walker>();
        var walkerV2 = actor.GetComponent<WalkerV2>();
        var clockwork = actor.GetComponent<ClockworkHatchling>();
        var clockworkOffset = clockwork == null ? Vector3.zero : (Vector3)AccessTools.Field(typeof(ClockworkHatchling), "offset").GetValue(clockwork);
        return new JObject { ["Id"] = actor.GetInstanceID(), ["Asset"] = actor.AssetKey,
            ["Walker"] = walker == null ? null : new JObject { ["RightScale"] = (float)AccessTools.Field(typeof(Walker), "rightScale").GetValue(walker), ["WalkClip"] = (string)AccessTools.Field(typeof(Walker), "walkClip").GetValue(walker), ["IdleClip"] = (string)AccessTools.Field(typeof(Walker), "idleClip").GetValue(walker), ["SpeedL"] = walker.walkSpeedL, ["SpeedR"] = walker.walkSpeedR },
            ["WalkerV2"] = walkerV2 == null ? null : new JObject { ["RightDirection"] = walkerV2.RightDirection, ["WalkClip"] = walkerV2.WalkAnim, ["IdleClip"] = walkerV2.IdleAnim, ["Speed"] = walkerV2.WalkSpeed },
            ["CrawlerFacesRight"] = crawler == null ? null : (bool?)AccessTools.Field(typeof(Crawler), "spriteFacesRight").GetValue(crawler),
            ["Climber"] = ClimberGroundProfile(actor.GetComponent<Climber>()),
            ["Clockwork"] = clockwork == null ? null : new JObject { ["State"] = clockwork.CurrentState.ToString(),
                ["OffsetX"] = clockworkOffset.x, ["OffsetY"] = clockworkOffset.y,
                ["TargetRadius"] = (float)AccessTools.Field(typeof(ClockworkHatchling), "targetRadius").GetValue(clockwork) },
            // 只读核对原作父子物理所有权，避免把补间巡游错误归为缺少刚体或擅自改约束。
            ["Rigidbodies"] = new JArray(actor.GetComponentsInChildren<Rigidbody2D>(true).Select(rb => new JObject {
                ["Id"] = rb.GetInstanceID(), ["Path"] = Hierarchy(rb.transform), ["Type"] = rb.bodyType.ToString(),
                ["Constraints"] = rb.constraints.ToString(), ["Simulated"] = rb.simulated, ["Gravity"] = rb.gravityScale,
                ["X"] = rb.position.x, ["Y"] = rb.position.y, ["Vx"] = rb.linearVelocity.x, ["Vy"] = rb.linearVelocity.y })),
            ["PhysicalColliders"] = new JArray(actor.GetComponentsInChildren<Collider2D>(true).Select(c => new JObject {
                ["Id"] = c.GetInstanceID(), ["Path"] = Hierarchy(c.transform), ["Type"] = c.GetType().Name,
                ["Enabled"] = c.enabled, ["Active"] = c.gameObject.activeInHierarchy, ["Trigger"] = c.isTrigger,
                ["Layer"] = c.gameObject.layer, ["BodyId"] = c.attachedRigidbody != null ? (int?)c.attachedRigidbody.GetInstanceID() : null,
                ["CenterX"] = c.bounds.center.x, ["CenterY"] = c.bounds.center.y,
                ["Width"] = c.bounds.size.x, ["Height"] = c.bounds.size.y })),
            ["AnimationClips"] = new JArray(actor.GetComponentsInChildren<tk2dSpriteAnimator>(true)
                .Where(a => a.Library != null).SelectMany(a => a.Library.clips)
                .Where(c => c != null).Select(c => c.name).Distinct()),
            ["Actions"] = new JArray(actor.GetComponentsInChildren<PlayMakerFSM>(true).SelectMany(f => f.FsmStates).SelectMany(s => s.Actions)
                .Where(a => a is WalkLeftRight or FaceObjectV2 or FaceDirection).Select(a => new JObject { ["Type"] = a.GetType().Name,
                    ["FacesLeft"] = a is WalkLeftRight walk ? walk.spriteFacesLeft : null,
                    ["FacesRight"] = a is FaceObjectV2 face ? face.spriteFacesRight.Value : a is FaceDirection direction ? direction.spriteFacesRight.Value : null })),
            ["SpriteFlipX"] = actor.GetComponentInChildren<tk2dBaseSprite>()?.FlipX,
            ["Renderers"] = new JArray(actor.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled && r.gameObject.activeInHierarchy).Select(r =>
                new JObject { ["Name"] = r.name, ["WorldScaleX"] = r.transform.lossyScale.x, ["LocalScaleX"] = r.transform.localScale.x,
                    ["RotationZ"] = r.transform.eulerAngles.z, ["Width"] = r.bounds.size.x, ["Height"] = r.bounds.size.y })) };
    }));

    /// <summary>复现原作Climber的只读地面射线；不调用Setup、不修改旋转或碰撞/全局查询设置。</summary>
    private static JObject? ClimberGroundProfile(Climber? climber)
    {
        if (climber == null) return null;
        var col = AccessTools.Field(typeof(Climber), "col").GetValue(climber) as BoxCollider2D;
        if (col == null) return new JObject { ["Enabled"] = climber.enabled, ["ColliderReady"] = false };
        var origin = (Vector2)climber.transform.TransformPoint(new Vector3(0, col.offset.y, 0));
        var direction = (Vector2)climber.transform.TransformVector(Vector3.down);
        var distance = col.size.y / 2 + .5f;
        var filter = new ContactFilter2D { useTriggers = false, useLayerMask = true, layerMask = 256 };
        var hits = new RaycastHit2D[4];
        var count = Physics2D.Raycast(origin, direction, filter, hits, distance);
        var floorHits = new RaycastHit2D[4];
        var floorCount = Physics2D.Raycast(col.bounds.center, Vector2.down, filter, floorHits, 6);
        return new JObject {
            ["Enabled"] = climber.enabled, ["IsTurning"] = climber.IsTurning,
            ["SpriteFacesRight"] = climber.SpriteFacesRight, ["Speed"] = climber.speed,
            ["RotationZ"] = climber.transform.eulerAngles.z, ["NativeMask"] = 256,
            ["NativeLayer"] = LayerMask.LayerToName(8), ["ColliderReady"] = true,
            ["ColliderEnabled"] = col.enabled, ["ColliderTrigger"] = col.isTrigger,
            ["ColliderOffsetX"] = col.offset.x, ["ColliderOffsetY"] = col.offset.y,
            ["RayX"] = origin.x, ["RayY"] = origin.y, ["DirectionX"] = direction.x, ["DirectionY"] = direction.y,
            ["Distance"] = distance, ["Hits"] = new JArray(hits.Take(count).Select(h => new JObject {
                ["Name"] = h.collider.name, ["X"] = h.point.x, ["Y"] = h.point.y,
                ["NormalX"] = h.normal.x, ["NormalY"] = h.normal.y, ["Distance"] = h.distance })),
            ["WorldFloorHits"] = new JArray(floorHits.Take(floorCount).Select(h => new JObject {
                ["Name"] = h.collider.name, ["X"] = h.point.x, ["Y"] = h.point.y, ["Distance"] = h.distance })) };
    }

    private GameObject? terrainFixture;

    /// <summary>隔离验收专用地形：只有标准地形碰撞，不改怪物、攻击或生命。</summary>
    private JObject ConfigureTerrain(JObject command)
    {
        RequireIsolation();
        if(terrainFixture!=null)UnityEngine.Object.DestroyImmediate(terrainFixture);
        if((bool?)command["Clear"]==true)return new JObject{["Cleared"]=true};
        var hero=HeroController.SilentInstance;
        var point=hero.transform.position;
        var floor=point.y-.7f;
        terrainFixture=new GameObject("QA disposable terrain fixture");
        var layer=LayerMask.NameToLayer("Terrain");
        void Box(string name,float x,float y,float width,float height,float angle=0)
        {
            var item=new GameObject(name){layer=layer};item.transform.SetParent(terrainFixture.transform);
            item.transform.position=new Vector3(point.x+x,y,0);item.transform.eulerAngles=new Vector3(0,0,angle);
            item.AddComponent<BoxCollider2D>().size=new Vector2(width,height);
        }
        Box("QA floor",0,floor-.5f,30,1);
        var layout=(string?)command["Layout"]??"wall";
        switch(layout)
        {
            case "wall":Box("QA solid wall",0,floor+2, .7f,4);break;
            case "slope":Box("QA incline",-2.5f,floor+.5f,5,.3f,12);Box("QA upper floor",5,floor+.5f,10,1);break;
            case "steps":
                for(var step=0;step<6;step++)Box("QA step "+step,-4+step,floor+(step+1)*.075f,1,(step+1)*.15f);
                Box("QA upper floor",6,floor+.45f,10,.9f);break;
            case "platform":Box("QA raised platform",0,floor+3,9,.4f);break;
            default:throw new System.ArgumentException("Unknown QA terrain: "+layout);
        }
        Physics2D.SyncTransforms();
        return new JObject{["Layout"]=layout,["X"]=point.x,["FloorY"]=floor,["Colliders"]=terrainFixture.GetComponentsInChildren<Collider2D>().Length};
    }

    private JObject FollowSnapshot()
    {
        var hero=HeroController.SilentInstance;
        return new JObject { ["Frame"]=Time.frameCount,["Time"]=Time.realtimeSinceStartup,
            ["FriendlyCollisionPairs"]=FriendlyCollisionPairs.Count,["Focused"]=Application.isFocused,["TargetFrameRate"]=Application.targetFrameRate,["VSyncCount"]=QualitySettings.vSyncCount,
            ["HeroHp"]=PlayerData.instance.health,["HeroBlueHp"]=PlayerData.instance.healthBlue,
            ["HeroX"]=hero.transform.position.x,["HeroY"]=hero.transform.position.y,["FocusId"]=CompanionFocus.For(hero.transform.position)?.GetComponentInParent<OwnedSummon>()?.GetInstanceID(),
            ["Projectiles"]=new JArray(UnityEngine.Object.FindObjectsByType<NativeAllyProjectile>(FindObjectsSortMode.None)
                .Where(p=>p.Owner!=null&&p.gameObject!=p.Owner.gameObject&&p.gameObject.activeInHierarchy).Select(p=>
                {
                    var body=p.GetComponent<Rigidbody2D>();var target=p.Owner!.TargetObject;
                    return new JObject{["Id"]=p.GetInstanceID(),["Name"]=p.name,["X"]=p.transform.position.x,["Y"]=p.transform.position.y,
                        ["Vx"]=body?.linearVelocity.x,["Vy"]=body?.linearVelocity.y,["Gravity"]=body?.gravityScale,
                        ["TargetX"]=target!=null?target.transform.position.x:0,["TargetY"]=target!=null?target.transform.position.y:0};
                })),
            ["Actors"]=new JArray(UnityEngine.Object.FindObjectsByType<OwnedSummon>(FindObjectsSortMode.None).Select(a=>
            {
                var ally=a.GetComponent<NativeEnemyAlly>();var rb=a.GetComponent<Rigidbody2D>();
                var animator=a.GetComponentInChildren<tk2dSpriteAnimator>();var clock=a.GetComponent<ClockworkHatchling>();
                return new JObject { ["Id"]=a.GetInstanceID(),["RootId"]=a.gameObject.GetInstanceID(),["Asset"]=a.AssetKey,["Name"]=a.name,
                    ["X"]=a.transform.position.x,["Y"]=a.transform.position.y,["Z"]=a.transform.position.z,["Locomotion"]=ally?.LocomotionMode,["StepProbe"]=ally?.StepProbe,["SafeRetarget"]=ally?.CanChangeTarget,["ScaleX"]=a.transform.localScale.x,
                    ["Hp"]=a.GetComponentInChildren<HealthManager>()?.hp,["Gravity"]=rb?.gravityScale,["Vx"]=rb?.linearVelocity.x,["Vy"]=rb?.linearVelocity.y,["Visible"]=ally?.IsOnScreen,
                    ["HasEnemy"]=ally?.HasEnemy,["Target"]=ally?.TargetObject?.name,
                    ["TargetHp"]=ally?.TargetObject?.GetComponentInChildren<HealthManager>()?.hp,["TargetId"]=ally?.TargetObject?.GetComponentInParent<OwnedSummon>()?.GetInstanceID(),
                    ["Clip"]=animator?.CurrentClip?.name,["ClipTime"]=animator?.CurrentClip==null?null:animator.ClipTimeSeconds,
                    ["ClockTargetId"]=clock==null?null:(AccessTools.Field(typeof(ClockworkHatchling),"target").GetValue(clock) as GameObject)?.GetComponentInParent<OwnedSummon>()?.GetInstanceID(),
                    ["ClockState"]=clock==null?null:AccessTools.Field(typeof(ClockworkHatchling),"currentState").GetValue(clock)?.ToString(),
                    ["Fsms"]=new JArray(a.GetComponentsInChildren<PlayMakerFSM>(true).Select(f=>new JObject{["Fsm"]=f.FsmName,["State"]=f.ActiveStateName})) };
            })) };
    }
}
#endif

