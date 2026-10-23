using System;
using System.Collections.Generic;
using System.Linq;
using LiveInteract.Contracts;
using UnityEngine;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;
namespace LiveInteract.Silksong;

/// <summary>只为召唤本体增加轮廓渲染，不复制角色脚本、碰撞体或攻击组件。</summary>
[DefaultExecutionOrder(30000)]
internal sealed class SummonFactionOutline : MonoBehaviour
{
#if LIVEINTERACT_QA
    // 仅隔离渲染诊断使用，正式编译不包含可调排序入口。
    internal static int QaOrderOffset;
#endif
    private readonly Dictionary<Renderer,Part> parts=new();
    private float nextScan;
    private bool friendly;
    private bool reported;

    /// <summary>复制已有召唤物时移除继承的装饰网格，避免描边递归叠加。</summary>
    public static void Attach(GameObject root,bool friendly)
    {
        var outline=root.GetComponent<SummonFactionOutline>()??root.AddComponent<SummonFactionOutline>();
        outline.ReleaseParts();
        foreach(var inherited in root.GetComponentsInChildren<SummonOutlineVisual>(true))Object.DestroyImmediate(inherited.gameObject);
        outline.friendly=friendly;outline.nextScan=0;outline.reported=false;
    }

    /// <summary>动画更新后同步当前网格与UV；新增身体部件按低频扫描收录。</summary>
    private void LateUpdate()
    {
        var camera=Camera.main;
        if(camera==null||GameActions.Current?.FactionOutlineEnabled==false){HideParts();return;}
        if(Time.realtimeSinceStartup>=nextScan)
        {
            nextScan=Time.realtimeSinceStartup+.5f;
            foreach(var renderer in GetComponentsInChildren<Renderer>(true))
            {
                if(renderer.GetComponent<SummonOutlineVisual>()!=null||parts.ContainsKey(renderer))continue;
                // Screen材质是原作灯光薄雾，不是身体；绘成实体颜色会产生巨大不透明光团。
                if(renderer.sharedMaterials.Any(material=>material!=null&&material.shader!=null&&
                    !SummonOutlineStyle.ShouldOutlineShader(material.shader.name)))continue;
                if(renderer is not SpriteRenderer&&renderer is not SkinnedMeshRenderer&&
                    (renderer is not MeshRenderer||renderer.GetComponent<MeshFilter>()==null))continue;
                parts.Add(renderer,new Part(renderer,friendly));
            }
            foreach(var removed in parts.Keys.Where(r=>r==null).ToArray()){parts[removed].Dispose();parts.Remove(removed);}
        }
        foreach(var part in parts.Values)
        {
            try{part.Update(camera,(float)SummonOutlineStyle.NormalizeWidth(GameActions.Current?.FactionOutlineWidth??2));}
            catch(Exception error)
            {
                part.Hide();part.RetryAt=Time.realtimeSinceStartup+2;
                if(!reported){reported=true;GameActions.Current?.ReportOutlineIssue(gameObject.name+"："+error.Message);}
            }
        }
    }

    /// <summary>本体隐藏时立即隐藏辅助轮廓。</summary>
    private void OnDisable()=>HideParts();
    private void OnDestroy()=>ReleaseParts();
    private void HideParts(){foreach(var part in parts.Values)part.Hide();}
    private void ReleaseParts(){foreach(var part in parts.Values)part.Dispose();parts.Clear();}

    /// <summary>多方向轮廓合并到一个辅助网格，每个原渲染器只增加一个渲染器。</summary>
    private sealed class Part : IDisposable
    {
        private readonly Renderer source;
        private readonly SpriteRenderer? sprite;
        private readonly SkinnedMeshRenderer? skin;
        private readonly MeshFilter? filter;
        private readonly GameObject visual;
        private readonly MeshRenderer target;
        private readonly MeshFilter targetFilter;
        private readonly Mesh mesh;
        private readonly Mesh? baked;
        private readonly bool friendly;
        private readonly List<Vector3> vertices=new(),normals=new(),outputVertices=new(),outputNormals=new();
        private readonly List<Vector2> uvs=new(),outputUvs=new();
        private readonly List<Color32> colors=new(),outputColors=new();
        private readonly List<int> indices=new(),outputIndices=new();
        private readonly List<Material> sourceMaterials=new();
        private readonly List<Lease> leases=new();
        private readonly List<Material> materials=new();
        private readonly MaterialPropertyBlock global=new(),specific=new(),block=new();
        private Sprite? lastSprite;
        private int spriteFlip;
        public float RetryAt;

