// 直搬自 fork：~/ECS/FairyGUI-unity Assets/Scripts/Tween/EaseType.cs @ d1a9d7d
// 改动：namespace FairyGUI → FairyNext.State.Anim；仅搬 EaseType 枚举。
// 偏离 fork：同文件的 CustomEase 类依赖 GPath 曲线求值（UnityEngine.Vector2 + GPath），
// 本期不搬——EaseManager 的 Custom 分支改收 Func<float,float> 自定义评估器；
// GPath 版 CustomEase 随 M2 tween 引擎回归。

namespace FairyNext.State.Anim
{
    /// <summary>
    ///
    /// </summary>
    public enum EaseType
    {
        Linear,
        SineIn,
        SineOut,
        SineInOut,
        QuadIn,
        QuadOut,
        QuadInOut,
        CubicIn,
        CubicOut,
        CubicInOut,
        QuartIn,
        QuartOut,
        QuartInOut,
        QuintIn,
        QuintOut,
        QuintInOut,
        ExpoIn,
        ExpoOut,
        ExpoInOut,
        CircIn,
        CircOut,
        CircInOut,
        ElasticIn,
        ElasticOut,
        ElasticInOut,
        BackIn,
        BackOut,
        BackInOut,
        BounceIn,
        BounceOut,
        BounceInOut,
        Custom
    }
}
