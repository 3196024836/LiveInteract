using System;
using System.Collections.Generic;
namespace LiveInteract.Contracts;

/// <summary>召唤物本体轮廓的统一阵营颜色与屏幕像素宽度。</summary>
public static class SummonOutlineStyle
{
    public const double DefaultWidth=2;
    public static string ColorHex(bool friendly)=>friendly?"#63F58D":"#FF5261";
    public static double NormalizeWidth(double value)=>double.IsNaN(value)||double.IsInfinity(value)?DefaultWidth:Math.Max(1,Math.Min(5,value));
    public readonly struct Direction
    {
        public readonly float X,Y;
        public Direction(float x,float y){X=x;Y=y;}
    }
    public static IReadOnlyList<Direction> Directions { get; }=Array.AsReadOnly(new[]{
        new Direction(1,0),new Direction(-1,0),new Direction(0,1),new Direction(0,-1),
        new Direction(.70710678f,.70710678f),new Direction(-.70710678f,.70710678f),
        new Direction(.70710678f,-.70710678f),new Direction(-.70710678f,-.70710678f)});
    /// <summary>排除已核对的屏幕混合灯光薄雾，不能把整张光晕网格染成实体轮廓。</summary>
    /// <param name="shader">原作渲染器材质的Shader名称。</param>
    /// <returns>是否为允许增加阵营轮廓的材质类型。</returns>
    public static bool ShouldOutlineShader(string shader) => shader != "Sprites/Screen";
    /// <summary>同组轮廓排在身体后，组外仍保留原生深度排序，不抬高整个召唤物。</summary>
    /// <param name="sourceOrder">原身体的组内排序值。</param>
    /// <param name="grouped">是否位于原生SortingGroup内部。</param>
    /// <returns>轮廓的组内排序值。</returns>
    public static int SortingOrder(int sourceOrder, bool grouped) => grouped ? Math.Max(short.MinValue, sourceOrder - 1) : sourceOrder;
}