        public Part(Renderer source,bool friendly)
        {
            this.source=source;this.friendly=friendly;
            sprite=source as SpriteRenderer;skin=source as SkinnedMeshRenderer;filter=source.GetComponent<MeshFilter>();
            mesh=new Mesh{name="LiveInteract faction contour",hideFlags=HideFlags.DontSave,indexFormat=IndexFormat.UInt32};mesh.MarkDynamic();
            if(skin!=null)baked=new Mesh{name="LiveInteract contour bake",hideFlags=HideFlags.DontSave};
            visual=new GameObject("[LiveInteract faction outline]"){hideFlags=HideFlags.HideInHierarchy|HideFlags.DontSave,layer=source.gameObject.layer};
            visual.transform.SetParent(source.transform,false);visual.AddComponent<SummonOutlineVisual>();
            targetFilter=visual.AddComponent<MeshFilter>();targetFilter.sharedMesh=mesh;
            target=visual.AddComponent<MeshRenderer>();target.enabled=false;
            target.shadowCastingMode=ShadowCastingMode.Off;target.receiveShadows=false;
            target.lightProbeUsage=LightProbeUsage.Off;target.reflectionProbeUsage=ReflectionProbeUsage.Off;
        }

        /// <summary>保持原排序和原纹理透明度，仅在本体后方偏移轮廓。</summary>
        public void Update(Camera camera,float width)
        {
            if(source==null||!source.enabled||source.forceRenderingOff||!source.gameObject.activeInHierarchy||!source.isVisible||Time.realtimeSinceStartup<RetryAt){Hide();return;}
            if(sprite!=null&&sprite.sprite==null){Hide();return;}
            var meshSource=ReadSource();
            if(meshSource==null&&sprite==null){Hide();return;}
            source.GetSharedMaterials(sourceMaterials);
            var count=sprite!=null?1:meshSource!.subMeshCount;
            if(count==0||sourceMaterials.Count==0||sourceMaterials.Any(material=>material==null)){Hide();return;}
            // SortingGroup内相同排序值不按子物体Z分先后，轮廓必须明确排在原身体之后。
            target.sortingLayerID=source.sortingLayerID;
            target.sortingOrder=SummonOutlineStyle.SortingOrder(source.sortingOrder,
                source.GetComponentsInParent<SortingGroup>(true).Any(group=>group.isActiveAndEnabled));
#if LIVEINTERACT_QA
            target.sortingOrder+=QaOrderOffset;
#endif
            var screen=camera.WorldToScreenPoint(source.bounds.center);
            if(screen.z<=0){Hide();return;}
            var origin=camera.ScreenToWorldPoint(screen);
            var dx=source.transform.InverseTransformVector(camera.ScreenToWorldPoint(screen+Vector3.right*width)-origin);
            var dy=source.transform.InverseTransformVector(camera.ScreenToWorldPoint(screen+Vector3.up*width)-origin);
            var behind=source.transform.InverseTransformVector(camera.transform.forward*.002f);
            visual.transform.localPosition=behind;visual.transform.localScale=Vector3.one;
            var readable=sprite!=null||meshSource!.isReadable;
            if(readable)
            {
                if(sprite==null){meshSource!.GetVertices(vertices);meshSource.GetNormals(normals);meshSource.GetUVs(0,uvs);meshSource.GetColors(colors);}
                if(vertices.Count==0||uvs.Count!=vertices.Count){Hide();return;}
                BuildGeometry(dx,dy,count);
                targetFilter.sharedMesh=mesh;
            }
            else
            {
                // 原作只读静态网格复用GPU数据，仍只有一个辅助渲染器；不强制开启资源读写。
                var bounds=meshSource!.bounds;
                var padding=new Vector3(Mathf.Sqrt(dx.x*dx.x+dy.x*dy.x),Mathf.Sqrt(dx.y*dx.y+dy.y*dy.y),0)*2;
                var scale=new Vector3(1+padding.x/Math.Max(.001f,bounds.size.x),1+padding.y/Math.Max(.001f,bounds.size.y),1);
                visual.transform.localScale=scale;visual.transform.localPosition=behind+bounds.center-Vector3.Scale(bounds.center,scale);
                targetFilter.sharedMesh=meshSource;
            }
            SynchronizeMaterials(count);
            if(readable)
            {
                for(var sub=0;sub<count;sub++)
                {
                    if(sprite==null)meshSource!.GetTriangles(indices,sub);
                    outputIndices.Clear();
                    for(var sample=0;sample<SummonOutlineStyle.Directions.Count;sample++)
                        foreach(var index in indices)outputIndices.Add(index+sample*vertices.Count);
                    mesh.SetTriangles(outputIndices,sub,false);
                }
                mesh.RecalculateBounds();
            }
            target.enabled=materials.Count==count;
        }

