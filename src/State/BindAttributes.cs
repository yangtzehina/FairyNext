// 直搬自 fork：~/ECS/FairyGUI-unity Assets/MVVM/BindAttributes.cs @ d1a9d7d
// 改动：namespace FairyGUI.Mvvm → FairyNext.State；其余原样。

using System;

namespace FairyNext.State
{
    /// <summary>
    /// Declares the ViewModel type a partial view class binds against. The source
    /// generator emits BindTo(Binder, TViewModel) wiring every [Bind] member.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class BindContextAttribute : Attribute
    {
        public BindContextAttribute(Type viewModelType)
        {
        }
    }

    /// <summary>
    /// On a field: binds a UI object to a ViewModel property; the apply code is derived
    /// from the field/property types (numeric text via SetIntText, string text, bool to
    /// visible, numeric to GProgressBar/GSlider value). On a parameterless void method:
    /// the method is invoked whenever the property is dirty (escape hatch for anything
    /// the field rules cannot express).
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Method)]
    public sealed class BindAttribute : Attribute
    {
        public BindAttribute(string propertyName)
        {
        }
    }

}
