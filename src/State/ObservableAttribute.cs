// 直搬自 fork：~/ECS/FairyGUI-unity Assets/MVVM/ObservableAttribute.cs @ d1a9d7d
// 改动：namespace FairyGUI.Mvvm → FairyNext.State；其余原样。

using System;

namespace FairyNext.State
{
    /// <summary>
    /// Marks a field of a partial ViewModel subclass for property generation. The source
    /// generator emits a property with an equality-guarded setter that calls MarkDirty,
    /// plus a "{Name}Property" index constant for binding registration.
    /// Field naming: _camelCase or m_camelCase.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class ObservableAttribute : Attribute
    {
    }
}