        /// <summary>复制当前帧数据到辅助缓冲，不写回原作Mesh、Sprite或材质。</summary>
        private Mesh? ReadSource()
        {
            if(sprite!=null)
            {
                var current=sprite.sprite;if(current==null)return null;
                var flip=(sprite.flipX?1:0)|(sprite.flipY?2:0);
                if(lastSprite!=current||spriteFlip!=flip)
                {
                    lastSprite=current;spriteFlip=flip;vertices.Clear();uvs.Clear();indices.Clear();normals.Clear();colors.Clear();
                    foreach(var point in current.vertices)vertices.Add(new Vector3(sprite.flipX?-point.x:point.x,sprite.flipY?-point.y:point.y,0));
                    uvs.AddRange(current.uv);indices.AddRange(current.triangles.Select(index=>(int)index));
                }
                colors.Clear();for(var i=0;i<vertices.Count;i++)colors.Add(sprite.color);
                return null;
            }
            if(skin!=null){skin.BakeMesh(baked!,false);return baked;}
            return filter!=null?filter.sharedMesh:null;
        }

        /// <summary>八个屏幕方向合并提交；放大、缩小和镜头缩放不改变像素粗细。</summary>
        private void BuildGeometry(Vector3 dx,Vector3 dy,int submeshes)
        {
            outputVertices.Clear();outputUvs.Clear();outputColors.Clear();outputNormals.Clear();
            foreach(var direction in SummonOutlineStyle.Directions)
            {
                var offset=dx*direction.X+dy*direction.Y;
                for(var i=0;i<vertices.Count;i++)
                {
                    outputVertices.Add(vertices[i]+offset);outputUvs.Add(uvs[i]);
                    outputColors.Add(colors.Count==vertices.Count?colors[i]:new Color32(255,255,255,255));
                    outputNormals.Add(normals.Count==vertices.Count?normals[i]:Vector3.back);
                }
            }
            if(mesh.vertexCount!=outputVertices.Count||mesh.subMeshCount!=submeshes){mesh.Clear();mesh.subMeshCount=submeshes;}
            mesh.SetVertices(outputVertices);mesh.SetUVs(0,outputUvs);mesh.SetColors(outputColors);mesh.SetNormals(outputNormals);
        }

        /// <summary>复用原作透明闪光Shader绘制纯色轮廓，材质按纹理与阵营共享且引用计数释放。</summary>
        private void SynchronizeMaterials(int count)
        {
            source.GetPropertyBlock(global);
            var changed=materials.Count!=count;
            while(leases.Count>count){Release(leases[^1]);leases.RemoveAt(leases.Count-1);materials.RemoveAt(materials.Count-1);}
            for(var sub=0;sub<count;sub++)
            {
                var original=sourceMaterials[Math.Min(sub,sourceMaterials.Count-1)];
                source.GetPropertyBlock(specific,Math.Min(sub,sourceMaterials.Count-1));
                var texture=specific.HasTexture(MainTex)?specific.GetTexture(MainTex):global.HasTexture(MainTex)?global.GetTexture(MainTex):sprite!=null?sprite.sprite.texture:original.HasProperty(MainTex)?original.GetTexture(MainTex):null;
                texture??=Texture2D.whiteTexture;
                var key=(texture.GetInstanceID(),friendly,Math.Max(0,original.renderQueue));
                if(sub>=leases.Count){var added=Acquire(key,texture);leases.Add(added);materials.Add(added.Material);changed=true;}
                else if(leases[sub].Key!=key||leases[sub].Material==null||leases[sub].Material.mainTexture!=texture)
                {Release(leases[sub]);var added=Acquire(key,texture);leases[sub]=added;materials[sub]=added.Material;changed=true;}
                var color=specific.HasColor(ColorId)?specific.GetColor(ColorId):global.HasColor(ColorId)?global.GetColor(ColorId):original.HasProperty(ColorId)?original.GetColor(ColorId):Color.white;
                block.Clear();block.SetColor(ColorId,new Color(1,1,1,color.a));
                foreach(var property in StencilIds)
                    if(specific.HasFloat(property))block.SetFloat(property,specific.GetFloat(property));
                    else if(global.HasFloat(property))block.SetFloat(property,global.GetFloat(property));
                    else if(original.HasProperty(property))block.SetFloat(property,original.GetFloat(property));
                if(changed)target.SetSharedMaterials(materials);
                target.SetPropertyBlock(block,sub);
            }
            if(changed)target.SetSharedMaterials(materials);
        }

        public void Hide(){if(target!=null)target.enabled=false;}
        public void Dispose()
        {
            foreach(var lease in leases)Release(lease);leases.Clear();materials.Clear();
            if(visual!=null)Object.Destroy(visual);if(mesh!=null)Object.Destroy(mesh);if(baked!=null)Object.Destroy(baked);
        }
    }

    private static readonly int MainTex=Shader.PropertyToID("_MainTex"),ColorId=Shader.PropertyToID("_Color");
    private static readonly int[] StencilIds={Shader.PropertyToID("_StencilRef"),Shader.PropertyToID("_StencilComp")};
    private static readonly Dictionary<(int Texture,bool Friendly,int Queue),Lease> cache=new();
    private static Shader? shader;
    private sealed class Lease
    {
        public (int Texture,bool Friendly,int Queue) Key;
        public Material Material=null!;
        public int References;
    }

    /// <summary>只引用已经安装游戏的Shader；缺失时明确报错，不改原模型颜色或使用粉色错误材质。</summary>
    private static Lease Acquire((int Texture,bool Friendly,int Queue) key,Texture texture)
    {
        if(cache.TryGetValue(key,out var existing)&&existing.Material!=null&&existing.Material.mainTexture==texture){existing.References++;return existing;}
        if(shader==null)shader=Shader.Find("Sprites/Default-ColorFlash")??Resources.FindObjectsOfTypeAll<Shader>().FirstOrDefault(s=>s.name=="Sprites/Default-ColorFlash");
        if(shader==null||!shader.isSupported)throw new InvalidOperationException("原作透明描边所需Shader尚未加载或不可用");
        ColorUtility.TryParseHtmlString(SummonOutlineStyle.ColorHex(key.Friendly),out var color);
        var material=new Material(shader){name="LiveInteract "+(key.Friendly?"friendly":"hostile")+" contour",hideFlags=HideFlags.DontSave,mainTexture=texture,renderQueue=key.Queue};
        material.SetFloat("_IsCharacter",0);material.SetFloat("_IsHero",0);material.SetFloat("_CanDesaturate",0);material.SetFloat("_CanLerpAmbient",0);material.SetFloat("_CanHueShift",0);material.SetFloat("_BlackThreadAmount",0);material.SetFloat("_IsBlackThreaded",0);
        material.SetFloat("_FlashAmount",1);material.SetColor("_FlashColor",color);material.SetColor("_Color",Color.white);
        var lease=new Lease{Key=key,Material=material,References=1};cache[key]=lease;return lease;
    }

    private static void Release(Lease lease)
    {
        if(--lease.References>0)return;
        if(cache.TryGetValue(lease.Key,out var current)&&ReferenceEquals(current,lease))cache.Remove(lease.Key);
        if(lease.Material!=null)Object.Destroy(lease.Material);
    }
}

/// <summary>仅标识辅助渲染层，克隆时移除；不包含碰撞、生命或攻击。</summary>
internal sealed class SummonOutlineVisual : MonoBehaviour { }
